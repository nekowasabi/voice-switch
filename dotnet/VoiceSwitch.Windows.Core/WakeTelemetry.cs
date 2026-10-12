using System.Globalization;
using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

public enum WakeRule { Strict }

public sealed record WakeOrigin(WakeRule Rule, double? InputIdleSeconds)
{
    public string RuleName => Rule switch
    {
        WakeRule.Strict => "strict",
        _ => throw new ArgumentOutOfRangeException(nameof(Rule))
    };
    public string Fields => $"rule={RuleName} inputIdleSeconds={FormatIdle(InputIdleSeconds)}";
    public static string FormatIdle(double? seconds) => seconds is >= 0 && double.IsFinite(seconds.Value)
        ? seconds.Value.ToString("0.000", CultureInfo.InvariantCulture) : "-1";
    public static string Context(double? seconds) => seconds is null or < 0 || !double.IsFinite(seconds.Value)
        ? "unknown" : seconds < 3 ? "after_input" : "idle";
}

public sealed class WakeTelemetrySummary
{
    private static readonly Regex Quoted = new("\"[^\"]*\"");
    private static readonly Regex Rule = new(@"(?:^|\s)rule=(strict)(?=\s|$)");
    private static readonly Regex Idle = new(@"(?:^|\s)inputIdleSeconds=(\S+)");
    public Dictionary<string, int> Rules { get; } = new();
    public Dictionary<string, int> Contexts { get; } = new();
    public int MissingFields { get; private set; }
    public int MissLabels { get; private set; }

    public void Observe(string line)
    {
        line = Quoted.Replace(line, "");
        if (line.Contains("wake label: MISS ", StringComparison.Ordinal)) MissLabels++;
        if (!line.Contains("recognition: complete ", StringComparison.Ordinal)) return;
        var rule = Rule.Match(line);
        var idle = Idle.Match(line);
        if (!rule.Success || !idle.Success)
        {
            MissingFields++;
            return;
        }
        var name = rule.Groups[1].Value;
        double? seconds = double.TryParse(idle.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : null;
        Rules[name] = Rules.GetValueOrDefault(name) + 1;
        var context = name + ":" + WakeOrigin.Context(seconds);
        Contexts[context] = Contexts.GetValueOrDefault(context) + 1;
    }
}
