using System.Text.RegularExpressions;

namespace VoiceSwitch.Windows.Core;

// One row per product whose wake action should replace the mic with an image.
// A new icon is another row, or an action's "icon" stem plus a png of that name. No new HUD phase.
public sealed record ActionIconRule(string Icon, string[] Names, string? Scheme = null, string? Host = null, string? Path = null);

public static class ActionIcons
{
    public static readonly ActionIconRule[] Builtin =
    [
        new("grok-bot", ["grok", "grokbot"], "grokbot", "app", "/v1/prompt"),
    ];

    private static readonly Regex Stem = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,40}$", RegexOptions.Compiled);

    public static bool IsStem(string? value) => value is not null && Stem.IsMatch(value);

    public static string? Resolve(string? name, string? url, string? configured = null)
    {
        if (IsStem(configured)) return configured;
        var compact = Compact(name);
        foreach (var rule in Builtin)
        {
            if (compact.Length > 0 && rule.Names.Contains(compact, StringComparer.OrdinalIgnoreCase)) return rule.Icon;
            if (Matches(url, rule)) return rule.Icon;
        }

        return null;
    }

    private static bool Matches(string? url, ActionIconRule rule)
    {
        if (rule.Scheme is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (!uri.Scheme.Equals(rule.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.Host is not null && !string.Equals(uri.Host, rule.Host, StringComparison.OrdinalIgnoreCase)) return false;
        return rule.Path is null || string.Equals(uri.AbsolutePath, rule.Path, StringComparison.Ordinal);
    }

    private static string Compact(string? name) =>
        (name ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
}
