using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoiceSwitch.Windows.Core;

var repo = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
var scratch = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "voice-switch-journal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var only = args.Length > 2 && args[2].Length > 0
    ? new HashSet<string>(args[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal)
    : null;

var failed = 0;
var ran = 0;

void Run(string id, Action body)
{
    if (only is not null && !only.Contains(id)) return;
    ran++;
    try
    {
        body();
        Console.WriteLine("PASS " + id);
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine("FAIL " + id + " " + ex.Message.Replace('\n', ' '));
    }
}

void Skip(string id)
{
    if (only is not null && !only.Contains(id)) return;
    Console.WriteLine("SKIP " + id);
}

void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

string CaseDir(string name)
{
    var path = Path.Combine(scratch, name);
    Directory.CreateDirectory(path);
    return path;
}

DictationJournal JournalAt(string dir, List<string>? logs = null) =>
    DictationJournal.Open(dir, logs is null ? static _ => { }
: logs.Add, JournalPlatform.Windows, dir + "-fallback");

DictationHistory History(DictationJournal journal, Func<DateTimeOffset>? now = null)
{
    var history = new DictationHistory(now);
    history.Journal = journal;
    return history;
}

Guid Add(DictationHistory history, string text, WakeOrigin? origin = null, string? endReason = null, double? seconds = null, bool manual = false, Guid? id = null, long? generation = null, AutomaticTranscriptPolicy policy = default, FocusApp? focus = null)
{
    var key = id ?? Guid.NewGuid();
    Expect(history.Add(key, text, origin ?? new WakeOrigin(WakeRule.Strict, null), generation, policy, endReason, seconds, manual, focus), "Add returned false for " + text);
    return key;
}

string DayFile(string dir, DateTimeOffset stamp) =>
    Path.Combine(dir, stamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");

string ReadText(string path) => File.ReadAllText(path);

JsonElement Line(string path)
{
    var text = ReadText(path).TrimEnd('\n');
    Expect(!text.Contains('\n'), "record is more than one physical line");
    return JsonDocument.Parse(text).RootElement.Clone();
}

void ExpectMode(string path, UnixFileMode expected)
{
    var actual = File.GetUnixFileMode(path) & (UnixFileMode)0x1FF;
    Expect(actual == expected, path + " mode " + Convert.ToString((int)actual, 8) + " wanted " + Convert.ToString((int)expected, 8));
}

const UnixFileMode Mode700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
const UnixFileMode Mode644 = Mode600 | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
const UnixFileMode Mode755 = Mode700 | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

var savedEnv = new Dictionary<string, string?>();
void SetEnv(string key, string? value)
{
    if (!savedEnv.ContainsKey(key)) savedEnv[key] = Environment.GetEnvironmentVariable(key);
    Environment.SetEnvironmentVariable(key, value);
}
void RestoreEnv()
{
    foreach (var pair in savedEnv) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
    savedEnv.Clear();
}

string Source(string relative) => File.ReadAllText(Path.Combine(repo, relative));

void WriteConfig(string path, string json)
{
    File.WriteAllText(path, json);
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
}

Run("history-hook", () =>
{
    var dir = CaseDir("history-hook");
    var journal = JournalAt(dir);
    var history = History(journal);
    var id = Guid.NewGuid();
    var text = "メモ";
    Expect(history.Add(id, text, new WakeOrigin(WakeRule.Strict, null)), "Add failed");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    using var second = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(second, Encoding.UTF8);
    var line = reader.ReadToEnd();
    Expect(line.Contains(id.ToString("D"), StringComparison.Ordinal), "id missing from the line");
    Expect(line.Contains(text, StringComparison.Ordinal), "text missing from the line");
    var cs = Source("dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs");
    var addAt = cs.IndexOf("public bool Add(", StringComparison.Ordinal);
    var next = cs.IndexOf("public IReadOnlyList", addAt, StringComparison.Ordinal);
    Expect(addAt >= 0 && next > addAt && cs[addAt..next].Contains("Append", StringComparison.Ordinal), "C# Add does not append");
    var swift = Source("Sources/voice-switch/WakeSafety.swift");
    var swiftAdd = swift.IndexOf("func add(", StringComparison.Ordinal);
    var swiftNext = swift.IndexOf("func snapshot(", swiftAdd, StringComparison.Ordinal);
    Expect(swiftAdd >= 0 && swiftNext > swiftAdd && swift[swiftAdd..swiftNext].Contains("DictationJournal", StringComparison.Ordinal), "Swift add does not call the journal");
    Expect(!Source("dotnet/VoiceSwitch.Windows.Core/DictationCore.cs").Contains("DictationJournal", StringComparison.Ordinal), "Finish path references the journal");
    Expect(ReferenceEquals(journal, history.Journal), "journal dropped");
});

Run("rejected-add-skips-journal", () =>
{
    var dir = CaseDir("rejected");
    var journal = JournalAt(dir);
    var history = History(journal);
    Expect(!history.Add(Guid.NewGuid(), "  ", new WakeOrigin(WakeRule.Strict, null)), "blank accepted");
    Expect(!history.Add(Guid.NewGuid(), new string('あ', 100_001), new WakeOrigin(WakeRule.Strict, null)), "long text accepted");
    Expect(!history.Add(Guid.NewGuid(), "あっ", new WakeOrigin(WakeRule.AutoB, 1), policy: new AutomaticTranscriptPolicy(Ignored: ["あっ"])), "policy accepted");
    Expect(Directory.GetFiles(dir, "*.jsonl").Length == 0, "rejected adds wrote a file");
    var id = Add(history, "kept");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    var before = File.ReadAllBytes(file);
    Expect(!history.Add(id, "duplicate", new WakeOrigin(WakeRule.Strict, null)), "duplicate accepted");
    var generation = history.Generation;
    history.Clear();
    Expect(!history.Add(Guid.NewGuid(), "stale", new WakeOrigin(WakeRule.Strict, null), expectedGeneration: generation), "stale generation accepted");
    Expect(File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "rejected adds changed journal bytes");
});

Run("memory-bounds", () =>
{
    var fresh = new DictationHistory();
    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
    var capacity = (int)typeof(DictationHistory).GetField("capacity", flags)!.GetValue(fresh)!;
    var retention = (TimeSpan)typeof(DictationHistory).GetField("retention", flags)!.GetValue(fresh)!;
    Expect(capacity == 50, "capacity " + capacity);
    Expect(retention == TimeSpan.FromMinutes(30), "retention " + retention);
    var dir = CaseDir("memory-bounds");
    var stamp = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(9));
    var now = stamp;
    var history = History(JournalAt(dir), () => now);
    var first = Add(history, "first");
    for (var i = 0; i < 49; i++) Add(history, "n" + i);
    Expect(history.Snapshot().Count == 50 && history.Snapshot().Any(entry => entry.Id == first), "50th still holds the first");
    Add(history, "overflow");
    Expect(history.Snapshot().Count == 50 && history.Snapshot().All(entry => entry.Id != first), "capacity eviction left the first in memory");
    Expect(File.ReadAllText(Directory.GetFiles(dir, "*.jsonl").Single()).Contains(first.ToString("D"), StringComparison.Ordinal), "capacity eviction dropped the journal line");
    now = stamp;
    var timed = History(JournalAt(CaseDir("memory-bounds-time")), () => now);
    var kept = Add(timed, "kept");
    now = stamp.AddMinutes(29);
    Expect(timed.Snapshot().Any(entry => entry.Id == kept), "29 min dropped the entry");
    now = stamp.AddMinutes(30);
    Expect(timed.Snapshot().All(entry => entry.Id != kept), "30 min kept the entry");
});

Run("memory-clear-keeps-journal", () =>
{
    var dir = CaseDir("memory-clear");
    var history = History(JournalAt(dir));
    var id = Add(history, "stay");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    var before = File.ReadAllBytes(file);
    history.Clear();
    Expect(history.Snapshot().Count == 0, "clear left memory");
    Expect(File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "clear changed journal bytes");
    Expect(ReadText(file).Contains(id.ToString("D"), StringComparison.Ordinal), "clear dropped the line");
});

Run("memory-evict-keeps-journal", () =>
{
    var dir = CaseDir("memory-evict");
    var stamp = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(9));
    var now = stamp;
    var history = History(JournalAt(dir), () => now);
    var id = Add(history, "evict me");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    var before = File.ReadAllBytes(file);
    now = stamp.AddMinutes(30);
    Expect(history.Snapshot().Count == 0, "retention did not evict");
    Expect(File.ReadAllBytes(file).AsSpan().SequenceEqual(before), "retention prune changed journal bytes");
    var capped = CaseDir("memory-evict-cap");
    var many = History(JournalAt(capped));
    var oldest = Add(many, "oldest");
    var oldestFile = Directory.GetFiles(capped, "*.jsonl").Single();
    var oldestBytes = File.ReadAllBytes(oldestFile);
    for (var i = 0; i < 50; i++) Add(many, "more" + i);
    Expect(many.Snapshot().All(entry => entry.Id != oldest), "capacity did not evict");
    var after = File.ReadAllBytes(oldestFile);
    Expect(after.AsSpan(0, oldestBytes.Length).SequenceEqual(oldestBytes), "capacity eviction rewrote the first line");
});

Run("daily-file", () =>
{
    var dir = CaseDir("daily");
    var now = DateTimeOffset.UnixEpoch;
    var history = History(JournalAt(dir), () => now);
    var evening = new DateTimeOffset(2026, 10, 23, 23, 30, 0, TimeSpan.FromHours(9));
    var morning = new DateTimeOffset(2026, 10, 24, 0, 30, 0, TimeSpan.FromHours(9));
    now = evening;
    Add(history, "evening");
    now = morning;
    Add(history, "morning");
    Expect(File.Exists(DayFile(dir, evening)) && File.Exists(DayFile(dir, morning)), "offset dates did not split");
    Expect(Directory.GetFiles(dir, "*.jsonl").Length == 2, "unexpected daily files");
    var same = CaseDir("daily-same");
    now = new DateTimeOffset(2026, 10, 23, 1, 0, 0, TimeSpan.FromHours(9));
    var again = History(JournalAt(same), () => now);
    Add(again, "one");
    now = new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.FromHours(9));
    Add(again, "two");
    Expect(Directory.GetFiles(same, "*.jsonl").Length == 1, "same offset date split the file");
    Expect(ReadText(Directory.GetFiles(same, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "same-day append missed a line");
    var zulu = CaseDir("daily-z");
    now = new DateTimeOffset(2026, 10, 23, 16, 0, 0, TimeSpan.Zero);
    Add(History(JournalAt(zulu), () => now), "utc");
    Expect(File.Exists(Path.Combine(zulu, "2026-10-23.jsonl")), "Z instant used a different calendar date");
});

Run("single-file", () =>
{
    var dir = CaseDir("single");
    var file = Path.Combine(dir, "All.JSONL");
    var logs = new List<string>();
    var journal = DictationJournal.Open(file, logs.Add, JournalPlatform.Windows, Path.Combine(dir, "fallback"));
    var history = History(journal);
    Add(history, "one");
    Add(history, "two");
    Expect(File.Exists(file), "single file was not created");
    Expect(ReadText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "single file did not collect both lines");
    Expect(Directory.GetFiles(dir).All(path => !Regex.IsMatch(Path.GetFileName(path), @"^\d{4}-\d{2}-\d{2}\.jsonl$")), "dated sibling was created");
    var folder = Path.Combine(dir, "as-dir");
    Add(History(DictationJournal.Open(folder, static _ => { }, JournalPlatform.Windows, Path.Combine(dir, "fallback-2"))), "dir");
    Expect(Directory.Exists(folder) && Directory.GetFiles(folder, "*.jsonl").Length == 1, "a non-jsonl path was not a directory");
});

// 2026-10-29: journalDir on \\wsl.localhost made every handoff fail, because the share refuses Windows ACLs.
var wslShare = Environment.GetEnvironmentVariable("VOICE_SWITCH_JOURNAL_WSL_DIR");
if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(wslShare))
    Run("wsl-share", () =>
    {
        var dir = Path.Combine(wslShare, "journal-" + Guid.NewGuid().ToString("N"));
        var logs = new List<string>();
        var history = History(DictationJournal.Open(dir, logs.Add, JournalPlatform.Windows, CaseDir("wsl-fallback")));
        Add(history, "one");
        Add(history, "two");
        var files = Directory.GetFiles(dir, "*.jsonl");
        Expect(files.Length == 1 && ReadText(files[0]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "the share did not get both lines");
        Expect(logs.Count(line => line.Contains("does not take Windows permissions", StringComparison.Ordinal)) == 1, "the missing ACL was not reported once: " + string.Join(" | ", logs));
        Expect(!logs.Any(line => line.Contains("using default", StringComparison.Ordinal)), "fell back to the default: " + string.Join(" | ", logs));
        Directory.Delete(dir, recursive: true);
    });
else
    Skip("wsl-share");

Run("fields", () =>
{
    var dir = CaseDir("fields");
    var stamp = new DateTimeOffset(2026, 10, 24, 14, 24, 0, TimeSpan.FromHours(9));
    var now = stamp;
    var history = History(JournalAt(dir), () => now);
    var text = "say \"hi\"\nnext";
    var id = Add(history, text, endReason: "Silence", seconds: 1.25);
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    var raw = File.ReadAllBytes(file);
    Expect(raw.Length == 0 || raw[0] != 0xEF, "BOM");
    Expect(raw.Count(b => b == (byte)'\n') == 1, "quote or newline broke the physical line");
    var doc = Line(file);
    var names = doc.EnumerateObject().Select(property => property.Name).ToArray();
    Expect(names.SequenceEqual(new[] { "id", "timestamp", "text", "origin", "endReason", "recordingSeconds", "schemaVersion", "focusApp", "taskchute" }), "keys " + string.Join(",", names));
    Expect(doc.GetProperty("id").GetString() == id.ToString("D"), "id");
    var timestamp = doc.GetProperty("timestamp").GetString() ?? "";
    Expect(Regex.IsMatch(timestamp, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$"), "timestamp " + timestamp);
    Expect(doc.GetProperty("text").GetString() == text, "text round trip");
    Expect(doc.GetProperty("endReason").GetString() == "Silence", "endReason");
    Expect(doc.GetProperty("recordingSeconds").GetDouble() == 1.25, "recordingSeconds");
    Expect(doc.GetProperty("schemaVersion").ValueKind == JsonValueKind.Number && doc.GetProperty("schemaVersion").GetInt32() == 1, "schemaVersion");
    Expect(doc.GetProperty("focusApp").ValueKind == JsonValueKind.Null, "focusApp not null without a focus");
    Expect(doc.GetProperty("taskchute").ValueKind == JsonValueKind.Null, "taskchute not null without a task file");
    var bare = CaseDir("fields-null");
    Add(History(JournalAt(bare)), "bare");
    var nulls = Line(Directory.GetFiles(bare, "*.jsonl").Single());
    Expect(nulls.GetProperty("endReason").ValueKind == JsonValueKind.Null && nulls.GetProperty("recordingSeconds").ValueKind == JsonValueKind.Null, "missing facts were not null");
});

Run("origin-automatic", () =>
{
    var dir = CaseDir("origin-auto");
    Add(History(JournalAt(dir)), "auto", new WakeOrigin(WakeRule.AutoB, 1));
    Expect(Line(Directory.GetFiles(dir, "*.jsonl").Single()).GetProperty("origin").GetString() == "自動", "origin");
});

Run("origin-wake", () =>
{
    var dir = CaseDir("origin-wake");
    Add(History(JournalAt(dir)), "wake", new WakeOrigin(WakeRule.Strict, null));
    Expect(Line(Directory.GetFiles(dir, "*.jsonl").Single()).GetProperty("origin").GetString() == "ウェイク", "origin");
});

Run("origin-manual", () =>
{
    var dir = CaseDir("origin-manual");
    var history = History(JournalAt(dir));
    Add(history, "button", new WakeOrigin(WakeRule.Strict, null), manual: true);
    var noted = Guid.NewGuid();
    JournalNotes.Note(noted, manual: true);
    Expect(history.Add(noted, "hotkey", new WakeOrigin(WakeRule.Strict, null)), "noted add failed");
    var origins = ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("origin").GetString()).ToArray();
    Expect(origins.All(origin => origin == "手動"), "origins " + string.Join(",", origins));
    var windows = Source("dotnet/VoiceSwitch.Windows/DictationRuntime.cs");
    var mac = Source("Sources/voice-switch/MacApp.swift");
    Expect(windows.Contains("manual-hotkey", StringComparison.Ordinal) && windows.Contains("JournalNotes.Note", StringComparison.Ordinal) && windows.Contains("manual: true", StringComparison.Ordinal), "Windows hotkey does not note manual");
    Expect(mac.Contains("manual-hotkey", StringComparison.Ordinal) && mac.Contains("DictationJournal.note", StringComparison.Ordinal) && mac.Contains("manual: true", StringComparison.Ordinal), "Mac hotkey does not note manual");
    var audio = Source("dotnet/VoiceSwitch.Windows.Core/DictationCore.cs");
    var audioAt = audio.IndexOf("record DictationAudio(", StringComparison.Ordinal);
    var audioEnd = audio.IndexOf("public WakeOrigin Wake", audioAt, StringComparison.Ordinal);
    Expect(audioAt >= 0 && audioEnd > audioAt && !audio[audioAt..audioEnd].Contains("Manual", StringComparison.Ordinal), "DictationAudio gained Manual");
});

Run("expand-tilde", () =>
{
    var home = CaseDir("tilde-home");
    try
    {
        SetEnv("USERPROFILE", home);
        var configured = "~/Documents/voice-switch/journal";
        var expected = WindowsPaths.ExpandPath(configured);
        var stamp = new DateTimeOffset(2026, 10, 24, 2, 0, 0, TimeSpan.FromHours(9));
        var journal = DictationJournal.Open(configured, static _ => { }, JournalPlatform.Windows, Path.Combine(scratch, "tilde-fallback"));
        journal.Append(Entry("tilde", stamp));
        var file = Path.Combine(expected, "2026-10-24.jsonl");
        Expect(File.Exists(file), "expanded file missing at " + expected);
        Expect(expected.StartsWith(home, StringComparison.Ordinal), "home rule differed from WindowsPaths");
    }
    finally { RestoreEnv(); }
});

Run("expand-localappdata", () =>
{
    var local = CaseDir("localapp");
    try
    {
        SetEnv("LOCALAPPDATA", local);
        var configured = @"%LOCALAPPDATA%\voice-switch\journal";
        var expected = WindowsPaths.ExpandPath(configured);
        var journal = DictationJournal.Open(configured, static _ => { }, JournalPlatform.Windows, Path.Combine(scratch, "local-fallback"));
        var stamp = new DateTimeOffset(2026, 10, 24, 1, 2, 3, TimeSpan.FromHours(9));
        journal.Append(Entry("local", stamp));
        Expect(File.Exists(Path.Combine(expected, "2026-10-24.jsonl")), "expanded file missing at " + expected);
        var swift = Source("Sources/voice-switch/Platform.swift");
        Expect(swift.Contains("LOCALAPPDATA", StringComparison.Ordinal) && swift.Contains("%\\(key)%", StringComparison.Ordinal), "Swift Windows expand does not replace %VAR%");
    }
    finally { RestoreEnv(); }
});

Run("default-mac", () =>
{
    var home = CaseDir("mac-home");
    var logs = new List<string>();
    try
    {
        SetEnv("USERPROFILE", home);
        var stamp = new DateTimeOffset(2026, 10, 24, 2, 30, 0, TimeSpan.FromHours(9));
        var journal = DictationJournal.Open(null, logs.Add, JournalPlatform.Mac);
        var expected = DictationJournal.DefaultDirectory(JournalPlatform.Mac);
        journal.Append(Entry("mac", stamp));
        var file = Path.Combine(expected, "2026-10-24.jsonl");
        Expect(File.Exists(file), "mac default missing " + expected);
        Expect(expected.Contains("Library/Application Support/voice-switch/journal", StringComparison.Ordinal), expected);
        Expect(logs.All(line => !line.Contains("using default", StringComparison.Ordinal)), "null path logged a fallback");
    }
    finally { RestoreEnv(); }
});

Run("default-windows", () =>
{
    var local = CaseDir("win-local");
    var logs = new List<string>();
    try
    {
        SetEnv("LOCALAPPDATA", local);
        var journal = DictationJournal.Open(null, logs.Add, JournalPlatform.Windows);
        var expected = DictationJournal.DefaultDirectory(JournalPlatform.Windows);
        var stamp = new DateTimeOffset(2026, 10, 24, 3, 4, 5, TimeSpan.FromHours(9));
        journal.Append(Entry("win", stamp));
        Expect(File.Exists(Path.Combine(expected, "2026-10-24.jsonl")), "windows default missing " + expected);
        Expect(logs.All(line => !line.Contains("using default", StringComparison.Ordinal)), "null path logged a fallback");
    }
    finally { RestoreEnv(); }
});

Run("invalid-fallback", () =>
{
    var root = CaseDir("invalid");
    var fallback = Path.Combine(root, "os-default");
    var previous = Path.Combine(root, "previous");
    Directory.CreateDirectory(previous);
    var previousJournal = DictationJournal.Open(previous, static _ => { }, JournalPlatform.Windows, fallback);
    previousJournal.Append(Entry("old"));
    var previousBytes = File.ReadAllBytes(Directory.GetFiles(previous, "*.jsonl").Single());
    var leaf = Path.Combine(root, "leaf-file");
    File.WriteAllText(leaf, "not a directory");
    string[] bad = ["", "   ", "%UNSET%/journal", "bad\0path", leaf];
    var index = 0;
    foreach (var configured in bad)
    {
        var configPath = Path.Combine(root, "cfg-" + index + ".json");
        WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(configured) + "}}");
        var loaded = ConfigLoader.Load(configPath);
        Expect(loaded.Dictation?.JournalDir == configured, "config load changed journalDir");
        var logs = new List<string>();
        var journal = DictationJournal.Open(loaded.Dictation?.JournalDir, logs.Add, JournalPlatform.Windows, fallback);
        var stamp = new DateTimeOffset(2026, 10, 24, 4, 0, index, TimeSpan.FromHours(9));
        journal.Append(Entry("bad" + index, stamp));
        Expect(File.Exists(Path.Combine(fallback, "2026-10-24.jsonl")), "fallback missing for " + JsonSerializer.Serialize(configured));
        Expect(logs.Any(line => line.Contains("dictation journal:", StringComparison.Ordinal) && line.Contains("using default", StringComparison.Ordinal)), "log missing for " + JsonSerializer.Serialize(configured));
        index++;
    }
    Expect(File.ReadAllBytes(Directory.GetFiles(previous, "*.jsonl").Single()).AsSpan().SequenceEqual(previousBytes), "previous custom directory was kept");
});

Run("unwritable-fallback", () =>
{
    var root = CaseDir("unwritable");
    var fallback = Path.Combine(root, "os-default");
    var previous = Path.Combine(root, "previous");
    Directory.CreateDirectory(previous);
    var previousJournal = DictationJournal.Open(previous, static _ => { }, JournalPlatform.Windows, fallback);
    previousJournal.Append(Entry("old"));
    var previousBytes = File.ReadAllBytes(Directory.GetFiles(previous, "*.jsonl").Single());
    var parent = Path.Combine(root, "parent-file");
    File.WriteAllText(parent, "file");
    var configured = Path.Combine(parent, "journal");
    var configPath = Path.Combine(root, "cfg.json");
    WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(configured) + "}}");
    var loaded = ConfigLoader.Load(configPath);
    var logs = new List<string>();
    var journal = DictationJournal.Open(loaded.Dictation?.JournalDir, logs.Add, JournalPlatform.Windows, fallback);
    journal.Append(Entry("blocked", new DateTimeOffset(2026, 10, 24, 5, 0, 0, TimeSpan.FromHours(9))));
    Expect(File.Exists(Path.Combine(fallback, "2026-10-24.jsonl")), "fallback missing");
    Expect(logs.Any(line => line.Contains("dictation journal:", StringComparison.Ordinal) && line.Contains("using default", StringComparison.Ordinal)), "log missing");
    Expect(File.ReadAllBytes(Directory.GetFiles(previous, "*.jsonl").Single()).AsSpan().SequenceEqual(previousBytes), "previous custom directory was kept");
});

Run("reload-keeps-config", () =>
{
    var root = CaseDir("reload");
    var custom = Path.Combine(root, "custom");
    var other = Path.Combine(root, "other");
    var configPath = Path.Combine(root, "config.json");
    WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(custom) + "}}");
    var logs = new List<string>();
    var file = new JournaledConfig(configPath, logs.Add, JournalPlatform.Windows, Path.Combine(root, "fallback"));
    file.LoadInitial();
    file.Journal!.Append(Entry("first", new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.FromHours(9))));
    File.WriteAllText(configPath, "{");
    File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(5));
    var rejected = file.ReloadIfChanged();
    Expect(rejected is null, "invalid reload returned a config");
    Expect(logs.Any(line => line.Contains("config reload failed, keeping previous", StringComparison.Ordinal)), "reload log missing");
    file.Journal.Append(Entry("second", new DateTimeOffset(2026, 10, 24, 6, 1, 0, TimeSpan.FromHours(9))));
    var customFile = Path.Combine(custom, "2026-10-24.jsonl");
    Expect(ReadText(customFile).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "append left the previous directory");
    WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(other) + "}}");
    File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(10));
    var accepted = file.ReloadIfChanged();
    Expect(accepted is not null, "valid reload was ignored");
    file.Journal!.Append(Entry("third", new DateTimeOffset(2026, 10, 24, 6, 2, 0, TimeSpan.FromHours(9))));
    Expect(File.Exists(Path.Combine(other, "2026-10-24.jsonl")), "new journalDir was not used");
    Expect(ReadText(customFile).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 2, "new journalDir kept writing to the old directory");
});

Run("no-symlink", () =>
{
    var root = CaseDir("symlink");
    var real = Path.Combine(root, "real");
    Directory.CreateDirectory(real);
    var link = Path.Combine(root, "link");
    Directory.CreateSymbolicLink(link, real);
    var fallback = Path.Combine(root, "fallback");
    var logs = new List<string>();
    var journal = DictationJournal.Open(link, logs.Add, JournalPlatform.Windows, fallback);
    journal.Append(Entry("via-link", new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.FromHours(9))));
    Expect(Directory.GetFiles(real).Length == 0 && Directory.GetDirectories(real).Length == 0, "writer followed the directory symlink");
    Expect(File.Exists(Path.Combine(fallback, "2026-10-24.jsonl")), "symlink did not fall back");
    Expect(logs.Any(line => line.Contains("dictation journal:", StringComparison.Ordinal) && line.Contains("using default", StringComparison.Ordinal)), "symlink log missing");
    var realFile = Path.Combine(root, "real.jsonl");
    File.WriteAllText(realFile, "");
    var fileLink = Path.Combine(root, "link.jsonl");
    File.CreateSymbolicLink(fileLink, realFile);
    var fileLogs = new List<string>();
    DictationJournal.Open(fileLink, fileLogs.Add, JournalPlatform.Windows, fallback)
        .Append(Entry("file-link", new DateTimeOffset(2026, 10, 24, 7, 1, 0, TimeSpan.FromHours(9))));
    Expect(File.ReadAllText(realFile).Length == 0, "writer followed the file symlink");
    Expect(fileLogs.Any(line => line.Contains("using default", StringComparison.Ordinal)), "file symlink log missing");
});

Run("perms-unix", () =>
{
    if (OperatingSystem.IsWindows()) return;
    var dir = CaseDir("perms");
    var journalDir = Path.Combine(dir, "journal");
    var stamp = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.FromHours(9));
    var file = DayFile(journalDir, stamp);
    Directory.CreateDirectory(journalDir);
    File.WriteAllText(file, "");
    File.SetUnixFileMode(file, Mode644);
    DictationJournal.Open(journalDir, static _ => { }, JournalPlatform.Windows, Path.Combine(dir, "fallback"))
        .Append(Entry("mode", stamp));
    ExpectMode(journalDir, Mode700);
    ExpectMode(file, Mode600);
});

Run("perms-leaf-only", () =>
{
    if (OperatingSystem.IsWindows()) return;
    var root = CaseDir("leaf");
    File.SetUnixFileMode(root, Mode755);
    var journalDir = Path.Combine(root, "journal");
    DictationJournal.Open(journalDir, static _ => { }, JournalPlatform.Windows, Path.Combine(root, "fallback"))
        .Append(Entry("leaf", new DateTimeOffset(2026, 10, 24, 8, 30, 0, TimeSpan.FromHours(9))));
    ExpectMode(journalDir, Mode700);
    ExpectMode(root, Mode755);
});

if (OperatingSystem.IsWindows())
{
    Run("windows-acl", () => throw new InvalidOperationException("Windows ACL check is not implemented on this host"));
}
else
{
    Skip("windows-acl");
}

Run("append-only", () =>
{
    var dir = CaseDir("append");
    var journal = JournalAt(dir);
    var first = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.FromHours(9));
    var second = first.AddMinutes(1);
    journal.Append(Entry("first", first, id: Guid.Parse("11111111-1111-1111-1111-111111111111")));
    var file = DayFile(dir, first);
    var original = File.ReadAllBytes(file);
    journal.Append(Entry("second", second, id: Guid.Parse("22222222-2222-2222-2222-222222222222")));
    var grown = File.ReadAllBytes(file);
    Expect(grown.AsSpan(0, original.Length).SequenceEqual(original), "second append rewrote the first line");
    var added = Encoding.UTF8.GetString(grown[original.Length..]);
    Expect(added.EndsWith("\n", StringComparison.Ordinal) && !added[..^1].Contains('\n'), "growth was not one line");
    Expect(grown.Length - original.Length == Encoding.UTF8.GetByteCount(added), "file grew by more than the new line");
    var crashed = original.Concat(Encoding.UTF8.GetBytes("PARTIAL")).ToArray();
    File.WriteAllBytes(file, crashed);
    journal.Append(Entry("third", first.AddMinutes(2), id: Guid.Parse("33333333-3333-3333-3333-333333333333")));
    var after = File.ReadAllBytes(file);
    Expect(after.AsSpan(0, crashed.Length).SequenceEqual(crashed), "crash fragment was rewritten");
    var parsed = JsonDocument.Parse(Encoding.UTF8.GetString(after).Split('\n')[0]);
    Expect(parsed.RootElement.GetProperty("text").GetString() == "first", "first record did not survive");
});

Run("flush-visible", () =>
{
    var dir = CaseDir("flush");
    var journal = JournalAt(dir);
    var history = History(journal);
    var id = Add(history, "visible");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    using var second = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(second, Encoding.UTF8);
    var seen = reader.ReadToEnd();
    Expect(seen.Contains(id.ToString("D"), StringComparison.Ordinal), "second handle missed the line");
    Expect(journal is not null && DictationJournal.SchemaVersion == 1, "writer was released");
});

Run("immutable-text", () =>
{
    var names = typeof(DictationJournal).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Select(method => method.Name);
    Expect(names.All(name => name is not ("Replace" or "Update" or "Edit" or "ReplaceText" or "UpdateText")), "journal type can replace text");
    var dir = CaseDir("immutable");
    var journal = JournalAt(dir);
    var keep = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var drop = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    var stamp = new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(9));
    journal.Append(Entry("keep-me", stamp, id: keep));
    journal.Append(Entry("drop-me", stamp.AddSeconds(1), id: drop));
    var file = DayFile(dir, stamp);
    var keepLine = ReadText(file).Split('\n').First(line => line.Contains(keep.ToString("D"), StringComparison.Ordinal));
    Expect(journal.DeleteEntry(drop), "delete failed");
    var after = ReadText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(after.Length == 1 && after[0] == keepLine, "survivor was reformatted");
});

Run("delete-day", () =>
{
    var dir = CaseDir("delete-day");
    var journal = JournalAt(dir);
    var day1 = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.FromHours(9));
    var day2 = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(9));
    journal.Append(Entry("monday", day1));
    journal.Append(Entry("tuesday", day2));
    var tuesday = File.ReadAllBytes(DayFile(dir, day2));
    Expect(journal.DeleteDay("2026-10-23") == 1, "day delete returned 0");
    Expect(!File.Exists(DayFile(dir, day1)), "day file remains");
    Expect(File.ReadAllBytes(DayFile(dir, day2)).AsSpan().SequenceEqual(tuesday), "other day changed");
    var before = File.ReadAllBytes(DayFile(dir, day2));
    Expect(journal.DeleteDay("1999-01-01") == 0, "unknown day did not return 0");
    Expect(File.ReadAllBytes(DayFile(dir, day2)).AsSpan().SequenceEqual(before), "unknown day rewrote a file");
    var singleDir = CaseDir("delete-day-single");
    var single = Path.Combine(singleDir, "one.jsonl");
    var singleJournal = DictationJournal.Open(single, static _ => { }, JournalPlatform.Windows, Path.Combine(singleDir, "fallback"));
    singleJournal.Append(Entry("monday", day1));
    singleJournal.Append(Entry("tuesday", day2));
    var tuesdayLine = ReadText(single).Split('\n').First(line => line.Contains("tuesday", StringComparison.Ordinal));
    Expect(singleJournal.DeleteDay("2026-10-23") == 1, "single-file day delete returned 0");
    var left = ReadText(single).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(left.Length == 1 && left[0] == tuesdayLine, "single-file survivor changed");
    var untouched = File.ReadAllBytes(single);
    Expect(singleJournal.DeleteDay("1999-01-01") == 0 && File.ReadAllBytes(single).AsSpan().SequenceEqual(untouched), "unknown day rewrote the single file");
});

Run("delete-entry", () =>
{
    var dir = CaseDir("delete-entry");
    var journal = JournalAt(dir);
    var stamp = new DateTimeOffset(2026, 10, 24, 11, 0, 0, TimeSpan.FromHours(9));
    var keep = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    var drop = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    journal.Append(Entry("keep", stamp, id: keep));
    journal.Append(Entry("drop", stamp.AddSeconds(1), id: drop));
    var file = DayFile(dir, stamp);
    var keepLine = ReadText(file).Split('\n').First(line => line.Contains(keep.ToString("D"), StringComparison.Ordinal));
    Expect(journal.DeleteEntry(drop), "delete entry failed");
    var left = ReadText(file).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(left.Length == 1 && left[0] == keepLine, "other line changed");
    var bytes = File.ReadAllBytes(file);
    Expect(!journal.DeleteEntry(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")) && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes), "missing id changed the file");
});

Run("text-only", () =>
{
    var dir = CaseDir("text-only");
    var journalDir = Path.Combine(dir, "journal");
    DictationJournal.Open(journalDir, static _ => { }, JournalPlatform.Windows, Path.Combine(dir, "fallback"))
        .Append(Entry("words", new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.FromHours(9))));
    var doc = Line(Directory.GetFiles(journalDir, "*.jsonl").Single());
    foreach (var property in doc.EnumerateObject())
    {
        var name = property.Name.ToLowerInvariant();
        Expect(name is not ("audio" or "path" or "sample" or "samples" or "wav"), "extra field " + property.Name);
    }
    var files = Directory.GetFiles(journalDir);
    Expect(files.All(path => path.EndsWith(".jsonl", StringComparison.Ordinal)) && files.All(path => !path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)), "non-jsonl file in the journal directory");
});

Run("journal-failure-isolated", () =>
{
    var dir = CaseDir("isolated");
    var configured = Path.Combine(dir, "configured");
    var fallback = Path.Combine(dir, "fallback");
    File.WriteAllText(configured, "file");
    File.WriteAllText(fallback, "file");
    var logs = new List<string>();
    var history = History(DictationJournal.Open(configured, logs.Add, JournalPlatform.Windows, fallback));
    Expect(history.Add(Guid.NewGuid(), "still here", new WakeOrigin(WakeRule.Strict, null)), "Add failed when the journal could not be written");
    Expect(history.Snapshot().Any(entry => entry.Text == "still here"), "snapshot missed the text");
});

Run("examples-readme", () =>
{
    foreach (var path in Directory.GetFiles(repo, "config.example*.json"))
    {
        var name = Path.GetFileName(path);
        var text = File.ReadAllText(path);
        if (name == "config.example.macos-url.json")
        {
            Expect(!text.Contains("\"dictation\"", StringComparison.Ordinal) && !text.Contains("journalDir", StringComparison.Ordinal), name + " gained dictation");
            continue;
        }
        if (text.Contains("\"dictation\"", StringComparison.Ordinal))
            Expect(text.Contains("journalDir", StringComparison.Ordinal), name + " dictation has no journalDir");
    }
    foreach (var name in new[] { "README.md", "README_ja.md" })
    {
        var text = Source(name);
        Expect(text.Contains("dictation.journalDir", StringComparison.Ordinal), name + " missing dictation.journalDir");
        Expect(text.Contains("~/Library/Application Support/voice-switch/journal", StringComparison.Ordinal), name + " missing mac default");
        Expect(text.Contains(@"%LOCALAPPDATA%\voice-switch\journal", StringComparison.Ordinal), name + " missing windows default");
        Expect(text.Contains(".jsonl", StringComparison.Ordinal), name + " missing .jsonl");
        Expect(text.Contains("using default", StringComparison.Ordinal), name + " missing fallback");
        Expect(text.Contains("0700", StringComparison.Ordinal) && text.Contains("0600", StringComparison.Ordinal), name + " missing modes");
        Expect(text.Contains("day", StringComparison.Ordinal) && text.Contains("entry", StringComparison.Ordinal), name + " missing deletion");
        Expect(text.Contains("no viewer", StringComparison.Ordinal), name + " missing no viewer");
    }
});

Run("no-ui", () =>
{
    foreach (var relative in new[] { "Sources/voice-switch/DictationJournal.swift", "dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs" })
    {
        var text = Source(relative);
        Expect(!text.Contains("Windows.Forms", StringComparison.Ordinal), relative);
        Expect(!text.Contains("NSWindow", StringComparison.Ordinal), relative);
        Expect(!text.Contains("HistoryWindow", StringComparison.Ordinal), relative);
    }
    var hits = Directory.EnumerateFiles(repo, "*Journal*Window*", SearchOption.AllDirectories)
        .Where(path => path.Split(Path.DirectorySeparatorChar).All(part => part is not (".git" or "bin" or "obj")))
        .ToArray();
    Expect(hits.Length == 0, "viewer file " + string.Join(",", hits));
});

Run("both-sources", () =>
{
    var swift = Source("Sources/voice-switch/DictationJournal.swift");
    var cs = Source("dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs");
    foreach (var token in new[] { "自動", "ウェイク", "手動", "schemaVersion" })
    {
        Expect(swift.Contains(token, StringComparison.Ordinal), "swift missing " + token);
        Expect(cs.Contains(token, StringComparison.Ordinal), "csharp missing " + token);
    }
    Expect(Source("Sources/voice-switch/WakeSafety.swift").Contains("DictationJournal", StringComparison.Ordinal), "Swift add misses the journal");
    Expect(Source("dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs").Contains("DictationJournal", StringComparison.Ordinal), "C# Add misses the journal");
});

Run("focus-app-shape", () =>
{
    var dir = CaseDir("focus-shape");
    var history = History(JournalAt(dir));
    Add(history, "mac-focus", focus: new FocusApp("Ghostty", "com.mitchellh.ghostty", null, 4242));
    Add(history, "win-focus", focus: new FocusApp("WindowsTerminal", null, "WindowsTerminal", 9001));
    var lines = ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(lines.Length == 2, "expected two lines");
    var mac = JsonDocument.Parse(lines[0]).RootElement.GetProperty("focusApp");
    Expect(mac.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "name", "bundleId", "exe", "pid" }), "focus keys");
    Expect(mac.GetProperty("name").GetString() == "Ghostty", "mac name");
    Expect(mac.GetProperty("bundleId").GetString() == "com.mitchellh.ghostty", "mac bundle");
    Expect(mac.GetProperty("exe").ValueKind == JsonValueKind.Null, "mac exe");
    Expect(mac.GetProperty("pid").GetInt32() == 4242, "mac pid");
    var win = JsonDocument.Parse(lines[1]).RootElement.GetProperty("focusApp");
    Expect(win.GetProperty("name").GetString() == "WindowsTerminal", "win name");
    Expect(win.GetProperty("bundleId").ValueKind == JsonValueKind.Null, "win bundle");
    var exe = win.GetProperty("exe").GetString() ?? "";
    Expect(exe == "WindowsTerminal", "win exe");
    Expect(!exe.Contains('/') && !exe.Contains('\\'), "exe has a separator");
    Expect(win.GetProperty("pid").GetInt32() == 9001, "win pid");
    foreach (var line in lines)
    {
        Expect(!line.Contains("title", StringComparison.OrdinalIgnoreCase), "window title leaked");
        Expect(!line.Contains("\"path\"", StringComparison.Ordinal), "path leaked");
        Expect(JsonDocument.Parse(line).RootElement.GetProperty("schemaVersion").GetInt32() == 1, "schemaVersion");
    }
    var swift = Source("Sources/voice-switch/DictationJournal.swift");
    foreach (var key in new[] { "name", "bundleId", "exe", "pid" })
        Expect(swift.Contains(key, StringComparison.Ordinal), "swift missing " + key);
});

Run("focus-app-null", () =>
{
    var dir = CaseDir("focus-null");
    var history = History(JournalAt(dir));
    Add(history, "no-focus");
    Add(history, "empty-focus", focus: new FocusApp(null, null, null, null));
    var lines = ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(lines.Length == 2, "expected two lines");
    foreach (var line in lines)
        Expect(JsonDocument.Parse(line).RootElement.GetProperty("focusApp").ValueKind == JsonValueKind.Null, "focusApp not null: " + line);
    var zeroDir = CaseDir("focus-zero-pid");
    Add(History(JournalAt(zeroDir)), "zero-pid", focus: new FocusApp("Ghostty", "com.mitchellh.ghostty", null, 0));
    var zero = Line(Directory.GetFiles(zeroDir, "*.jsonl").Single()).GetProperty("focusApp");
    Expect(zero.ValueKind == JsonValueKind.Object, "zero pid dropped the object");
    Expect(zero.GetProperty("name").GetString() == "Ghostty", "zero pid dropped the name");
    Expect(zero.GetProperty("pid").ValueKind == JsonValueKind.Null, "pid 0 not stored as null");
});

Run("focus-app-at-start", () =>
{
    var dir = CaseDir("focus-start");
    var history = History(JournalAt(dir));
    var id = Guid.NewGuid();
    JournalNotes.Note(id, focus: new FocusApp("Ghostty", "com.mitchellh.ghostty", null, 4242));
    JournalNotes.Note(id, endReason: "Silence");
    Expect(history.Add(id, "noted", new WakeOrigin(WakeRule.Strict, null)), "noted add failed");
    var kept = Line(Directory.GetFiles(dir, "*.jsonl").Single());
    Expect(kept.GetProperty("focusApp").GetProperty("name").GetString() == "Ghostty", "later note cleared the focus");
    Expect(kept.GetProperty("endReason").GetString() == "Silence", "end reason missing");
    var other = Guid.NewGuid();
    JournalNotes.Note(other, focus: new FocusApp("Ghostty", "com.mitchellh.ghostty", null, 4242));
    Expect(history.Add(other, "arg-wins", new WakeOrigin(WakeRule.Strict, null), focus: new FocusApp("WindowsTerminal", null, "WindowsTerminal", 9001)), "arg add failed");
    var lines = ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(JsonDocument.Parse(lines[1]).RootElement.GetProperty("focusApp").GetProperty("name").GetString() == "WindowsTerminal", "argument did not win");
    foreach (var relative in new[] { "dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs", "dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs" })
    {
        var text = Source(relative);
        Expect(!text.Contains("GetForegroundWindow", StringComparison.Ordinal), relative + " reads the live foreground app");
        Expect(!text.Contains("frontmostApplication", StringComparison.Ordinal), relative + " reads the live frontmost app");
    }
});

Run("focus-wired-at-start", () =>
{
    var mac = Source("Sources/voice-switch/MacApp.swift");
    var submitAt = mac.IndexOf("private func submit(", StringComparison.Ordinal);
    Expect(submitAt >= 0, "submit missing");
    var submitEnd = mac.IndexOf("private func ", submitAt + 1, StringComparison.Ordinal);
    var submit = submitEnd < 0 ? mac[submitAt..] : mac[submitAt..submitEnd];
    Expect(submit.Contains("DictationJournal.note", StringComparison.Ordinal), "submit does not note the journal");
    Expect(submit.Contains("d.target", StringComparison.Ordinal), "submit does not note d.target");
    var journal = Source("Sources/voice-switch/DictationJournal.swift");
    Expect(journal.Contains("localizedName", StringComparison.Ordinal), "swift focus misses localizedName");
    Expect(journal.Contains("bundleIdentifier", StringComparison.Ordinal), "swift focus misses bundleIdentifier");
    Expect(!journal.Contains("frontmostApplication", StringComparison.Ordinal), "swift journal reads the paste target");
    var runtime = Source("dotnet/VoiceSwitch.Windows/DictationRuntime.cs");
    var wakeStatement = "target = foregroundWindow();";
    var wakeAssign = runtime.IndexOf(wakeStatement, StringComparison.Ordinal);
    Expect(wakeAssign >= 0, "wake target assignment missing");
    var afterWake = runtime[(wakeAssign + wakeStatement.Length)..];
    var wakeNote = afterWake.IndexOf("JournalNotes.Note(session.SessionId, focus:", StringComparison.Ordinal);
    var wakeNextForeground = afterWake.IndexOf("foregroundWindow()", StringComparison.Ordinal);
    Expect(wakeNote >= 0 && wakeNote < 300 && (wakeNextForeground < 0 || wakeNote < wakeNextForeground), "wake does not note the process");
    var autoCount = 0;
    var cursor = 0;
    while (true)
    {
        var at = runtime.IndexOf("target = window;", cursor, StringComparison.Ordinal);
        if (at < 0) break;
        autoCount++;
        var after = runtime[(at + 1)..];
        var noteAt = after.IndexOf("JournalNotes.Note(", StringComparison.Ordinal);
        var nextForeground = after.IndexOf("foregroundWindow()", StringComparison.Ordinal);
        Expect(noteAt >= 0 && (nextForeground < 0 || noteAt < nextForeground), "target = window does not note before the next foregroundWindow()");
        var noted = after[noteAt..Math.Min(noteAt + 200, after.Length)];
        Expect(noted.Contains("focus:", StringComparison.Ordinal), "note misses the focus");
        cursor = at + 1;
    }
    Expect(autoCount == 2, "expected two target = window assignments, saw " + autoCount);
    var resetAt = runtime.IndexOf("private DictationSession ResetSession(", StringComparison.Ordinal);
    Expect(resetAt >= 0, "ResetSession missing");
    var resetEnd = runtime.IndexOf("private bool ApplyHotkey(", resetAt, StringComparison.Ordinal);
    var reset = runtime[resetAt..(resetEnd < 0 ? runtime.Length : resetEnd)];
    Expect(reset.Contains("target = 0;", StringComparison.Ordinal), "ResetSession does not clear the target");
    Expect(!reset.Contains("JournalNotes.Note", StringComparison.Ordinal), "ResetSession notes an app");
    Expect(runtime.Contains("ProcessOfWindow", StringComparison.Ordinal) && runtime.Contains("ProcessName", StringComparison.Ordinal), "noted process misses ProcessOfWindow");
    Expect(!runtime.Contains("MainModule", StringComparison.Ordinal), "noted process reads MainModule");
    Expect(runtime.ToLowerInvariant().Contains("window title") == false, "noted process reads a window title");
    Expect(runtime.Contains("audio = audio with { Target = foregroundWindow() }", StringComparison.Ordinal), "paste retarget missing");
});

Run("task-unset-null", () =>
{
    var dir = CaseDir("task-unset");
    var history = History(JournalAt(dir));
    Add(history, "plain");
    var doc = Line(Directory.GetFiles(dir, "*.jsonl").Single());
    Expect(doc.GetProperty("taskchute").ValueKind == JsonValueKind.Null, "taskchute not null without a task path");
    foreach (var relative in new[] { "dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs", "dotnet/VoiceSwitch.Windows.Core/DictationHistory.cs", "Sources/voice-switch/DictationJournal.swift", "Sources/voice-switch/WakeSafety.swift" })
        Expect(!Source(relative).Contains("tcc2", StringComparison.Ordinal), relative + " calls tcc2");
    var swift = Source("Sources/voice-switch/DictationJournal.swift");
    Expect(swift.Contains("taskchute", StringComparison.Ordinal), "swift journal misses taskchute");
    Expect(swift.Contains("journalTaskFile", StringComparison.Ordinal), "swift journal misses journalTaskFile");
});

Run("task-json-file", () =>
{
    var dir = CaseDir("task-json");
    var task = Path.Combine(dir, "task.txt");
    File.WriteAllText(task, "{\"id\":12,\"name\":\"メール\",\"startedAt\":\"2026-10-30T09:00:00+09:00\",\"extra\":true}");
    var history = History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, dir + "-fallback", task));
    Add(history, "json-task");
    var doc = Line(Directory.GetFiles(dir, "*.jsonl").Single());
    var parsed = doc.GetProperty("taskchute");
    Expect(parsed.ValueKind == JsonValueKind.Object, "taskchute not an object");
    Expect(parsed.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "name", "id", "startedAt", "source", "readAt" }), "task keys");
    Expect(parsed.GetProperty("name").GetString() == "メール", "task name");
    Expect(parsed.GetProperty("id").GetString() == "12", "numeric id not a decimal string");
    Expect(parsed.GetProperty("startedAt").GetString() == "2026-10-30T09:00:00+09:00", "startedAt not copied");
    Expect(parsed.GetProperty("source").GetString() == "file", "source");
    Expect(parsed.GetProperty("readAt").GetString() == doc.GetProperty("timestamp").GetString(), "readAt differs from timestamp");
    var second = CaseDir("task-json-string");
    var stringTask = Path.Combine(second, "task.txt");
    File.WriteAllText(stringTask, "{\"id\":\"abc\",\"name\":\"メール\"}");
    Add(History(DictationJournal.Open(second, static _ => { }, JournalPlatform.Windows, second + "-fallback", stringTask)), "string-id");
    Expect(Line(Directory.GetFiles(second, "*.jsonl").Single()).GetProperty("taskchute").GetProperty("id").GetString() == "abc", "string id not kept");
});

Run("task-plain-file", () =>
{
    var dir = CaseDir("task-plain");
    var task = Path.Combine(dir, "task.txt");
    File.WriteAllText(task, "\n   \nメール\nsecond line\n");
    Add(History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, dir + "-fallback", task)), "plain-task");
    var doc = Line(Directory.GetFiles(dir, "*.jsonl").Single());
    var parsed = doc.GetProperty("taskchute");
    Expect(parsed.ValueKind == JsonValueKind.Object, "taskchute not an object");
    Expect(parsed.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "name", "id", "startedAt", "source", "readAt" }), "task keys");
    Expect(parsed.GetProperty("name").GetString() == "メール", "first non-empty line not the name");
    Expect(parsed.GetProperty("id").ValueKind == JsonValueKind.Null, "plain id not null");
    Expect(parsed.GetProperty("startedAt").ValueKind == JsonValueKind.Null, "plain startedAt not null");
    Expect(parsed.GetProperty("source").GetString() == "file", "source");
    Expect(parsed.GetProperty("readAt").GetString() == doc.GetProperty("timestamp").GetString(), "readAt differs from timestamp");
});

Run("task-missing-file", () =>
{
    var dir = CaseDir("task-missing");
    var missing = Path.Combine(dir, "no-such-task.txt");
    var history = History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, dir + "-fallback", missing));
    var id = Guid.NewGuid();
    Expect(history.Add(id, "still here", new WakeOrigin(WakeRule.Strict, null)), "Add failed with a missing task file");
    Expect(history.Snapshot().Any(entry => entry.Id == id && entry.Text == "still here"), "snapshot missed the text");
    Expect(Line(Directory.GetFiles(dir, "*.jsonl").Single()).GetProperty("taskchute").ValueKind == JsonValueKind.Null, "missing file not null");
});

Run("task-oversize", () =>
{
    var dir = CaseDir("task-over");
    var okTask = Path.Combine(dir, "ok.txt");
    File.WriteAllText(okTask, new string('x', 4096));
    Add(History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, dir + "-fallback", okTask)), "ok-size");
    Expect(Line(Directory.GetFiles(dir, "*.jsonl").Single()).GetProperty("taskchute").GetProperty("name").GetString() == new string('x', 4096), "4096 bytes not accepted");
    var bigDir = CaseDir("task-big");
    var bigTask = Path.Combine(bigDir, "big.txt");
    File.WriteAllText(bigTask, new string('y', 4097));
    var history = History(DictationJournal.Open(bigDir, static _ => { }, JournalPlatform.Windows, bigDir + "-fallback", bigTask));
    var id = Guid.NewGuid();
    Expect(history.Add(id, "big-task", new WakeOrigin(WakeRule.Strict, null)), "Add failed with an oversized task file");
    Expect(Line(Directory.GetFiles(bigDir, "*.jsonl").Single()).GetProperty("taskchute").ValueKind == JsonValueKind.Null, "4097 bytes not ignored");
    Expect(Source("dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs").Contains("4096", StringComparison.Ordinal), "csharp cap is not 4096");
    Expect(Source("Sources/voice-switch/DictationJournal.swift").Contains("4096", StringComparison.Ordinal), "swift cap is not 4096");
});

Run("task-bad-json", () =>
{
    var dir = CaseDir("task-bad");
    var bodies = new[] { "{not json", "{", "{}", "{\"startedAt\":\"2026-10-30T09:00:00+09:00\"}" };
    var index = 0;
    foreach (var body in bodies)
    {
        var task = Path.Combine(dir, "bad-" + index + ".txt");
        File.WriteAllText(task, body);
        var history = History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, dir + "-fallback", task));
        Add(history, "bad-" + index);
        index++;
    }
    var lines = ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(lines.Length == bodies.Length, "expected one line per bad body");
    foreach (var line in lines)
        Expect(JsonDocument.Parse(line).RootElement.GetProperty("taskchute").ValueKind == JsonValueKind.Null, "bad JSON not null: " + line);
    Expect(!ReadText(Directory.GetFiles(dir, "*.jsonl").Single()).Contains("not json", StringComparison.Ordinal), "bad body used as a name");
    var typedDir = CaseDir("task-typed");
    var typedTask = Path.Combine(typedDir, "typed.txt");
    File.WriteAllText(typedTask, "{\"name\":\"メール\",\"startedAt\":12}");
    Add(History(DictationJournal.Open(typedDir, static _ => { }, JournalPlatform.Windows, typedDir + "-fallback", typedTask)), "typed");
    var typed = Line(Directory.GetFiles(typedDir, "*.jsonl").Single()).GetProperty("taskchute");
    Expect(typed.GetProperty("name").GetString() == "メール", "name missing");
    Expect(typed.GetProperty("startedAt").ValueKind == JsonValueKind.Null, "non-string startedAt not null");
});

Run("task-expand-path", () =>
{
    var home = CaseDir("task-home");
    var local = CaseDir("task-local");
    try
    {
        SetEnv("USERPROFILE", home);
        SetEnv("LOCALAPPDATA", local);
        File.WriteAllText(Path.Combine(home, "task.txt"), "ホーム");
        var envTask = WindowsPaths.ExpandPath(@"%LOCALAPPDATA%\task.txt");
        File.WriteAllText(envTask, "ローカル");
        var tildeDir = CaseDir("task-tilde");
        Add(History(DictationJournal.Open(tildeDir, static _ => { }, JournalPlatform.Windows, Path.Combine(scratch, "task-tilde-fb"), "~/task.txt")), "tilde");
        Expect(Line(Directory.GetFiles(tildeDir, "*.jsonl").Single()).GetProperty("taskchute").GetProperty("name").GetString() == "ホーム", "tilde name not read");
        var envDir = CaseDir("task-env");
        Add(History(DictationJournal.Open(envDir, static _ => { }, JournalPlatform.Windows, Path.Combine(scratch, "task-env-fb"), @"%LOCALAPPDATA%\task.txt")), "env");
        Expect(Line(Directory.GetFiles(envDir, "*.jsonl").Single()).GetProperty("taskchute").GetProperty("name").GetString() == "ローカル", "env name not read");
        var badDir = CaseDir("task-unexpanded");
        var badJournal = DictationJournal.Open(badDir, static _ => { }, JournalPlatform.Windows, Path.Combine(scratch, "task-unexpanded-fb"), @"%UNSET%\task.txt");
        Add(History(badJournal), "unexpanded");
        var badFile = Path.Combine(badDir, DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        Expect(File.Exists(badFile), "journal directory moved for a bad task path");
        Expect(Line(badFile).GetProperty("taskchute").ValueKind == JsonValueKind.Null, "unexpanded variable not null");
        Expect(Source("dotnet/VoiceSwitch.Windows.Core/VoiceSwitchConfig.cs").Contains("JournalTaskFile", StringComparison.Ordinal), "csharp config misses JournalTaskFile");
        Expect(Source("Sources/voice-switch/Config.swift").Contains("journalTaskFile", StringComparison.Ordinal), "swift config misses journalTaskFile");
    }
    finally { RestoreEnv(); }
});

Run("task-failure-isolated", () =>
{
    var dir = CaseDir("task-isolated");
    var fallback = Path.Combine(dir, "fallback");
    var taskDir = Path.Combine(dir, "taskdir");
    Directory.CreateDirectory(taskDir);
    var history = History(DictationJournal.Open(dir, static _ => { }, JournalPlatform.Windows, fallback, taskDir));
    var id = Guid.NewGuid();
    Expect(history.Add(id, "kept", new WakeOrigin(WakeRule.Strict, null)), "Add failed when the task path is a directory");
    Expect(history.Snapshot().Any(entry => entry.Id == id && entry.Text == "kept"), "snapshot missed the text");
    var file = Directory.GetFiles(dir, "*.jsonl").Single();
    Expect(File.Exists(file), "journal line missing");
    Expect(Line(file).GetProperty("taskchute").ValueKind == JsonValueKind.Null, "directory task not null");
});

Run("task-reload", () =>
{
    var root = CaseDir("task-reload");
    var custom = Path.Combine(root, "custom");
    var first = Path.Combine(root, "one.txt");
    var second = Path.Combine(root, "two.txt");
    File.WriteAllText(first, "一つ目");
    File.WriteAllText(second, "二つ目");
    var configPath = Path.Combine(root, "config.json");
    WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(custom) + ",\"journalTaskFile\":" + JsonSerializer.Serialize(first) + "}}");
    var logs = new List<string>();
    var file = new JournaledConfig(configPath, logs.Add, JournalPlatform.Windows, Path.Combine(root, "fallback"));
    file.LoadInitial();
    file.Journal!.Append(new JournalEntry(Guid.NewGuid(), new DateTimeOffset(2026, 10, 24, 6, 0, 0, TimeSpan.FromHours(9)), "first", "ウェイク", "Silence", 1));
    WriteConfig(configPath, "{\"wakeWords\":[\"音声入力\"],\"command\":\"true\",\"dictation\":{\"journalDir\":" + JsonSerializer.Serialize(custom) + ",\"journalTaskFile\":" + JsonSerializer.Serialize(second) + "}}");
    File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(10));
    Expect(file.ReloadIfChanged() is not null, "valid reload ignored");
    file.Journal!.Append(new JournalEntry(Guid.NewGuid(), new DateTimeOffset(2026, 10, 24, 6, 1, 0, TimeSpan.FromHours(9)), "second", "ウェイク", "Silence", 1));
    File.WriteAllText(configPath, "{");
    File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddMinutes(20));
    Expect(file.ReloadIfChanged() is null, "invalid reload returned a config");
    file.Journal!.Append(new JournalEntry(Guid.NewGuid(), new DateTimeOffset(2026, 10, 24, 6, 2, 0, TimeSpan.FromHours(9)), "third", "ウェイク", "Silence", 1));
    var lines = ReadText(Path.Combine(custom, "2026-10-24.jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    Expect(lines.Length == 3, "expected three lines in the same journal directory");
    Expect(JsonDocument.Parse(lines[0]).RootElement.GetProperty("taskchute").GetProperty("name").GetString() == "一つ目", "first task wrong");
    Expect(JsonDocument.Parse(lines[1]).RootElement.GetProperty("taskchute").GetProperty("name").GetString() == "二つ目", "reload did not follow the task path");
    Expect(JsonDocument.Parse(lines[2]).RootElement.GetProperty("taskchute").GetProperty("name").GetString() == "二つ目", "broken reload did not keep the task path");
    var cs = Source("dotnet/VoiceSwitch.Windows.Core/DictationJournal.cs");
    Expect(cs.Contains("JournalTaskFile", StringComparison.Ordinal), "csharp journal misses JournalTaskFile");
    Expect(Source("Sources/voice-switch/MacApp.swift").Contains("journalTaskFile", StringComparison.Ordinal), "swift bind misses journalTaskFile");
});

Run("examples-task-file", () =>
{
    foreach (var path in Directory.GetFiles(repo, "config.example*.json"))
    {
        var name = Path.GetFileName(path);
        var text = File.ReadAllText(path);
        if (name == "config.example.macos-url.json")
        {
            Expect(!text.Contains("\"dictation\"", StringComparison.Ordinal) && !text.Contains("journalTaskFile", StringComparison.Ordinal), name + " gained a dictation block");
            continue;
        }
        if (text.Contains("\"dictation\"", StringComparison.Ordinal))
        {
            Expect(text.Contains("\"journalTaskFile\": null", StringComparison.Ordinal), name + " dictation has no journalTaskFile null");
            Expect(!text.Contains("taskchute.txt", StringComparison.Ordinal), name + " uses a machine-specific cache path");
        }
    }
    Expect(File.ReadAllText(Path.Combine(repo, "config.example.grokbot.json")).Contains("journalDir", StringComparison.Ordinal), "grokbot missing journalDir");
    foreach (var name in new[] { "README.md", "README_ja.md" })
        Expect(Source(name).Contains("dictation.journalTaskFile", StringComparison.Ordinal), name + " missing dictation.journalTaskFile");
});

if (Which("swiftc") is null)
{
    Skip("swift-harness");
}
else
{
    Run("swift-harness", () => SwiftHarness(repo, scratch));
}

Console.WriteLine("journal-tests ran " + ran + " failed " + failed);
return failed == 0 ? 0 : 1;

static JournalEntry Entry(string text, DateTimeOffset? stamp = null, Guid? id = null) =>
    new(id ?? Guid.NewGuid(), stamp ?? DateTimeOffset.Now, text, "ウェイク", "Silence", 1);

static string? Which(string name)
{
    var path = Environment.GetEnvironmentVariable("PATH") ?? "";
    foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
    {
        var full = Path.Combine(dir, name);
        if (File.Exists(full)) return full;
    }
    return null;
}

static void SwiftHarness(string repo, string scratch)
{
    var work = Path.Combine(scratch, "swift-harness");
    Directory.CreateDirectory(work);
    var binary = Path.Combine(work, "journal-swift");
    var compile = new ProcessStartInfo("swiftc")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    compile.ArgumentList.Add("-module-cache-path");
    compile.ArgumentList.Add(Path.Combine(work, "module-cache"));
    foreach (var file in new[] { "Config.swift", "Platform.swift", "DictationJournal.swift", "WakeSafety.swift" })
        compile.ArgumentList.Add(Path.Combine(repo, "Sources/voice-switch", file));
    compile.ArgumentList.Add(Path.Combine(repo, "tests/journal/main.swift"));
    compile.ArgumentList.Add("-o");
    compile.ArgumentList.Add(binary);
    var compiled = Process.Start(compile) ?? throw new InvalidOperationException("swiftc did not start");
    var compileOut = compiled.StandardOutput.ReadToEnd() + compiled.StandardError.ReadToEnd();
    compiled.WaitForExit();
    if (compiled.ExitCode != 0) throw new InvalidOperationException("swiftc failed " + compileOut);
    var run = new ProcessStartInfo(binary)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    run.ArgumentList.Add(repo);
    run.ArgumentList.Add(Path.Combine(work, "scratch"));
    var ran = Process.Start(run) ?? throw new InvalidOperationException("swift harness did not start");
    var stdout = ran.StandardOutput.ReadToEnd();
    var stderr = ran.StandardError.ReadToEnd();
    ran.WaitForExit();
    Console.Write(stdout);
    if (stderr.Length > 0) Console.Error.Write(stderr);
    if (ran.ExitCode != 0) throw new InvalidOperationException("swift harness exit " + ran.ExitCode);
    foreach (var id in new[] { "expand-tilde", "expand-localappdata", "append-only", "invalid-fallback", "unwritable-fallback", "perms-unix", "delete-day", "delete-entry", "reload-keeps-config" })
    {
        if (!stdout.Contains("PASS " + id, StringComparison.Ordinal))
            throw new InvalidOperationException("swift harness missing PASS " + id);
    }
}
