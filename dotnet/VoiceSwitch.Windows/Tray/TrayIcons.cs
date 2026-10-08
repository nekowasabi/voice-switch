using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace VoiceSwitch.Windows.Tray;

// Mac mic.fill / mic.slash: a microphone while listening, the same one struck through otherwise.
// Drawn once per process; Icon.FromHandle does not own its HICON, so these two are kept for the process lifetime.
public static class TrayIcons
{
    public static readonly Icon Listening = Draw(slashed: false);
    public static readonly Icon Stopped = Draw(slashed: true);

    public static Bitmap Render(bool slashed, Color ink)
    {
        var bitmap = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(ink);
        using var pen = new Pen(ink, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var capsule = new GraphicsPath())
        {
            capsule.AddArc(11, 3, 10, 10, 180, 180);
            capsule.AddArc(11, 10, 10, 10, 0, 180);
            capsule.CloseFigure();
            g.FillPath(brush, capsule);
        }

        g.DrawArc(pen, 7, 7, 18, 17, 0, 180);
        g.DrawLine(pen, 16, 24, 16, 28);
        g.DrawLine(pen, 11, 28.5f, 21, 28.5f);
        if (slashed)
        {
            // A gap in the background colour keeps the slash readable where it crosses the capsule.
            using var gap = new Pen(Color.Transparent, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawLine(gap, 6, 4, 27, 29);
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawLine(pen, 6, 4, 27, 29);
        }

        return bitmap;
    }

    private static Icon Draw(bool slashed)
    {
        using var bitmap = Render(slashed, TaskbarIsLight() ? Color.FromArgb(32, 32, 32) : Color.White);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    // ponytail: read once at startup; a theme switch shows the old ink until voice-switch restarts.
    private static bool TaskbarIsLight() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int value && value != 0;
}
