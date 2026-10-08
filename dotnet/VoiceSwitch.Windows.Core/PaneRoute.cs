using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoiceSwitch.Windows.Core;

// Closed catalog row from `tmux list-panes`. Addressed only by pane id (`%0`), never by index.
public sealed record PaneLabel(string Id, string Window, string Title, string Command);

// Jev's answer to "which listed pane does the speaker address". Pane null is the "none" choice.
public sealed record JevPick(string? Pane, double Confidence);

// Jev's answer to which candidate is the literal pane payload. Choice null is "none".
public sealed record JevBodyPick(string? Choice, double Confidence);

public sealed record SendBodyResult(string? Body, string Reason);

// SuppressFallback is true when a pane was chosen but ExtractSendBody failed: do not paste the full dictation.
public sealed record RouteDecision(string? Pane, string Reason, bool SuppressFallback = false);

// Mirrors PaneRoute.swift. The policy table and the response parser share tests/parity/fixtures/pane_route.json.
public static class PaneRoute
{
    // Probe (scripts/pane_jev_probe.py, 8 cases): wrong answers came back at 0.49 and 0.52, right ones at
    // 0.80 to 1.00. Recalibrate from the `tmux:` log lines once real dictations accumulate.
    public const double JevConfidenceFloor = 0.8;

    public static readonly string[] ListPanesArguments =
        ["list-panes", "-a", "-F", "#{pane_id}\t#{window_name}\t#{pane_title}\t#{pane_current_command}"];

    public const string JevEndpoint = "https://api.typesafe.ai/v1/systemone";

    // `%` plus ASCII digits. Rejects indexes and anything else.
    public static bool IsPaneId(string id) =>
        id.Length >= 2 && id[0] == '%' && id.Skip(1).All(char.IsAsciiDigit);

    public static IReadOnlyList<PaneLabel>? ParsePanes(string text)
    {
        var panes = new List<PaneLabel>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split('\t');
            if (fields.Length != 4)
            {
                return null;
            }

            panes.Add(new PaneLabel(fields[0], fields[1], fields[2], fields[3]));
        }

        return panes;
    }

    // A pane hits when any non-empty label is a case-insensitive substring of the utterance.
    // Labels are the title, the window name, the current command, and any agent name injected for that pane id.
    public static IReadOnlyList<PaneLabel> MatchingPanes(string utterance, IReadOnlyList<PaneLabel> panes, IReadOnlyDictionary<string, string[]>? agents = null)
    {
        var folded = utterance.ToLowerInvariant();
        return panes.Where(pane =>
        {
            IEnumerable<string> labels = [pane.Title, pane.Window, pane.Command];
            if (agents is not null && agents.TryGetValue(pane.Id, out var names))
            {
                labels = labels.Concat(names);
            }

            return labels.Any(label => label.Length > 0 && folded.Contains(label.ToLowerInvariant(), StringComparison.Ordinal));
        }).ToList();
    }

    // Jev never invents a target: it can only confirm, narrow, or veto what the label match found.
    // Without a confident pick the PR #4 rule stands: send iff exactly one label hit.
    public static RouteDecision Decide(IReadOnlyList<PaneLabel> hits, JevPick? pick)
    {
        var confident = pick is not null && pick.Confidence >= JevConfidenceFloor;
        switch (hits.Count)
        {
            case 0:
                return confident && pick!.Pane is { } suggested
                    ? new RouteDecision(null, $"no pane matched; jev suggests {suggested}")
                    : new RouteDecision(null, "no pane matched");
            case 1:
                return confident && pick!.Pane != hits[0].Id
                    ? new RouteDecision(null, "jev rejected")
                    : new RouteDecision(hits[0].Id, "unique hit");
            default:
                return confident && pick!.Pane is { } chosen && hits.Any(hit => hit.Id == chosen)
                    ? new RouteDecision(chosen, "jev narrowed")
                    : new RouteDecision(null, $"{hits.Count} panes matched");
        }
    }

    // Question shape shared with scripts/pane_jev_probe.py: criteria keyed by pane id plus "none".
    public static string JevRequestBody(string dictation, IReadOnlyList<PaneLabel> panes, IReadOnlyDictionary<string, string[]>? agents = null)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("state");
            json.WriteString("dictation", dictation);
            json.WriteEndObject();
            json.WriteString("model", "jev-latest");
            json.WriteStartObject("questions");
            json.WriteStartObject("pane");
            json.WriteString("type", "choice");
            json.WriteString("instructions",
                "`dictation` is speech-to-text output, so pane names may be misheard or written in katakana. "
                + "Which listed tmux pane does the speaker address by name (window, title, command, or agent)?");
            json.WriteStartObject("criteria");
            foreach (var pane in panes)
            {
                json.WriteStartObject(pane.Id);
                json.WriteString("window", pane.Window);
                json.WriteString("title", pane.Title);
                json.WriteString("command", pane.Command);
                json.WriteStartArray("agents");
                if (agents is not null && agents.TryGetValue(pane.Id, out var names))
                {
                    foreach (var name in names)
                    {
                        json.WriteStringValue(name);
                    }
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteString("none", "The dictation does not name or clearly address any one of the listed panes.");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // `answers.pane` must be a choice answer naming a catalog id or "none"; anything else is null (no pick).
    public static JevPick? ParseJevPick(string body, IEnumerable<string> catalog)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Object
                || !answers.TryGetProperty("pane", out var answer)
                || answer.ValueKind != JsonValueKind.Object
                || !answer.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "choice"
                || !answer.TryGetProperty("choice", out var choice)
                || choice.ValueKind != JsonValueKind.String
                || !answer.TryGetProperty("confidence", out var confidence)
                || confidence.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var chosen = choice.GetString()!;
            if (chosen == "none")
            {
                return new JevPick(null, confidence.GetDouble());
            }

            return catalog.Contains(chosen) ? new JevPick(chosen, confidence.GetDouble()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Quote pairs tried in order: every 「…」 first, then every 『…』.
    private static readonly (char Open, char Close)[] SendBodyQuotes = [('「', '」'), ('『', '』')];

    // The text that goes to the pane: the interior of the first balanced, non-empty 「…」,
    // else of the first balanced, non-empty 『…』, trimmed. Null when there is none.
    // Callers must not fall back to the full dictation on null. Same rule as extractSendBody (Swift)
    // and the `extract` rows of tests/parity/fixtures/pane_route.json.
    public static string? ExtractSendBody(string dictation)
    {
        foreach (var (open, close) in SendBodyQuotes)
        {
            var depth = 0;
            var start = 0;
            for (var index = 0; index < dictation.Length; index++)
            {
                var ch = dictation[index];
                if (ch == open)
                {
                    if (depth == 0)
                    {
                        start = index + 1;
                    }

                    depth++;
                }
                else if (ch == close && depth > 0)
                {
                    depth--;
                    if (depth == 0)
                    {
                        var body = dictation[start..index].Trim();
                        if (body.Length > 0)
                        {
                            return body;
                        }
                    }
                }
            }
        }

        return null;
    }

    // Trailing cues for unquoted body candidates (longest first).
    private static readonly string[] SendBodyCues =
    [
        "を送信して", "と送信して", "を入力して", "と入力して",
        "を送って", "と送って", "に送って", "を貼って", "と貼って",
    ];

    private static readonly string[] SendBodyParticles = ["の pane に", "に", "へ"];

    // Deterministic unquoted body candidates. Never includes the full (trimmed) dictation.
    // Labels are hit / catalog strings present in the dictation. No Jev. Same rule as
    // sendBodyCandidates (Swift) and the `candidates` rows of pane_route.json.
    public static IReadOnlyList<string> SendBodyCandidates(string dictation, IReadOnlyList<string> labels)
    {
        var full = dictation.Trim();
        if (full.Length == 0)
        {
            return [];
        }

        var outList = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string raw)
        {
            var t = raw.Trim();
            if (t.Length == 0 || t == full || !seen.Add(t))
            {
                return;
            }

            outList.Add(t);
        }

        var bestStart = -1;
        var bestLen = 0;
        foreach (var label in labels)
        {
            if (string.IsNullOrEmpty(label))
            {
                continue;
            }

            var index = dictation.IndexOf(label, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            if (bestStart < 0 || index < bestStart || (index == bestStart && label.Length > bestLen))
            {
                bestStart = index;
                bestLen = label.Length;
            }
        }

        if (bestStart >= 0)
        {
            Add(dictation[(bestStart + bestLen)..]);
        }

        string? cueStripped = null;
        foreach (var cue in SendBodyCues.OrderByDescending(c => c.Length))
        {
            if (!full.EndsWith(cue, StringComparison.Ordinal))
            {
                continue;
            }

            cueStripped = full[..^cue.Length].Trim();
            Add(cueStripped);
            break;
        }

        if (cueStripped is not null)
        {
            var bestEnd = -1;
            foreach (var particle in SendBodyParticles)
            {
                var start = 0;
                while (true)
                {
                    var index = cueStripped.IndexOf(particle, start, StringComparison.Ordinal);
                    if (index < 0)
                    {
                        break;
                    }

                    var end = index + particle.Length;
                    if (end > bestEnd)
                    {
                        bestEnd = end;
                    }

                    start = index + 1;
                }
            }

            if (bestEnd > 0)
            {
                Add(cueStripped[bestEnd..]);
            }
        }

        return outList;
    }

    // Question shape for body Choice: criteria keyed by c0..cN-1 (candidate text) plus "none".
    public static string JevBodyRequestBody(string dictation, IReadOnlyList<string> candidates)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("state");
            json.WriteString("dictation", dictation);
            json.WriteStartObject("candidates");
            for (var i = 0; i < candidates.Count; i++)
            {
                json.WriteString($"c{i}", candidates[i]);
            }

            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteString("model", "jev-latest");
            json.WriteStartObject("questions");
            json.WriteStartObject("body");
            json.WriteString("type", "choice");
            json.WriteString("instructions",
                "`dictation` is speech-to-text. Pick which listed candidate is the literal text to type into the pane. "
                + "Do not invent wording; choose only from the candidates, or none.");
            json.WriteStartObject("criteria");
            for (var i = 0; i < candidates.Count; i++)
            {
                json.WriteString($"c{i}", candidates[i]);
            }

            json.WriteString("none", "None of these candidates is the literal text to type into the pane.");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // `answers.body` must be a choice naming cK in catalog or "none"; anything else is null.
    public static JevBodyPick? ParseJevBodyPick(string body, IEnumerable<string> catalog)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Object
                || !answers.TryGetProperty("body", out var answer)
                || answer.ValueKind != JsonValueKind.Object
                || !answer.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "choice"
                || !answer.TryGetProperty("choice", out var choice)
                || choice.ValueKind != JsonValueKind.String
                || !answer.TryGetProperty("confidence", out var confidence)
                || confidence.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            var chosen = choice.GetString()!;
            if (chosen == "none")
            {
                return new JevBodyPick(null, confidence.GetDouble());
            }

            return catalog.Contains(chosen) ? new JevBodyPick(chosen, confidence.GetDouble()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Quote extract first; else candidates + injected body pick (no HTTP).
    public static SendBodyResult ResolveSendBody(string dictation, IReadOnlyList<string> labels, JevBodyPick? bodyPick)
    {
        var quoted = ExtractSendBody(dictation);
        if (quoted is not null)
        {
            return new SendBodyResult(quoted, "quoted");
        }

        var candidates = SendBodyCandidates(dictation, labels);
        if (candidates.Count == 0)
        {
            return new SendBodyResult(null, "no candidates");
        }

        if (bodyPick is null)
        {
            return new SendBodyResult(null, "no body pick");
        }

        if (bodyPick.Confidence < JevConfidenceFloor)
        {
            return new SendBodyResult(null, "jev body low confidence");
        }

        if (bodyPick.Choice is null)
        {
            return new SendBodyResult(null, "jev body none");
        }

        var choice = bodyPick.Choice;
        if (choice.Length < 2
            || choice[0] != 'c'
            || !int.TryParse(choice.AsSpan(1), out var index)
            || index < 0
            || index >= candidates.Count
            || choice != $"c{index}")
        {
            return new SendBodyResult(null, "jev body rejected");
        }

        return new SendBodyResult(candidates[index], "jev body");
    }


    // Non-empty title / window / command / agent tokens from panes that already hit.
    public static IReadOnlyList<string> LabelsForHits(IReadOnlyList<PaneLabel> hits, IReadOnlyDictionary<string, string[]>? agents = null)
    {
        var labels = new List<string>();
        foreach (var pane in hits)
        {
            foreach (var label in new[] { pane.Title, pane.Window, pane.Command })
            {
                if (label.Length > 0)
                {
                    labels.Add(label);
                }
            }

            if (agents is not null && agents.TryGetValue(pane.Id, out var names))
            {
                foreach (var name in names)
                {
                    if (!string.IsNullOrEmpty(name))
                    {
                        labels.Add(name);
                    }
                }
            }
        }

        return labels;
    }

    // Early body resolve for a chosen pane: quoted or no-candidates finish without HTTP.
    // NeedJev means the router must call body Choice, then CompleteBodyResolve.
    public static (bool NeedJev, SendBodyResult? Early, IReadOnlyList<string> Candidates) BeginBodyResolve(
        string dictation,
        IReadOnlyList<string> labels)
    {
        if (ExtractSendBody(dictation) is { } quoted)
        {
            return (false, new SendBodyResult(quoted, "quoted"), []);
        }

        var candidates = SendBodyCandidates(dictation, labels);
        if (candidates.Count == 0)
        {
            return (false, new SendBodyResult(null, "no candidates"), candidates);
        }

        return (true, null, candidates);
    }

    // After body Jev HTTP: status is ok / off / error. Never returns the full dictation.
    public static SendBodyResult CompleteBodyResolve(
        string dictation,
        IReadOnlyList<string> labels,
        JevBodyPick? pick,
        string status)
    {
        if (status == "off")
        {
            return new SendBodyResult(null, "no body pick");
        }

        if (status == "error")
        {
            return new SendBodyResult(null, "jev body error");
        }

        return ResolveSendBody(dictation, labels, pick);
    }

    // A chosen pane without a send body is skipped; the full dictation is never sent or pasted instead.
    public static RouteDecision RequireSendBody(RouteDecision decision, string? body) =>
        decision.Pane is { } pane && body is null
            ? new RouteDecision(null, $"no send body for {pane}; not sending", SuppressFallback: true)
            : decision;

    // Pane still set means RequireSendBody kept a body; send-keys failed → SendFailed carries that same body
    // so the handoff pastes it (quoted or body-Jev), never the full text and never a re-extract.
    public static RouteResult Disposition(RouteDecision decision, bool sent, string? body = null) =>
        sent ? RouteResult.Sent
        : decision.SuppressFallback ? RouteResult.SkippedNoBody
        : decision.Pane is not null ? RouteResult.SendFailed(body)
        : RouteResult.NotRouted;

    // `%2@0.87`, `none@0.91`; the adapter writes `off` or `error` itself.
    public static string JevField(JevPick pick) =>
        $"{pick.Pane ?? "none"}@{pick.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}";

    // Sibling observability line (measurement only). Cap cand at 8; title/cmd ≤24; key ≤16.
    // Does not change routing. Never logs the full dictated body.
    public const int ScanCandCap = 8;
    public const int ScanFieldMax = 24;
    public const int ScanKeyMax = 16;

    public static string ScanLog(IReadOnlyList<PaneLabel> panes, string matchKey)
    {
        var n = panes.Count;
        var parts = new List<string>(Math.Min(n, ScanCandCap));
        for (var i = 0; i < n && i < ScanCandCap; i++)
        {
            var p = panes[i];
            parts.Add($"{p.Id}:{SanitizeScanToken(p.Command, ScanFieldMax)}:{SanitizeScanToken(p.Title, ScanFieldMax)}");
        }

        return $"tmux: scan panes={n} cand=[{string.Join(" ", parts)}] key=\"{SanitizeScanToken(matchKey, ScanKeyMax)}\"";
    }

    // Space-splitable cand entries: collapse whitespace and strip separators from fields.
    public static string SanitizeScanToken(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || max <= 0)
        {
            return "";
        }

        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var c in value)
        {
            if (sb.Length >= max)
            {
                break;
            }

            if (char.IsWhiteSpace(c) || c is '[' or ']' or '"' or ':')
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    // One log line per dictation, e.g. `tmux: hits=2 jev=%2@0.87 body=quoted -> send %2 (jev narrowed)`.
    public static string LogLine(int hits, string jevField, RouteDecision decision, string bodySource = "-") =>
        decision.Pane is { } pane
            ? $"tmux: hits={hits} jev={jevField} body={bodySource} -> send {pane} ({decision.Reason})"
            : $"tmux: hits={hits} jev={jevField} body={bodySource} -> skip ({decision.Reason})";
}
