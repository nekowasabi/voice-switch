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

// Wake-word audio is pasted by Superwhisper in its active mode; automatic audio needs a verified no-auto-paste mode and stays in history.
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
    private readonly Func<string, Task<RouteResult>>? onTranscribed;
    private readonly string? superwhisperMode;
    private readonly string preferencesPath;
    private readonly string modesDir;
    private static readonly SemaphoreSlim ModeOwner = new(1, 1);
    private string JournalPath => Path.Combine(root, "mode-restore.json");

    public RegisteredSuperwhisperHandoff(
        string root,
        string recordingsDir,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        bool dryRun = false,
        Action<nint>? restoreFocus = null,
        Func<TimeSpan, Task>? delay = null,
        Func<string, Task<RouteResult>>? onTranscribed = null,
        string? superwhisperMode = null,
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
        this.preferencesPath = preferencesPath ?? WindowsPaths.SuperwhisperPreferencesPath();
        this.modesDir = modesDir ?? WindowsPaths.SuperwhisperModesPath();
        if (!dryRun)
        {
            Sweep();
        }
    }

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        await ModeOwner.WaitAsync(cancellation);
        try { return await SubmitOwnedAsync(audio, cancellation); }
        finally { ModeOwner.Release(); }
    }

    private async Task<HandoffResult> SubmitOwnedAsync(DictationAudio audio, CancellationToken cancellation)
    {
        // Nobody asked for an automatic recording, so it may only enter a mode verified not to paste.
        var controlled = audio.Wake.IsAutomatic;
        // Keeps the file write and process launch off the runtime loop even though they start synchronously.
        await Task.Yield();
        if (dryRun)
        {
            return new HandoffResult(HandoffStatus.DryRunSuppressed, audio.SessionId, null, "dry-run kept audio in memory only");
        }

        if (DictationHistory.Shared.Select(audio.SessionId) is not null)
            return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null, "duplicate session already retained");
        var historyGeneration = DictationHistory.Shared.Generation;
        var wavPath = Path.Combine(root, $"{audio.SessionId:N}.wav");
        if (BuildFileIntakeArgument(wavPath) is not { } argument)
        {
            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null,
                $"Superwhisper file intake needs an ASCII path without spaces; nothing was written or sent: {Path.GetFullPath(wavPath)}");
        }

        if (!TryOwnMode(out var ownership))
            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "mode ownership unavailable; local history required");
        using var heldOwnership = ownership;
        if (!await RecoverOwnedAsync(audio.Target))
            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "mode recovery is unresolved");
        var actionMode = string.IsNullOrWhiteSpace(audio.Action?.SuperwhisperMode) ? null : audio.Action.SuperwhisperMode.Trim();
        // An action's text goes only to its URL, so it also needs the no-auto-paste mode unless the action names its own.
        var wantedMode = controlled ? superwhisperMode : audio.Action is null ? null : actionMode ?? superwhisperMode;
        if (controlled && !SuperwhisperModes.HasAutoPasteDisabled(wantedMode, ReadModes()))
            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "no verified no-auto-paste mode; local history required");
        var (modeRequested, previousMode) = await EnterModeAsync(wantedMode, audio.Target);
        string? text = null;
        HashSet<string> previousRuns = [];
        try
        {
            if (controlled && (!modeRequested || !SuperwhisperModes.HasAutoPasteDisabled(wantedMode, ReadModes())))
                return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "controlled mode not confirmed; local history required");
            if (controlled)
            {
                try { previousRuns = Directory.Exists(recordingsDir) ? Directory.GetDirectories(recordingsDir).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal) : []; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "recordings baseline unavailable; local history required"); }
            }
            var ownsWav = false;
            try
            {
                ValidateOwnedState(root);
                Directory.CreateDirectory(root);
                ValidateOwnedState(root);
                ownsWav = true;
                await File.WriteAllBytesAsync(wavPath, Pcm16Wav.Encode(TrimQuietHead(audio.Samples.AsSpan())), cancellation);
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
                text = FindResult(recordingsDir, since, previousRuns);
                if (text is null)
                {
                    await delay(Tick);
                }
            }

            if (text is not null)
            {
                Log.Info($"dictation: {text.Length} chars in {clock.ElapsedMilliseconds} ms audio={audio.Samples.Length / (double)Pcm16Wav.Rate:F2}s sid={audio.SessionId}");
            }
        }
        finally
        {
            if (previousMode is not null) await RecoverOwnedAsync(audio.Target);
        }

        if (text is null)
        {
            Log.Info($"dictation: no superwhisper result within 30 s, kept {wavPath}");
            return new HandoffResult(HandoffStatus.NoResult, audio.SessionId, wavPath, "no Superwhisper result within 30 s; WAV kept");
        }

        TryDelete(wavPath);
        cancellation.ThrowIfCancellationRequested();
        if (!DictationHistory.Shared.Add(audio.SessionId, text, audio.Wake, historyGeneration, audio.TranscriptPolicy))
            return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null, "transcript discarded");
        if (controlled)
            return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Superwhisper result added to history");
        if (audio.Action is { } action)
        {
            // Mac parity: an action's text goes only to its URL, never to a tmux pane or a paste.
            try
            {
                LaunchUrl(action.InputUrl(text));
                Log.Info($"dictation delivered: action {action.Name}");
            }
            catch (Exception ex)
            {
                Log.Info($"dictation action {action.Name} failed to open: {ex.Message}");
            }

            return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Superwhisper result found; WAV deleted");
        }

        var route = RouteResult.NotRouted;
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
                route = RouteResult.NotRouted;
            }
        }

        // Wake-word audio never switches mode, so Superwhisper has already pasted unless the pane took it.
        if (SuperwhisperModes.Decide(modeRequested, route.Disposition) == DictationDelivery.Superwhisper)
            Log.Info("dictation delivered: superwhisper");

        return new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "Superwhisper result found; WAV deleted");
    }

    // Superwhisper drops the first sentence of quiet speech when the file opens with the session's 300 ms preroll of
    // room noise (voice-switch.log 2026-10-12; reproduced 4/5 with the real app, 0/20 once the head was cut), so the file
    // it reads starts at most 120 ms before the first sample that is clearly speech. The session range is untouched.
    internal static ReadOnlySpan<short> TrimQuietHead(ReadOnlySpan<short> samples)
    {
        const short speech = 300;
        const int keep = Pcm16Wav.Rate * 120 / 1000;
        var first = 0;
        while (first < samples.Length && Math.Abs((int)samples[first]) < speech) first++;
        // ponytail: a fixed amplitude gate; a click in the preroll keeps the whole head. Use frame RMS if that shows up in the log.
        return first == samples.Length ? samples : samples[Math.Max(0, first - keep)..];
    }

    // Requested is true only when the configured mode is confirmed active; Previous is the mode to switch back to.
    // Without a readable activeMode there would be nothing to switch back to, so the mode is left alone.
    // A failed poll leaves Requested false and production rejects file intake.
    private async Task<(bool Requested, string? Previous)> EnterModeAsync(string? superwhisperMode, nint target)
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

        if (!SaveRestore(new ModeRestore(active, key))) return (false, null);
        Log.Info($"superwhisper mode: {key} (was {active})");
        await SwitchModeAsync(key, target);
        // Mirror Mac enterSuperwhisperMode: only claim Requested when activeMode actually matches.
        if (ReadActiveMode() != key)
        {
            return (false, active);
        }

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

    public async Task<bool> RecoverAsync(CancellationToken cancellation)
    {
        if (dryRun) return true;
        await ModeOwner.WaitAsync(cancellation);
        try
        {
            if (!File.Exists(JournalPath)) return true;
            if (!TryOwnMode(out var ownership)) return false;
            using var heldOwnership = ownership;
            return await RecoverOwnedAsync(0);
        }
        finally { ModeOwner.Release(); }
    }

    private bool TryOwnMode(out FileStream? ownership)
    {
        ownership = null;
        try
        {
            ValidateOwnedState(root);
            Directory.CreateDirectory(root);
            ValidateOwnedState(root);
            ownership = new FileStream(Path.Combine(root, "mode-owner.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private bool SaveRestore(ModeRestore state)
    {
        try
        {
            ValidateOwnedState(root);
            Directory.CreateDirectory(root);
            ValidateOwnedState(root);
            var temporary = JournalPath + "." + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, JournalPath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Info("superwhisper mode: cannot save recovery state; no mode switch");
            return false;
        }
    }

    private async Task<bool> RecoverOwnedAsync(nint target)
    {
        try
        {
            ValidateOwnedState(root);
            if (!File.Exists(JournalPath)) return true;
            if (new FileInfo(JournalPath).Length > 8192) return false;
            var state = JsonSerializer.Deserialize<ModeRestore>(File.ReadAllText(JournalPath));
            if (state is null) return false;
            switch (state.Decide(ReadActiveMode()))
            {
                case ModeRecovery.Unknown: return false;
                case ModeRecovery.Restore:
                    if (!await SwitchModeAsync(state.Previous, target)) return false;
                    Log.Info("superwhisper mode restored from recovery state");
                    break;
            }
            File.Delete(JournalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            Log.Info("superwhisper mode recovery unavailable; local history required");
            return false;
        }
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

    // Same launcher as PlatformDefaults.SuperwhisperToggle, without a shell; the URL is validated and percent-encoded, so it
    // needs no quoting.
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
    internal static string? FindResult(string recordingsDir, long sinceUnixSeconds, IReadOnlySet<string>? previousRuns = null)
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
            .Where(run => run.Ok && run.Time >= sinceUnixSeconds && previousRuns?.Contains(Path.GetFileName(run.Dir)) != true)
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
