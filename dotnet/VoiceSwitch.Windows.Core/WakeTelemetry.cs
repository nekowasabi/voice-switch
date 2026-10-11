using System.Globalization;
using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

public enum WakeRule { Strict, RelaxedD2, Followup, AutoB }

public sealed record WakeOrigin(WakeRule Rule, double? InputIdleSeconds)
{
    public bool IsAutomatic => Rule != WakeRule.Strict;
    public string RuleName => Rule switch
    {
        WakeRule.Strict => "strict",
        WakeRule.RelaxedD2 => "relaxed_d2",
        WakeRule.Followup => "followup",
        WakeRule.AutoB => "auto_b",
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
    private static readonly Regex Rule = new(@"\brule=(strict|relaxed_d2|followup|auto_b)\b");
    private static readonly Regex Idle = new(@"\binputIdleSeconds=(-?\d+(?:\.\d+)?)\b");
    public Dictionary<string, int> Rules { get; } = new();
    public Dictionary<string, int> Contexts { get; } = new();
    public int MissingFields { get; private set; }
    public int MissLabels { get; private set; }

    public void Observe(string line)
    {
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
        var seconds = double.Parse(idle.Groups[1].Value, CultureInfo.InvariantCulture);
        Rules[name] = Rules.GetValueOrDefault(name) + 1;
        var context = name + ":" + WakeOrigin.Context(seconds);
        Contexts[context] = Contexts.GetValueOrDefault(context) + 1;
    }
}
