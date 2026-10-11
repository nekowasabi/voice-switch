using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceSwitch.Windows.Tray;

public sealed class WakeLabelHotkey : NativeWindow, IDisposable
{
    private const int Id = 0x5653;
    private readonly bool registered;
    public WakeLabelHotkey()
    {
        CreateHandle(new CreateParams { Parent = new nint(-3) });
        registered = RegisterHotKey(Handle, Id, 0x4000 | 0x2 | 0x4, 0x77);
        if (!registered) Log.Info("wake MISS hotkey unavailable: Ctrl+Shift+F8 is already registered");
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == Id) WakeMissLabel.Record();
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        DestroyHandle();
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
