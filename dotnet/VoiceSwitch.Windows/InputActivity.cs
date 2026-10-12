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

    public static uint? LastInputTick()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) ? info.Time : null;
    }

    public static double ElapsedSeconds(uint now, uint lastInput) => unchecked(now - lastInput) / 1000.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint Size; public uint Time; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

public static class PlaybackMeter
{
    public static float? Peak()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0, 1, out var device);
            var iid = typeof(IAudioMeterInformation).GUID;
            device.Activate(ref iid, 23, 0, out var meter);
            ((IAudioMeterInformation)meter).GetPeakValue(out var peak);
            return peak;
        }
        catch { return null; }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // Vtable slot 0; declared so GetDefaultAudioEndpoint lands in slot 1.
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        void GetPeakValue(out float peak);
    }
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
