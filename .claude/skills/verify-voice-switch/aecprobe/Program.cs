using System.Media;
using System.Runtime.InteropServices;

// Records, at the same time and for the same seconds:
//   mic-raw.wav   the default capture endpoint in the default (raw-ish) category
//   mic-comms.wav the same endpoint as a Communications stream, where Windows applies its own AEC if it has one
//   loopback.wav  what the default render endpoint played
// All three are written as 16 kHz mono PCM16 so voice-switch.exe --input-wav can replay them.
// Usage: aecprobe <outDir> <seconds> [play.wav]
var outDir = args[0];
var seconds = int.Parse(args[1]);
Directory.CreateDirectory(outDir);

var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
enumerator.GetDefaultAudioEndpoint(1, 0, out var mic);
enumerator.GetDefaultAudioEndpoint(0, 0, out var speaker);
mic.GetId(out var micId);
speaker.GetId(out var speakerId);
Console.WriteLine($"mic={micId}\nspeaker={speakerId}");

var raw = Stream.Open(mic, 0, communications: false, "mic-raw");
var comms = Stream.Open(mic, 0, communications: true, "mic-comms");
var loop = Stream.Open(speaker, 0x00020000, communications: false, "loopback");

// IID from audioclient.h is unverified here; E_NOINTERFACE is only a hint, the measured levels decide.
try
{
    var iid = new Guid("F4AE25B5-AAA3-437D-B6B3-DBBE2D0E9549");
    var control = (IAcousticEchoCancellationControl)comms.Client.GetService(ref iid);
    control.SetEchoCancellationRenderEndpoint(speakerId);
    Console.WriteLine("aec control: reference set to speaker");
}
catch (Exception e) { Console.WriteLine($"aec control: {e.GetType().Name} 0x{e.HResult:X8}"); }

SoundPlayer? player = args.Length > 2 ? new SoundPlayer(args[2]) : null;
foreach (var s in new[] { raw, comms, loop }) s.Client.Start();
player?.Play();
var until = Environment.TickCount64 + seconds * 1000L;
while (Environment.TickCount64 < until)
{
    Thread.Sleep(10);
    foreach (var s in new[] { raw, comms, loop }) s.Drain();
}
player?.Stop();
foreach (var s in new[] { raw, comms, loop })
{
    s.Client.Stop();
    var path = Path.Combine(outDir, s.Name + ".wav");
    s.Write16kMono(path);
    Console.WriteLine($"{s.Name}: {s.Rate} Hz {s.Channels}ch frames={s.Frames} rms={s.RmsDb():0.0} dBFS -> {path}");
}

sealed class Stream
{
    public required IAudioClient Client;
    public required IAudioCaptureClient Capture;
    public required string Name;
    public int Rate, Channels;
    public long Frames;
    private readonly List<float> mono = new();

    public static Stream Open(IMMDevice device, uint flags, bool communications, string name)
    {
        var iid = new Guid("726778CD-F60A-4eda-82DE-E47610CD78AA");
        device.Activate(ref iid, 23, 0, out var o);
        var client = (IAudioClient2)o;
        if (communications)
        {
            var props = new AudioClientProperties { cbSize = 16, eCategory = 3 };
            client.SetClientProperties(ref props);
        }
        var mix = client.GetMixFormat();
        var channels = Marshal.ReadInt16(mix, 2);
        var rate = Marshal.ReadInt32(mix, 4);
        var tag = (ushort)Marshal.ReadInt16(mix, 0);
        var bits = Marshal.ReadInt16(mix, 14);
        if (!(tag == 3 || (tag == 0xFFFE && bits == 32))) throw new NotSupportedException($"{name} format tag {tag} bits {bits}");
        client.Initialize(0, flags, 2_000_000, 0, mix, 0);
        Marshal.FreeCoTaskMem(mix);
        var cid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        return new Stream { Client = (IAudioClient)client, Capture = (IAudioCaptureClient)client.GetService(ref cid), Name = name, Rate = rate, Channels = channels };
    }

    public void Drain()
    {
        while (true)
        {
            Capture.GetNextPacketSize(out var next);
            if (next == 0) return;
            Capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
            var buf = new float[frames * Channels];
            if ((flags & 2) == 0) Marshal.Copy(data, buf, 0, buf.Length);
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var c = 0; c < Channels; c++) sum += buf[f * Channels + c];
                mono.Add(sum / Channels);
            }
            Frames += frames;
            Capture.ReleaseBuffer(frames);
        }
    }

    public double RmsDb()
    {
        double p = 0;
        foreach (var x in mono) p += x * x;
        return 10 * Math.Log10(p / Math.Max(1, mono.Count) + 1e-12);
    }

    // ponytail: box-filter decimation, good enough for VAD and level comparison, not for listening tests.
    public void Write16kMono(string path)
    {
        var step = Rate / 16000.0;
        var n = (int)(mono.Count / step);
        var pcm = new short[n];
        for (var i = 0; i < n; i++)
        {
            int a = (int)(i * step), b = Math.Min(mono.Count, (int)((i + 1) * step));
            float s = 0;
            for (var k = a; k < b; k++) s += mono[k];
            pcm[i] = (short)Math.Clamp(s / Math.Max(1, b - a) * 32767, -32768, 32767);
        }
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(n * 2);
        foreach (var x in pcm) w.Write(x);
    }
}

[StructLayout(LayoutKind.Sequential)]
struct AudioClientProperties { public int cbSize; public int bIsOffload; public int eCategory; public int Options; }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
    void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, nint activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(uint access, out nint store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioClient
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
interface IAudioClient2
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
    void GetBufferSizeLimits(nint format, int eventDriven, out long min, out long max);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioCaptureClient
{
    void GetBuffer(out nint data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    void ReleaseBuffer(uint frames);
    void GetNextPacketSize(out uint frames);
}

[ComImport, Guid("F4AE25B5-AAA3-437D-B6B3-DBBE2D0E9549"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAcousticEchoCancellationControl
{
    void SetEchoCancellationRenderEndpoint([MarshalAs(UnmanagedType.LPWStr)] string endpointId);
}
