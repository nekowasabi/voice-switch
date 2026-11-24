// Word enrollment: turn mis-transcriptions of a spoken wake/stop word into aliases.
//
// A user records the word a few times, each clip goes through the production
// recognizer, and the normalized results become alias candidates. Mirrors the
// Swift WordEnrollment line for line (see tests/parity/fixtures/enroll_words.json).
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceSwitch.Windows.Core;

/// <summary>One enrollment clip after the filter.</summary>
public sealed record WordEnrollmentResult(string Transcript, string Normalized, WordEnrollmentVerdict Verdict, string? Reason)
{
    public string Label => Verdict switch
    {
        WordEnrollmentVerdict.AlreadyMatches => "already matches",
        WordEnrollmentVerdict.Alias => "new alias candidate",
        WordEnrollmentVerdict.Rejected => $"rejected: {Reason}",
        _ => "rejected",
    };
}

public enum WordEnrollmentVerdict
{
    AlreadyMatches,
    Alias,
    Rejected,
}

public static class WordEnrollment
{
    /// <summary>Normalized candidates shorter than this are rejected (they would false-trigger).</summary>
    public const int MinAliasLength = 3;

    /// <summary>A candidate heard in this many past dictation journal entries is rejected.</summary>
    public const int JournalHitThreshold = 3;

    /// <summary>
    /// Normalized aliases rejected for --stop: a stop word matches at the END of a
    /// transcript, so an alias that is a common sentence ending would stop ordinary speech.
    /// </summary>
    public static readonly string[] StopSuffixRisky = ["です", "ます", "でした", "ました", "した", "ない", "ある", "いる", "する", "こと"];

    /// <summary>
    /// Classify one transcript. <paramref name="existingWords"/> is the configured list plus
    /// the enrolled word itself; <paramref name="journalTexts"/> are raw past dictation texts.
    /// </summary>
    public static WordEnrollmentResult Classify(string candidate, IEnumerable<string> existingWords, IReadOnlyList<string> journalTexts, bool isStop)
    {
        var norm = TextMatching.Normalize(candidate);
        if (norm.Length == 0)
        {
            return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.Rejected, "empty transcript");
        }

        if (existingWords.Select(TextMatching.Normalize).Contains(norm))
        {
            return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.AlreadyMatches, null);
        }

        if (norm.Length < MinAliasLength)
        {
            return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.Rejected, $"too short ({norm.Length} chars, minimum {MinAliasLength})");
        }

        var hits = journalTexts.Count(text => TextMatching.Normalize(text).Contains(norm, StringComparison.Ordinal));
        if (hits >= JournalHitThreshold)
        {
            return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.Rejected, $"heard in {hits} past dictations (limit {JournalHitThreshold})");
        }

        if (isStop && StopSuffixRisky.Contains(norm))
        {
            return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.Rejected, "common sentence ending; it would stop ordinary speech");
        }

        return new WordEnrollmentResult(candidate, norm, WordEnrollmentVerdict.Alias, null);
    }

    /// <summary>Classify every clip transcript; keeps one entry per clip, in order.</summary>
    public static IReadOnlyList<WordEnrollmentResult> Candidates(IEnumerable<string> transcripts, IEnumerable<string> existingWords, IReadOnlyList<string> journalTexts, bool isStop) =>
        transcripts.Select(candidate => Classify(candidate, existingWords, journalTexts, isStop)).ToList();

    /// <summary>Append aliases to the configured list without reordering or duplicating.</summary>
    public static (string[] Merged, string[] Added) MergeWordList(string[] existing, IEnumerable<string> aliases)
    {
        var merged = new List<string>(existing);
        var seen = new HashSet<string>(existing.Select(TextMatching.Normalize), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var alias in aliases)
        {
            var norm = TextMatching.Normalize(alias);
            if (norm.Length == 0 || !seen.Add(norm)) continue;
            merged.Add(alias);
            added.Add(alias);
        }

        return (merged.ToArray(), added.ToArray());
    }

    /// <summary>The "text" of one journal JSONL line, or null when the line is not a dictation record.</summary>
    public static string? JournalLineText(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Past dictation texts for the false-trigger check. Best effort: missing or
    /// unreadable files contribute nothing. Handles both a directory of daily
    /// <c>yyyy-MM-dd.jsonl</c> files and a single <c>.jsonl</c> file.
    /// </summary>
    public static IReadOnlyList<string> ReadJournalTexts(string? journalDir, string? fallbackDirectory = null)
    {
        fallbackDirectory ??= DictationJournal.DefaultDirectory(JournalPlatform.Windows);
        var raw = string.IsNullOrWhiteSpace(journalDir) ? fallbackDirectory : journalDir.Trim();
        string expanded;
        try
        {
            expanded = WindowsPaths.ExpandPath(raw);
        }
        catch
        {
            return [];
        }

        var files = expanded.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            ? (IReadOnlyList<string>)[expanded]
            : Directory.Exists(expanded)
                ? Directory.GetFiles(expanded, "*.jsonl").OrderBy(path => path, StringComparer.Ordinal).ToList()
                : [];
        var texts = new List<string>();
        foreach (var file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (JournalLineText(line) is { } text) texts.Add(text);
                if (texts.Count >= 50_000) return texts;
            }
        }

        return texts;
    }

    /// <summary>
    /// Append aliases to the <paramref name="key"/> array in the user config file, preserving
    /// every other key and value. Makes a <c>config.json.bak</c> backup first. A dry run
    /// returns what would be added without writing. Returns the entries added.
    /// When the key is absent, <paramref name="effectiveExisting"/> (the list the app
    /// effectively uses, e.g. the per-locale default stop words after defaults are applied)
    /// seeds the written list, so a missing key never drops the default word.
    /// An explicit [] stays [] + aliases. Comments and trailing commas are accepted on
    /// read (like the config loader) but dropped on write.
    /// </summary>
    public static string[] AppendAliasesToConfig(string configPath, string key, IEnumerable<string> aliases, bool dryRun, string[]? effectiveExisting = null)
    {
        var documentOptions = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var root = JsonNode.Parse(File.ReadAllText(configPath), nodeOptions: null, documentOptions: documentOptions) as JsonObject
            ?? throw new InvalidDataException($"config is not a JSON object: {configPath}");
        var hasKey = root[key] is not null;
        if (hasKey && root[key] is not JsonArray)
        {
            throw new InvalidDataException($"config key {key} is not a word list");
        }

        var existing = hasKey
            ? root[key]!.AsArray().Select(node => node?.GetValue<string>() ?? "").ToArray()
            : effectiveExisting ?? [];
        var (merged, added) = MergeWordList(existing, aliases);
        if (added.Length == 0) return [];
        if (dryRun) return added;
        File.Copy(configPath, configPath + ".bak", overwrite: true);
        root[key] = new JsonArray(merged.Select(word => JsonValue.Create(word)).ToArray());
        var writeOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(configPath, root.ToJsonString(writeOptions) + Environment.NewLine);
        return added;
    }
}
