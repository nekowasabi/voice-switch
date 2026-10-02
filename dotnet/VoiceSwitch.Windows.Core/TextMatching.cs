namespace VoiceSwitch.Windows.Core;

public static class TextMatching
{
    private static readonly HashSet<char> Punctuation = new("、。,.!?！？「」");

    public static string Normalize(string value)
    {
        var chars = value.Where(c => !char.IsWhiteSpace(c) && !Punctuation.Contains(c));
        return new string(chars.ToArray());
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
            return RuntimeDecision.RunCommand(text, config.StopCommand ?? PlatformDefaults.SuperwhisperToggle, "stop");
        }

        return RuntimeDecision.Ignore(text);
    }
}

public sealed record RuntimeDecision(string Kind, string Text, string? Command, string Reason)
{
    public static RuntimeDecision RunCommand(string text, string command, string reason) =>
        new("run-command", text, command, reason);

    public static RuntimeDecision Ignore(string text) =>
        new("ignore", text, null, "not a configured word");
}

public static class PlatformDefaults
{
    public const string SuperwhisperToggle = "cmd /c start \"\" superwhisper://record";
}
