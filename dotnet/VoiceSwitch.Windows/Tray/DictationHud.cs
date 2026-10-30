using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows.Tray;

// The nine HUD states, mirrored by `HUDLabel` in MacApp.swift. They are vector glyphs now, not emoji, so the two
// platforms show the same artwork instead of whatever emoji font each OS ships.
public enum HudGlyph
{
    Followup,
    Waiting,
    Recording,
    Processing,
    Done,
    Failed,
    Saved,
    Paused,
    Resumed
}

// Floating label at the top of the screen while a dictation is open, the Windows twin of MacApp.swift HUD.
// It never takes focus: Superwhisper pastes into whatever is foreground, and that must stay the user's window.
public sealed class DictationHud : Form
{
    private const int WsExTopMost = 0x8;
    private const int WsExToolWindow = 0x80;
    private const int WsExNoActivate = 0x8000000;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly GlyphPanel glyph = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer hide = new() { Interval = 1500 };

    private DictationPhase phase = DictationPhase.Idle;
    public event Action? HistoryRequested;

    /// The glyph currently on screen, or null when the HUD is hidden. `HUDScreenProbe` and the parity check read it.
    /// Visibility is read from the native window, because the HUD hides through `ShowWindow`, which `Form.Visible` does not track.
    public HudGlyph? VisibleGlyph => IsWindowVisible(Handle) ? glyph.Glyph : null;

    public void HistoryAvailable()
    {
        // Wake-word dictation also keeps a copy in history, but its output was delivered, so only an automatic entry earns this label.
        if (phase is DictationPhase.Waiting or DictationPhase.Recording or DictationPhase.Followup
            || DictationHistory.Shared.Snapshot() is not [{ Origin.IsAutomatic: true }, ..]) return;
        Show(DictationPhase.Ended);
        glyph.Glyph = HudGlyph.Saved;
    }

    // UI thread only. The icon an action's wake word shows in place of the mic, so the user sees which mode opened.
    // An exe or dll gives its own icon; anything else is read as an image. A file that fails to load keeps the mic.
    public void WaitingIcon(string? path)
    {
        if (path is null)
        {
            glyph.WaitingImage = null;
            return;
        }

        var full = Environment.ExpandEnvironmentVariables(path);
        if (!icons.TryGetValue(full, out var image))
        {
            try
            {
                image = Path.GetExtension(full).ToLowerInvariant() is ".exe" or ".dll"
                    ? Icon.ExtractAssociatedIcon(full)?.ToBitmap()
                    : Image.FromFile(full);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or OutOfMemoryException)
            {
                Log.Info($"hud: action icon {full} could not be loaded: {ex.Message}");
            }

            icons[full] = image;
        }

        glyph.WaitingImage = image;
    }

    private readonly Dictionary<string, Image?> icons = new(StringComparer.OrdinalIgnoreCase);

    // Pause/resume confirmation, hidden after 1.5 s like Done; the hotkey otherwise gives no feedback.
    public void Notify(bool paused)
    {
        Show(DictationPhase.Ended);
        glyph.Glyph = paused ? HudGlyph.Paused : HudGlyph.Resumed;
    }

    public DictationHud()
    {
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(64, 40);
        BackColor = Color.FromArgb(32, 32, 32);
        Controls.Add(glyph);
        glyph.Click += (_, _) => HistoryRequested?.Invoke();
        var region = CreateRoundRectRgn(0, 0, Width, Height, 12, 12);
        Region = Region.FromHrgn(region);
        DeleteObject(region);
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            ShowWindow(Handle, SwHide);
            glyph.Stop();
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
                glyph.Stop();
                return;
            case DictationPhase.Followup:
                glyph.Glyph = HudGlyph.Followup;
                break;
            case DictationPhase.Waiting:
                glyph.Glyph = HudGlyph.Waiting;
                break;
            case DictationPhase.Recording:
                glyph.Glyph = HudGlyph.Recording;
                break;
            case DictationPhase.Ended:
                // Mac `(.ended, nil)`: a recording dropped before its handoff never reports a delivery, so Processing hides after
                // 1.5 s unless the handoff's own Processing holds it.
                Deliver(DeliveryState.Processing);
                hide.Interval = 1500;
                hide.Start();
                return;
        }

        Place();
    }

    // UI thread only. Mirrors Mac HUD.deliver: Processing while Superwhisper works, Done for 1.5 s, Failed for 3 s.
    // A late result never covers a new dictation, and Saved survives a Done or Dropped (Mac sequence saved-survives-done).
    public void Deliver(DeliveryState state)
    {
        if (phase is DictationPhase.Waiting or DictationPhase.Recording or DictationPhase.Followup) return;
        if (glyph.Glyph == HudGlyph.Saved && state is DeliveryState.Done or DeliveryState.Dropped) return;
        hide.Stop();
        switch (state)
        {
            case DeliveryState.Processing:
                glyph.Glyph = HudGlyph.Processing;
                break;
            case DeliveryState.Done:
                glyph.Glyph = HudGlyph.Done;
                hide.Interval = 1500;
                hide.Start();
                break;
            case DeliveryState.Failed:
                glyph.Glyph = HudGlyph.Failed;
                hide.Interval = 3000;
                hide.Start();
                break;
            default:
                if (glyph.Glyph == HudGlyph.Processing)
                {
                    ShowWindow(Handle, SwHide);
                    glyph.Stop();
                }

                return;
        }

        Place();
    }

    private void Place()
    {
        // Why: Follow the foreground window instead of the pointer; dictation targets the focused display.
        var foreground = WindowFocus.Foreground();
        var screen = foreground != 0 ? Screen.FromHandle(foreground) : Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + 12);
        glyph.Resume();
        ShowWindow(Handle, SwShowNoActivate);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hide.Dispose();
            glyph.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    // Draws one HudGlyph in a 24x24 design space, the same geometry as HUDGlyphView in MacApp.swift.
    // Recording and Processing animate at 30 fps; every other glyph is static, so a hidden HUD costs nothing.
    private sealed class GlyphPanel : Panel
    {
        private readonly System.Windows.Forms.Timer frames = new() { Interval = 33 };
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private HudGlyph current = HudGlyph.Waiting;

        public GlyphPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            // Opaque, matching the form: a transparent child panel re-paints the parent on every animation frame.
            BackColor = Color.FromArgb(32, 32, 32);
            frames.Tick += (_, _) => Invalidate();
        }

        // Drawn instead of the mic while Waiting; null draws the mic.
        public Image? WaitingImage
        {
            get => waitingImage;
            set
            {
                waitingImage = value;
                Invalidate();
            }
        }

        private Image? waitingImage;

        public HudGlyph Glyph
        {
            get => current;
            set
            {
                if (current == value) return;
                current = value;
                clock.Restart();
                if (Animated(current))
                {
                    frames.Start();
                }
                else
                {
                    frames.Stop();
                }

                Invalidate();
            }
        }

        // Animation must stop with the pill: a hidden HUD that keeps a 30 fps timer burns CPU for nothing.
        public void Stop() => frames.Stop();

        // Restarts the animation for a glyph that was already current when the HUD hid, so a second dictation pulses again.
        public void Resume()
        {
            if (!Animated(current)) return;
            // A glyph that reappears after the HUD hid starts its animation from zero, as on Mac.
            clock.Restart();
            frames.Start();
        }

        private static bool Animated(HudGlyph value) => value is HudGlyph.Recording or HudGlyph.Processing;

        protected override void Dispose(bool disposing)
        {
            if (disposing) frames.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var side = Math.Min(Width, Height);
            if (side <= 0) return;
            if (current == HudGlyph.Waiting && waitingImage is { } image)
            {
                // The same 22 px box the Mac glyph view uses, in screen pixels, not the Y-up design space.
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, (Width - 22) / 2, (Height - 22) / 2, 22, 22);
                return;
            }
            // Y-up design space, matching HUDGlyphView in MacApp.swift: one glyph table, one meaning for "above".
            // The matrix maps (x, y) in 0..24 to a side-sized box centered in the panel, with y growing upwards.
            var s = side / 24f;
            using var transform = new Matrix(s, 0, 0, -s, (Width - side) / 2f, (Height - side) / 2f + side);
            graphics.Transform = transform;
            Draw(graphics);
        }

        private static Color ColorFor(HudGlyph value) => value switch
        {
            HudGlyph.Waiting => Color.FromArgb(166, 242, 242, 244),
            HudGlyph.Recording or HudGlyph.Failed => Color.FromArgb(255, 69, 58),
            HudGlyph.Done or HudGlyph.Saved => Color.FromArgb(48, 209, 88),
            HudGlyph.Processing => Color.FromArgb(230, 242, 242, 244),
            HudGlyph.Paused => Color.FromArgb(142, 142, 147),
            HudGlyph.Followup => Color.FromArgb(204, 242, 242, 244),
            _ => Color.FromArgb(191, 242, 242, 244)
        };

        private void Draw(Graphics g)
        {
            var color = ColorFor(current);
            // Stroke widths match MacApp.swift glyph for glyph; the outline is the design, so it must not drift.
            var width = current switch
            {
                HudGlyph.Waiting => 1.6f,
                HudGlyph.Resumed or HudGlyph.Followup => 1.7f,
                HudGlyph.Done or HudGlyph.Failed => 1.9f,
                HudGlyph.Processing => 2.2f,
                _ => 1.8f
            };
            using var pen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            using var fill = new SolidBrush(color);
            switch (current)
            {
                case HudGlyph.Waiting:
                    DrawMic(g, pen);
                    break;
                case HudGlyph.Resumed:
                    // A play triangle: "listening is back on", next to the pause bars it toggles with.
                    g.DrawPolygon(pen, [new PointF(9, 17), new PointF(17.5f, 12), new PointF(9, 7)]);
                    break;
                case HudGlyph.Recording:
                {
                    var elapsed = clock.Elapsed.TotalSeconds % 1.4 / 1.4;
                    var radius = 5.5f + 5f * (float)elapsed;
                    using var halo = new SolidBrush(Color.FromArgb((int)(102 * (1 - elapsed)), color));
                    g.FillEllipse(halo, 12 - radius, 12 - radius, radius * 2, radius * 2);
                    g.FillEllipse(fill, 6.5f, 6.5f, 11, 11);
                    break;
                }

                case HudGlyph.Processing:
                {
                    using var track = new Pen(Color.FromArgb(46, 242, 242, 244), 2f);
                    g.DrawEllipse(track, 3.5f, 3.5f, 17, 17);
                    // Arc points are built here, not handed to DrawArc, so the spin follows the Mac's angle reading
                    // exactly instead of depending on GDI+'s own direction convention.
                    var angle = clock.Elapsed.TotalSeconds / 1.1 * 360;
                    g.DrawLines(pen, Arc(12, 12, 8.5f, (float)(angle - 90), (float)(angle + 10)));
                    break;
                }

                case HudGlyph.Done:
                    g.DrawLines(pen, [new PointF(6, 11.5f), new PointF(9.7f, 7.8f), new PointF(18, 16.2f)]);
                    break;
                case HudGlyph.Saved:
                    // A check with an underline: the result was also filed into history.
                    g.DrawLines(pen, [new PointF(6, 13), new PointF(9.5f, 9.5f), new PointF(17, 17)]);
                    using (var rule = new Pen(color, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(rule, 8, 5.5f, 16, 5.5f);
                    }

                    break;
                case HudGlyph.Failed:
                    g.DrawLine(pen, 6.5f, 17.5f, 17.5f, 6.5f);
                    g.DrawLine(pen, 17.5f, 17.5f, 6.5f, 6.5f);
                    break;
                case HudGlyph.Paused:
                    FillRounded(g, fill, 8.7f, 6, 2.4f, 12, 1.2f);
                    FillRounded(g, fill, 12.9f, 6, 2.4f, 12, 1.2f);
                    break;
                case HudGlyph.Followup:
                {
                    // A loop arrow: the follow-up window is open without a wake word. The head sits on the arc's end
                    // (330°), as on Mac, so it reads as an arrow rather than a stray tick.
                    g.DrawLines(pen, Arc(12, 12, 7, 50, 330));
                    var radians = 330 * MathF.PI / 180;
                    var tip = new PointF(12 + 7 * MathF.Cos(radians), 12 + 7 * MathF.Sin(radians));
                    var travel = new PointF(-MathF.Sin(radians), MathF.Cos(radians));
                    var normal = new PointF(MathF.Cos(radians), MathF.Sin(radians));
                    var back = new PointF(tip.X - travel.X * 3.4f, tip.Y - travel.Y * 3.4f);
                    g.DrawLines(pen, [
                        new PointF(back.X + normal.X * 2.4f, back.Y + normal.Y * 2.4f),
                        tip,
                        new PointF(back.X - normal.X * 2.4f, back.Y - normal.Y * 2.4f)
                    ]);
                    break;
                }
            }
        }

        // Points along a counter-clockwise arc in the Y-up design space, one degree at a time.
        private static PointF[] Arc(float cx, float cy, float radius, float fromDegrees, float toDegrees)
        {
            var count = Math.Max(2, (int)Math.Abs(toDegrees - fromDegrees));
            var points = new PointF[count + 1];
            for (var i = 0; i <= count; i++)
            {
                var radians = (fromDegrees + (toDegrees - fromDegrees) * i / count) * MathF.PI / 180;
                points[i] = new PointF(cx + radius * MathF.Cos(radians), cy + radius * MathF.Sin(radians));
            }

            return points;
        }

        private static void DrawMic(Graphics g, Pen pen)
        {
            // Capsule from y=10 to y=21, cradle below it, stem to the bottom: up is +y, so it stands upright.
            using var capsule = RoundedRect(9, 10, 6, 11, 3);
            g.DrawPath(pen, capsule);
            g.DrawLines(pen, Arc(12, 12.5f, 6, 180, 360));
            g.DrawLine(pen, 12, 6.5f, 12, 3.5f);
        }

        private static void FillRounded(Graphics g, Brush brush, float x, float y, float width, float height, float radius) =>
            g.FillPath(brush, RoundedRect(x, y, width, height, radius));

        private static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
