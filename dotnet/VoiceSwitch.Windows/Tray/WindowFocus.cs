using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Text;
using VoiceSwitch.Windows.Core;

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
        if (target == 0 || !OperatingSystem.IsWindows() || !IsWindow(target)) return false;
        var pasted = false;
        var thread = new Thread(() =>
        {
            string? previous = null;
            uint? ownedClipboard = null;
            try
            {
                var input = InputActivity.LastInputTick();
                if (input is null || InputActivity.IdleSeconds() is not >= 1) return;
                var foreground = GetForegroundWindow();
                if (foreground == 0) return;
                if (foreground != target && !ForceForeground(GetWindowThreadProcessId(foreground, out _), target).Restored) return;
                var data = Clipboard.GetDataObject();
                var formats = data?.GetFormats(false) ?? [];
                var restorable = formats.All(format => format is "UnicodeText" or "Text" or "System.String" or "Locale" or "OEMText");
                if (!restorable) return;
                previous = Clipboard.ContainsText() ? Clipboard.GetText() : null;
                if (formats.Length != 0 && previous is null) return;
                if (!CanPaste(target, input.Value, true)) return;
                Clipboard.SetText(text);
                ownedClipboard = GetClipboardSequenceNumber();
                if (!CanPaste(target, input.Value, true)) return;
                SendKeys.SendWait("^v");
                pasted = true;
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                Log.Info("dictation paste: clipboard or target unavailable; retained in history");
            }
            finally
            {
                if (ownedClipboard is { } sequence && GetClipboardSequenceNumber() == sequence)
                {
                    try
                    {
                        if (previous is null) Clipboard.Clear(); else Clipboard.SetText(previous);
                    }
                    catch (ExternalException) { Log.Info("dictation paste: clipboard restore failed"); }
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return pasted;
    }

    private static bool CanPaste(nint target, uint input, bool clipboardRestorable)
    {
        var focused = GetForegroundWindow() == target;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(target, out _), ref info) || info.Focus == 0) return false;
        var name = new StringBuilder(256);
        if (GetClassName(info.Focus, name, name.Capacity) == 0) return false;
        var style = GetWindowLong(info.Focus, -16);
        var plain = (style & 0x40000000) != 0 && name.ToString().Equals("Edit", StringComparison.OrdinalIgnoreCase)
            && (style & 0x20) == 0 && GetAncestor(info.Focus, 2) == target;
        var context = ImmGetContext(info.Focus);
        var compositionClear = context == 0;
        if (context != 0)
        {
            try { compositionClear = ImmGetCompositionString(context, 0x0008, 0, 0) == 0; }
            finally { ImmReleaseContext(info.Focus, context); }
        }
        return new PasteSafety(focused, plain, compositionClear, clipboardRestorable,
            InputActivity.LastInputTick() == input).AllowsPaste;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("imm32.dll")] private static extern nint ImmGetContext(nint window);
    [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(nint window, nint context);
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)] private static extern int ImmGetCompositionString(nint context, uint index, nint buffer, uint length);

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
