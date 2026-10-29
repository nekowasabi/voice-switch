using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

public sealed class WakeLabelHotkey : NativeWindow, IDisposable
{
    private const int Id = 0x5653;
    private readonly bool registered;
    private readonly bool historyRegistered;
    private readonly bool startRegistered;
    private readonly bool copyRegistered;
    private readonly Action? startRecording;
    private readonly Action? showHistory;
    private readonly bool pauseRegistered;
    private readonly Action? togglePause;
    private readonly Action<string>? onAction;
    private readonly Dictionary<int, string> actionNames = new();
    public WakeLabelHotkey(Action? showHistory = null, Action? startRecording = null, KeyChord? pause = null, Action? togglePause = null,
        IReadOnlyList<(KeyChord Chord, string Name)>? actions = null, Action<string>? onAction = null)
    {
        this.showHistory = showHistory;
        this.startRecording = startRecording;
        this.togglePause = togglePause;
        this.onAction = onAction;
        CreateHandle(new CreateParams { Parent = new nint(-3) });
        if (pause is { } chord)
        {
            pauseRegistered = RegisterHotKey(Handle, Id + 4, 0x4000 | ModFlags(chord.Mods), (uint)chord.Vk);
            Log.Info(pauseRegistered ? $"pause hotkey: vk=0x{chord.Vk:X2} mods={chord.Mods}" : "pause hotkey unavailable: the key is already registered");
        }
        registered = RegisterHotKey(Handle, Id, 0x4000 | 0x2 | 0x4, 0x77);
        startRegistered = RegisterHotKey(Handle, Id + 2, 0x4000 | 0x2 | 0x4, 0x79);
        if (!startRegistered) Log.Info("manual wake hotkey unavailable: Ctrl+Shift+F10 is already registered");
        historyRegistered = RegisterHotKey(Handle, Id + 1, 0x4000 | 0x2 | 0x4, 0x78);
        if (!historyRegistered) Log.Info("history hotkey unavailable: Ctrl+Shift+F9 is already registered");
        copyRegistered = RegisterHotKey(Handle, Id + 3, 0x4000 | 0x2 | 0x4, 0x7A);
        if (!copyRegistered) Log.Info("history copy hotkey unavailable: Ctrl+Shift+F11 is already registered");
        if (!registered) Log.Info("wake MISS hotkey unavailable: Ctrl+Shift+F8 is already registered");
        var next = Id + 5;
        foreach (var (actionChord, name) in actions ?? [])
        {
            var ok = RegisterHotKey(Handle, next, 0x4000 | ModFlags(actionChord.Mods), (uint)actionChord.Vk);
            Log.Info(ok ? $"action hotkey: {name} vk=0x{actionChord.Vk:X2} mods={actionChord.Mods}" : $"action hotkey unavailable: {name}");
            if (ok) actionNames[next] = name;
            next++;
        }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == Id) WakeMissLabel.Record();
        if (message.Msg == 0x0312 && message.WParam == Id + 1) showHistory?.Invoke();
        if (message.Msg == 0x0312 && message.WParam == Id + 2) startRecording?.Invoke();
        if (message.Msg == 0x0312 && message.WParam == Id + 3) CopyNewestHistory();
        if (message.Msg == 0x0312 && message.WParam == Id + 4) togglePause?.Invoke();
        if (message.Msg == 0x0312 && actionNames.TryGetValue((int)message.WParam, out var action)) onAction?.Invoke(action);
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        if (historyRegistered) UnregisterHotKey(Handle, Id + 1);
        if (startRegistered) UnregisterHotKey(Handle, Id + 2);
        if (copyRegistered) UnregisterHotKey(Handle, Id + 3);
        if (pauseRegistered) UnregisterHotKey(Handle, Id + 4);
        foreach (var id in actionNames.Keys) UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
    // RegisterHotKey MOD_* bits differ from KeyMods.
    private static uint ModFlags(KeyMods mods) =>
        (mods.HasFlag(KeyMods.Alt) ? 0x1u : 0) | (mods.HasFlag(KeyMods.Control) ? 0x2u : 0)
        | (mods.HasFlag(KeyMods.Shift) ? 0x4u : 0) | (mods.HasFlag(KeyMods.Win) ? 0x8u : 0);
    // Copies only; pasting stays a manual step, as in the history window.
    private static void CopyNewestHistory()
    {
        if (HistoryHotkey.NewestText(DictationHistory.Shared) is not { } text) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.ExternalException) { Log.Info("history copy hotkey: clipboard unavailable"); }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);
}
