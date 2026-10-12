using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

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
    private static readonly Regex Fields = new(@"\brule=(strict|relaxed_d2|followup|auto_b)\s+inputIdleSeconds=(-?[0-9]+(?:\.[0-9]+)?)(?=\s|$)");
    [JsonPropertyName("rules")]
    public Dictionary<string, int> Rules { get; } = new();
    [JsonPropertyName("contexts")]
    public Dictionary<string, int> Contexts { get; } = new();
    [JsonPropertyName("miss_contexts")]
    public Dictionary<string, int> MissContexts { get; } = new();
    [JsonPropertyName("missing_fields")]
    public int MissingFields { get; private set; }
    [JsonPropertyName("miss_labels")]
    public int MissLabels { get; private set; }

    public void Observe(string line)
    {
        var miss = line.Contains("wake label: MISS ", StringComparison.Ordinal);
        if (miss) MissLabels++;
        if (!miss && !line.Contains("recognition: complete ", StringComparison.Ordinal)) return;
        var fields = Fields.Matches(line).LastOrDefault();
        if (fields is null)
        {
            if (!miss) MissingFields++;
            return;
        }
        var name = fields.Groups[1].Value;
        var seconds = double.Parse(fields.Groups[2].Value, CultureInfo.InvariantCulture);
        var context = name + ":" + WakeOrigin.Context(seconds);
        if (miss)
        {
            MissContexts[context] = MissContexts.GetValueOrDefault(context) + 1;
            return;
        }
        Rules[name] = Rules.GetValueOrDefault(name) + 1;
        Contexts[context] = Contexts.GetValueOrDefault(context) + 1;
    }
}
