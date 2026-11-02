using System.Collections.Immutable;
using System.Reflection;
using VoiceSwitch.Windows.Core;

// Bounded-exhaustive check of the wake relaxations against the real Core code.
// Each property states what the matcher may accept beyond main (2c28b7d); any other change fails.

var failures = 0;
const int Show = 5;

void Fail(string property, string detail)
{
    failures++;
    Console.WriteLine($"FAIL {property} {detail}");
}

// P0: the thresholds the relaxations are measured against stay where PLAN pins them.
// Read at run time: a const compared in source folds away and only warns.
object? Constant(string name) => typeof(DictationBoundaries).GetField(name)?.GetRawConstantValue();
var (shortWake, minD2, minConfidence) = (Constant("ShortWakeReading"), Constant("MinReadingDistance2"), Constant("RejectedWakeMinConfidence"));
if (!Equals(shortWake, 4) || !Equals(minD2, 10) || !Equals(minConfidence, 0.2))
{
    Fail("P0-thresholds", $"ShortWakeReading={shortWake} MinReadingDistance2={minD2} RejectedWakeMinConfidence={minConfidence}");
}
else
{
    Console.WriteLine("PASS P0-thresholds ShortWakeReading=4 MinReadingDistance2=10 RejectedWakeMinConfidence=0.2");
}

// P1: ReadingRuleAllows == main's inline guard OR s1 (particle tail) OR s2 (short copula), on every reachable input class.
var rule = typeof(DictationBoundaries).GetMethod(
    "ReadingRuleAllows",
    BindingFlags.NonPublic | BindingFlags.Static,
    [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(string)])
    ?? throw new MissingMethodException("DictationBoundaries.ReadingRuleAllows(int size, int length, int distance, bool closed, int lexemeCount, string all) not found; update this check");

string[] particles = ["は", "よ", "ね", "な", "わ"];
string[] copulas = ["です", "ます"];
string[] tails = ["", .. particles, .. copulas, "の", "ば", "が", "で", "でし", "でした", "はは", "よね", "くは", "ですよ", "あ"];

static bool MainAllows(int size, int length, int distance, bool closed, string all)
{
    // Verbatim from ReadingWake at 2c28b7d, minus the allowFuzzy term that still sits at the call site.
    var maxDistance = (size >= 10 && closed) ? 2 : 1;
    return !(distance > maxDistance || (size <= 4 && !(closed && length == all.Length)) || (distance == 2 && length != all.Length));
}

var cases = 0;
var p1Shown = 0;
var gained = new Dictionary<string, int>();
for (var size = 1; size <= 13; size++)
{
    foreach (var length in new[] { size - 1, size, size + 1 })
    {
        if (length <= 0)
        {
            continue;
        }

        for (var distance = 0; distance <= 4; distance++)
        {
            foreach (var closed in new[] { false, true })
            {
                for (var lexemeCount = 1; lexemeCount <= 3; lexemeCount++)
                {
                    foreach (var tail in tails)
                    {
                        cases++;
                        var all = new string('あ', length) + tail;
                        var actual = (bool)rule.Invoke(null, [size, length, distance, closed, lexemeCount, all])!;
                        var main = MainAllows(size, length, distance, closed, all);
                        var s1 = size >= 10 && closed && distance == 2 && particles.Contains(tail);
                        var s2 = size <= 4 && closed && distance == 0 && length == size && lexemeCount == 1 && copulas.Contains(tail);
                        var expected = main || s1 || s2;
                        if (actual != expected)
                        {
                            if (p1Shown++ < Show)
                            {
                                Fail("P1-reading-rule", $"size={size} length={length} distance={distance} closed={closed} lexemes={lexemeCount} tail=\"{tail}\" actual={actual} expected={expected} (main={main} s1={s1} s2={s2})");
                            }
                            else
                            {
                                failures++;
                            }
                        }
                        else if (actual && !main)
                        {
                            var key = s1 ? "s1" : "s2";
                            gained[key] = gained.GetValueOrDefault(key) + 1;
                        }
                    }
                }
            }
        }
    }
}

if (p1Shown == 0)
{
    Console.WriteLine($"PASS P1-reading-rule cases={cases} gained_s1={gained.GetValueOrDefault("s1")} gained_s2={gained.GetValueOrDefault("s2")}");
}

// P2: RejectedWake accepts only a rejected text equal to a wake's surface or, at distance 0, its reading, never cuts a body,
// and keeps the closed / empty-text / confidence gates. Every rejected string up to 5 kana over the alphabet below.
WakeWord[] rejectedWakes = [WakeWord.From("音声", "おんせい"), WakeWord.From("明日", "せい")];
char[] alphabet = ['お', 'ん', 'せ', 'い', 'セ', 'ほ'];
var strings = new List<string> { "" };
var frontier = new List<string> { "" };
for (var n = 1; n <= 5; n++)
{
    frontier = frontier.SelectMany(prefix => alphabet.Select(c => prefix + c)).ToList();
    strings.AddRange(frontier);
}

strings.AddRange(["音声", "明日", "音声入力", "おんせい ", "オンセイ"]);
var readingSet = new HashSet<string> { "おんせい", "せい" };
var surfaceSet = new HashSet<string> { "音声", "明日" };
var p2Cases = 0;
var p2Shown = 0;
var p2Wakes = 0;
foreach (var extent in new[] { RecognitionExtent.ClosedUtterance, RecognitionExtent.PrefixHead, RecognitionExtent.Probe })
{
    foreach (var text in new[] { "", "あ" })
    {
        foreach (var confidence in new double?[] { null, 0.05, 0.2, 0.35 })
        {
            foreach (var rejected in strings)
            {
                p2Cases++;
                var utterance = new RecognizedUtterance(
                    1,
                    extent,
                    new SampleRange(0, 8000),
                    text,
                    ImmutableArray<LexicalRun>.Empty,
                    HadRejectedSpeech: true,
                    Confidence: confidence,
                    RejectedText: rejected,
                    Alternates: ImmutableArray<RecognitionAlternate>.Empty,
                    TailSilenceSamples: 0);
                var got = DictationBoundaries.RejectedWake(utterance, rejectedWakes);
                var gate = extent == RecognitionExtent.ClosedUtterance && text.Length == 0 && confidence >= 0.2;
                var expected = gate && (surfaceSet.Contains(TextMatching.Normalize(rejected)) || readingSet.Contains(TextMatching.NormalizeReading(rejected)));
                string? problem = null;
                if ((got is not null) != expected)
                {
                    problem = $"got={(got is not null ? "wake" : "none")} expected={(expected ? "wake" : "none")}";
                }
                else if (got is { BodyStart: not null })
                {
                    problem = $"cut a body at {got.BodyStart}";
                }

                if (problem is not null)
                {
                    if (p2Shown++ < Show)
                    {
                        Fail("P2-rejected-reading", $"extent={extent} text=\"{text}\" conf={confidence?.ToString() ?? "null"} rejected=\"{rejected}\" {problem}");
                    }
                    else
                    {
                        failures++;
                    }
                }
                else if (got is not null)
                {
                    p2Wakes++;
                }
            }
        }
    }
}

if (p2Shown == 0)
{
    Console.WriteLine($"PASS P2-rejected-reading cases={p2Cases} wakes={p2Wakes}");
}

// P3: alternates only add wakes, and only as s3 says: closed, an alternate reading equal to a wake reading of length >= 10,
// the primary reading within edit distance 3 of that wake, wake-only (BodyStart null).
WakeWord[] wakes = [WakeWord.From("音声入力", "おんせいにゅうりょく"), WakeWord.From("音声", "おんせい"), WakeWord.From("音声に入る", "おんせいにはいる")];
const string Long = "おんせいにゅうりょく";
var primaries = new HashSet<string>();
void Substitute(char[] chars, int from, int left)
{
    primaries.Add(new string(chars));
    if (left == 0)
    {
        return;
    }

    for (var i = from; i < chars.Length; i++)
    {
        var keep = chars[i];
        foreach (var c in new[] { 'ほ', 'あ' })
        {
            chars[i] = c;
            Substitute(chars, i + 1, left - 1);
        }

        chars[i] = keep;
    }
}

Substitute(Long.ToCharArray(), 0, 4);
foreach (var p in primaries.ToArray())
{
    primaries.Add(p[..^1]);
    primaries.Add(p + "は");
}

primaries.UnionWith(["おんせい", "おんせん", "ざつだん", "あ"]);
RecognitionAlternate[][] alternateSets =
[
    [new("音声入力", Long)],
    [new("音声", "おんせい")],
    [new("音声に入る", "おんせいにはいる")],
    [new("法政入力", "ほうせいにゅうりょく")],
    [new("オンセイニュウリョク", "オンセイニュウリョク")],
];
var p3Cases = 0;
var p3Shown = 0;
var p3Gained = 0;
foreach (var extent in new[] { RecognitionExtent.ClosedUtterance, RecognitionExtent.PrefixHead, RecognitionExtent.Probe })
{
    foreach (var primary in primaries)
    {
        var lexemes = ImmutableArray.Create(new LexicalRun("x", new SampleRange(0, 8000), primary));
        RecognizedUtterance Utterance(ImmutableArray<RecognitionAlternate> alternates) => new(
            1, extent, new SampleRange(0, 8000), "x", lexemes,
            HadRejectedSpeech: false, Confidence: 0.9, RejectedText: null, Alternates: alternates, TailSilenceSamples: 0);
        var without = DictationBoundaries.LeadingWake(Utterance(ImmutableArray<RecognitionAlternate>.Empty), wakes);
        foreach (var set in alternateSets)
        {
            p3Cases++;
            var with = DictationBoundaries.LeadingWake(Utterance([.. set]), wakes);
            string? problem = null;
            if (without is not null && with != without)
            {
                problem = $"alternates changed an existing result {without} -> {with?.ToString() ?? "null"}";
            }
            else if (without is null && with is not null)
            {
                var allowed = extent == RecognitionExtent.ClosedUtterance
                    && set.Any(alternate => wakes.Any(wake => wake.Reading.Length >= 10 && TextMatching.NormalizeReading(alternate.Reading) == wake.Reading
                        && TextMatching.EditDistance(TextMatching.NormalizeReading(primary), wake.Reading) <= 3))
                    && with.BodyStart is null;
                if (!allowed)
                {
                    problem = $"alternate-only wake outside s3: {with}";
                }
                else
                {
                    p3Gained++;
                }
            }
            else if (without is null && with is null && extent == RecognitionExtent.ClosedUtterance
                && set.Any(alternate => TextMatching.NormalizeReading(alternate.Reading) == Long)
                && TextMatching.EditDistance(TextMatching.NormalizeReading(primary), Long) <= 3)
            {
                problem = "s3 shape did not wake";
            }

            if (problem is not null)
            {
                if (p3Shown++ < Show)
                {
                    Fail("P3-alternate", $"extent={extent} primary=\"{primary}\" alternate=\"{set[0].Reading}\" {problem}");
                }
                else
                {
                    failures++;
                }
            }
        }
    }
}

if (p3Shown == 0)
{
    Console.WriteLine($"PASS P3-alternate cases={p3Cases} gained={p3Gained}");
}

// P4: the built-in reading table fills only an empty reading, only for its seven words.
var table = new Dictionary<string, string>
{
    ["音声入力"] = "おんせいにゅうりょく",
    ["音声"] = "おんせい",
    ["音声入る"] = "おんせいはいる",
    ["音声に入る"] = "おんせいにはいる",
    ["音声に入るよ"] = "おんせいにはいるよ",
    ["音声によって"] = "おんせいによって",
    ["インプット"] = "いんぷっと",
};
var p4Shown = 0;
foreach (var word in table.Keys.Concat(["明日", "音声認識", "音声メモ", "温泉", "入力", "おんせい", "音 声", "ｲﾝﾌﾟｯﾄ"]))
{
    var expected = table.TryGetValue(TextMatching.Normalize(word), out var r) ? r : TextMatching.NormalizeReading(word);
    foreach (var reading in new string?[] { null, "" })
    {
        var got = WakeWord.From(word, reading).Reading;
        if (got != expected && p4Shown++ < Show)
        {
            Fail("P4-reading-fallback", $"word=\"{word}\" reading={(reading is null ? "null" : "\"\"")} got=\"{got}\" expected=\"{expected}\"");
        }
    }

    var explicitReading = WakeWord.From(word, "ぜんぜんちがう").Reading;
    if (explicitReading != "ぜんぜんちがう" && p4Shown++ < Show)
    {
        Fail("P4-reading-fallback", $"word=\"{word}\" explicit reading lost: got=\"{explicitReading}\"");
    }
}

if (p4Shown == 0)
{
    Console.WriteLine("PASS P4-reading-fallback words=15");
}

Console.WriteLine(failures == 0 ? "wake-allowances: ok" : $"wake-allowances: {failures} failure(s)");
return failures == 0 ? 0 : 1;
