using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceSwitch.Windows;

// Mac micInUse(by:): the first configured process that has a capture stream running right now. Every Windows capture
// API, WinMM and SAPI included, runs through a WASAPI audio session whose state turns Active while it records.
// Superwhisper keeps its session Inactive while idle, so an Active one means it is recording.
public static class MicInUse
{
    private const int CaptureFlow = 1;
    private const int DeviceStateActive = 1;
    private const int ClsctxAll = 23;
    private const int SessionActive = 1;

    // processNames: executable names, with or without ".exe", compared without case.
    public static string? By(IReadOnlyCollection<string> processNames)
    {
        if (processNames.Count == 0 || !OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            foreach (var pid in ActiveCapturePids())
            {
                string name;
                try
                {
                    using var process = Process.GetProcessById(pid);
                    name = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    continue;
                }

                if (processNames.Any(wanted => string.Equals(Path.GetFileNameWithoutExtension(wanted), name, StringComparison.OrdinalIgnoreCase)))
                {
                    return name;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // Not knowing must not block dictation; the guard then lets the wake through.
            Log.Info($"mic-in-use check failed: {ex.Message}");
        }

        return null;
    }

    private static IEnumerable<int> ActiveCapturePids()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.EnumAudioEndpoints(CaptureFlow, DeviceStateActive, out var devices) < 0 || devices.GetCount(out var count) < 0)
        {
            yield break;
        }

        for (uint i = 0; i < count; i++)
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (devices.Item(i, out var device) < 0
                || device.Activate(ref iid, ClsctxAll, 0, out var activated) < 0
                || ((IAudioSessionManager2)activated).GetSessionEnumerator(out var sessions) < 0
                || sessions.GetCount(out var sessionCount) < 0)
            {
                continue;
            }

            for (var s = 0; s < sessionCount; s++)
            {
                if (sessions.GetSession(s, out var session) >= 0
                    && session.GetState(out var state) >= 0 && state == SessionActive
                    && session.GetProcessId(out var pid) >= 0)
                {
                    yield return (int)pid;
                }
            }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, int flags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, int flags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    // IAudioSessionControl's methods come first in the vtable; only GetState and GetProcessId are called.
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out nint name);
        [PreserveSig] int SetDisplayName(nint name, nint context);
        [PreserveSig] int GetIconPath(out nint path);
        [PreserveSig] int SetIconPath(nint path, nint context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(nint grouping, nint context);
        [PreserveSig] int RegisterAudioSessionNotification(nint client);
        [PreserveSig] int UnregisterAudioSessionNotification(nint client);
        [PreserveSig] int GetSessionIdentifier(out nint id);
        [PreserveSig] int GetSessionInstanceIdentifier(out nint id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
