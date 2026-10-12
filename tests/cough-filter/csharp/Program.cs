using System.Collections.Immutable;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

if (args.Length != 1) throw new ArgumentException("Pass cough_policy.json.");
using var fixture = JsonDocument.Parse(File.ReadAllText(args[0]));
var assertions = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    assertions++;
}

foreach (var item in fixture.RootElement.GetProperty("cases").EnumerateArray())
{
    var text = item.GetProperty("text").GetString()!;
    var automaticExpected = item.GetProperty("automaticExpectedText").GetString();
    foreach (var rule in Enum.GetValues<WakeRule>())
    foreach (var enabled in new[] { false, true })
    {
        var history = new DictationHistory();
        var changes = 0;
        history.Changed += () => changes++;
        var id = Guid.NewGuid();
        var expected = enabled && rule != WakeRule.Strict ? automaticExpected : text;
        var retained = history.Add(id, text, new(rule, 4), policy: new(enabled));
        var name = $"{item.GetProperty("name").GetString()} {rule} {enabled}";
        Check(retained == (expected is not null), name + " admission");
        Check(history.Select(id) == expected, name + " exact selected text");
        Check(history.Snapshot().Select(entry => entry.Text).SequenceEqual(expected is null ? [] : new[] { expected }), name + " exact history");
        Check(changes == (expected is null ? 0 : 1), name + " notification");
    }
}

var path = Path.Combine(Path.GetTempPath(), "voice-policy-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    VoiceSwitchConfig Load(string noWake)
    {
        File.WriteAllText(path, "{\"wakeWords\":[\"wake\"],\"command\":\"unused\",\"dictation\":{\"noWakeWord\":" + noWake + "}}");
        return ConfigLoader.Load(path);
    }
    DictationAudio Record(VoiceSwitchConfig config)
    {
        var session = new DictationSession(config);
        Check(session.StartAutomatic(new(0, 2), new(WakeRule.AutoB, 4), 2), "automatic session starts");
        return session.Finish(FinishReason.FinishCommand, _ => ImmutableArray.Create<short>(1, 2))!;
    }
    void CheckRecording(DictationAudio audio, string? expected)
    {
        var history = new DictationHistory();
        history.Add(audio.SessionId, "あっ", audio.Wake, policy: audio.TranscriptPolicy);
        Check(history.Select(audio.SessionId) == expected, "captured policy admission");
        Check(history.Snapshot().Select(entry => entry.Text).SequenceEqual(expected is null ? [] : new[] { expected }), "captured policy exact history");
    }
    CheckRecording(Record(Load("{\"autoIdle\":true}")), "あっ");
    CheckRecording(Record(Load("{\"autoIdle\":true,\"filterInterjections\":false}")), "あっ");
    var before = Record(Load("{\"autoIdle\":true,\"filterInterjections\":true}"));
    var after = Record(Load("{\"autoIdle\":true,\"filterInterjections\":false}"));
    CheckRecording(before, null);
    CheckRecording(after, "あっ");
    CheckRecording(before with { Origin = new(WakeRule.Strict, 0) }, "あっ");
    Check(!Load("{\"filterInterjections\":true}").Dictation!.NoWakeWord!.Enabled, "filter alone does not start automatic recording");
    CheckRecording(new(Guid.NewGuid(), new(0, 2), [1, 2], Origin: new(WakeRule.AutoB, 4)), "あっ");
}
finally { File.Delete(path); }

var guarded = new DictationHistory();
var automatic = new WakeOrigin(WakeRule.AutoB, 4);
var generation = guarded.Generation;
guarded.Clear();
Check(!guarded.Add(Guid.NewGuid(), "はい", automatic, generation, new(true)), "clear invalidates retained words");
var duplicate = Guid.NewGuid();
Check(guarded.Add(duplicate, "はい", automatic, policy: new(true)), "retained word enters history");
Check(!guarded.Add(duplicate, "うん", automatic, policy: new(true)), "duplicate cannot replace text");
Check(guarded.Select(duplicate) == "はい", "duplicate keeps original text");
Check(!guarded.Add(Guid.NewGuid(), "  ", automatic, policy: new(true)), "blank transcript rejected");
Check(!guarded.Add(Guid.NewGuid(), new string('あ', 100001), automatic, policy: new(true)), "oversized transcript rejected");
Console.WriteLine($"PASS C# policy {assertions} assertions");
