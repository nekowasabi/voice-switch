using System.Text;

namespace VoiceSwitch.Windows.Core;

public static class TextMatching
{
    private static readonly HashSet<char> Punctuation = new("、。,.!?！？「」");

    public static string Normalize(string value)
    {
        var chars = value.Where(c => !char.IsWhiteSpace(c) && !Punctuation.Contains(c));
        return new string(chars.ToArray());
    }

    // ー (U+30FC) is outside the folded range on purpose: MS-IME and SAPI both keep it in readings.
    public static string NormalizeReading(string value) =>
        Normalize(new string(value.Normalize(NormalizationForm.FormKC)
            .Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c)
            .ToArray()));

    public static int EditDistance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    public static RuntimeDecision Decide(string recognizedText, VoiceSwitchConfig config)
    {
        var text = Normalize(recognizedText);
        if (text.Length == 0)
        {
            return RuntimeDecision.Ignore(text);
        }

        if (config.WakeWords.Select(Normalize).Contains(text))
        {
            return RuntimeDecision.RunCommand(text, config.Command, "wake");
        }

        if ((config.StopWords ?? []).Select(Normalize).Contains(text))
        {
            if (string.IsNullOrWhiteSpace(config.StopCommand)
                || string.Equals(config.StopCommand.Trim(), PlatformDefaults.SuperwhisperToggle, StringComparison.OrdinalIgnoreCase))
            {
                return RuntimeDecision.Ignore(text, "stop command disabled");
            }

            return RuntimeDecision.RunCommand(text, config.StopCommand, "stop");
        }

        return RuntimeDecision.Ignore(text);
    }
}

public sealed record RuntimeDecision(string Kind, string Text, string? Command, string Reason)
{
    public static RuntimeDecision RunCommand(string text, string command, string reason) =>
        new("run-command", text, command, reason);

    public static RuntimeDecision Ignore(string text, string reason = "not a configured word") =>
        new("ignore", text, null, reason);
}

public static class PlatformDefaults
{
    public const string SuperwhisperToggle = "rundll32 url.dll,FileProtocolHandler superwhisper://record";
}
