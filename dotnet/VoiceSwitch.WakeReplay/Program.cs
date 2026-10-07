using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VoiceSwitch.Windows.Core;

// Track A wake log replay: measurement only. Does not change matcher behavior.
// Parses dictation recognition: complete lines (+ wakeReadings from timing) and
// runs DictationBoundaries.LeadingWake — the same API the live log's leadingWake= flag uses.

static int Usage()
{
    Console.Error.WriteLine(
        """
        Usage:
          dotnet run --project dotnet/VoiceSwitch.WakeReplay -- --log <path> [--label NAME] [--gained-cap 40] [--examples-cap 40] [--json-out path]
          dotnet run --project dotnet/VoiceSwitch.WakeReplay -- --self-test
        """);
    return 2;
}

if (args.Length == 0)
{
    return Usage();
}

var selfTest = false;
string? logPath = null;
var label = "matcher";
var gainedCap = 40;
var examplesCap = 40;
string? jsonOut = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--self-test":
            selfTest = true;
            break;
        case "--log" when i + 1 < args.Length:
            logPath = args[++i];
            break;
        case "--label" when i + 1 < args.Length:
            label = args[++i];
            break;
        case "--gained-cap" when i + 1 < args.Length:
            gainedCap = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--examples-cap" when i + 1 < args.Length:
            examplesCap = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--json-out" when i + 1 < args.Length:
            jsonOut = args[++i];
            break;
        case "--help" or "-h":
            return Usage();
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return Usage();
    }
}

if (selfTest)
{
    return RunSelfTest();
}

if (logPath is null)
{
    return Usage();
}

var report = Replay.Run(logPath, label, gainedCap, examplesCap);
report.WriteHuman(Console.Out);
if (jsonOut is not null)
{
    File.WriteAllText(jsonOut, report.ToJson(), Encoding.UTF8);
}

return 0;

static int RunSelfTest()
{
    var root = FindRepoRoot();
    var fixture = Path.Combine(root, "tests", "fixtures", "wake_log_replay_sample.log");
    if (!File.Exists(fixture))
    {
        Console.Error.WriteLine($"FAIL: missing fixture {fixture}");
        return 1;
    }

    var report = Replay.Run(fixture, "self-test", gainedCap: 40, examplesCap: 40);
    // Known counts on the fixture (main matcher, d≤1):
    // - 縫製入力 (ほうせいにゅうりょく) → no hit (d=2 needs H2d); H5b near-miss example
    // - 縫製入力の本文… → size±1 prefix d=2 but whole≫2 → not an H5b example
    // - 法政入力 (ほうぜいにゅうりょく) → no hit; whole d=3 vs おんせいにゅうりょく; H7 example
    // - 温泉 alone → hit short おんせい (d=1 whole closed); excluded from long+short+d3 examples (!hit)
    // - ほうせいにはいるよ prefix body → no d=2 prefix hit on main
    // - 音声入力 exact → hit; excluded from near-miss examples
    // - PrefixHead no-text leadingWake=False → unscored (H8 PrefixHead class)
    // - PrefixHead no-text leadingWake=True → unscored_live_true (H4b ±2% path)
    // - ClosedUtterance no-text → unscored (H8 no-text= class)
    // - ClosedUtterance reading-only leadingWake=True → unscored_live_true; H8 score-from-reading recovers
    // - 妊婦と / インクと ambient scored false (main: no gain)
    var failures = new List<string>();
    void Expect(string name, bool cond, string detail = "")
    {
        if (!cond)
        {
            failures.Add(detail.Length == 0 ? name : $"{name}: {detail}");
        }
    }

    Expect("scored", report.Scored == 9, $"got {report.Scored}");
    Expect("live_true", report.LiveTrue == 4, $"got {report.LiveTrue}");
    Expect("unscored_live_true", report.UnscoredLiveTrue == 2, $"got {report.UnscoredLiveTrue}");
    Expect("wake_hits", report.WakeHits == 2, $"got {report.WakeHits}");
    Expect("agreed_true", report.AgreedTrue == 2, $"got {report.AgreedTrue}");
    Expect("gained", report.Gained == 0, $"got {report.Gained} (main must not gain 縫製)");
    Expect("lost", report.Lost == 0, $"got {report.Lost}");
    Expect("unscored", report.Unscored == 4, $"got {report.Unscored}");
    Expect("near_miss_d2_has_hosei", report.NearMissByDistance.GetValueOrDefault(2) >= 1,
        $"d2={report.NearMissByDistance.GetValueOrDefault(2)}");
    // 縫製入力 reading vs おんせいにゅうりょく is d=2 near-miss
    Expect("near_miss_lists_housei", report.NearMissReadings.Any(r => r.Contains("ほうせい", StringComparison.Ordinal)),
        string.Join(',', report.NearMissReadings));

    // H5b: long-wake near-miss examples = !hit ∧ whole-utterance d=1..2 ∧ Reading.Length>=10.
    // Main matcher: 縫製入力 is a miss (d=2 needs H2d) → sole example. Hits (音声入力 / 温泉) excluded.
    // Prefix-of-longer (縫製入力の本文…) size±1 d=2 must NOT become an example (H2d d=2 is whole-only).
    Expect("near_miss_examples_count", report.NearMissExamples.Count == 1,
        $"got {report.NearMissExamples.Count}: {string.Join(" | ", report.NearMissExamples)}");
    Expect("near_miss_example_housei",
        report.NearMissExamples.Any(r => r.Contains("縫製入力", StringComparison.Ordinal)
            && !r.Contains("縫製入力の本文", StringComparison.Ordinal)
            && r.Contains("d=2", StringComparison.Ordinal)
            && r.Contains("おんせいにゅうりょく", StringComparison.Ordinal)
            && r.Contains("intentional=True", StringComparison.Ordinal)),
        string.Join(" | ", report.NearMissExamples));
    Expect("near_miss_examples_exclude_hits",
        !report.NearMissExamples.Any(r => r.Contains("音声入力", StringComparison.Ordinal)
            || r.Contains("text=\"温泉\"", StringComparison.Ordinal)),
        string.Join(" | ", report.NearMissExamples));
    Expect("near_miss_examples_no_prefix_of_longer",
        !report.NearMissExamples.Any(r => r.Contains("縫製入力の本文", StringComparison.Ordinal)
            || r.Contains("のほんぶん", StringComparison.Ordinal)),
        string.Join(" | ", report.NearMissExamples));
    Expect("near_miss_examples_no_short_onsei",
        !report.NearMissExamples.Any(r => r.Contains("wake=おんせい ", StringComparison.Ordinal)
            || r.Contains("wake=おんせい/", StringComparison.Ordinal)
            || r.EndsWith("wake=おんせい", StringComparison.Ordinal)),
        string.Join(" | ", report.NearMissExamples));

    // H6: short-wake near-miss examples = !hit ∧ whole-utterance d=1 ∧ Reading.Length<10.
    // Fixture: 温泉 is a matcher hit on おんせい → excluded. No other short whole d=1 miss on fixture → count=0.
    Expect("short_near_miss_examples_count", report.ShortNearMissExamples.Count == 0,
        $"got {report.ShortNearMissExamples.Count}: {string.Join(" | ", report.ShortNearMissExamples)}");
    Expect("short_near_miss_examples_exclude_onsen_hit",
        !report.ShortNearMissExamples.Any(r => r.Contains("text=\"温泉\"", StringComparison.Ordinal)),
        string.Join(" | ", report.ShortNearMissExamples));
    Expect("short_near_miss_examples_no_long_wake",
        !report.ShortNearMissExamples.Any(r => r.Contains("おんせいにゅうりょく", StringComparison.Ordinal)),
        string.Join(" | ", report.ShortNearMissExamples));
    Expect("short_near_miss_intentional_zero", report.ShortNearMissIntentional == 0,
        $"got {report.ShortNearMissIntentional}");
    Expect("short_near_miss_ambient_zero", report.ShortNearMissAmbient == 0,
        $"got {report.ShortNearMissAmbient}");

    // H7: long-wake near-miss examples = !hit ∧ whole-utterance d=3 ∧ Reading.Length>=10.
    // Fixture: 法政入力 (ほうぜいにゅうりょく) is the sole d=3 long whole miss. d=2 縫製入力 stays in H5b.
    Expect("d3_near_miss_examples_count", report.D3NearMissExamples.Count == 1,
        $"got {report.D3NearMissExamples.Count}: {string.Join(" | ", report.D3NearMissExamples)}");
    Expect("d3_near_miss_example_houzei",
        report.D3NearMissExamples.Any(r => r.Contains("法政入力", StringComparison.Ordinal)
            && r.Contains("d=3", StringComparison.Ordinal)
            && r.Contains("おんせいにゅうりょく", StringComparison.Ordinal)
            && r.Contains("intentional=True", StringComparison.Ordinal)),
        string.Join(" | ", report.D3NearMissExamples));
    Expect("d3_near_miss_examples_exclude_d2_housei",
        !report.D3NearMissExamples.Any(r => r.Contains("縫製入力", StringComparison.Ordinal)),
        string.Join(" | ", report.D3NearMissExamples));
    Expect("d3_near_miss_examples_exclude_hits",
        !report.D3NearMissExamples.Any(r => r.Contains("音声入力", StringComparison.Ordinal)
            || r.Contains("text=\"温泉\"", StringComparison.Ordinal)),
        string.Join(" | ", report.D3NearMissExamples));
    Expect("d3_near_miss_examples_no_prefix_of_longer",
        !report.D3NearMissExamples.Any(r => r.Contains("縫製入力の本文", StringComparison.Ordinal)
            || r.Contains("のほんぶん", StringComparison.Ordinal)),
        string.Join(" | ", report.D3NearMissExamples));
    Expect("d3_near_miss_intentional_one", report.D3NearMissIntentional == 1,
        $"got {report.D3NearMissIntentional}");
    Expect("d3_near_miss_ambient_zero", report.D3NearMissAmbient == 0,
        $"got {report.D3NearMissAmbient}");

    // H8: classify unscored (no text= / PrefixHead / other); score-from-reading when reading= remains.
    // Fixture: 2 PrefixHead no-text (lw F/T); 1 Closed no-text; 1 Closed reading-only lw=True → recover.
    Expect("unscored_no_text", report.UnscoredNoText == 2, $"got {report.UnscoredNoText}");
    Expect("unscored_prefix_head", report.UnscoredPrefixHead == 2, $"got {report.UnscoredPrefixHead}");
    Expect("unscored_other", report.UnscoredOther == 0, $"got {report.UnscoredOther}");
    Expect("unscored_live_true_no_text", report.UnscoredLiveTrueNoText == 1, $"got {report.UnscoredLiveTrueNoText}");
    Expect("unscored_live_true_prefix_head", report.UnscoredLiveTruePrefixHead == 1, $"got {report.UnscoredLiveTruePrefixHead}");
    Expect("unscored_with_reading", report.UnscoredWithReading == 1, $"got {report.UnscoredWithReading}");
    Expect("unscored_with_alts", report.UnscoredWithAlts == 0, $"got {report.UnscoredWithAlts}");
    Expect("score_from_reading_tried", report.ScoreFromReadingTried == 1, $"got {report.ScoreFromReadingTried}");
    Expect("score_from_reading_hits", report.ScoreFromReadingHits == 1, $"got {report.ScoreFromReadingHits}");
    Expect("score_from_reading_recovered_live_true", report.ScoreFromReadingRecoveredLiveTrue == 1,
        $"got {report.ScoreFromReadingRecoveredLiveTrue}");
    Expect("after_unscored_live_true", report.AfterUnscoredLiveTrue == 1, $"got {report.AfterUnscoredLiveTrue}");
    Expect("after_wake_hits", report.AfterWakeHits == 3, $"got {report.AfterWakeHits}");
    // Before: unscored_live_true includes the reading-only row; after recovers it.
    Expect("unscored_live_true_before_includes_reading_only", report.UnscoredLiveTrue == 2,
        $"got {report.UnscoredLiveTrue}");
    Expect("live_true_with_reading_only", report.LiveTrue == 4, $"got {report.LiveTrue}");

    // H4b LooksIntentional goldens — keep/revert not locked only to substring heuristic.
    Expect("intentional_housei", Replay.LooksIntentional("縫製入力", "ほうせい にゅうりょく"), "縫製入力 must be intentional");
    Expect("ambient_ninpu", !Replay.LooksIntentional("妊婦と", "にんぷ と"), "妊婦と must be ambient");
    Expect("ambient_inku", !Replay.LooksIntentional("インクと", "いんく と"), "インクと must be ambient");

    // H4b shell trap restore proof (failure path).
    var sh = Path.Combine(root, "scripts", "wake_log_replay.sh");
    if (!File.Exists(sh))
    {
        failures.Add($"missing {sh}");
    }
    else
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { sh, "--self-test-restore" },
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start wake_log_replay.sh");
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Expect("restore_trap", proc.ExitCode == 0 && stdout.Contains("OK wake_log_replay restore trap self-test", StringComparison.Ordinal),
            $"exit={proc.ExitCode} stdout={stdout.Trim()} stderr={stderr.Trim()}");
    }

    if (failures.Count > 0)
    {
        Console.Error.WriteLine("FAIL self-test:");
        foreach (var f in failures)
        {
            Console.Error.WriteLine("  " + f);
        }

        report.WriteHuman(Console.Error);
        return 1;
    }

    Console.WriteLine("OK wake_log_replay self-test");
    report.WriteHuman(Console.Out);
    return 0;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Package.swift"))
            && Directory.Exists(Path.Combine(dir.FullName, "dotnet")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    // dotnet run from repo: cwd is usually the project or repo root
    var cwd = Directory.GetCurrentDirectory();
    if (File.Exists(Path.Combine(cwd, "Package.swift")))
    {
        return cwd;
    }

    if (File.Exists(Path.Combine(cwd, "..", "..", "Package.swift")))
    {
        return Path.GetFullPath(Path.Combine(cwd, "..", ".."));
    }

    return cwd;
}

internal static class Replay
{
    private static readonly Regex Complete = new(
        @"dictation recognition: complete\b(?<rest>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Field = new(
        @"(?<k>[a-zA-Z]+)=(?:""(?<qs>(?:\\.|[^""])*)""|(?<bare>[^\s]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WakeReadings = new(
        @"wakeReadings=""(?<v>[^""]*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Fallback when the log never emitted wakeReadings (early portion of frozen overnight log).
    private static readonly WakeWord[] DefaultWakes =
    [
        WakeWord.From("インプット", "いんぷっと"),
        WakeWord.From("音声", "おんせい"),
        WakeWord.From("音声入力", "おんせいにゅうりょく"),
        WakeWord.From("音声入る", "おんせいはいる"),
        WakeWord.From("おんせい", "おんせい"),
        WakeWord.From("音声に入るよ", "おんせいにはいるよ"),
        WakeWord.From("音声に入る", "おんせいにはいる"),
        WakeWord.From("音声によって", "おんせいによって"),
    ];

    public static Report Run(string logPath, string label, int gainedCap, int examplesCap = 40)
    {
        var wakes = DefaultWakes;
        var scored = 0;
        var unscored = 0;
        var liveTrue = 0;
        var liveFalse = 0;
        var unscoredLiveTrue = 0;
        var wakeHits = 0;
        var agreedTrue = 0;
        var agreedFalse = 0;
        var gained = 0;
        var lost = 0;
        var gainedRows = new List<string>();
        var nearMissByDistance = new Dictionary<int, int>();
        var nearMissByWake = new Dictionary<string, int>(StringComparer.Ordinal);
        var nearMissReadings = new List<string>();
        var nearMissExamples = new List<string>();
        var shortNearMissExamples = new List<string>();
        var shortNearMissIntentional = 0;
        var shortNearMissAmbient = 0;
        var d3NearMissExamples = new List<string>();
        var d3NearMissIntentional = 0;
        var d3NearMissAmbient = 0;
        var intentionalGained = 0;
        var ambientGained = 0;
        // H8: unscored classification + score-from-reading (replay only; no runtime privacy change).
        var unscoredNoText = 0;
        var unscoredPrefixHead = 0;
        var unscoredOther = 0;
        var unscoredWithReading = 0;
        var unscoredWithAlts = 0;
        var scoreFromReadingTried = 0;
        var scoreFromReadingHits = 0;
        var scoreFromReadingRecoveredLiveTrue = 0;
        var unscoredLiveTrueNoText = 0;
        var unscoredLiveTruePrefixHead = 0;
        const int MinExampleWakeReading = 10;

        foreach (var line in File.ReadLines(logPath, Encoding.UTF8))
        {
            var wr = WakeReadings.Match(line);
            if (wr.Success && line.Contains("dictation timing:", StringComparison.Ordinal))
            {
                wakes = ParseWakeReadings(wr.Groups["v"].Value);
                continue;
            }

            if (!line.Contains("dictation recognition: complete", StringComparison.Ordinal))
            {
                continue;
            }

            var parsed = ParseComplete(line);
            if (parsed is null)
            {
                // Missing leadingWake=/extent= or unparseable complete line.
                unscored++;
                unscoredOther++;
                continue;
            }

            if (parsed.Text is null)
            {
                // Live still set leadingWake, but the log omitted text= (privacy / older body omit).
                // H8 classifies: PrefixHead vs no-text= (Closed without text) vs other (above).
                unscored++;
                if (parsed.Extent == RecognitionExtent.PrefixHead)
                {
                    unscoredPrefixHead++;
                    if (parsed.LiveLeadingWake)
                    {
                        unscoredLiveTruePrefixHead++;
                    }
                }
                else
                {
                    unscoredNoText++;
                    if (parsed.LiveLeadingWake)
                    {
                        unscoredLiveTrueNoText++;
                    }
                }

                if (!string.IsNullOrEmpty(parsed.Reading))
                {
                    unscoredWithReading++;
                }

                if (!string.IsNullOrEmpty(parsed.Alts))
                {
                    unscoredWithAlts++;
                }

                if (parsed.LiveLeadingWake)
                {
                    liveTrue++;
                    unscoredLiveTrue++;
                }
                else
                {
                    liveFalse++;
                }

                // Score-from-reading trial: when reading= remains without text=,
                // run the same LeadingWake API offline. Does not change production log writing.
                if (!string.IsNullOrEmpty(parsed.Reading))
                {
                    scoreFromReadingTried++;
                    var readingOnly = BuildUtterance(parsed);
                    var readingHit = DictationBoundaries.LeadingWake(readingOnly, wakes);
                    if (readingHit is not null)
                    {
                        scoreFromReadingHits++;
                        if (parsed.LiveLeadingWake)
                        {
                            scoreFromReadingRecoveredLiveTrue++;
                        }
                    }
                }

                continue;
            }

            scored++;
            if (parsed.LiveLeadingWake)
            {
                liveTrue++;
            }
            else
            {
                liveFalse++;
            }

            var utterance = BuildUtterance(parsed);
            var prefix = DictationBoundaries.LeadingWake(utterance, wakes);
            var hit = prefix is not null;
            if (hit)
            {
                wakeHits++;
            }

            if (hit == parsed.LiveLeadingWake)
            {
                if (hit)
                {
                    agreedTrue++;
                }
                else
                {
                    agreedFalse++;
                }
            }
            else if (hit && !parsed.LiveLeadingWake)
            {
                gained++;
                var reading = Compact(parsed.Reading ?? parsed.Text, 24);
                var dist = prefix!.Distance;
                var via = prefix.ByReading is { } w ? w.Reading : "text";
                if (gainedRows.Count < gainedCap)
                {
                    gainedRows.Add($"reading=\"{reading}\" d={dist} via={via} text=\"{Compact(parsed.Text, 24)}\"");
                }

                if (LooksIntentional(parsed.Text, parsed.Reading))
                {
                    intentionalGained++;
                }
                else
                {
                    ambientGained++;
                }
            }
            else if (!hit && parsed.LiveLeadingWake)
            {
                lost++;
            }

            // Near-miss census: closed primary reading vs each wake reading, distance 1–3, no match required.
            if (parsed.Extent == RecognitionExtent.ClosedUtterance)
            {
                var primary = TextMatching.NormalizeReading(
                    string.IsNullOrEmpty(parsed.Reading) ? parsed.Text : parsed.Reading.Replace(" ", "", StringComparison.Ordinal));
                if (primary.Length > 0)
                {
                    var best = 99;
                    WakeWord? bestWake = null;
                    // H5b long examples: whole-utterance among wakes with Reading.Length>=10, d=1..2.
                    // H7 long examples: same wake length gate, whole-utterance d=3 only.
                    // H6 short examples: whole-utterance among wakes with Reading.Length<10, d=1 only.
                    // size±1 prefixes may still update the census (best), never example ranking.
                    var bestLong = 99;
                    WakeWord? bestLongWake = null;
                    var bestShort = 99;
                    WakeWord? bestShortWake = null;
                    foreach (var wake in wakes.Where(w => w.Reading.Length > 0))
                    {
                        // Whole-utterance edit distance.
                        var dWhole = TextMatching.EditDistance(primary, wake.Reading);
                        if (dWhole >= 1 && dWhole <= 3 && dWhole < best)
                        {
                            best = dWhole;
                            bestWake = wake;
                        }

                        if (wake.Reading.Length >= MinExampleWakeReading && dWhole >= 1 && dWhole <= 3 && dWhole < bestLong)
                        {
                            bestLong = dWhole;
                            bestLongWake = wake;
                        }

                        if (wake.Reading.Length < MinExampleWakeReading && dWhole == 1 && dWhole < bestShort)
                        {
                            bestShort = dWhole;
                            bestShortWake = wake;
                        }

                        // Census only: size±1 window on longer utterances (matches ReadingWake length set).
                        var size = wake.Reading.Length;
                        foreach (var length in new[] { size, size - 1, size + 1 })
                        {
                            if (length <= 0 || length > primary.Length || length == primary.Length)
                            {
                                // length == primary.Length already covered by dWhole above.
                                continue;
                            }

                            var d = TextMatching.EditDistance(primary[..length], wake.Reading);
                            if (d >= 1 && d <= 3 && d < best)
                            {
                                best = d;
                                bestWake = wake;
                            }
                        }
                    }

                    if (bestWake is not null && best >= 1 && best <= 3)
                    {
                        nearMissByDistance[best] = nearMissByDistance.GetValueOrDefault(best) + 1;
                        var key = $"{bestWake.Text}/{bestWake.Reading}";
                        nearMissByWake[key] = nearMissByWake.GetValueOrDefault(key) + 1;
                        if (nearMissReadings.Count < 80)
                        {
                            nearMissReadings.Add($"d={best} wake={bestWake.Reading} reading=\"{Compact(primary, 24)}\"");
                        }
                    }

                    // H5b: misses only (!hit). Long-wake whole-utterance d=1..2. Cap <=40.
                    // Matcher hits (incl. H2d gained 縫製入力) are gained, not near-misses.
                    if (!hit
                        && bestLongWake is not null
                        && bestLong >= 1
                        && bestLong <= 2
                        && nearMissExamples.Count < examplesCap)
                    {
                        var intentional = LooksIntentional(parsed.Text, parsed.Reading);
                        nearMissExamples.Add(
                            $"text=\"{Compact(parsed.Text, 24)}\" reading=\"{Compact(primary, 24)}\" wake={bestLongWake.Reading} d={bestLong} intentional={intentional}");
                    }

                    // H6: misses only (!hit). Short-wake whole-utterance d=1. Cap <=40.
                    // Complements H5b (>=10): Reading.Length < MinExampleWakeReading. Includes おんせい / いんぷっと.
                    if (!hit
                        && bestShortWake is not null
                        && bestShort == 1
                        && shortNearMissExamples.Count < examplesCap)
                    {
                        var intentional = LooksIntentional(parsed.Text, parsed.Reading);
                        shortNearMissExamples.Add(
                            $"text=\"{Compact(parsed.Text, 24)}\" reading=\"{Compact(primary, 24)}\" wake={bestShortWake.Reading} d={bestShort} intentional={intentional}");
                        if (intentional)
                        {
                            shortNearMissIntentional++;
                        }
                        else
                        {
                            shortNearMissAmbient++;
                        }
                    }

                    // H7: misses only (!hit). Long-wake whole-utterance d=3. Cap <=40.
                    // bestLong is the closest long-wake whole distance (1..3); d=1|2 stay in H5b.
                    if (!hit
                        && bestLongWake is not null
                        && bestLong == 3
                        && d3NearMissExamples.Count < examplesCap)
                    {
                        var intentional = LooksIntentional(parsed.Text, parsed.Reading);
                        d3NearMissExamples.Add(
                            $"text=\"{Compact(parsed.Text, 24)}\" reading=\"{Compact(primary, 24)}\" wake={bestLongWake.Reading} d={bestLong} intentional={intentional}");
                        if (intentional)
                        {
                            d3NearMissIntentional++;
                        }
                        else
                        {
                            d3NearMissAmbient++;
                        }
                    }
                }
            }
        }

        return new Report(
            Label: label,
            Scored: scored,
            Unscored: unscored,
            UnscoredLiveTrue: unscoredLiveTrue,
            LiveTrue: liveTrue,
            LiveFalse: liveFalse,
            WakeHits: wakeHits,
            AgreedTrue: agreedTrue,
            AgreedFalse: agreedFalse,
            Gained: gained,
            Lost: lost,
            IntentionalGained: intentionalGained,
            AmbientGained: ambientGained,
            GainedRows: gainedRows,
            NearMissByDistance: nearMissByDistance,
            NearMissByWake: nearMissByWake,
            NearMissReadings: nearMissReadings,
            NearMissExamples: nearMissExamples,
            ShortNearMissExamples: shortNearMissExamples,
            ShortNearMissIntentional: shortNearMissIntentional,
            ShortNearMissAmbient: shortNearMissAmbient,
            D3NearMissExamples: d3NearMissExamples,
            D3NearMissIntentional: d3NearMissIntentional,
            D3NearMissAmbient: d3NearMissAmbient,
            UnscoredNoText: unscoredNoText,
            UnscoredPrefixHead: unscoredPrefixHead,
            UnscoredOther: unscoredOther,
            UnscoredWithReading: unscoredWithReading,
            UnscoredWithAlts: unscoredWithAlts,
            ScoreFromReadingTried: scoreFromReadingTried,
            ScoreFromReadingHits: scoreFromReadingHits,
            ScoreFromReadingRecoveredLiveTrue: scoreFromReadingRecoveredLiveTrue,
            UnscoredLiveTrueNoText: unscoredLiveTrueNoText,
            UnscoredLiveTruePrefixHead: unscoredLiveTruePrefixHead);
    }

    // H4b: explicit ambient/intentional goldens first so keep/revert is not locked only to
    // loose substring heuristics. Remaining markers cover other gained near-wakes.
    internal static bool LooksIntentional(string text, string? reading)
    {
        var n = TextMatching.Normalize(text);
        // Measured H4 gained goldens (keep these stable for revert vs keep verdict).
        if (n == "縫製入力")
        {
            return true;
        }

        if (n is "妊婦と" or "インクと")
        {
            return false;
        }

        if (n.Contains("縫製", StringComparison.Ordinal)
            || n.Contains("音声", StringComparison.Ordinal)
            || n.Contains("温水", StringComparison.Ordinal)
            || n.Contains("温泉", StringComparison.Ordinal)
            || n.Contains("インプット", StringComparison.Ordinal)
            || n.Contains("法政", StringComparison.Ordinal)
            || n.Contains("補正", StringComparison.Ordinal))
        {
            return true;
        }

        var r = TextMatching.NormalizeReading(reading ?? "");
        return r.Contains("おんせい", StringComparison.Ordinal)
            || r.Contains("ほうせい", StringComparison.Ordinal)
            || r.Contains("いんぷっと", StringComparison.Ordinal);
    }

    private static string Compact(string value, int max)
    {
        var flat = value.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    private static WakeWord[] ParseWakeReadings(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return DefaultWakes;
        }

        var list = new List<WakeWord>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                list.Add(WakeWord.From(part));
                continue;
            }

            list.Add(WakeWord.From(part[..eq], part[(eq + 1)..]));
        }

        return list.Count > 0 ? list.ToArray() : DefaultWakes;
    }

    private static CompleteLine? ParseComplete(string line)
    {
        var m = Complete.Match(line);
        if (!m.Success)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match f in Field.Matches(m.Groups["rest"].Value))
        {
            var k = f.Groups["k"].Value;
            var v = f.Groups["qs"].Success ? Unescape(f.Groups["qs"].Value) : f.Groups["bare"].Value;
            fields[k] = v;
        }

        if (!fields.TryGetValue("leadingWake", out var lwRaw)
            || !fields.TryGetValue("extent", out var extentRaw))
        {
            return null;
        }

        var live = bool.Parse(lwRaw);
        var extent = extentRaw == "PrefixHead" ? RecognitionExtent.PrefixHead : RecognitionExtent.ClosedUtterance;
        fields.TryGetValue("text", out var text);
        // Absence of text= means the runtime omitted heard (active body privacy) — unscored.
        var hasTextKey = fields.ContainsKey("text");
        fields.TryGetValue("reading", out var reading);
        fields.TryGetValue("range", out var rangeRaw);
        long start = 0, end = 1000;
        if (rangeRaw is not null)
        {
            var dots = rangeRaw.IndexOf("..", StringComparison.Ordinal);
            if (dots > 0
                && long.TryParse(rangeRaw[..dots], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
                && long.TryParse(rangeRaw[(dots + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
            {
                // ok
            }
        }

        fields.TryGetValue("alts", out var alts);
        return new CompleteLine(live, extent, hasTextKey ? text ?? "" : null, reading, start, end, alts);
    }

    private static string Unescape(string value) =>
        value.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);

    private static RecognizedUtterance BuildUtterance(CompleteLine line)
    {
        var text = line.Text ?? "";
        // One LexicalRun keeps Normalize(text) intact for exact/fused wakes; ReadingWake concatenates
        // NormalizeReading, so spaces in the logged reading= field are stripped the same way as live.
        var reading = string.IsNullOrEmpty(line.Reading) ? null : line.Reading;
        var range = new SampleRange(line.Start, Math.Max(line.End, line.Start));
        var lexemes = text.Length == 0 && reading is null
            ? ImmutableArray<LexicalRun>.Empty
            : ImmutableArray.Create(new LexicalRun(text.Length == 0 ? (reading ?? "") : text, range, reading));

        var alts = ImmutableArray<RecognitionAlternate>.Empty;
        if (!string.IsNullOrEmpty(line.Alts))
        {
            var list = new List<RecognitionAlternate>();
            foreach (var part in line.Alts.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var slash = part.IndexOf('/');
                if (slash <= 0)
                {
                    continue;
                }

                list.Add(new RecognitionAlternate(part[..slash], part[(slash + 1)..]));
            }

            if (list.Count > 0)
            {
                alts = list.ToImmutableArray();
            }
        }

        return new RecognizedUtterance(
            Id: 1,
            Extent: line.Extent,
            Source: range,
            Text: text,
            Lexemes: lexemes,
            Alternates: alts);
    }

    private sealed record CompleteLine(
        bool LiveLeadingWake,
        RecognitionExtent Extent,
        string? Text,
        string? Reading,
        long Start,
        long End,
        string? Alts);
}

internal sealed record Report(
    string Label,
    int Scored,
    int Unscored,
    int UnscoredLiveTrue,
    int LiveTrue,
    int LiveFalse,
    int WakeHits,
    int AgreedTrue,
    int AgreedFalse,
    int Gained,
    int Lost,
    int IntentionalGained,
    int AmbientGained,
    IReadOnlyList<string> GainedRows,
    IReadOnlyDictionary<int, int> NearMissByDistance,
    IReadOnlyDictionary<string, int> NearMissByWake,
    IReadOnlyList<string> NearMissReadings,
    IReadOnlyList<string> NearMissExamples,
    IReadOnlyList<string> ShortNearMissExamples,
    int ShortNearMissIntentional,
    int ShortNearMissAmbient,
    IReadOnlyList<string> D3NearMissExamples,
    int D3NearMissIntentional,
    int D3NearMissAmbient,
    int UnscoredNoText,
    int UnscoredPrefixHead,
    int UnscoredOther,
    int UnscoredWithReading,
    int UnscoredWithAlts,
    int ScoreFromReadingTried,
    int ScoreFromReadingHits,
    int ScoreFromReadingRecoveredLiveTrue,
    int UnscoredLiveTrueNoText,
    int UnscoredLiveTruePrefixHead)
{
    // After = text-scored wake_hits plus score-from-reading hits; unscored_live_true minus recovered.
    public int AfterWakeHits => WakeHits + ScoreFromReadingHits;
    public int AfterUnscoredLiveTrue => UnscoredLiveTrue - ScoreFromReadingRecoveredLiveTrue;

    public void WriteHuman(TextWriter w)
    {
        var liveTrueTotal = LiveTrue;
        var pct = liveTrueTotal == 0 ? 0.0 : 100.0 * WakeHits / liveTrueTotal;
        var bandLo = (int)Math.Round(liveTrueTotal * 0.98);
        var bandHi = (int)Math.Round(liveTrueTotal * 1.02);
        var inBand = WakeHits >= bandLo && WakeHits <= bandHi;
        var scoredLiveTrue = LiveTrue - UnscoredLiveTrue;
        w.WriteLine($"label={Label}");
        w.WriteLine($"scored={Scored} unscored={Unscored} unscored_live_true={UnscoredLiveTrue} (no text=; before score-from-reading)");
        w.WriteLine($"live_true={LiveTrue} live_false={LiveFalse} scored_live_true={scoredLiveTrue}");
        w.WriteLine($"wake_hits={WakeHits} wake_hits_vs_live_true_pct={pct:0.00} band=[{bandLo},{bandHi}] in_band={inBand}");
        if (!inBand)
        {
            w.WriteLine($"parity_note=wake_hits outside ±2% of live_true; cause=unscored_live_true={UnscoredLiveTrue} plus gained/lost; do not tune matcher");
        }
        // H8 classification of unscored rows + optional score-from-reading trial (replay only).
        w.WriteLine($"unscored_class no_text={UnscoredNoText} PrefixHead={UnscoredPrefixHead} other={UnscoredOther}");
        w.WriteLine($"unscored_live_true_class no_text={UnscoredLiveTrueNoText} PrefixHead={UnscoredLiveTruePrefixHead}");
        w.WriteLine($"unscored_meta with_reading={UnscoredWithReading} with_alts={UnscoredWithAlts}");
        w.WriteLine($"score_from_reading tried={ScoreFromReadingTried} hits={ScoreFromReadingHits} recovered_live_true={ScoreFromReadingRecoveredLiveTrue}");
        var afterPct = liveTrueTotal == 0 ? 0.0 : 100.0 * AfterWakeHits / liveTrueTotal;
        var afterInBand = AfterWakeHits >= bandLo && AfterWakeHits <= bandHi;
        w.WriteLine($"after_score_from_reading unscored_live_true={AfterUnscoredLiveTrue} wake_hits={AfterWakeHits} wake_hits_vs_live_true_pct={afterPct:0.00} in_band={afterInBand}");
        w.WriteLine($"agreed_true={AgreedTrue} agreed_false={AgreedFalse} gained={Gained} lost={Lost}");
        w.WriteLine($"gained_intentional={IntentionalGained} gained_ambient={AmbientGained}");
        w.WriteLine("near_miss_by_distance:");
        foreach (var kv in NearMissByDistance.OrderBy(k => k.Key))
        {
            w.WriteLine($"  d={kv.Key} count={kv.Value}");
        }

        w.WriteLine("near_miss_by_wake (top 12):");
        foreach (var kv in NearMissByWake.OrderByDescending(k => k.Value).Take(12))
        {
            w.WriteLine($"  {kv.Key} count={kv.Value}");
        }

        w.WriteLine($"gained_readings (cap {GainedRows.Count}):");
        foreach (var row in GainedRows)
        {
            w.WriteLine("  " + row);
        }

        w.WriteLine($"near_miss_examples_long_wake_d1_2_misses_whole (shown {NearMissExamples.Count}, wake.Reading.Length>=10, !hit, whole-utterance):");
        foreach (var row in NearMissExamples)
        {
            w.WriteLine("  " + row);
        }

        w.WriteLine($"near_miss_examples_short_wake_d1_misses_whole (shown {ShortNearMissExamples.Count}, wake.Reading.Length<10, !hit, whole-utterance, d=1):");
        w.WriteLine($"short_near_miss_intentional={ShortNearMissIntentional} short_near_miss_ambient={ShortNearMissAmbient}");
        foreach (var row in ShortNearMissExamples)
        {
            w.WriteLine("  " + row);
        }

        w.WriteLine($"near_miss_examples_long_wake_d3_misses_whole (shown {D3NearMissExamples.Count}, wake.Reading.Length>=10, !hit, whole-utterance, d=3):");
        w.WriteLine($"d3_near_miss_intentional={D3NearMissIntentional} d3_near_miss_ambient={D3NearMissAmbient}");
        foreach (var row in D3NearMissExamples)
        {
            w.WriteLine("  " + row);
        }
    }

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append('{');
        void Num(string k, int v) => sb.Append(CultureInfo.InvariantCulture, $"\"{k}\":{v},");
        Num("scored", Scored);
        Num("unscored", Unscored);
        Num("unscored_live_true", UnscoredLiveTrue);
        Num("live_true", LiveTrue);
        Num("live_false", LiveFalse);
        Num("wake_hits", WakeHits);
        Num("agreed_true", AgreedTrue);
        Num("agreed_false", AgreedFalse);
        Num("gained", Gained);
        Num("lost", Lost);
        Num("gained_intentional", IntentionalGained);
        Num("gained_ambient", AmbientGained);
        Num("short_near_miss_intentional", ShortNearMissIntentional);
        Num("short_near_miss_ambient", ShortNearMissAmbient);
        Num("d3_near_miss_intentional", D3NearMissIntentional);
        Num("d3_near_miss_ambient", D3NearMissAmbient);
        Num("unscored_no_text", UnscoredNoText);
        Num("unscored_prefix_head", UnscoredPrefixHead);
        Num("unscored_other", UnscoredOther);
        Num("unscored_with_reading", UnscoredWithReading);
        Num("unscored_with_alts", UnscoredWithAlts);
        Num("score_from_reading_tried", ScoreFromReadingTried);
        Num("score_from_reading_hits", ScoreFromReadingHits);
        Num("score_from_reading_recovered_live_true", ScoreFromReadingRecoveredLiveTrue);
        Num("after_unscored_live_true", AfterUnscoredLiveTrue);
        Num("after_wake_hits", AfterWakeHits);
        Num("unscored_live_true_no_text", UnscoredLiveTrueNoText);
        Num("unscored_live_true_prefix_head", UnscoredLiveTruePrefixHead);
        sb.Append(CultureInfo.InvariantCulture, $"\"label\":\"{Escape(Label)}\",");
        sb.Append("\"gained_readings\":[");
        sb.Append(string.Join(',', GainedRows.Select(r => $"\"{Escape(r)}\"")));
        sb.Append("],\"near_miss_by_distance\":{");
        sb.Append(string.Join(',', NearMissByDistance.OrderBy(k => k.Key).Select(kv => $"\"{kv.Key}\":{kv.Value}")));
        sb.Append("},\"near_miss_by_wake\":{");
        sb.Append(string.Join(',', NearMissByWake.Select(kv => $"\"{Escape(kv.Key)}\":{kv.Value}")));
        sb.Append("},\"near_miss_examples\":[");
        sb.Append(string.Join(',', NearMissExamples.Select(r => $"\"{Escape(r)}\"")));
        sb.Append("],\"near_miss_examples_short\":[");
        sb.Append(string.Join(',', ShortNearMissExamples.Select(r => $"\"{Escape(r)}\"")));
        sb.Append("],\"near_miss_examples_d3\":[");
        sb.Append(string.Join(',', D3NearMissExamples.Select(r => $"\"{Escape(r)}\"")));
        sb.Append("]}");
        return sb.ToString();

        static string Escape(string s) =>
            s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
