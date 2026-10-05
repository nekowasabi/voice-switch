using System.Text.Json;

namespace VoiceSwitch.Windows.Core;

public enum DictationDelivery
{
    Pane,
    Paste,
    Superwhisper
}

// How the pane router disposed of a dictation. SkippedNoBody is not "unrouted":
// a pane was chosen but had no extractable body, so paste must not fall back to the full text.
public enum RouteDisposition
{
    Sent,
    SkippedNoBody,
    NotRouted
}

// Superwhisper keeps one modes\<file>.json per mode ({"key", "name", ...}) and the current one in preferences.json "activeMode".
public static class SuperwhisperModes
{
    // A key match anywhere wins over a name match, so a mode named like another mode's key cannot shadow it.
    public static string? ResolveKey(string wanted, IEnumerable<string> modeJsons)
    {
        var modes = modeJsons.Select(json => (Key: Field(json, "key"), Name: Field(json, "name")))
            .Where(mode => mode.Key is not null)
            .ToList();
        return modes.FirstOrDefault(mode => string.Equals(mode.Key, wanted, StringComparison.OrdinalIgnoreCase)).Key
            ?? modes.FirstOrDefault(mode => string.Equals(mode.Name, wanted, StringComparison.OrdinalIgnoreCase)).Key;
    }

    public static string? ActiveMode(string? preferencesJson) =>
        preferencesJson is null ? null : Field(preferencesJson, "activeMode");

    // With a no-auto-paste mode in use, a dictation no pane took would otherwise land nowhere.
    // SkippedNoBody must not Paste: that would be a full-text fallback after extract failure.
    public static DictationDelivery Decide(bool modeRequested, RouteDisposition route) =>
        route is RouteDisposition.Sent or RouteDisposition.SkippedNoBody ? DictationDelivery.Pane
        : modeRequested ? DictationDelivery.Paste
        : DictationDelivery.Superwhisper;

    private static string? Field(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text
                    ? text
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
