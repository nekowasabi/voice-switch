using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
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
    private readonly Func<string, Task<RouteDisposition>>? onTranscribed;
    private readonly string? superwhisperMode;
    private readonly Func<string, nint, bool> paste;
    private readonly string preferencesPath;
    private readonly string modesDir;

    public RegisteredSuperwhisperHandoff(
        string root,
        string recordingsDir,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        bool dryRun = false,
        Action<nint>? restoreFocus = null,
        Func<TimeSpan, Task>? delay = null,
        Func<string, Task<RouteDisposition>>? onTranscribed = null,
        string? superwhisperMode = null,
        Func<string, nint, bool>? paste = null,
        string? preferencesPath = null,
        string? modesDir = null)
    {
        this.root = root;
        this.recordingsDir = recordingsDir;
        this.startProcess = startProcess ?? Process.Start;
        this.dryRun = dryRun;
        this.restoreFocus = restoreFocus ?? (_ => { });
        this.delay = delay ?? (span => Task.Delay(span));
        this.onTranscribed = onTranscribed;
        this.superwhisperMode = string.IsNullOrWhiteSpace(superwhisperMode) ? null : superwhisperMode.Trim();
        this.paste = paste ?? ((_, _) => false);
        this.preferencesPath = preferencesPath ?? WindowsPaths.SuperwhisperPreferencesPath();
        this.modesDir = modesDir ?? WindowsPaths.SuperwhisperModesPath();
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

        var (modeRequested, previousMode) = await EnterModeAsync(audio.Target);
        string? text = null;
        try
        {
            var ownsWav = false;
            try
            {
                ValidateOwnedState(root);
                Directory.CreateDirectory(root);
                ValidateOwnedState(root);
                ownsWav = true;
                await File.WriteAllBytesAsync(wavPath, Pcm16Wav.Encode(audio.Samples.AsSpan()), cancellation);
                Launch(argument);
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

            for (var i = 0; i < 300 && text is null; i++)
            {
                text = FindResult(recordingsDir, since);
                if (text is null)
                {
                    await delay(Tick);
                }
            }

            if (text is not null)
            {
                Log.Info($"dictation: {text.Length} chars in {clock.ElapsedMilliseconds} ms");
            }
        }
        finally
        {
            if (previousMode is not null && await SwitchModeAsync(previousMode, audio.Target))
            {
                Log.Info($"superwhisper mode restored: {previousMode}");
            }
        }

        if (text is null)
        {
            Log.Info($"dictation: no superwhisper result within 30 s, kept {wavPath}");
            return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, "no Superwhisper result within 30 s; WAV kept");
        }

        TryDelete(wavPath);
        var route = RouteDisposition.NotRouted;
        if (onTranscribed is not null)
        {
            // The route is a side effect on the result; whatever it throws must not change the handoff status.
            try
            {
                route = await onTranscribed(text);
            }
            catch (Exception ex)
            {
                Log.Info($"tmux: route failed: {ex.Message}");
                route = RouteDisposition.NotRouted;
            }
        }

        switch (SuperwhisperModes.Decide(modeRequested, route))
        {
            case DictationDelivery.Paste:
                Log.Info(paste(text, audio.Target) ? "dictation delivered: paste" : "dictation not delivered: no target window to paste into");
                break;
            case DictationDelivery.Superwhisper:
                Log.Info("dictation delivered: superwhisper");
                break;
        }

        return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Superwhisper result found; WAV deleted");
    }

    // Requested is true when the configured mode is (or now should be) active; Previous is the mode to switch back to.
    // Without a readable activeMode there would be nothing to switch back to, so the mode is left alone.
    private async Task<(bool Requested, string? Previous)> EnterModeAsync(nint target)
    {
        if (superwhisperMode is null)
        {
            return (false, null);
        }

        var key = SuperwhisperModes.ResolveKey(superwhisperMode, ReadModes());
        if (key is null)
        {
            Log.Info($"superwhisper mode \"{superwhisperMode}\" not found; using the active mode");
            return (false, null);
        }

        var active = ReadActiveMode();
        if (active is null)
        {
            Log.Info("superwhisper mode: activeMode is unreadable; using the active mode");
            return (false, null);
        }

        if (active == key)
        {
            return (true, null);
        }

        Log.Info($"superwhisper mode: {key} (was {active})");
        await SwitchModeAsync(key, target);
        return (true, active);
    }

    private async Task<bool> SwitchModeAsync(string key, nint target)
    {
        try
        {
            Launch("superwhisper://mode?key=" + Uri.EscapeDataString(key));
        }
        catch (Exception ex)
        {
            Log.Info($"superwhisper mode: switch to {key} failed to start: {ex.Message}");
            return false;
        }

        for (var i = 0; i < 30; i++)
        {
            await delay(Tick);
            // A Superwhisper URL may bring it to the front, as the file URL does.
            restoreFocus(target);
            if (ReadActiveMode() == key)
            {
                return true;
            }
        }

        Log.Info($"superwhisper mode: {key} not active after 3 s; continuing");
        return false;
    }

    private string? ReadActiveMode()
    {
        try
        {
            return SuperwhisperModes.ActiveMode(File.Exists(preferencesPath) ? File.ReadAllText(preferencesPath) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IEnumerable<string> ReadModes()
    {
        try
        {
            return Directory.Exists(modesDir)
                ? Directory.EnumerateFiles(modesDir, "*.json").Order(StringComparer.Ordinal).Select(File.ReadAllText).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Launch(string argument)
    {
        var psi = new ProcessStartInfo(ResolveSuperwhisperExecutable())
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(argument);
        (startProcess(psi) ?? throw new InvalidOperationException("Process.Start returned null")).Dispose();
    }

    // The production root, decided once per run start and logged.
    public static string DefaultRoot()
    {
        var preferred = WindowsPaths.DefaultHandoffPath();
        var sid = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User?.Value ?? "user" : "user";
        var shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "voice-switch", "dictation-handoffs", sid);
        var root = ResolveRoot(preferred, ShortPath, shared);
        Log.Info(root == preferred ? $"dictation handoff: WAV folder {root}" : $"dictation handoff: WAV folder {root} (fallback: {preferred} is not a plain ASCII path)");
        return root;
    }

    // Superwhisper's file intake takes only plain ASCII paths, so a non-ASCII or spaced profile name would fail every
    // dictation. Fall back to the folder's 8.3 short name, then to a folder under ProgramData that only this user can open.
    public static string ResolveRoot(string preferred, Func<string, string?> shortPath, string shared)
    {
        if (IsIntakeSafe(Path.GetFullPath(preferred)))
        {
            return preferred;
        }

        try
        {
            // A short name exists only for a folder that exists, and only where the volume generates 8.3 names.
            Directory.CreateDirectory(preferred);
            if (shortPath(preferred) is { } shortened && IsIntakeSafe(Path.GetFullPath(shortened)))
            {
                return shortened;
            }

            CreatePrivateDirectory(shared);
            return shared;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Info($"dictation handoff: no intake-safe WAV folder: {ex.Message}");
            return preferred;
        }
    }

    public static string? ShortPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new StringBuilder(1024);
        var length = GetShortPathName(path, buffer, buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : null;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        // ProgramData lets every user read what another creates, and these WAVs are the user's voice.
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("current user SID is unavailable");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var dir = new DirectoryInfo(path);
        Directory.CreateDirectory(dir.Parent!.FullName);
        if (dir.Exists)
        {
            dir.SetAccessControl(security);
        }
        else
        {
            dir.Create(security);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathName(string longPath, StringBuilder shortPath, int bufferLength);

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
        return IsIntakeSafe(fullPath) ? "superwhisper://file//" + fullPath : null;
    }

    private static bool IsIntakeSafe(string fullPath) =>
        fullPath.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '\\' or ':' or '-' or '_' or '.' or '~');

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
