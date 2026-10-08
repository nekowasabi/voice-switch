using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

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

        var (attached, restored) = ForceForeground(thread, target);
        Log.Info($"dictation focus: foreground={name} target=0x{target:X} attached={attached} restored={restored}");
    }

    // Used when Superwhisper runs a mode with auto-paste off and no pane took the dictation. Clipboard needs an STA
    // thread, and the wait before restoring it must not stall the HUD, so the paste runs on its own STA thread.
    public static bool Paste(string text, nint target)
    {
        if (target == 0 || !OperatingSystem.IsWindows() || !IsWindow(target))
        {
            return false;
        }

        var thread = new Thread(() =>
        {
            try
            {
                var previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                Clipboard.SetText(text);
                var foreground = GetForegroundWindow();
                if (foreground != 0 && foreground != target && !ForceForeground(GetWindowThreadProcessId(foreground, out _), target).Restored)
                {
                    Log.Info($"dictation paste: could not bring target=0x{target:X} to the front");
                }

                SendKeys.SendWait("^v");
                // Ctrl+V is only queued; the target reads the clipboard when it handles it. 1 s outlasts a busy target.
                Thread.Sleep(TimeSpan.FromSeconds(1));
                if (previous is not null && Clipboard.ContainsText() && Clipboard.GetText() == text)
                {
                    Clipboard.SetText(previous);
                }
            }
            catch (ExternalException ex)
            {
                Log.Info($"dictation paste: clipboard failed: {ex.Message}");
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    // AllowSetForegroundWindow can only be granted by the foreground process itself, and a synthetic Alt tap toggles
    // menu bars and can eat the paste; sharing the foreground thread's input queue is what lets SetForegroundWindow succeed.
    private static (bool Attached, bool Restored) ForceForeground(uint foregroundThread, nint target)
    {
        var attached = AttachThreadInput(GetCurrentThreadId(), foregroundThread, true);
        try
        {
            return (attached, SetForegroundWindow(target));
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(GetCurrentThreadId(), foregroundThread, false);
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
