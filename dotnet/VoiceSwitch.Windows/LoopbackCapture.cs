using System.Globalization;
using System.Runtime.InteropServices;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

// dBFS levels keyed by a caller-chosen clock (sample index or ms), oldest dropped first.
public sealed class LevelTrace(int capacity)
{
    private readonly (long At, float Db)[] ring = new (long, float)[capacity];
    private int count, next;

    public void Add(long at, float db)
    {
        lock (ring)
        {
            ring[next] = (at, db);
            next = (next + 1) % ring.Length;
            if (count < ring.Length) count++;
        }
    }

    // Entries with from <= At < to, oldest first.
    public (long At, float Db)[] Between(long from, long to)
    {
        lock (ring)
        {
            var found = new List<(long, float)>();
            for (var i = 0; i < count; i++)
            {
                var entry = ring[(next - count + i + ring.Length) % ring.Length];
                if (entry.At >= from && entry.At < to) found.Add(entry);
            }

            return found.ToArray();
        }
    }
}

// Shadow log only: compares what the speakers played with what the microphone heard over a session. Nothing is decided here.
public static class LoopbackShadow
{
    // ponytail: one lag for every setup. Measured 2026-10-12 on the user's PC with WASAPI mic and loopback both stamped by
    // the device clock: the mic envelope trails the loopback by about 250 ms (r=0.58..0.88 on 10 ms dB envelopes). Here the
    // mic frames are stamped when they arrive through WinMM (4 x 30 ms buffers) and the noise processor, so the true offset
    // is off by a few tens of ms; session medians absorb that. Headphones or another room need a new measurement.
    public const int LagMs = 250;
    public const int BinMs = 10;
    // Bins at or below this never had audible playback (an empty bin is -100 dBFS).
    public const float SilentDb = -90;
    public const float FloorDb = -100;

    public static float Dbfs(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;
        return Dbfs(samples.Length == 0 ? 0 : sum / samples.Length / (32768.0 * 32768.0));
    }

    public static float Dbfs(double meanSquare) => (float)Math.Max(FloorDb, 10 * Math.Log10(meanSquare + 1e-12));

    // The ms on the loopback clock when the microphone heard the playback of sample `sample`.
    public static long LoopbackMs(long sample, long epochMs) => epochMs + sample * 1000 / (long)Segmenter.Rate - LagMs;

    // mic: dBFS per frame keyed by sample index. loopback: dBFS per bin keyed by ms (null or empty = not captured).
    // epochMs: the loopback-clock ms of sample 0.
    public static string Fields(IReadOnlyList<(long At, float Db)> mic, IReadOnlyList<(long At, float Db)>? loopback, long epochMs)
    {
        var micText = $"micDb={Fmt(Median(mic.Select(m => m.Db)))}";
        if (loopback is not { Count: > 0 }) return $"loopback=unknown {micText}";
        if (loopback.All(l => l.Db <= SilentDb)) return $"loopback=silent {micText}";
        var ats = loopback.Select(l => l.At).ToArray();
        var diffs = new List<float>();
        foreach (var m in mic)
        {
            var at = LoopbackMs(m.At, epochMs);
            var index = Array.BinarySearch(ats, at);
            if (index < 0) index = ~index;
            if (index > 0 && (index == ats.Length || at - ats[index - 1] < ats[index] - at)) index--;
            if (index < ats.Length && Math.Abs(ats[index] - at) <= BinMs && loopback[index].Db > SilentDb) diffs.Add(m.Db - loopback[index].Db);
        }

        var ratio = diffs.Count == 0 ? "-" : (diffs.Count(d => d >= 0) / (double)diffs.Count).ToString("0.00", CultureInfo.InvariantCulture);
        return $"loopbackDb={Fmt(Median(loopback.Select(l => l.Db)))} {micText} micOverLoopbackDb={Fmt(Median(diffs))} micOverLoopbackGe0={ratio} micOverLoopbackN={diffs.Count}";
    }

    // Lower median; "-" when empty.
    private static float? Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? null : sorted[(sorted.Length - 1) / 2];
    }

    private static string Fmt(float? value) => value is { } db ? db.ToString("0.0", CultureInfo.InvariantCulture) : "-";
}

// WASAPI loopback of the default playback device, dBFS per 10 ms keyed by Environment.TickCount64, on its own thread.
// Any failure leaves Levels empty for that stretch (logged as loopback=unknown) and never touches the microphone path.
public sealed class LoopbackCapture : IDisposable
{
    public readonly LevelTrace Levels = new(12_000);
    private readonly Thread thread;
    private volatile bool stop;

    private LoopbackCapture() => thread = new Thread(Run) { IsBackground = true, Name = "loopback-levels" };

    public static LoopbackCapture Start()
    {
        var capture = new LoopbackCapture();
        capture.thread.Start();
        return capture;
    }

    public void Dispose()
    {
        stop = true;
        thread.Join(3000);
    }

    private void Run()
    {
        while (!stop)
        {
            try
            {
                Capture();
                continue; // the default device changed: reopen at once
            }
            catch (Exception ex)
            {
                Log.Info($"dictation loopback: unavailable {ex.GetType().Name}; retry in 5 s");
            }

            for (var i = 0; i < 50 && !stop; i++) Thread.Sleep(100);
        }
    }

    // Runs until Dispose or until the default playback device changes.
    private void Capture()
    {
        var enumerator = (IMMDeviceEnumerator)MicInUse.NewDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(0, 0, out var device);
        device.GetId(out var id);
        var iid = typeof(IAudioClient).GUID;
        device.Activate(ref iid, 23, 0, out var activated);
        var client = (IAudioClient)activated;
        var mix = client.GetMixFormat();
        int rate, channels, bits;
        bool isFloat;
        try
        {
            var tag = (ushort)Marshal.ReadInt16(mix, 0);
            channels = Marshal.ReadInt16(mix, 2);
            rate = Marshal.ReadInt32(mix, 4);
            bits = Marshal.ReadInt16(mix, 14);
            isFloat = tag == 3 || (tag == 0xFFFE && Marshal.PtrToStructure<Guid>(mix + 24) == new Guid("00000003-0000-0010-8000-00aa00389b71"));
            client.Initialize(0, Loopback | EventCallback, BufferHns, 0, mix, 0);
        }
        finally { Marshal.FreeCoTaskMem(mix); }

        if (!isFloat && bits != 16 && bits != 32) throw new NotSupportedException($"mix format pcm{bits}");
        using var ready = new AutoResetEvent(false);
        client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle());
        var captureIid = typeof(IAudioCaptureClient).GUID;
        var capture = (IAudioCaptureClient)client.GetService(ref captureIid);
        client.Start();
        Log.Info($"dictation loopback: {rate} Hz {channels}ch {(isFloat ? "float" : "pcm")}{bits} device={id}");
        try
        {
            // Power per 10 ms bin, keyed by ms; a bin nobody wrote is silence, as in the measurement tool.
            var bins = new double[64];
            var binAt = new long[64];
            var nextBin = Environment.TickCount64 / LoopbackShadow.BinMs * LoopbackShadow.BinMs;
            var framesPerBin = rate * LoopbackShadow.BinMs / 1000.0;
            var floats = new float[rate * channels];
            var shorts = new short[rate * channels];
            var ints = new int[rate * channels];
            var checkedDevice = Environment.TickCount64;
            while (!stop)
            {
                // Loopback may not signal while nothing plays, so also poll on timeout.
                ready.WaitOne(20);
                while (true)
                {
                    capture.GetNextPacketSize(out var next);
                    if (next == 0) break;
                    capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
                    var count = (int)frames * channels;
                    if ((flags & 2) == 0 && count <= floats.Length)
                    {
                        double power = 0;
                        if (isFloat) { Marshal.Copy(data, floats, 0, count); for (var i = 0; i < count; i++) power += (double)floats[i] * floats[i]; }
                        else if (bits == 16) { Marshal.Copy(data, shorts, 0, count); for (var i = 0; i < count; i++) power += shorts[i] / 32768.0 * (shorts[i] / 32768.0); }
                        else { Marshal.Copy(data, ints, 0, count); for (var i = 0; i < count; i++) power += ints[i] / 2147483648.0 * (ints[i] / 2147483648.0); }
                        // Stamped on arrival: the packet ends about now, so it started frames/rate earlier.
                        var at = (Environment.TickCount64 - frames * 1000L / rate) / LoopbackShadow.BinMs * LoopbackShadow.BinMs;
                        var slot = (int)(at / LoopbackShadow.BinMs % bins.Length);
                        if (at >= nextBin)
                        {
                            if (binAt[slot] != at) { binAt[slot] = at; bins[slot] = 0; }
                            bins[slot] += power / channels;
                        }
                    }

                    capture.ReleaseBuffer(frames);
                }

                // Emit bins older than 50 ms so late packets still land in theirs.
                var emitUntil = (Environment.TickCount64 - 50) / LoopbackShadow.BinMs * LoopbackShadow.BinMs;
                for (; nextBin < emitUntil; nextBin += LoopbackShadow.BinMs)
                {
                    var slot = (int)(nextBin / LoopbackShadow.BinMs % bins.Length);
                    Levels.Add(nextBin, LoopbackShadow.Dbfs(binAt[slot] == nextBin ? bins[slot] / framesPerBin : 0));
                }

                if (Environment.TickCount64 - checkedDevice > 2000)
                {
                    checkedDevice = Environment.TickCount64;
                    enumerator.GetDefaultAudioEndpoint(0, 0, out var current);
                    current.GetId(out var currentId);
                    if (currentId != id)
                    {
                        Log.Info("dictation loopback: default playback device changed; reopening");
                        return;
                    }
                }
            }
        }
        finally { client.Stop(); }
    }

    private const uint Loopback = 0x00020000, EventCallback = 0x00040000;
    private const long BufferHns = 2_000_000; // 200 ms

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
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

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }
}
