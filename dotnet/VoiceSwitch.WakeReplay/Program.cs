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
          dotnet run --project dotnet/VoiceSwitch.WakeReplay -- --log <path> [--label NAME] [--gained-cap 40] [--json-out path]
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

var report = Replay.Run(logPath, label, gainedCap);
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

    var report = Replay.Run(fixture, "self-test", gainedCap: 40);
    // Known counts on the fixture (main matcher, d≤1):
    // - 縫製入力 (ほうせいにゅうりょく) → no hit (d=2 needs H2b)
    // - 温泉 alone → hit short おんせい (d=1 whole closed)
    // - ほうせいにはいるよ prefix body → no d=2 prefix hit on main
    // - 音声入力 exact → hit
    // - PrefixHead no-text leadingWake=False → unscored
    // - PrefixHead no-text leadingWake=True → unscored_live_true (H4b ±2% path)
    // - 妊婦と / インクと ambient scored false (main: no gain)
    var failures = new List<string>();
    void Expect(string name, bool cond, string detail = "")
    {
        if (!cond)
        {
            failures.Add(detail.Length == 0 ? name : $"{name}: {detail}");
        }
    }

    Expect("scored", report.Scored == 7, $"got {report.Scored}");
    Expect("live_true", report.LiveTrue == 3, $"got {report.LiveTrue}");
    Expect("unscored_live_true", report.UnscoredLiveTrue == 1, $"got {report.UnscoredLiveTrue}");
    Expect("wake_hits", report.WakeHits == 2, $"got {report.WakeHits}");
    Expect("agreed_true", report.AgreedTrue == 2, $"got {report.AgreedTrue}");
    Expect("gained", report.Gained == 0, $"got {report.Gained} (main must not gain 縫製)");
    Expect("lost", report.Lost == 0, $"got {report.Lost}");
    Expect("unscored", report.Unscored == 2, $"got {report.Unscored}");
    Expect("near_miss_d2_has_hosei", report.NearMissByDistance.GetValueOrDefault(2) >= 1,
        $"d2={report.NearMissByDistance.GetValueOrDefault(2)}");
    // 縫製入力 reading vs おんせいにゅうりょく is d=2 near-miss
    Expect("near_miss_lists_housei", report.NearMissReadings.Any(r => r.Contains("ほうせい", StringComparison.Ordinal)),
        string.Join(',', report.NearMissReadings));

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

    public static Report Run(string logPath, string label, int gainedCap)
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
        var intentionalGained = 0;
        var ambientGained = 0;

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
                unscored++;
                continue;
            }

            if (parsed.Text is null)
            {
                // Live still set leadingWake, but the log omitted text/reading (active-session PrefixHead privacy).
                unscored++;
                if (parsed.LiveLeadingWake)
                {
                    liveTrue++;
                    unscoredLiveTrue++;
                }
                else
                {
                    liveFalse++;
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
                    foreach (var wake in wakes.Where(w => w.Reading.Length > 0))
                    {
                        // Compare whole utterance reading to wake reading (and size±1 prefixes already covered by matcher;
                        // census uses full-string edit distance capped at 3 for ranking).
                        var d = TextMatching.EditDistance(primary, wake.Reading);
                        if (d >= 1 && d <= 3 && d < best)
                        {
                            best = d;
                            bestWake = wake;
                        }

                        // Also allow size±1 window on longer utterances (matches ReadingWake length set).
                        var size = wake.Reading.Length;
                        foreach (var length in new[] { size, size - 1, size + 1 })
                        {
                            if (length <= 0 || length > primary.Length)
                            {
                                continue;
                            }

                            d = TextMatching.EditDistance(primary[..length], wake.Reading);
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
            NearMissReadings: nearMissReadings);
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
    IReadOnlyList<string> NearMissReadings)
{
    public void WriteHuman(TextWriter w)
    {
        var liveTrueTotal = LiveTrue;
        var pct = liveTrueTotal == 0 ? 0.0 : 100.0 * WakeHits / liveTrueTotal;
        var bandLo = (int)Math.Round(liveTrueTotal * 0.98);
        var bandHi = (int)Math.Round(liveTrueTotal * 1.02);
        var inBand = WakeHits >= bandLo && WakeHits <= bandHi;
        var scoredLiveTrue = LiveTrue - UnscoredLiveTrue;
        w.WriteLine($"label={Label}");
        w.WriteLine($"scored={Scored} unscored={Unscored} unscored_live_true={UnscoredLiveTrue} (no text=; PrefixHead privacy)");
        w.WriteLine($"live_true={LiveTrue} live_false={LiveFalse} scored_live_true={scoredLiveTrue}");
        w.WriteLine($"wake_hits={WakeHits} wake_hits_vs_live_true_pct={pct:0.00} band=[{bandLo},{bandHi}] in_band={inBand}");
        if (!inBand)
        {
            w.WriteLine($"parity_note=wake_hits outside ±2% of live_true; cause=unscored_live_true={UnscoredLiveTrue} plus gained/lost; do not tune matcher");
        }
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
        sb.Append(CultureInfo.InvariantCulture, $"\"label\":\"{Escape(Label)}\",");
        sb.Append("\"gained_readings\":[");
        sb.Append(string.Join(',', GainedRows.Select(r => $"\"{Escape(r)}\"")));
        sb.Append("],\"near_miss_by_distance\":{");
        sb.Append(string.Join(',', NearMissByDistance.OrderBy(k => k.Key).Select(kv => $"\"{kv.Key}\":{kv.Value}")));
        sb.Append("},\"near_miss_by_wake\":{");
        sb.Append(string.Join(',', NearMissByWake.Select(kv => $"\"{Escape(kv.Key)}\":{kv.Value}")));
        sb.Append("}}");
        return sb.ToString();

        static string Escape(string s) =>
            s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
