using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed class WinMmCapture : IPcmCapture
{
    private const int WaveMapper = -1;
    public const int WaveInputDataMessage = 0x3C0;
    private const int CallbackFunction = 0x00030000;
    private readonly BlockingCollection<PcmFrame> frames = new(boundedCapacity: 16);
    private readonly BlockingCollection<IntPtr> completedHeaders = new(boundedCapacity: 16);
    private readonly List<GCHandle> pins = new();
    private readonly List<IntPtr> headers = new();
    private readonly WaveCallback callback;
    private readonly IWaveInNative native;
    private IntPtr device;
    private Task? requeueWorker;
    private long nextSample;
    private bool started;
    private volatile bool shuttingDown;
    private bool disposed;
    private int deviceId = WaveMapper;

    public WinMmCapture()
        : this(new PInvokeWaveInNative())
    {
    }

    internal WinMmCapture(IWaveInNative native)
    {
        this.native = native;
        callback = OnWaveIn;
    }

    // The input device the open capture reads, by name; null while none is open. The tray compares it with
    // EffectiveDevice to notice an unplug or a new default device, which WinMM never reports to an open handle.
    public static string? CurrentDevice { get => Volatile.Read(ref currentDevice); internal set => Volatile.Write(ref currentDevice, value); }
    private static string? currentDevice;

    // pinned: a device name chosen in the tray, or null for the system default. A pinned device that is not
    // connected falls back to the default until it returns.
    public static WinMmCapture Open(string? pinned = null)
    {
        var devices = InputDevices();
        var capture = new WinMmCapture { deviceId = pinned is null ? WaveMapper : devices.ToList().IndexOf(pinned) };
        var name = EffectiveDevice(pinned, devices, DefaultInputName(devices));
        capture.Start();
        CurrentDevice = name;
        Log.Info($"dictation capture: {name ?? "-"}{(capture.deviceId < 0 ? " (system default)" : "")}{(pinned is not null && capture.deviceId < 0 ? $"; {pinned} is not connected" : "")}");
        return capture;
    }

    public static string? EffectiveDevice(string? pinned, IReadOnlyList<string> devices, string? defaultName) =>
        pinned is not null && devices.Contains(pinned) ? pinned : defaultName;

    public static string? EffectiveDevice(string? pinned)
    {
        var devices = InputDevices();
        return EffectiveDevice(pinned, devices, DefaultInputName(devices));
    }

    // WinMM names are cut at 31 characters; the cut name is what the tray persists and compares.
    // ponytail: two devices with the same cut name are indistinguishable; the first one wins.
    public static IReadOnlyList<string> InputDevices()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var names = new List<string>();
        for (var i = 0; i < waveInGetNumDevs(); i++)
        {
            var caps = new WaveInCaps();
            if (waveInGetDevCapsW(i, ref caps, Marshal.SizeOf<WaveInCaps>()) == 0)
            {
                names.Add(caps.Name);
            }
        }

        return names;
    }

    // The device WAVE_MAPPER opens: Windows' default recording device.
    private static string? DefaultInputName(IReadOnlyList<string> devices) =>
        OperatingSystem.IsWindows() && waveInMessage(WaveMapper, DrvmMapperPreferredGet, out var id, out _) == 0 && id >= 0 && id < devices.Count
            ? devices[id]
            : null;

    internal static WinMmCapture OpenForTest(IWaveInNative native)
    {
        var capture = new WinMmCapture(native);
        capture.Start();
        return capture;
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            PcmFrame frame;
            try
            {
                frame = frames.Take(cancellation);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return frame;
            await Task.Yield();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CurrentDevice = null;

        if (device != IntPtr.Zero)
        {
            shuttingDown = true;
            started = false;
            completedHeaders.CompleteAdding();
            if (requeueWorker is not null)
            {
                await requeueWorker.ConfigureAwait(false);
            }

            Check(native.Reset(device), "waveInReset");
            foreach (var header in headers)
            {
                Check(native.UnprepareHeader(device, header, Marshal.SizeOf<WaveHdr>()), "waveInUnprepareHeader");
                Marshal.FreeHGlobal(header);
            }

            Check(native.Close(device), "waveInClose");
            device = IntPtr.Zero;
        }

        foreach (var pin in pins)
        {
            if (pin.IsAllocated)
            {
                pin.Free();
            }
        }

        frames.Dispose();
        completedHeaders.Dispose();
    }

    private void Start()
    {
        try
        {
            var format = new WaveFormat
            {
                FormatTag = 1,
                Channels = 1,
                SamplesPerSec = 16000,
                AvgBytesPerSec = 32000,
                BlockAlign = 2,
                BitsPerSample = 16,
                Size = 0
            };
            var result = native.Open(out device, deviceId, ref format, callback, IntPtr.Zero, CallbackFunction);
            if (result != 0)
            {
                throw new InvalidOperationException($"WinMM could not open PCM16/16000 mono input {deviceId}. waveInOpen={result}");
            }

            for (var i = 0; i < 4; i++)
            {
                QueueBuffer(new byte[Segmenter.FrameLength * 2]);
            }

            requeueWorker = Task.Run(RequeueCompletedBuffers);
            result = native.Start(device);
            if (result != 0)
            {
                shuttingDown = true;
                completedHeaders.CompleteAdding();
                throw new InvalidOperationException($"WinMM could not start capture. waveInStart={result}");
            }

            started = true;
        }
        catch
        {
            shuttingDown = true;
            completedHeaders.CompleteAdding();
            DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private void QueueBuffer(byte[] buffer)
    {
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        pins.Add(pin);
        var header = new WaveHdr
        {
            Data = pin.AddrOfPinnedObject(),
            BufferLength = buffer.Length,
            BytesRecorded = 0,
            User = GCHandle.ToIntPtr(pin),
            Flags = 0,
            Loops = 0,
            Next = IntPtr.Zero,
            Reserved = IntPtr.Zero
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
        Marshal.StructureToPtr(header, ptr, false);
        headers.Add(ptr);
        Check(native.PrepareHeader(device, ptr, Marshal.SizeOf<WaveHdr>()), "waveInPrepareHeader");
        Check(native.AddBuffer(device, ptr, Marshal.SizeOf<WaveHdr>()), "waveInAddBuffer");
    }

    private void OnWaveIn(IntPtr hwi, int message, IntPtr instance, IntPtr param1, IntPtr param2)
    {
        try
        {
            if (message != WaveInputDataMessage || !started || shuttingDown)
            {
                return;
            }

            if (!completedHeaders.TryAdd(param1))
            {
                frames.CompleteAdding();
            }
        }
        catch
        {
        }
    }

    private void RequeueCompletedBuffers()
    {
        foreach (var headerPtr in completedHeaders.GetConsumingEnumerable())
        {
            if (shuttingDown)
            {
                return;
            }

            CopyRecordedBytes(headerPtr);
            if (shuttingDown)
            {
                return;
            }

            Check(native.AddBuffer(device, headerPtr, Marshal.SizeOf<WaveHdr>()), "waveInAddBuffer");
        }
    }

    private void CopyRecordedBytes(IntPtr headerPtr)
    {
        var header = Marshal.PtrToStructure<WaveHdr>(headerPtr);
        if (header.BytesRecorded <= 0)
        {
            return;
        }

        if (header.BytesRecorded % 2 != 0)
        {
            frames.CompleteAdding();
            return;
        }

        try
        {
            var bytes = new byte[header.BytesRecorded];
            Marshal.Copy(header.Data, bytes, 0, bytes.Length);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            var start = Interlocked.Add(ref nextSample, samples.Length) - samples.Length;
            if (!frames.TryAdd(new PcmFrame(start, samples.ToImmutableArray())))
            {
                frames.CompleteAdding();
            }
        }
        catch
        {
            frames.CompleteAdding();
        }
    }

    public static bool IsInputDataMessageForTest(int message) => message == WaveInputDataMessage;
    public static int WaveFormatSizeForTest() => Marshal.SizeOf<WaveFormat>();
    public static int WaveHeaderSizeForTest() => Marshal.SizeOf<WaveHdr>();

    private static void Check(int result, string name)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"{name} failed: {result}");
        }
    }

    internal delegate void WaveCallback(IntPtr hwi, int message, IntPtr instance, IntPtr param1, IntPtr param2);

    private const int DrvmMapperPreferredGet = 0x2015;

    [DllImport("winmm.dll")]
    private static extern int waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int waveInGetDevCapsW(nint deviceId, ref WaveInCaps caps, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInMessage(nint device, int message, out int param1, out int param2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        public ushort Mid;
        public ushort Pid;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSec;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    internal interface IWaveInNative
    {
        int Open(out IntPtr device, int deviceId, ref WaveFormat format, WaveCallback callback, IntPtr instance, int flags);
        int PrepareHeader(IntPtr device, IntPtr header, int size);
        int AddBuffer(IntPtr device, IntPtr header, int size);
        int Start(IntPtr device);
        int Reset(IntPtr device);
        int UnprepareHeader(IntPtr device, IntPtr header, int size);
        int Close(IntPtr device);
    }

    private sealed class PInvokeWaveInNative : IWaveInNative
    {
        public int Open(out IntPtr device, int deviceId, ref WaveFormat format, WaveCallback callback, IntPtr instance, int flags) =>
            waveInOpen(out device, deviceId, ref format, callback, instance, flags);

        public int PrepareHeader(IntPtr device, IntPtr header, int size) =>
            waveInPrepareHeader(device, header, size);

        public int AddBuffer(IntPtr device, IntPtr header, int size) =>
            waveInAddBuffer(device, header, size);

        public int Start(IntPtr device) => waveInStart(device);
        public int Reset(IntPtr device) => waveInReset(device);
        public int UnprepareHeader(IntPtr device, IntPtr header, int size) => waveInUnprepareHeader(device, header, size);
        public int Close(IntPtr device) => waveInClose(device);

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr device, int deviceId, ref WaveFormat format, WaveCallback callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr device);
        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr device);
        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);
        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr device);
    }
}
