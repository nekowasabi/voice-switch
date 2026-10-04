using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceSwitch.Windows.Tray;

// Mirrors MacApp.swift handoff: opening the WAV brings Superwhisper to the front, and it skips auto-paste while frontmost,
// so for a moment after each handoff the window the user dictated into is put back in front.
public static class WindowFocus
{
    public static nint Foreground() => OperatingSystem.IsWindows() ? GetForegroundWindow() : 0;

    // UI thread (it owns a message queue, which AttachThreadInput needs on our side).
    public static void RestoreIfSuperwhisperForeground(nint target)
    {
        if (target == 0 || !OperatingSystem.IsWindows() || !IsWindow(target))
        {
            return;
        }

        var foreground = GetForegroundWindow();
        if (foreground == 0 || foreground == target)
        {
            return;
        }

        var thread = GetWindowThreadProcessId(foreground, out var processId);
        string name;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return;
        }

        if (!name.Equals("Superwhisper", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // AllowSetForegroundWindow can only be granted by the foreground process itself, and a synthetic Alt tap toggles
        // menu bars and can eat the paste; sharing Superwhisper's input queue is what lets SetForegroundWindow succeed.
        var attached = AttachThreadInput(GetCurrentThreadId(), thread, true);
        try
        {
            var restored = SetForegroundWindow(target);
            Log.Info($"dictation focus: foreground={name} target=0x{target:X} attached={attached} restored={restored}");
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(GetCurrentThreadId(), thread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
