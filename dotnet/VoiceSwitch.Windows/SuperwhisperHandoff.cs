using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// Mirrors MacApp.swift handoff/awaitResult: Superwhisper transcribes the WAV and pastes into the frontmost app;
// we only hand focus back, wait for its result to appear, and clean up.
public sealed class RegisteredSuperwhisperHandoff : IDictationHandoff
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);
    // A WAV kept after NoResult may still be read by a cold-starting Superwhisper; ten minutes outlives that.
    private static readonly TimeSpan SweepAge = TimeSpan.FromMinutes(10);
    private readonly string root;
    private readonly string recordingsDir;
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly bool dryRun;
    private readonly Action<nint> restoreFocus;
    private readonly Func<TimeSpan, Task> delay;

    public RegisteredSuperwhisperHandoff(
        string root,
        string recordingsDir,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        bool dryRun = false,
        Action<nint>? restoreFocus = null,
        Func<TimeSpan, Task>? delay = null)
    {
        this.root = root;
        this.recordingsDir = recordingsDir;
        this.startProcess = startProcess ?? Process.Start;
        this.dryRun = dryRun;
        this.restoreFocus = restoreFocus ?? (_ => { });
        this.delay = delay ?? (span => Task.Delay(span));
        if (!dryRun)
        {
            Sweep();
        }
    }

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        // Keeps the file write and process launch off the runtime loop even though they start synchronously.
        await Task.Yield();
        if (dryRun)
        {
            return new HandoffResult(HandoffStatus.DryRunSuppressed, audio.SessionId, null, "dry-run kept audio in memory only");
        }

        var wavPath = Path.Combine(root, $"{audio.SessionId:N}.wav");
        if (BuildFileIntakeArgument(wavPath) is not { } argument)
        {
            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null,
                $"Superwhisper file intake needs an ASCII path without spaces; nothing was written or sent: {Path.GetFullPath(wavPath)}");
        }

        var ownsWav = false;
        try
        {
            ValidateOwnedState(root);
            Directory.CreateDirectory(root);
            ValidateOwnedState(root);
            ownsWav = true;
            await File.WriteAllBytesAsync(wavPath, Pcm16Wav.Encode(audio.Samples.AsSpan()), cancellation);
            var psi = new ProcessStartInfo(ResolveSuperwhisperExecutable())
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(argument);
            (startProcess(psi) ?? throw new InvalidOperationException("Process.Start returned null")).Dispose();
        }
        catch (Exception ex)
        {
            if (ownsWav)
            {
                TryDelete(wavPath);
            }

            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, ex.Message);
        }

        var since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2;
        var clock = Stopwatch.StartNew();
        // Opening a file brings Superwhisper to the front, and it skips auto-paste while it is still frontmost.
        for (var i = 0; i < 20; i++)
        {
            await delay(Tick);
            restoreFocus(audio.Target);
        }

        for (var i = 0; i < 300; i++)
        {
            if (FindResult(recordingsDir, since) is { } text)
            {
                Log.Info($"dictation: {text.Length} chars in {clock.ElapsedMilliseconds} ms");
                TryDelete(wavPath);
                return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Superwhisper result found; WAV deleted");
            }

            await delay(Tick);
        }

        Log.Info($"dictation: no superwhisper result within 30 s, kept {wavPath}");
        return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, "no Superwhisper result within 30 s; WAV kept");
    }

    // Superwhisper writes one <unix-seconds>/meta.json per run; the newest run at or after `since` is ours.
    internal static string? FindResult(string recordingsDir, long sinceUnixSeconds)
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(recordingsDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var runs = dirs
            .Select(dir => (Dir: dir, Ok: long.TryParse(Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var time), Time: time))
            .Where(run => run.Ok && run.Time >= sinceUnixSeconds)
            .OrderByDescending(run => run.Time);
        foreach (var run in runs)
        {
            try
            {
                using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run.Dir, "meta.json")));
                if (meta.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var key in new[] { "llmResult", "result" })
                {
                    if (meta.RootElement.TryGetProperty(key, out var value)
                        && value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } text)
                    {
                        return text;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return null;
    }

    private void Sweep()
    {
        try
        {
            ValidateOwnedState(root);
            if (!Directory.Exists(root))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - SweepAge;
            foreach (var path in Directory.EnumerateFiles(root).Where(path => File.GetLastWriteTimeUtc(path) < cutoff))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Info($"dictation handoff: sweep skipped: {ex.Message}");
        }
    }

    private static string ResolveSuperwhisperExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Superwhisper.exe";
        }

        using var key = Registry.ClassesRoot.OpenSubKey(@"Applications\Superwhisper.exe\shell\open\command");
        var command = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Superwhisper registered open command was not found.");
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                return trimmed[1..end];
            }
        }

        return trimmed.Split(' ', 2)[0];
    }

    private static void ValidateOwnedDirectory(string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.Exists(fullRoot)
            && (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("handoff owned directory root is a reparse point");
        }
    }

    // The profile above root may legitimately be redirected (e.g. FSLogix).
    // The owned root and all its entries must be ordinary paths before the WAV write or the sweep touches them.
    private static void ValidateOwnedState(string root)
    {
        ValidateOwnedDirectory(root);
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            {
                throw new InvalidDataException("handoff owned entry is a directory or reparse point");
            }
        }
    }

    private static string? BuildFileIntakeArgument(string wavPath)
    {
        var fullPath = Path.GetFullPath(wavPath);
        if (fullPath.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('/' or '\\' or ':' or '-' or '_' or '.' or '~')))
        {
            return null;
        }

        return "superwhisper://file//" + fullPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info($"dictation handoff: could not delete {path}: {ex.Message}");
        }
    }
}
