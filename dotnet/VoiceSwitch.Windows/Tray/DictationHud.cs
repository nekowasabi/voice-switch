using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

// Floating label at the top of the screen while a dictation is open, the Windows twin of MacApp.swift HUD.
// It never takes focus: Superwhisper pastes into whatever is foreground, and that must stay the user's window.
public sealed class DictationHud : Form
{
    private const int WsExTopMost = 0x8;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x8000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly Label label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        // Segoe UI Emoji renders the label emoji; plain Segoe UI would show a box.
        Font = new Font("Segoe UI Emoji", 12f, FontStyle.Bold),
        ForeColor = Color.White
    };
    private readonly System.Windows.Forms.Timer hide = new() { Interval = 1500 };

    private DictationPhase phase = DictationPhase.Idle;
    public event Action? HistoryRequested;

    public void HistoryAvailable()
    {
        // Wake-word dictation also keeps a copy in history, but its output was delivered, so only an automatic entry earns this label.
        if (phase is DictationPhase.Waiting or DictationPhase.Recording or DictationPhase.Followup
            || DictationHistory.Shared.Snapshot() is not [{ Origin.IsAutomatic: true }, ..]) return;
        Show(DictationPhase.Ended);
        label.Text = "📝👆";
    }

    public DictationHud()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(64, 40);
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(label);
        label.Click += (_, _) => HistoryRequested?.Invoke();
        var region = CreateRoundRectRgn(0, 0, Width, Height, 12, 12);
        Region = Region.FromHrgn(region);
        DeleteObject(region);
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            ShowWindow(Handle, SwHide);
        };
        // Created here, on the STA thread, so BeginInvoke from the runtime thread has a window to post to.
        _ = Handle;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopMost;
            return parameters;
        }
    }

    // Runs `action` on the UI thread; dropped once the form is gone (the runtime may outlive it briefly at shutdown).
    public void Post(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
        }
    }

    // UI thread only.
    public void Show(DictationPhase phase)
    {
        this.phase = phase;
        // A new phase replaces the pending auto-hide, like Mac's `shown` generation counter.
        hide.Stop();
        switch (phase)
        {
            case DictationPhase.Idle:
                ShowWindow(Handle, SwHide);
                return;
            case DictationPhase.Followup:
                label.Text = "🔁📝";
                label.ForeColor = Color.White;
                break;
            case DictationPhase.Waiting:
                label.Text = "🎤";
                label.ForeColor = Color.White;
                break;
            case DictationPhase.Recording:
                label.Text = "🔴";
                label.ForeColor = Color.FromArgb(255, 80, 80);
                break;
            case DictationPhase.Ended:
                // Mac `(.ended, nil)`: a recording dropped before its handoff never reports a delivery, so ⏳ hides after
                // 1.5 s unless the handoff's own Processing holds it.
                Deliver(DeliveryState.Processing);
                hide.Interval = 1500;
                hide.Start();
                return;
        }

        Place();
    }

    // UI thread only. Mirrors Mac HUD.deliver: ⏳ while Superwhisper works, ✅ for 1.5 s, ❌ for 3 s.
    // A late result never covers a new dictation, and 📝👆 survives a Done or Dropped (Mac sequence saved-survives-done).
    public void Deliver(DeliveryState state)
    {
        if (phase is DictationPhase.Waiting or DictationPhase.Recording or DictationPhase.Followup) return;
        if (label.Text == "📝👆" && state is DeliveryState.Done or DeliveryState.Dropped) return;
        hide.Stop();
        switch (state)
        {
            case DeliveryState.Processing:
                label.Text = "⏳";
                break;
            case DeliveryState.Done:
                label.Text = "✅";
                hide.Interval = 1500;
                hide.Start();
                break;
            case DeliveryState.Failed:
                label.Text = "❌";
                hide.Interval = 3000;
                hide.Start();
                break;
            default:
                if (label.Text == "⏳") ShowWindow(Handle, SwHide);
                return;
        }

        label.ForeColor = Color.White;
        Place();
    }

    private void Place()
    {
        // Why: Follow the foreground window instead of the pointer; dictation targets the focused display.
        var foreground = WindowFocus.Foreground();
        var screen = foreground != 0 ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 12);
        ShowWindow(Handle, SwShowNoActivate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hide.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);
}
