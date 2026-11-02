using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

// Scores a frozen recognized-text corpus with the production wake APIs.
// This is not a microphone and not a claim about real speech.

if (args.Length != 2 || args[0] != "--corpus")
{
    Console.Error.WriteLine("usage: wake-corpus --corpus corpus.json");
    return 2;
}

var corpusPath = Path.GetFullPath(args[1]);
var json = File.ReadAllText(corpusPath, Encoding.UTF8);
var corpus = JsonSerializer.Deserialize<CorpusFile>(json, new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
}) ?? throw new InvalidOperationException("empty corpus");

var configured = corpus.Wakes.Select(wake => WakeWord.From(wake.Text, wake.Reading)).ToArray();
var surface = corpus.Wakes.Select(wake => WakeWord.From(wake.Text)).ToArray();

var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corpusPath))).ToLowerInvariant();
Console.WriteLine($"corpus_sha256={sha}");
Console.WriteLine($"rows={corpus.Rows.Count}");
Console.WriteLine("UNVERIFIED real-audio mac windows-hardware");

var failed = corpus.Rows.Count != 47;
failed |= Report("configured", configured, static row => row.Expect, 15, 8);
failed |= Report("surface", surface, static row => row.ExpectSurface ?? row.Expect, 7, 16);
return failed ? 1 : 0;

bool Report(string rule, WakeWord[] wakes, Func<CorpusRow, string> expectOf, int hitFloor, int missCeil)
{
    var hits = 0;
    var misses = 0;
    var falseWakes = 0;
    var rejects = 0;
    foreach (var row in corpus.Rows)
    {
        var expect = expectOf(row);
        if (expect is not ("wake" or "no-wake"))
        {
            throw new InvalidOperationException($"{row.Id} expect {expect}");
        }

        var got = DecidesWake(row, wakes) ? "wake" : "no-wake";
        var kind = (expect, got) switch
        {
            ("wake", "wake") => "hit",
            ("wake", "no-wake") => "miss",
            ("no-wake", "wake") => "false_wake",
            _ => "reject",
        };
        if (kind == "hit")
        {
            hits++;
        }
        else if (kind == "miss")
        {
            misses++;
        }
        else if (kind == "false_wake")
        {
            falseWakes++;
        }
        else
        {
            rejects++;
        }

        Console.WriteLine($"row {rule} {row.Id} expect={expect} got={got} {kind}");
    }

    Console.WriteLine($"rule={rule} hits={hits} misses={misses} false_wakes={falseWakes} rejects={rejects} rows={corpus.Rows.Count}");
    Console.WriteLine($"floor {rule} hits>={hitFloor} misses<={missCeil} false_wakes=0");
    if (falseWakes != 0 || hits < hitFloor || misses > missCeil)
    {
        Console.Error.WriteLine($"FAIL {rule} hits={hits} misses={misses} false_wakes={falseWakes}");
        return true;
    }

    return false;
}

static bool DecidesWake(CorpusRow row, WakeWord[] wakes)
{
    var lexemes = (row.Lexemes ?? []).Select(item => new LexicalRun(
        item.Text,
        new SampleRange(item.Start, item.End),
        string.IsNullOrEmpty(item.Reading) ? null : item.Reading)).ToImmutableArray();
    var end = lexemes.Length == 0 ? 8000 : lexemes.Max(item => item.Range.End);
    var alternates = (row.Alternates ?? []).Select(item => new RecognitionAlternate(item.Text, item.Reading)).ToImmutableArray();
    var utterance = new RecognizedUtterance(
        1,
        ParseExtent(row.Extent),
        new SampleRange(0, end),
        row.Text,
        lexemes,
        HadRejectedSpeech: row.Channel == "rejected",
        Confidence: row.Confidence,
        RejectedText: row.RejectedText,
        Alternates: alternates,
        TailSilenceSamples: row.TailSilence);
    return row.Channel switch
    {
        "early" => DictationBoundaries.EarlyWake(utterance, wakes) is not null,
        "rejected" => DictationBoundaries.RejectedWake(utterance, wakes) is not null,
        "leading" => DictationBoundaries.LeadingWake(utterance, wakes) is not null,
        _ => throw new InvalidOperationException($"{row.Id} channel {row.Channel}"),
    };
}

static RecognitionExtent ParseExtent(string extent) => extent switch
{
    "ClosedUtterance" => RecognitionExtent.ClosedUtterance,
    "PrefixHead" => RecognitionExtent.PrefixHead,
    "Probe" => RecognitionExtent.Probe,
    _ => throw new InvalidOperationException($"extent {extent}"),
};

internal sealed class CorpusFile
{
    public List<CorpusWake> Wakes { get; set; } = [];

    public List<CorpusRow> Rows { get; set; } = [];
}

internal sealed class CorpusWake
{
    public string Text { get; set; } = "";

    public string Reading { get; set; } = "";
}

internal sealed class CorpusRow
{
    public string Id { get; set; } = "";

    public string Expect { get; set; } = "";

    public string? ExpectSurface { get; set; }

    public string Channel { get; set; } = "leading";

    public string Extent { get; set; } = "ClosedUtterance";

    public string Text { get; set; } = "";

    public long TailSilence { get; set; }

    public double? Confidence { get; set; }

    public string? RejectedText { get; set; }

    public List<CorpusLexeme>? Lexemes { get; set; }

    public List<CorpusAlternate>? Alternates { get; set; }
}

internal sealed class CorpusLexeme
{
    public string Text { get; set; } = "";

    public string? Reading { get; set; }

    public long Start { get; set; }

    public long End { get; set; }
}

internal sealed class CorpusAlternate
{
    public string Text { get; set; } = "";

    public string Reading { get; set; } = "";
}
