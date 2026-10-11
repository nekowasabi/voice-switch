using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceSwitch.Windows.Tray;

public sealed class WakeLabelHotkey : NativeWindow, IDisposable
{
    private const int Id = 0x5653;
    private readonly bool registered;
    private readonly bool historyRegistered;
    private readonly bool startRegistered;
    private readonly Action? startRecording;
    private readonly Action? showHistory;
    public WakeLabelHotkey(Action? showHistory = null, Action? startRecording = null)
    {
        this.showHistory = showHistory;
        this.startRecording = startRecording;
        CreateHandle(new CreateParams { Parent = new nint(-3) });
        registered = RegisterHotKey(Handle, Id, 0x4000 | 0x2 | 0x4, 0x77);
        startRegistered = RegisterHotKey(Handle, Id + 2, 0x4000 | 0x2 | 0x4, 0x79);
        if (!startRegistered) Log.Info("manual wake hotkey unavailable: Ctrl+Shift+F10 is already registered");
        historyRegistered = RegisterHotKey(Handle, Id + 1, 0x4000 | 0x2 | 0x4, 0x78);
        if (!historyRegistered) Log.Info("history hotkey unavailable: Ctrl+Shift+F9 is already registered");
        if (!registered) Log.Info("wake MISS hotkey unavailable: Ctrl+Shift+F8 is already registered");
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == Id) WakeMissLabel.Record();
        if (message.Msg == 0x0312 && message.WParam == Id + 1) showHistory?.Invoke();
        if (message.Msg == 0x0312 && message.WParam == Id + 2) startRecording?.Invoke();
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        if (historyRegistered) UnregisterHotKey(Handle, Id + 1);
        if (startRegistered) UnregisterHotKey(Handle, Id + 2);
        DestroyHandle();
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
