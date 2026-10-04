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
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x8000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly Label label = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        // Segoe UI Emoji renders the microphone glyph; plain Segoe UI would show a box.
        Font = new Font("Segoe UI Emoji", 12f, FontStyle.Bold),
        ForeColor = Color.White
    };
    private readonly System.Windows.Forms.Timer hide = new() { Interval = 1500 };

    public DictationHud()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(160, 40);
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(label);
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
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTransparent | WsExTopMost;
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
        // A new phase replaces the pending auto-hide, like Mac's `shown` generation counter.
        hide.Stop();
        switch (phase)
        {
            case DictationPhase.Idle:
                ShowWindow(Handle, SwHide);
                return;
            case DictationPhase.Waiting:
                label.Text = "🎙 どうぞ";
                label.ForeColor = Color.White;
                break;
            case DictationPhase.Recording:
                label.Text = "● 録音中";
                label.ForeColor = Color.FromArgb(255, 80, 80);
                break;
            case DictationPhase.Ended:
                label.Text = "■ 録音終了";
                label.ForeColor = Color.White;
                hide.Start();
                break;
        }

        // The screen with the mouse, so it shows where the user is looking on multi-monitor setups.
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
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
