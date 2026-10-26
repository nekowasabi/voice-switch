using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public static class MicCapture
{
    // dictation.echoCancellation (default on): what the speakers play is cancelled before the VAD, so a video never starts a recording.
    // Measured 2026-10-26 (features/playback-silence.md): own voice replayed on the speakers -22 dBFS raw, -56 dBFS here, no session.
    public static IPcmCapture Open(string? pinned, VoiceSwitchConfig config)
    {
        if (config.Dictation?.EchoCancellation != false && OperatingSystem.IsWindows())
        {
            try
            {
                return EchoCancelledCapture.Open(pinned);
            }
            catch (Exception ex)
            {
                Log.Info($"dictation capture: echo cancellation unavailable, raw input: {ex.GetType().Name} {ex.Message}");
            }
        }

        return WinMmCapture.Open(pinned);
    }
}

// The microphone as a WASAPI Communications stream whose OS echo canceller references the default playback device.
public sealed class EchoCancelledCapture : IPcmCapture
{
    private readonly Channel<PcmFrame> frames = Channel.CreateBounded<PcmFrame>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Thread thread;
    private volatile bool stop;
    private IAudioClient client = null!;
    private IAudioCaptureClient capture = null!;
    private IAcousticEchoCancellationControl aec = null!;
    private IMMDeviceEnumerator enumerator = null!;
    private int rate, channels;
    private bool isFloat;
    private string? reference;

    private EchoCancelledCapture() => thread = new Thread(Run) { IsBackground = true, Name = "mic-aec" };

    // Setup runs on the capture thread (MTA): opened from the tray's STA thread, the Communications stream never came up
    // and the tray sat in Starting (2026-10-26 21:23). A setup that does not finish in 5 s falls back to raw input.
    public static EchoCancelledCapture Open(string? pinned)
    {
        var self = new EchoCancelledCapture();
        self.pinned = pinned;
        self.thread.Start();
        if (!self.ready.Task.Wait(TimeSpan.FromSeconds(5)))
        {
            self.stop = true;
            throw new TimeoutException("echo-cancelled input did not start within 5 s");
        }

        if (self.ready.Task.Exception is { } failed) throw failed.InnerException!;
        return self;
    }

    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? pinned;

    private void Setup()
    {
        enumerator = (IMMDeviceEnumerator)MicInUse.NewDeviceEnumerator();
        var devices = WinMmCapture.InputDevices();
        var name = WinMmCapture.EffectiveDevice(pinned);
        IMMDevice device;
        if (pinned is not null && devices.ToList().IndexOf(pinned) is var index and >= 0 && EndpointId(index) is { } id)
            enumerator.GetDevice(id, out device);
        else
            enumerator.GetDefaultAudioEndpoint(CaptureFlow, 0, out device);

        var iid = typeof(IAudioClient2).GUID;
        device.Activate(ref iid, 23, 0, out var activated);
        var client2 = (IAudioClient2)activated;
        var properties = new AudioClientProperties { Size = 16, Category = CommunicationsCategory };
        client2.SetClientProperties(ref properties);
        var mix = client2.GetMixFormat();
        try
        {
            var tag = (ushort)Marshal.ReadInt16(mix, 0);
            channels = Marshal.ReadInt16(mix, 2);
            rate = Marshal.ReadInt32(mix, 4);
            var bits = Marshal.ReadInt16(mix, 14);
            isFloat = tag == 3 || (tag == 0xFFFE && Marshal.PtrToStructure<Guid>(mix + 24) == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            if (!isFloat && bits != 16) throw new NotSupportedException($"mix format pcm{bits}");
            client2.Initialize(0, 0, BufferHns, 0, mix, 0);
        }
        finally { Marshal.FreeCoTaskMem(mix); }

        client = (IAudioClient)client2;
        var aecIid = typeof(IAcousticEchoCancellationControl).GUID;
        // E_NOINTERFACE here means this endpoint has no controllable AEC; the caller falls back to the raw WinMM input.
        aec = (IAcousticEchoCancellationControl)client.GetService(ref aecIid);
        PointAtDefaultPlayback();
        var captureIid = typeof(IAudioCaptureClient).GUID;
        capture = (IAudioCaptureClient)client.GetService(ref captureIid);
        client.Start();
        WinMmCapture.CurrentDevice = name;
        Log.Info($"dictation capture: {name ?? "-"}{(pinned is null ? " (system default)" : "")} echo-cancelled {rate} Hz {channels}ch reference={reference}");
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        await foreach (var frame in frames.Reader.ReadAllAsync(cancellation).ConfigureAwait(false))
            yield return frame;
    }

    public ValueTask DisposeAsync()
    {
        stop = true;
        thread.Join(3000);
        WinMmCapture.CurrentDevice = null;
        return ValueTask.CompletedTask;
    }

    // The reference follows the default playback device, like LoopbackCapture.
    private void PointAtDefaultPlayback()
    {
        enumerator.GetDefaultAudioEndpoint(RenderFlow, 0, out var render);
        render.GetId(out var id);
        if (id == reference) return;
        aec.SetEchoCancellationRenderEndpoint(id);
        if (reference is not null) Log.Info($"dictation capture: echo reference follows playback device {id}");
        reference = id;
    }

    private void Run()
    {
        try
        {
            Setup();
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            return;
        }

        ready.TrySetResult();
        var resampler = new Downsampler(rate);
        var pending = new List<short>(Segmenter.FrameLength * 4);
        var raw = new float[rate * channels];
        var shorts = new short[rate * channels];
        long next = 0;
        var checkedPlayback = Environment.TickCount64;
        try
        {
            while (!stop)
            {
                Thread.Sleep(10);
                while (true)
                {
                    capture.GetNextPacketSize(out var size);
                    if (size == 0) break;
                    capture.GetBuffer(out var data, out var count, out var flags, out _, out _);
                    var n = (int)count * channels;
                    if (n > raw.Length) { raw = new float[n]; shorts = new short[n]; }
                    if ((flags & SilentFlag) != 0) Array.Clear(raw, 0, n);
                    else if (isFloat) Marshal.Copy(data, raw, 0, n);
                    else { Marshal.Copy(data, shorts, 0, n); for (var i = 0; i < n; i++) raw[i] = shorts[i] / 32768f; }
                    capture.ReleaseBuffer(count);
                    resampler.Push(raw.AsSpan(0, n), channels, pending);
                    while (pending.Count >= Segmenter.FrameLength)
                    {
                        var frame = pending.GetRange(0, Segmenter.FrameLength).ToImmutableArray();
                        pending.RemoveRange(0, Segmenter.FrameLength);
                        frames.Writer.TryWrite(new PcmFrame(next, frame));
                        next += Segmenter.FrameLength;
                    }
                }

                if (Environment.TickCount64 - checkedPlayback > 2000)
                {
                    checkedPlayback = Environment.TickCount64;
                    // A failed re-point keeps the old reference; losing the microphone over it would be worse.
                    try { PointAtDefaultPlayback(); }
                    catch (Exception ex) { Log.Info($"dictation capture: echo reference not moved: {ex.GetType().Name} {ex.Message}"); }
                }
            }

            frames.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            Log.Info($"dictation capture: echo-cancelled input stopped: {ex.GetType().Name} {ex.Message}");
            frames.Writer.TryComplete(ex);
        }
        finally
        {
            try { client.Stop(); } catch { }
        }
    }

    // Box-filter downmix and decimation to 16 kHz PCM16. Superwhisper's text from this path matched or beat the raw
    // microphone's on 150 s of double talk (features/playback-silence.md VO2).
    public sealed class Downsampler(int rate)
    {
        private readonly double step = rate / (double)Segmenter.Rate;
        private double position;
        private double sum;
        private int count;

        public void Push(ReadOnlySpan<float> interleaved, int channels, List<short> output)
        {
            for (var f = 0; f + channels <= interleaved.Length; f += channels)
            {
                float mono = 0;
                for (var c = 0; c < channels; c++) mono += interleaved[f + c];
                sum += mono / channels;
                count++;
                position += 1;
                if (position < step) continue;
                position -= step;
                output.Add((short)Math.Clamp(Math.Round(sum / count * 32767), short.MinValue, short.MaxValue));
                sum = 0;
                count = 0;
            }
        }
    }

    private static string? EndpointId(int waveInIndex)
    {
        if (waveInMessage(waveInIndex, QueryFunctionInstanceIdSize, out var bytes, 0) != 0 || bytes <= 0) return null;
        var buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            return waveInMessage(waveInIndex, QueryFunctionInstanceId, buffer, bytes) == 0 ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const int CaptureFlow = 1, RenderFlow = 0, CommunicationsCategory = 3, SilentFlag = 2;
    private const int QueryFunctionInstanceId = 0x0811, QueryFunctionInstanceIdSize = 0x0812;
    private const long BufferHns = 2_000_000;

    [DllImport("winmm.dll")]
    private static extern int waveInMessage(nint device, int message, out int size, nint unused);

    [DllImport("winmm.dll")]
    private static extern int waveInMessage(nint device, int message, nint buffer, nint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProperties { public int Size; public int IsOffload; public int Category; public int Options; }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        void OpenPropertyStore(uint access, out nint store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();
        void IsFormatSupported();
        nint GetMixFormat();
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(nint handle);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetService(ref Guid iid);
    }

    [ComImport, Guid("726778CD-F60A-4eda-82DE-E47610CD78AA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient2
    {
        void Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
        uint GetBufferSize();
        long GetStreamLatency();
        uint GetCurrentPadding();
        void IsFormatSupported();
        nint GetMixFormat();
        void GetDevicePeriod(out long def, out long min);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(nint handle);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetService(ref Guid iid);
        void IsOffloadCapable(int category, out int capable);
        void SetClientProperties(ref AudioClientProperties properties);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F4AE25B5-AAA3-437D-B6B3-DBBE2D0E9549"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAcousticEchoCancellationControl
    {
        void SetEchoCancellationRenderEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpointId);
    }
}
