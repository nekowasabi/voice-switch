using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoiceSwitch.Windows.Core;

// Closed catalog row from `tmux list-panes`. Addressed only by pane id (`%0`), never by index.
public sealed record PaneLabel(string Id, string Window, string Title, string Command);

// Jev's answer to "which listed pane does the speaker address". Pane null is the "none" choice.
public sealed record JevPick(string? Pane, double Confidence);

public sealed record RouteDecision(string? Pane, string Reason);

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

    // `%2@0.87`, `none@0.91`; the adapter writes `off` or `error` itself.
    public static string JevField(JevPick pick) =>
        $"{pick.Pane ?? "none"}@{pick.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}";

    // One log line per dictation, e.g. `tmux: hits=2 jev=%2@0.87 -> send %2 (jev narrowed)`.
    public static string LogLine(int hits, string jevField, RouteDecision decision) =>
        decision.Pane is { } pane
            ? $"tmux: hits={hits} jev={jevField} -> send {pane} ({decision.Reason})"
            : $"tmux: hits={hits} jev={jevField} -> skip ({decision.Reason})";
}
