namespace VoiceSwitch.Windows.Tray;

// Test stub: the real WindowFocus needs WinForms (Windows Desktop). Slice A proofs run on Linux.
public static class WindowFocus
{
    public static nint Foreground() => 0;

    public static void RestoreIfSuperwhisperForeground(nint target)
    {
    }

    public static bool Paste(string text, nint target) => false;
}
