using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

// WH_KEYBOARD_LL shell around DictationHotkeys, the Windows twin of MacApp.swift Hotkeys.installTap.
// Installed only while a dictation is open so Superwhisper's shortcuts behave as if voice-switch were absent otherwise.
// Every member runs on the UI thread that owns `ui`; the hook callback runs there too because that thread installed it.
public sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const uint LlkhfInjected = 0x10;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly DictationHotkeys hotkeys;
    private readonly Control ui;
    // Held in a field so the GC never collects the delegate the hook still points at.
    private readonly LowLevelKeyboardProc callback;
    private readonly System.Windows.Forms.Timer release = new() { Interval = 2500 };
    private nint hook;
    private bool wanted;

    public KeyboardHook(DictationHotkeys hotkeys, Control ui)
    {
        this.hotkeys = hotkeys;
        this.ui = ui;
        callback = Callback;
        release.Tick += (_, _) => ReleaseIfPossible();
    }

    public void PhaseChanged(DictationPhase phase)
    {
        wanted = phase is DictationPhase.Waiting or DictationPhase.Recording;
        if (wanted)
        {
            Install();
        }
        else
        {
            ReleaseIfPossible();
        }
    }

    public void Dispose()
    {
        wanted = false;
        release.Dispose();
        if (hook != 0)
        {
            UnhookWindowsHookEx(hook);
            hook = 0;
        }
    }

    private void Install()
    {
        release.Stop();
        if (hook != 0)
        {
            return;
        }

        hook = SetWindowsHookEx(WhKeyboardLl, callback, GetModuleHandle(null), 0);
        Log.Info(hook == 0
            ? $"hotkey: keyboard hook install failed error={Marshal.GetLastWin32Error()}"
            : "hotkey: keyboard hook installed");
    }

    // The hook stays until the swallowed press has released its keyUp, so Superwhisper never sees half a press.
    private void ReleaseIfPossible()
    {
        if (wanted || hook == 0)
        {
            return;
        }

        if (!hotkeys.Releasable(Environment.TickCount64))
        {
            release.Start();
            return;
        }

        release.Stop();
        UnhookWindowsHookEx(hook);
        hook = 0;
        Log.Info("hotkey: keyboard hook released");
    }

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((info.Flags & LlkhfInjected) == 0)
            {
                var down = (int)wParam is WmKeyDown or WmSysKeyDown;
                var swallow = hotkeys.OnKey(info.VkCode, Modifiers(), down, Environment.TickCount64);
                if (!down)
                {
                    ui.BeginInvoke(ReleaseIfPossible);
                }

                if (swallow)
                {
                    return 1;
                }
            }
        }

        return CallNextHookEx(hook, code, wParam, lParam);
    }

    // GetAsyncKeyState, not GetKeyState: the hook runs before the message reaches a queue, so the queue state lags.
    private static KeyMods Modifiers()
    {
        var mods = KeyMods.None;
        if (Down(VkControl)) mods |= KeyMods.Control;
        if (Down(VkShift)) mods |= KeyMods.Shift;
        if (Down(VkMenu)) mods |= KeyMods.Alt;
        if (Down(VkLWin) || Down(VkRWin)) mods |= KeyMods.Win;
        return mods;
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
