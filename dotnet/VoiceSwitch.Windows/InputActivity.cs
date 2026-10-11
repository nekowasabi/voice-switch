using System.Runtime.InteropServices;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public static class InputActivity
{
    public static double? IdleSeconds()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? ElapsedSeconds(unchecked((uint)Environment.TickCount64), info.Time) : null;
    }

    public static double ElapsedSeconds(uint now, uint lastInput) => unchecked(now - lastInput) / 1000.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

public static class WakeMissLabel
{
    private static readonly object Sync = new();
    private static (long Id, WakeOrigin Origin, long At)? latest;
    private static long? labeled;
    public static void Observe(long id, WakeOrigin origin)
    {
        lock (Sync) { latest = (id, origin, Environment.TickCount64); }
    }
    public static void Record()
    {
        lock (Sync)
        {
            if (latest is not { } value || Environment.TickCount64 - value.At > 10_000 || labeled == value.At) return;
            labeled = value.At;
            Log.Info($"wake label: MISS id={value.Id} {value.Origin.Fields}");
        }
    }
}
