using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

public enum JournalPlatform { Windows, Mac }

public sealed record JournalEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string Text,
    string Origin,
    string? EndReason,
    double? RecordingSeconds);

/// <summary>
/// Append-only JSONL of utterances <see cref="DictationHistory"/> accepted.
/// A bad path is logged and the OS default directory is used. A failed config reload never constructs a new instance.
/// </summary>
public sealed class DictationJournal
{
    public const int SchemaVersion = 1;
    private static readonly Regex Unexpanded = new(@"%[A-Za-z0-9_]+%", RegexOptions.CultureInvariant);
    private static readonly JsonWriterOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly object gate = new();
    private readonly Action<string> log;
    private readonly string fallbackDirectory;
    private JournalTarget target;
    private bool reportedUnprotected;

    private DictationJournal(JournalTarget target, string fallbackDirectory, Action<string> log)
    {
        this.target = target;
        this.fallbackDirectory = fallbackDirectory;
        this.log = log;
    }

    public static bool Enabled { get; private set; }
    public static DictationJournal? Active { get; private set; }

    public static void Bind(VoiceSwitchConfig config, Action<string> log)
    {
        Enabled = true;
        Active = Open(config.Dictation?.JournalDir, log, JournalPlatform.Windows);
    }

    public static string DefaultDirectory(JournalPlatform platform) =>
        platform == JournalPlatform.Mac
            ? WindowsPaths.ExpandPath("~/Library/Application Support/voice-switch/journal")
            : WindowsPaths.ExpandPath(@"%LOCALAPPDATA%\voice-switch\journal");

    public static string OriginLabel(WakeOrigin origin, bool manual) =>
        manual ? "手動" : origin.IsAutomatic ? "自動" : "ウェイク";

    public static DictationJournal Open(string? configured, Action<string> log, JournalPlatform platform, string? defaultDirectory = null)
    {
        log ??= static _ => { };
        var fallback = string.IsNullOrWhiteSpace(defaultDirectory) ? DefaultDirectory(platform) : defaultDirectory;
        if (configured is null)
            return new DictationJournal(JournalTarget.Directory(fallback), fallback, log);
        if (string.IsNullOrWhiteSpace(configured))
        {
            log($"dictation journal: path unusable (empty); using default {fallback}");
            return new DictationJournal(JournalTarget.Directory(fallback), fallback, log);
        }

        string expanded;
        try { expanded = WindowsPaths.ExpandPath(configured.Trim()); }
        catch (Exception ex)
        {
            log($"dictation journal: path unusable ({ex.Message}); using default {fallback}");
            return new DictationJournal(JournalTarget.Directory(fallback), fallback, log);
        }

        if (Problem(expanded) is { } problem)
        {
            log($"dictation journal: path unusable ({problem}); using default {fallback}");
            return new DictationJournal(JournalTarget.Directory(fallback), fallback, log);
        }

        var single = expanded.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase);
        return new DictationJournal(single ? JournalTarget.File(expanded) : JournalTarget.Directory(expanded), fallback, log);
    }

    public void Append(JournalEntry entry)
    {
        var line = Line(entry);
        lock (gate)
        {
            if (TryAppend(target, line, entry.Timestamp, out var error)) return;
            if (string.Equals(target.Path, fallbackDirectory, StringComparison.Ordinal))
            {
                log($"dictation journal: default unwritable ({error})");
                return;
            }

            log($"dictation journal: {error}; using default {fallbackDirectory}");
            var fallback = JournalTarget.Directory(fallbackDirectory);
            if (!TryAppend(fallback, line, entry.Timestamp, out var fallbackError))
                log($"dictation journal: default unwritable ({fallbackError})");
            else
                target = fallback;
        }
    }

    public int DeleteDay(string day)
    {
        if (day.Length != 10) return 0;
        lock (gate)
        {
            if (target.SingleFile)
                return Filter(target.Path, line => DayOf(line) == day) ? 1 : 0;
            var path = Path.Combine(target.Path, day + ".jsonl");
            if (!File.Exists(path) || IsSymlink(path)) return 0;
            File.Delete(path);
            return 1;
        }
    }

    public bool DeleteEntry(Guid id)
    {
        var token = "\"id\":\"" + id.ToString("D") + "\"";
        lock (gate)
        {
            if (target.SingleFile)
                return Filter(target.Path, line => line.Contains(token, StringComparison.Ordinal));
            if (!Directory.Exists(target.Path)) return false;
            var removed = false;
            foreach (var path in Directory.GetFiles(target.Path, "*.jsonl"))
                removed |= Filter(path, line => line.Contains(token, StringComparison.Ordinal));
            return removed;
        }
    }

    private static string Line(JournalEntry entry)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("id", entry.Id.ToString("D"));
            writer.WriteString("timestamp", entry.Timestamp.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("text", entry.Text);
            writer.WriteString("origin", entry.Origin);
            if (entry.EndReason is null) writer.WriteNull("endReason");
            else writer.WriteString("endReason", entry.EndReason);
            if (entry.RecordingSeconds is null) writer.WriteNull("recordingSeconds");
            else writer.WriteNumber("recordingSeconds", entry.RecordingSeconds.Value);
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private bool TryAppend(JournalTarget where, string line, DateTimeOffset timestamp, out string error)
    {
        try
        {
            if (IsSymlink(where.Path) || AncestorIsFile(where.Path))
            {
                error = "not writable";
                return false;
            }

            string file;
            var guarded = true;
            if (where.SingleFile)
            {
                var parent = Path.GetDirectoryName(where.Path);
                if (string.IsNullOrEmpty(parent)) parent = ".";
                var createdParent = !Directory.Exists(parent);
                Directory.CreateDirectory(parent);
                if (createdParent) guarded &= ProtectDirectory(parent);
                file = where.Path;
            }
            else
            {
                Directory.CreateDirectory(where.Path);
                guarded &= ProtectDirectory(where.Path);
                file = Path.Combine(where.Path, timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
            }

            if (IsSymlink(file))
            {
                error = "symlink";
                return false;
            }

            var payload = Encoding.UTF8.GetBytes(line + "\n");
            // OpenOrCreate, not Append: a second line has to read the last byte, and Append refuses Seek.
            using (var stream = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
            {
                if (stream.Length > 0)
                {
                    stream.Position = stream.Length - 1;
                    var last = stream.ReadByte();
                    if (last != '\n') stream.WriteByte((byte)'\n');
                }

                stream.Write(payload);
                stream.Flush(true);
            }

            guarded &= ProtectFile(file);
            if (!guarded && !reportedUnprotected)
            {
                reportedUnprotected = true;
                log($"dictation journal: {where.Path} does not take Windows permissions; keeping its own");
            }

            error = "";
            return true;
        }
        // InvalidOperationException too: a journal line that fails must never take the handoff down with it.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            error = ex.Message;
            return false;
        }
    }

    // Copies raw lines. A line that is not removed is not parsed and not reformatted.
    private static bool Filter(string path, Func<string, bool> remove)
    {
        if (!File.Exists(path) || IsSymlink(path)) return false;
        var raw = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(raw);
        var pieces = text.Split('\n');
        var keep = new List<string>(pieces.Length);
        var removed = false;
        for (var i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i];
            var last = i == pieces.Length - 1;
            if (piece.Length == 0 && last) continue;
            if (remove(piece))
            {
                removed = true;
                continue;
            }

            keep.Add(piece);
        }

        if (!removed) return false;
        if (keep.Count == 0)
        {
            File.Delete(path);
            return true;
        }

        var next = Encoding.UTF8.GetBytes(string.Join("\n", keep) + "\n");
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, next);
        ProtectFile(temp);
        File.Move(temp, path, overwrite: true);
        ProtectFile(path);
        return true;
    }

    private static string? DayOf(string line)
    {
        const string key = "\"timestamp\":\"";
        var at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0 || at + key.Length + 10 > line.Length) return null;
        return line.Substring(at + key.Length, 10);
    }

    private static string? Problem(string path)
    {
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return "invalid character";
        if (Unexpanded.IsMatch(path)) return "unexpanded variable";
        try { _ = Path.GetFullPath(path); }
        catch (Exception ex) { return ex.Message; }
        if (IsSymlink(path)) return "symlink";
        if (Directory.Exists(path) && path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return "path is a directory";
        if (File.Exists(path) && !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return "path is a file";
        if (AncestorIsFile(path)) return "not writable";
        return null;
    }

    private static bool AncestorIsFile(string path)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            if (File.Exists(parent) && !Directory.Exists(parent)) return true;
            var next = Path.GetDirectoryName(parent);
            if (next == parent) break;
            parent = next;
        }

        return false;
    }

    private static bool IsSymlink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            if (new FileInfo(path).LinkTarget is not null) return true;
            if (new DirectoryInfo(path).LinkTarget is not null) return true;
        }

        return false;
    }

    // The journal directory only. Parents created along the way keep the permissions they already had.
    // False when the filesystem refuses Windows ACLs; the caller still writes.
    private static bool ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return ProtectWindows(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return true;
    }

    private static bool ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows()) return ProtectWindows(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool ProtectWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("current user SID is unavailable");
        // A \\wsl.localhost share answers SetSecurityInfo with an error .NET has no mapping for, which surfaces as
        // InvalidOperationException; the Linux side guards the file with its own mode bits instead.
        try
        {
            if (Directory.Exists(path))
            {
                var security = new DirectorySecurity();
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
                return true;
            }

            var file = new FileSecurity();
            file.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            file.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(file);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private readonly record struct JournalTarget(string Path, bool SingleFile)
    {
        public static JournalTarget Directory(string path) => new(path, false);
        public static JournalTarget File(string path) => new(path, true);
    }
}

public readonly record struct JournalNote(string? EndReason, double? RecordingSeconds, bool Manual);

public static class JournalNotes
{
    private static readonly object Gate = new();
    private static readonly Dictionary<Guid, JournalNote> Pending = new();

    public static void Note(Guid id, string? endReason = null, double? recordingSeconds = null, bool? manual = null)
    {
        lock (Gate)
        {
            Pending.TryGetValue(id, out var existing);
            Pending[id] = new JournalNote(endReason ?? existing.EndReason, recordingSeconds ?? existing.RecordingSeconds, manual ?? existing.Manual);
        }
    }

    public static JournalNote Take(Guid id)
    {
        lock (Gate)
        {
            if (!Pending.Remove(id, out var note)) return default;
            return note;
        }
    }
}

/// <summary>Same reload rule as Windows <c>ConfigFile</c>: a file that fails to parse does not replace the journal.</summary>
public sealed class JournaledConfig
{
    private readonly string path;
    private readonly Action<string> log;
    private readonly JournalPlatform platform;
    private readonly string? defaultDirectory;
    private DateTime lastWriteUtc;

    public JournaledConfig(string path, Action<string> log, JournalPlatform platform = JournalPlatform.Windows, string? defaultDirectory = null)
    {
        this.path = path;
        this.log = log;
        this.platform = platform;
        this.defaultDirectory = defaultDirectory;
        lastWriteUtc = File.GetLastWriteTimeUtc(path);
    }

    public DictationJournal? Journal { get; private set; }
    public VoiceSwitchConfig? Config { get; private set; }

    public VoiceSwitchConfig LoadInitial()
    {
        var loaded = ConfigLoader.Load(path);
        Config = loaded;
        Journal = DictationJournal.Open(loaded.Dictation?.JournalDir, log, platform, defaultDirectory);
        lastWriteUtc = File.GetLastWriteTimeUtc(path);
        return loaded;
    }

    public VoiceSwitchConfig? ReloadIfChanged()
    {
        var write = File.GetLastWriteTimeUtc(path);
        if (write == lastWriteUtc) return null;
        lastWriteUtc = write;
        try
        {
            var next = ConfigLoader.Load(path);
            log($"config reloaded: {string.Join(", ", next.WakeWords)}");
            Config = next;
            Journal = DictationJournal.Open(next.Dictation?.JournalDir, log, platform, defaultDirectory);
            return next;
        }
        catch (Exception ex)
        {
            log($"config reload failed, keeping previous: {ex.Message}");
            return null;
        }
    }
}
