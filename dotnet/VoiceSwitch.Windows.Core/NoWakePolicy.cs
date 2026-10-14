namespace VoiceSwitch.Windows.Core;

public sealed record NoWakeWordConfig(bool Followup = false, bool AutoIdle = false, bool RelaxedReadingDistance2 = false,
    int FollowupMs = 8000, int IdleMs = 3000, bool FilterInterjections = false, int MinHandoffMs = 1000,
    IReadOnlyList<string>? IgnoredTranscripts = null)
{
    public bool Valid => FollowupMs is >= 1000 and <= 30000 && IdleMs is >= 1000 and <= 60000;
    public bool Enabled => Followup || AutoIdle || RelaxedReadingDistance2;
}

public readonly record struct AutomaticTranscriptPolicy(bool FilterInterjections = false, IReadOnlyList<string>? Ignored = null)
{
    public static AutomaticTranscriptPolicy From(NoWakeWordConfig? options) =>
        new(options?.FilterInterjections == true, options?.IgnoredTranscripts?.Select(Token).ToArray());

    public bool Rejects(string text, WakeOrigin origin)
    {
        if (!origin.IsAutomatic) return false;
        var token = Token(text);
        return (FilterInterjections && (token == "あっ" || token == "ああ")) || (Ignored?.Contains(token) ?? false);
    }

    // Outer whitespace and trailing 。.!！?？ only; anything inside the text means it said more than the word.
    private static string Token(string text)
    {
        var candidate = text.TrimStart();
        var end = candidate.Length;
        while (end > 0 && (char.IsWhiteSpace(candidate[end - 1]) || "。.!！?？".Contains(candidate[end - 1]))) end--;
        return candidate[..end];
    }
}

// Shape of a transcript for the log when the text itself stays private.
public static class TranscriptShape
{
    // Interjections SAPI returns for a breath, a cough or a laugh. Longest spelling first so the greedy match takes "あっ" before "あ".
    public static readonly string[] Fillers = ["あっ", "ああ", "あ", "うん", "はい", "ええ", "えー", "えっ", "え", "おお", "お", "んー", "ん", "はは", "は", "ふふ", "ふ", "笑"];

    // Letters and digits only: no punctuation, no spaces.
    public static int Length(string text) => text.Count(char.IsLetterOrDigit);

    // True when the letters of the text are nothing but a run of fillers.
    public static bool IsFiller(string text)
    {
        var core = string.Concat(text.Where(char.IsLetterOrDigit));
        var index = 0;
        while (index < core.Length)
        {
            var hit = Fillers.FirstOrDefault(filler => core.AsSpan(index).StartsWith(filler));
            if (hit is null) return false;
            index += hit.Length;
        }

        return core.Length > 0;
    }
}

public enum AutomaticRoute { Local, Superwhisper }

public static class NoWakePolicy
{
    public static WakeOrigin? OnSpeech(NoWakeWordConfig? options, double? idleSeconds, bool inFollowupWindow)
    {
        if (options is null || !options.Valid || idleSeconds is null or < 0 || !double.IsFinite(idleSeconds.Value)) return null;
        if (options.Followup && inFollowupWindow) return new(WakeRule.Followup, idleSeconds);
        return options.AutoIdle ? new(WakeRule.AutoB, idleSeconds) : null;
    }

    // Decided at submit, not at start: input within IdleMs means the user is still working, and handing the file to
    // Superwhisper would steal their focus, so the transcript stays local.
    public static AutomaticRoute AutomaticRoute(double? idleSeconds, NoWakeWordConfig options) =>
        idleSeconds is { } idle && double.IsFinite(idle) && idle * 1000 >= options.IdleMs
            ? Core.AutomaticRoute.Superwhisper : Core.AutomaticRoute.Local;

    public static WakePrefix? RelaxedWake(RecognizedUtterance recognition, IReadOnlyList<WakeWord> wakes)
    {
        if (recognition.Extent != RecognitionExtent.ClosedUtterance || recognition.HadRejectedSpeech
            || recognition.FromStopGrammar || string.IsNullOrWhiteSpace(recognition.Text)) return null;
        var reading = string.Concat(recognition.Lexemes.Select(run =>
            TextMatching.NormalizeReading(string.IsNullOrEmpty(run.Reading) ? run.Text : run.Reading)));
        if (!KanaOnly(reading)) return null;
        foreach (var wake in wakes.Where(wake => wake.Reading.Length is >= 5 and <= 9 && KanaOnly(wake.Reading)).OrderByDescending(wake => wake.Reading.Length))
        {
            if (TextMatching.EditDistance(reading, wake.Reading) == 2)
                return new WakePrefix(recognition.Source.End, null, wake, 2);
        }
        return null;
    }

    private static bool KanaOnly(string text) => text.Length > 0 && text.All(character => character is >= 'ぁ' and <= 'ゖ' or 'ー');
}
