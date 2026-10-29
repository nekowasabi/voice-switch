namespace VoiceSwitch.Windows.Core;

/// <summary>Built-in surface-to-reading table used when no IME reading was supplied.</summary>
internal static class WakeReadingFallback
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["音声入力"] = "おんせいにゅうりょく",
        ["音声"] = "おんせい",
        ["音声入る"] = "おんせいはいる",
        ["音声に入る"] = "おんせいにはいる",
        ["音声に入るよ"] = "おんせいにはいるよ",
        ["音声によって"] = "おんせいによって",
        ["インプット"] = "いんぷっと",
    };

    public static string? For(string normalizedText) =>
        Table.TryGetValue(normalizedText, out var reading) ? reading : null;
}
