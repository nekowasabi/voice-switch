using VoiceSwitch.Windows.Core;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Tray;
using System.Diagnostics;
using System.Text;
using System.Collections.Immutable;
using System.Runtime.InteropServices;

sealed record TestRuntime(string Dir, string ConfigPath) : IDisposable
{
    public VoiceSwitchConfig Load() => ConfigLoader.Load(ConfigPath);
    public void Dispose() => Directory.Delete(Dir, recursive: true);
}

sealed class TestSpeechProcess : ISpeechProcess
{
    private readonly SpeechProcess inner;
    private readonly Action<string>? observeLine;
    public TestSpeechProcess(Process process, Action<string>? observeLine = null)
    {
        Process = process;
        this.observeLine = observeLine;
        inner = new SpeechProcess(process);
    }

    public Process Process { get; }
    public bool HasExited => inner.HasExited;
    public int ExitCode => inner.ExitCode;
    public string? ReadLine(TimeSpan timeout)
    {
        var line = inner.ReadLine(timeout);
        if (line is not null)
        {
            observeLine?.Invoke(line);
        }

        return line;
    }
    public void Dispose() => inner.Dispose();
}

sealed class FakeTrayRuntimeFactory : ITrayRuntimeFactory
{
    private int order;
    public List<FakeTrayRuntimeRun> Started { get; } = new();
    public bool ThrowOnStop { get; init; }
    public bool FaultCompletionOnStart { get; set; }
    public RecognitionProcessIdentity? InitialOwnedChild { get; set; } =
        new(1234, new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));

    public Task<ITrayRuntimeRun> StartAsync(VoiceSwitchConfig config, TrayInputSource source, CancellationToken cancellation)
    {
        var run = new FakeTrayRuntimeRun(Interlocked.Increment(ref order), () => Interlocked.Increment(ref order), ThrowOnStop);
        run.OwnedChild = InitialOwnedChild;
        if (FaultCompletionOnStart)
        {
            run.Fault(new InvalidOperationException("fixture runtime fault"));
        }

        Started.Add(run);
        return Task.FromResult<ITrayRuntimeRun>(run);
    }
}

sealed class FakeTrayRuntimeRun : ITrayRuntimeRun
{
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<int> nextOrder;

    public FakeTrayRuntimeRun(int startOrder, Func<int> nextOrder, bool throwOnStop)
    {
        StartOrder = startOrder;
        this.nextOrder = nextOrder;
        ThrowOnStop = throwOnStop;
    }

    public int StartOrder { get; }
    public int StopOrder { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool ThrowOnStop { get; set; }
    public Task<int> Completion => completion.Task;
    public int OwnedChildReadCount;
    public TaskCompletionSource? OwnedChildReadStarted { get; set; }
    public ManualResetEventSlim? BlockOwnedChildRead { get; set; }
    private RecognitionProcessIdentity? ownedChild;
    public RecognitionProcessIdentity? OwnedChild
    {
        get
        {
            Interlocked.Increment(ref OwnedChildReadCount);
            OwnedChildReadStarted?.TrySetResult();
            BlockOwnedChildRead?.Wait(TimeSpan.FromSeconds(5));
            return Volatile.Read(ref ownedChild);
        }
        set => Volatile.Write(ref ownedChild, value);
    }

    public void Fault(Exception exception) => completion.TrySetException(exception);

    public Task StopAsync(CancellationToken cancellation)
    {
        StopCount++;
        StopOrder = nextOrder();
        if (ThrowOnStop)
        {
            throw new TimeoutException("fixture stop timeout");
        }

        completion.TrySetResult(0);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCount++;
        completion.TrySetResult(0);
        return ValueTask.CompletedTask;
    }
}

sealed class FixturePcmCapture : IPcmCapture
{
    private readonly IReadOnlyList<PcmFrame> frames;
    private readonly int? delayAfterFrame;

    public FixturePcmCapture(IReadOnlyList<PcmFrame> frames, int? delayAfterFrame = null)
    {
        this.frames = frames;
        this.delayAfterFrame = delayAfterFrame;
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return frames[i];
            if (delayAfterFrame == i)
            {
                await Task.Delay(100, cancellation);
            }

            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class GatedPcmCapture : IPcmCapture
{
    private readonly IReadOnlyList<PcmFrame> beforeGate;
    private readonly IReadOnlyList<PcmFrame> afterGate;
    private readonly ManualResetEventSlim releaseGate;

    public GatedPcmCapture(IReadOnlyList<PcmFrame> beforeGate, IReadOnlyList<PcmFrame> afterGate, ManualResetEventSlim releaseGate)
    {
        this.beforeGate = beforeGate;
        this.afterGate = afterGate;
        this.releaseGate = releaseGate;
    }

    public bool GateWasReached { get; private set; }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        foreach (var frame in beforeGate)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }

        GateWasReached = true;
        await Task.Run(() => releaseGate.Wait(cancellation), cancellation);

        foreach (var frame in afterGate)
        {
            cancellation.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class ThrowingPcmCapture : IPcmCapture
{
    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        yield return new PcmFrame(0, Enumerable.Repeat((short)1, Segmenter.FrameLength).ToImmutableArray());
        await Task.Yield();
        throw new InvalidOperationException("fixture capture failure");
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

sealed class FakeWaveInNative : WinMmCapture.IWaveInNative
{
    private readonly List<IntPtr> headers = new();
    private readonly HashSet<IntPtr> queuedHeaders = new();
    private WinMmCapture.WaveCallback? callback;
    private IntPtr device;
    private bool started;

    public bool BlockRequeue { get; init; }
    public ManualResetEventSlim RequeueEntered { get; } = new(false);
    public ManualResetEventSlim ReleaseRequeue { get; } = new(false);
    public int ResetCount { get; private set; }
    public int UnprepareCount { get; private set; }
    public int CloseCount { get; private set; }
    public bool AddBufferAfterFinalReset { get; private set; }
    public bool PrematureUnprepare { get; private set; }

    public int Open(out IntPtr openedDevice, int deviceId, ref WinMmCapture.WaveFormat format, WinMmCapture.WaveCallback waveCallback, IntPtr instance, int flags)
    {
        callback = waveCallback;
        device = new IntPtr(1234);
        openedDevice = device;
        return 0;
    }

    public int PrepareHeader(IntPtr openedDevice, IntPtr header, int size) => 0;

    public int AddBuffer(IntPtr openedDevice, IntPtr header, int size)
    {
        if (!headers.Contains(header))
        {
            headers.Add(header);
        }

        if (started && BlockRequeue)
        {
            RequeueEntered.Set();
            ReleaseRequeue.Wait(TimeSpan.FromSeconds(2));
        }

        if (ResetCount > 0)
        {
            AddBufferAfterFinalReset = true;
        }

        queuedHeaders.Add(header);
        return 0;
    }

    public int Start(IntPtr openedDevice)
    {
        started = true;
        return 0;
    }

    public int Reset(IntPtr openedDevice)
    {
        ResetCount++;
        queuedHeaders.Clear();
        return 0;
    }

    public int UnprepareHeader(IntPtr openedDevice, IntPtr header, int size)
    {
        UnprepareCount++;
        if (!queuedHeaders.Remove(header))
        {
            return 0;
        }

        PrematureUnprepare = true;
        return 33;
    }

    public int Close(IntPtr openedDevice)
    {
        CloseCount++;
        return 0;
    }

    public void SignalOneBuffer()
    {
        var header = headers[0];
        queuedHeaders.Remove(header);
        Marshal.WriteInt32(header, IntPtr.Size + sizeof(int), 2);
        callback?.Invoke(device, WinMmCapture.WaveInputDataMessage, IntPtr.Zero, header, IntPtr.Zero);
    }
}

sealed class ScriptedDictationRecognizer : IDictationRecognizer
{
    private readonly Func<RecognitionRequest, Task<RecognizedUtterance>> recognize;
    private readonly List<RecognitionRequest> requests = new();

    public ScriptedDictationRecognizer(Func<RecognitionRequest, Task<RecognizedUtterance>> recognize)
    {
        this.recognize = recognize;
    }

    public IReadOnlyList<RecognitionRequest> Requests => requests;

    public async Task<RecognizedUtterance> RecognizeAsync(RecognitionRequest request, CancellationToken cancellation)
    {
        requests.Add(request);
        return await recognize(request);
    }
}

sealed class RecordingDictationHandoff : IDictationHandoff
{
    private readonly List<DictationAudio> submissions = new();
    private readonly Action<DictationAudio>? onSubmit;

    public RecordingDictationHandoff(Action<DictationAudio>? onSubmit = null)
    {
        this.onSubmit = onSubmit;
    }

    public IReadOnlyList<DictationAudio> Submissions => submissions;

    public Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        submissions.Add(audio);
        onSubmit?.Invoke(audio);
        return Task.FromResult(new HandoffResult(HandoffStatus.DryRunSuppressed, audio.SessionId, null, "recorded by test"));
    }
}

sealed class CapturingTextWriter : TextWriter
{
    private readonly StringBuilder output = new();
    private readonly StringBuilder currentLine = new();
    private readonly Action<string>? observeLine;

    public CapturingTextWriter(Action<string>? observeLine)
    {
        this.observeLine = observeLine;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void WriteLine(string? value)
    {
        Write(value);
        WriteLine();
    }

    public override void WriteLine()
    {
        string line;
        lock (output)
        {
            output.AppendLine();
            line = currentLine.ToString();
            currentLine.Clear();
        }

        observeLine?.Invoke(line);
    }

    public override void Write(char value)
    {
        string? line = null;
        lock (output)
        {
            output.Append(value);
            if (value == '\n')
            {
                line = currentLine.ToString().TrimEnd('\r');
                currentLine.Clear();
            }
            else
            {
                currentLine.Append(value);
            }
        }

        if (line is not null)
        {
            observeLine?.Invoke(line);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }

        foreach (var ch in value)
        {
            Write(ch);
        }
    }

    public override string ToString()
    {
        lock (output)
        {
            return output.ToString();
        }
    }
}

enum TestStatus
{
    Pass,
    Fail,
    Skip,
}

sealed record TestOutcome(TestStatus Status, string Message = "")
{
    public static TestOutcome Pass() => new(TestStatus.Pass);
    public static TestOutcome Fail(string message = "") => new(TestStatus.Fail, message);
    public static TestOutcome Skip(string message) => new(TestStatus.Skip, message);
}
