using System.Diagnostics;
using System.Collections.Immutable;
using System.Text;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// Opt-in Haas-ja-v1.0 body path: the WAV goes to an external command, not Superwhisper.
// Argv is command, then --vocab + vocabulary when set, then the WAV path last. No shell.
public sealed class HaasRunnerHandoff : IDictationHandoff
{
    private readonly string root;
    private readonly string[] command;
    private readonly double timeoutSeconds;
    private readonly string? vocabulary;
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly Func<string, Task<RouteResult>> onTranscribed;
    private readonly Func<WakeAction, string, CancellationToken, Task> post;
    private readonly Func<string, nint, bool> paste;

    public HaasRunnerHandoff(
        string root,
        string[] command,
        double timeoutSeconds,
        string? vocabulary,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        Func<string, Task<RouteResult>>? onTranscribed = null,
        Func<WakeAction, string, CancellationToken, Task>? post = null,
        Func<string, nint, bool>? paste = null)
    {
        this.root = root;
        this.command = command;
        this.timeoutSeconds = timeoutSeconds;
        this.vocabulary = string.IsNullOrWhiteSpace(vocabulary) ? null : vocabulary;
        this.startProcess = startProcess ?? (psi => Process.Start(psi));
        this.onTranscribed = onTranscribed ?? TmuxPaneRouter.RouteAsync;
        this.post = post ?? PostAction.SendAsync;
        this.paste = paste ?? Tray.WindowFocus.Paste;
    }

    public static string[] BuildArguments(string[] command, string? vocabulary, string wavPath)
    {
        var args = new List<string>(command);
        if (!string.IsNullOrWhiteSpace(vocabulary))
        {
            args.Add("--vocab");
            args.Add(vocabulary!);
        }
        args.Add(wavPath);
        return args.ToArray();
    }

    public static string? InterpretOutput(string output)
    {
        var trimmed = output.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        await Task.Yield();
        if (DictationHistory.Shared.Select(audio.SessionId) is not null)
            return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null, "duplicate session already retained");
        var historyGeneration = DictationHistory.Shared.Generation;
        var trimmed = RegisteredSuperwhisperHandoff.TrimQuietHead(audio.Samples.AsSpan()).ToArray();
        var wavPath = Path.Combine(root, $"{audio.SessionId:N}.wav");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(wavPath, Pcm16Wav.Encode(trimmed), cancellation);
        }
        catch (Exception ex)
        {
            return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, $"dictation: haas runner failed: {ex.Message}");
        }

        var argv = BuildArguments(command, vocabulary, wavPath);
        var psi = new ProcessStartInfo(argv[0])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var arg in argv.Skip(1)) psi.ArgumentList.Add(arg);
        Process? process;
        try
        {
            process = startProcess(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            Log.Info($"dictation: haas runner failed: {ex.Message} sid={audio.SessionId}");
            return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, $"dictation: haas runner failed: {ex.Message}");
        }
        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);
            try
            {
                var exited = await Task.Run(() => process.WaitForExit((int)Math.Max(1, timeout.TotalMilliseconds)), cancellation);
                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { try { process.Kill(); } catch { } }
                    try { await process.WaitForExitAsync(cancellation); } catch { }
                    Log.Info($"dictation: haas runner failed: timeout after {timeoutSeconds}s sid={audio.SessionId} wav={wavPath}");
                    return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, $"dictation: haas runner failed: timeout; WAV kept");
                }
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0)
            {
                Log.Info($"dictation: haas runner failed: exit {process.ExitCode} sid={audio.SessionId} wav={wavPath}");
                return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, $"dictation: haas runner failed: exit {process.ExitCode}; WAV kept");
            }
            var text = InterpretOutput(stdout);
            if (text is null)
            {
                Log.Info($"dictation: haas runner failed: empty stdout sid={audio.SessionId} wav={wavPath}");
                return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, "dictation: haas runner failed: empty stdout; WAV kept");
            }
            Log.Info($"dictation: haas {text.Length} chars sid={audio.SessionId}");
            try { File.Delete(wavPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            cancellation.ThrowIfCancellationRequested();
            if (!DictationHistory.Shared.Add(audio.SessionId, text, audio.Wake, historyGeneration, audio.TranscriptPolicy))
                return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null, "transcript discarded");
            if (audio.Wake.IsAutomatic)
                return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Haas result added to history");
            if (audio.Action is { } action)
            {
                try
                {
                    if (action.TokenEnv is not null)
                    {
                        await post(action, text, cancellation);
                        Log.Info($"dictation delivered: post {action.Name}");
                    }
                    else
                    {
                        if (GrokBotPrompt.IsPrompt(action.Url))
                        {
                            var (_, truncated) = GrokBotPrompt.Fit(text);
                            if (truncated) Log.Info($"grokbot prompt text truncated to 1000 encoded chars for action {action.Name}");
                        }
                        LaunchUrl(action.InputUrl(text));
                        Log.Info($"dictation delivered: action {action.Name}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"dictation action {action.Name} failed: {ex.Message}");
                }
                return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Haas result found; WAV deleted");
            }
            var route = RouteResult.NotRouted;
            try
            {
                route = await onTranscribed(text);
            }
            catch (Exception ex)
            {
                Log.Info($"tmux: route failed: {ex.Message}");
                route = RouteResult.NotRouted;
            }
            if (SuperwhisperModes.Decide(modeRequested: true, route.Disposition) == DictationDelivery.Paste)
            {
                var body = route.Disposition == RouteDisposition.SendFailed && !string.IsNullOrEmpty(route.Body) ? route.Body! : text;
                paste(body, audio.Target);
                Log.Info("dictation delivered: paste");
            }
            else
            {
                Log.Info("dictation delivered: pane");
            }
            return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Haas result found; WAV deleted");
        }
    }

    private void LaunchUrl(string url)
    {
        var psi = new ProcessStartInfo("rundll32.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("url.dll,FileProtocolHandler");
        psi.ArgumentList.Add(url);
        (startProcess(psi) ?? throw new InvalidOperationException("Process.Start returned null")).Dispose();
    }
}
