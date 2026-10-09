using System.Diagnostics;
using System.Runtime.InteropServices;
using VoiceSwitch.Windows.Core;
using VoiceSwitch.Windows.Tray;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        if (!OperatingSystem.IsWindows())
            return Skip("Windows is required.");

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var originalForeground = WindowFocus.Foreground();
        if (originalForeground == 0)
            return Skip("No foreground window is available.");

        using var target = new Form
        {
            Text = "voice-switch HUD display probe",
            StartPosition = FormStartPosition.Manual,
            Size = new Size(300, 160)
        };
        _ = target.Handle;
        using var hud = new DictationHud();
        var pointerScreen = Screen.FromPoint(Cursor.Position);
        var otherScreen = Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName != pointerScreen.DeviceName);
        if (otherScreen is null)
            return Skip("At least two displays are required.");

        try
        {
            foreach (var screen in new[] { otherScreen, pointerScreen })
            {
                var area = screen.WorkingArea;
                target.Location = new Point(area.Left + (area.Width - target.Width) / 2,
                    area.Top + (area.Height - target.Height) / 2);
                target.Show();
                BringToForeground(target.Handle);
                if (!WaitFor(() => WindowFocus.Foreground() == target.Handle, 2000))
                    return Skip($"The test window could not become foreground: foreground=0x{WindowFocus.Foreground():X}, target=0x{target.Handle:X}.");

                Check(Screen.FromHandle(target.Handle).DeviceName == screen.DeviceName,
                    "The test window did not reach the requested display.");
                foreach (var phase in new[] { DictationPhase.Waiting, DictationPhase.Recording, DictationPhase.Ended })
                {
                    hud.Show(phase);
                    Check(IsWindowVisible(hud.Handle), $"{phase}: native HUD window is hidden.");
                    Check(GetWindowRect(hud.Handle, out var rectangle), "GetWindowRect failed.");
                    var actual = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
                    var expected = new Rectangle(area.Left + (area.Width - hud.Width) / 2,
                        area.Top + 12, hud.Width, hud.Height);
                    Console.WriteLine($"{phase}: pointer={pointerScreen.DeviceName}, foreground={screen.DeviceName}, HUD={actual}, expected={expected}");
                    Check(actual == expected, $"{phase}: HUD is not at the foreground display's top center.");
                    Check(hud.Bounds == actual, $"{phase}: managed bounds differ from the native window rectangle.");
                    CheckUnchanged(target.Handle, pointerScreen.DeviceName);
                }

                Check(WaitFor(() => !IsWindowVisible(hud.Handle), 3000), "Ended: HUD did not auto-hide.");
                CheckUnchanged(target.Handle, pointerScreen.DeviceName);
                hud.Show(DictationPhase.Recording);
                hud.Show(DictationPhase.Idle);
                Check(!IsWindowVisible(hud.Handle), "Idle: HUD did not hide.");
                CheckUnchanged(target.Handle, pointerScreen.DeviceName);
            }

            Console.WriteLine("PASS: HUD follows foreground across two displays; pointer display and focus stay unchanged; Ended and Idle hide.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL: {exception.Message}");
            return 1;
        }
        finally
        {
            hud.Show(DictationPhase.Idle);
            target.Hide();
            if (IsWindow(originalForeground))
            {
                BringToForeground(originalForeground);
                if (!WaitFor(() => WindowFocus.Foreground() == originalForeground, 2000))
                    Console.Error.WriteLine($"WARNING: Windows refused to restore the original foreground window: foreground=0x{WindowFocus.Foreground():X}, target=0x{originalForeground:X}.");
            }
        }
    }

    private static void BringToForeground(nint window)
    {
        // Why: Attach to the foreground input queue so Windows permits this probe's focus changes.
        var foregroundThread = GetWindowThreadProcessId(WindowFocus.Foreground(), out _);
        var attached = AttachThreadInput(GetCurrentThreadId(), foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                AttachThreadInput(GetCurrentThreadId(), foregroundThread, false);
        }
    }

    private static void CheckUnchanged(nint foreground, string pointerDisplay)
    {
        Check(WindowFocus.Foreground() == foreground, "HUD changed the foreground window.");
        var cursor = Cursor.Position;
        var currentDisplay = Screen.FromPoint(cursor).DeviceName;
        Check(currentDisplay == pointerDisplay,
            $"The pointer changed displays: expected={pointerDisplay}, actual={currentDisplay}, position={cursor}.");
    }

    private static bool WaitFor(Func<bool> condition, int milliseconds)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            if (condition())
                return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static int Skip(string reason)
    {
        Console.WriteLine($"SKIP: {reason}");
        return 77;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out WindowRectangle rectangle);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
