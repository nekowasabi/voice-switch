namespace VoiceSwitch.Windows.Core;

public static class HistoryHotkey
{
    // Snapshot is newest first and already prunes expired entries, so an expired entry is never copied.
    public static string? NewestText(DictationHistory history) =>
        history.Snapshot() is [var newest, ..] ? newest.Text : null;
}
