namespace VoiceSwitch.Windows.Core;

public sealed record NoWakeWordConfig(bool Followup = false, bool AutoIdle = false, bool RelaxedReadingDistance2 = false,
    int FollowupMs = 8000, int IdleMs = 3000, bool FilterInterjections = false)
{
    public bool Valid => FollowupMs is >= 1000 and <= 30000 && IdleMs is >= 1000 and <= 60000;
    public bool Enabled => Followup || AutoIdle || RelaxedReadingDistance2;
}

public readonly record struct AutomaticTranscriptPolicy(bool FilterInterjections = false)
{
    public bool Rejects(string text, WakeOrigin origin)
    {
        if (!FilterInterjections || !origin.IsAutomatic) return false;
        var candidate = text.TrimStart();
        var end = candidate.Length;
        while (end > 0 && (char.IsWhiteSpace(candidate[end - 1]) || "。.!！?？".Contains(candidate[end - 1]))) end--;
        return candidate.AsSpan(0, end).SequenceEqual("あっ") || candidate.AsSpan(0, end).SequenceEqual("ああ");
    }
}

public static class NoWakePolicy
{
    public static WakeOrigin? OnSpeech(NoWakeWordConfig? options, double? idleSeconds, bool inFollowupWindow)
    {
        if (options is null || !options.Valid || idleSeconds is null or < 0 || !double.IsFinite(idleSeconds.Value)) return null;
        if (options.Followup && inFollowupWindow) return new(WakeRule.Followup, idleSeconds);
        return options.AutoIdle && idleSeconds * 1000 >= options.IdleMs ? new(WakeRule.AutoB, idleSeconds) : null;
    }

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
