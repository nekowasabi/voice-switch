using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed record PcmFrame(long Start, ImmutableArray<short> Samples);

public interface IPcmCapture : IAsyncDisposable
{
    IAsyncEnumerable<PcmFrame> ReadFramesAsync(CancellationToken cancellation);
}

public sealed record RecognitionProcessIdentity(int ProcessId, DateTimeOffset StartTimeUtc);

public sealed record RecognitionDiagnostic(
    RecognitionRequest Request,
    DateTimeOffset StartTime,
    long ElapsedMilliseconds,
    long? ChildCpuMilliseconds,
    int? ChildProcessId = null,
    bool Running = false,
    RecognitionProcessIdentity? ChildProcess = null);

public interface IDictationRuntimeObserver
{
    void RecognitionQueued(RecognitionRequest request, int pending, long retainedStart, long retainedEnd);
    void RecognitionCompleted(RecognitionRequest request, RecognizedUtterance? recognition, Exception? error);
    void HandoffSubmitted(DictationAudio audio, HandoffResult result);
    void RetentionObserved(long retainedStart, long retainedEnd, int pending);
    void NoiseProcessorCompleted(NoiseProcessorStatus status);
}

public sealed class WindowsDictationRuntime
{
    private const int MaxPendingRecognition = 8;
    private readonly VoiceSwitchConfig config;
    private readonly IPcmCapture capture;
    private readonly IDictationRecognizer recognizer;
    private readonly IDictationHandoff handoff;
    private readonly IDictationRuntimeObserver? observer;
    private readonly bool dryRun;
    private readonly SampleStore originalStore;
    private readonly SampleStore analysisStore;
    private readonly Segmenter segmenter;
    private readonly long idleRetainSamples;
    private long nextRecognitionId;
    private long eligibleRecognitionStart;
    private bool busy;
    private bool lastVadSpeech;
    private string? lastSilenceDiagnosticKey;

    public WindowsDictationRuntime(
        VoiceSwitchConfig config,
        IPcmCapture capture,
        IDictationRecognizer recognizer,
        IDictationHandoff handoff,
        bool dryRun,
        IDictationRuntimeObserver? observer = null)
    {
        this.config = config;
        this.capture = capture;
        this.recognizer = recognizer;
        this.handoff = handoff;
        this.observer = observer;
        this.dryRun = dryRun;
        var retainedSamples = checked((long)((config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60) + 10) * (long)Segmenter.Rate);
        originalStore = new SampleStore(retainedSamples);
        analysisStore = new SampleStore(retainedSamples);
        segmenter = new Segmenter(config with { MaxSeconds = config.Dictation?.MaxSeconds ?? config.MaxSeconds }) { AdaptFloor = true };
        idleRetainSamples = checked((long)((config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60) + 2) * (long)Segmenter.Rate);
    }

    public async Task<int> RunAsync(CancellationToken cancellation)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var frames = Channel.CreateBounded<AnalysisFrame>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var recognitions = Channel.CreateUnbounded<RecognitionOutcome>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var requests = Channel.CreateBounded<RecognitionWork>(new BoundedChannelOptions(MaxPendingRecognition)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var captureTask = PumpCaptureAsync(frames.Writer, linked.Token);
        var recognitionTask = PumpRecognitionAsync(requests.Reader, recognitions.Writer, linked.Token);
        var pending = new Dictionary<long, RecognitionWork>();
        var lastLiveSpeechEnd = 0L;
        var captureFlushed = false;
        try
        {
            Log.Info("dictation: Windows PCM capture, finite SAPI recognition, and manual Superwhisper handoff are enabled");
            Log.Info($"dictation timing: startTimeoutMs={config.Dictation?.StartTimeoutMs ?? 3000} endSilenceMs={config.Dictation?.EndSilenceMs ?? 1200} hangoverMs={config.HangoverMs ?? 300} minSpeechMs={config.MinSpeechMs ?? 300} maxSeconds={config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60}");
            if (dryRun)
            {
                Log.Info("dictation dry-run: WAV creation and external app launch are suppressed");
            }

            var session = new DictationSession(config);
            var captureDone = false;
            while (!linked.IsCancellationRequested)
            {
                var frameReady = captureDone
                    ? Task.FromResult(false)
                    : frames.Reader.WaitToReadAsync(linked.Token).AsTask();
                var recognitionReady = recognitions.Reader.WaitToReadAsync(linked.Token).AsTask();
                if (captureDone)
                {
                    await recognitionReady;
                }
                else
                {
                    _ = await Task.WhenAny(frameReady, recognitionReady);
                }

                if (frameReady.IsFaulted)
                {
                    await frameReady;
                }

                if (recognitionReady.IsFaulted)
                {
                    await recognitionReady;
                }
                if (frameReady.IsCompletedSuccessfully && !frameReady.Result)
                {
                    captureDone = true;
                    if (!captureFlushed)
                    {
                        if (!FlushOpenUtterance(requests.Writer, pending))
                        {
                            session.Cancel(FinishReason.Overflow);
                            return 1;
                        }

                        captureFlushed = true;
                    }

                    requests.Writer.TryComplete();
                }

                if (frameReady.IsCompletedSuccessfully && frameReady.Result)
                {
                    while (frames.Reader.TryRead(out var frame))
                    {
                        if (!ProcessFrame(frame, session, requests.Writer, pending, ref lastLiveSpeechEnd))
                        {
                            session.Cancel(FinishReason.Overflow);
                            return 1;
                        }

                        if (pending.Count == 0 && session.AwaitingWakeSourceEnd is long wakeSourceEnd && lastLiveSpeechEnd <= wakeSourceEnd)
                        {
                            if (session.AdvanceTo(analysisStore.Next, originalStore.Copy) is not null)
                            {
                                session = ResetSession(pending, analysisStore.Next);
                                TrimStore(session, pending);
                                continue;
                            }

                            if (session.IsTerminal)
                            {
                                session = ResetSession(pending, analysisStore.Next);
                                TrimStore(session, pending);
                                continue;
                            }
                        }

                        if (pending.Count == 0 && session.AdvanceSpeechTo(lastLiveSpeechEnd, originalStore.Copy) is { } advanced)
                        {
                            await SubmitAsync(advanced, linked.Token);
                            session = ResetSession(pending, advanced.Range.End);
                            TrimStore(session, pending);
                            continue;
                        }

                        if (await TryFinishSilenceAsync(session, pending, linked.Token))
                        {
                            session = ResetSession(pending, eligibleRecognitionStart);
                            TrimStore(session, pending);
                        }

                        if (session.IsTerminal)
                        {
                            session = ResetSession(pending, analysisStore.Next);
                        }

                        TrimStore(session, pending);
                    }
                }

                if (recognitionReady.IsCompletedSuccessfully && recognitionReady.Result)
                {
                    while (recognitions.Reader.TryRead(out var outcome))
                    {
                        var pendingBefore = pending.Count;
                        if (outcome.Recognition is { } observed)
                        {
                            var leadingWake = DictationBoundaries.LeadingWake(observed, config.WakeWords) is not null;
                            var stopRange = DictationBoundaries.StandaloneStopRange(observed, config.StopWords ?? []);
                            Log.Info($"dictation recognition: complete id={outcome.Work.Request.Id} extent={outcome.Work.Request.Extent} range={outcome.Work.Request.Range.Start}..{outcome.Work.Request.Range.End} rejected={observed.HadRejectedSpeech} leadingWake={leadingWake} standaloneStop={stopRange is not null} stopRange={(stopRange is null ? "-" : $"{stopRange.Value.Start}..{stopRange.Value.End}")} pendingBefore={pendingBefore}");
                        }
                        observer?.RecognitionCompleted(outcome.Work.Request, outcome.Recognition, outcome.Error);

                        if (!pending.Remove(outcome.Work.Request.Id) || outcome.Work.Request.Range.Start < eligibleRecognitionStart)
                        {
                            if (outcome.Recognition is { } stale)
                            {
                                var staleStop = DictationBoundaries.StandaloneStopRange(stale, config.StopWords ?? []) is not null;
                                Log.Info($"dictation recognition: stale id={outcome.Work.Request.Id} range={outcome.Work.Request.Range.Start}..{outcome.Work.Request.Range.End} standaloneStop={staleStop} eligibleStart={eligibleRecognitionStart}");
                            }

                            continue;
                        }

                        if (outcome.Error is not null)
                        {
                            Log.Info($"dictation recognition failed: {outcome.Error.Message}");
                            session.Cancel(FinishReason.RecognitionFailed);
                            session = ResetSession(pending, outcome.Work.Request.Range.End);
                            TrimStore(session, pending);
                            continue;
                        }

                        var wasActive = session.IsActive;
                        var wasAwaiting = session.IsAwaitingBody;
                        var wasIdle = !wasActive && !wasAwaiting;
                        var stopBeforeApply = DictationBoundaries.StandaloneStopRange(outcome.Recognition!, config.StopWords ?? []);
                        var audio = session.Apply(outcome.Recognition!, originalStore.Copy);
                        if (wasIdle && session.IsAwaitingBody)
                        {
                            Log.Info($"dictation session: wake-only id={outcome.Work.Request.Id} source={outcome.Work.Request.Range.Start}..{outcome.Work.Request.Range.End}");
                        }
                        else if (!wasActive && session.PendingBody is { } started)
                        {
                            Log.Info($"dictation session: body-start id={outcome.Work.Request.Id} range={started.Start}..{started.End}");
                        }
                        else if (wasIdle && stopBeforeApply is not null && !session.IsActive && !session.IsAwaitingBody)
                        {
                            Log.Info($"dictation session: ignored-stop-no-active id={outcome.Work.Request.Id} stopRange={stopBeforeApply.Value.Start}..{stopBeforeApply.Value.End}");
                        }

                        if (audio is not null)
                        {
                            if (audio.Reason == FinishReason.StandaloneStop && stopBeforeApply is { } stop)
                            {
                                Log.Info($"dictation session: standalone-stop id={outcome.Work.Request.Id} trimExcluded={audio.Range.End}..{stop.End}");
                            }

                            await SubmitAsync(audio, linked.Token);
                            session = ResetSession(pending, outcome.Recognition!.Source.End);
                        }

                        if (pending.Count == 0)
                        {
                            if (session.AwaitingWakeSourceEnd is long wakeSourceEnd && lastLiveSpeechEnd <= wakeSourceEnd)
                            {
                                _ = session.AdvanceTo(analysisStore.Next, originalStore.Copy);
                            }

                            var advanced = session.AdvanceSpeechTo(lastLiveSpeechEnd, originalStore.Copy);
                            if (advanced is not null)
                            {
                                await SubmitAsync(advanced, linked.Token);
                                session = ResetSession(pending, advanced.Range.End);
                            }
                            else if (await TryFinishSilenceAsync(session, pending, linked.Token))
                            {
                                session = ResetSession(pending, eligibleRecognitionStart);
                            }
                        }

                        if (session.IsTerminal)
                        {
                            session = ResetSession(pending, outcome.Work.Request.Range.End);
                        }

                        TrimStore(session, pending);
                    }
                }

                if (captureDone && recognitionTask.IsCompleted && !recognitions.Reader.TryPeek(out _))
                {
                    if (pending.Count == 0 && session.Finish(FinishReason.Silence, originalStore.Copy) is { } eof)
                    {
                        await SubmitAsync(eof, linked.Token);
                        session = ResetSession(pending, eof.Range.End);
                        TrimStore(session, pending);
                    }

                    break;
                }
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            try
            {
                linked.Cancel();
                requests.Writer.TryComplete();
                recognitions.Writer.TryComplete();
                try { await Task.WhenAll(captureTask, recognitionTask); }
                catch (OperationCanceledException) { }
            }
            finally
            {
                await capture.DisposeAsync();
            }
        }
    }

    private bool ProcessFrame(
        AnalysisFrame frame,
        DictationSession session,
        ChannelWriter<RecognitionWork> requests,
        Dictionary<long, RecognitionWork> pending,
        ref long lastLiveSpeechEnd)
    {
        if (!session.IsActive
            && !session.IsAwaitingBody
            && pending.Count == 0
            && analysisStore.Next - analysisStore.Start > idleRetainSamples)
        {
            segmenter.Reset();
            var keepFrom = Math.Max(analysisStore.Start, analysisStore.Next - Segmenter.Frames(config.PrerollMs ?? 300) * Segmenter.FrameLength);
            originalStore.RetainFrom(keepFrom);
            analysisStore.RetainFrom(keepFrom);
        }

        originalStore.Append(frame.Start, frame.Original.AsSpan());
        analysisStore.Append(frame.Start, frame.Analysis.AsSpan());
        var ev = segmenter.Push(ToFloat(frame.Analysis.AsSpan()));
        if (segmenter.LastWasSpeech != lastVadSpeech)
        {
            Log.Info(segmenter.LastWasSpeech
                ? $"dictation vad: speech-onset at={frame.Start}"
                : $"dictation vad: speech-end at={frame.Start}");
            lastVadSpeech = segmenter.LastWasSpeech;
        }

        if (segmenter.LastWasSpeech)
        {
            lastLiveSpeechEnd = frame.Start + frame.Analysis.Length;
        }

        if (ev is null)
        {
            return true;
        }

        return QueueRecognition(ev, frame.Start + frame.Analysis.Length, requests, pending);
    }

    private bool FlushOpenUtterance(
        ChannelWriter<RecognitionWork> requests,
        Dictionary<long, RecognitionWork> pending)
    {
        var ev = segmenter.Flush();
        return ev is null || QueueRecognition(ev, analysisStore.Next, requests, pending);
    }

    private bool QueueRecognition(
        SegmenterEvent ev,
        long rangeEnd,
        ChannelWriter<RecognitionWork> requests,
        Dictionary<long, RecognitionWork> pending)
    {
        var range = new SampleRange(rangeEnd - ev.Samples.Length, rangeEnd);
        var request = new RecognitionRequest(
            Interlocked.Increment(ref nextRecognitionId),
            ev.Kind == "head" ? RecognitionExtent.PrefixHead : RecognitionExtent.ClosedUtterance,
            range,
            analysisStore.Copy(range));
        var work = new RecognitionWork(request);
        if (!requests.TryWrite(work))
        {
            return false;
        }

        pending[request.Id] = work;
        observer?.RecognitionQueued(request, pending.Count, analysisStore.Start, analysisStore.Next);
        Log.Info($"dictation recognition: enqueued id={request.Id} extent={request.Extent} range={request.Range.Start}..{request.Range.End} pending={pending.Count}");
        return true;
    }

    private DictationSession ResetSession(Dictionary<long, RecognitionWork> pending, long preserveFrom)
    {
        eligibleRecognitionStart = Math.Max(eligibleRecognitionStart, preserveFrom);
        foreach (var item in pending.Where(item => item.Value.Request.Range.Start < eligibleRecognitionStart).Select(item => item.Key).ToArray())
        {
            pending.Remove(item);
        }

        lastSilenceDiagnosticKey = null;
        return new DictationSession(config);
    }

    private async Task<bool> TryFinishSilenceAsync(
        DictationSession session,
        Dictionary<long, RecognitionWork> pending,
        CancellationToken cancellation)
    {
        if (session.PendingBody is not { } body || segmenter.LastWasSpeech)
        {
            return false;
        }

        var silenceAt = body.End + MsToSamples(config.Dictation?.EndSilenceMs ?? 1200);
        var open = segmenter.HasOpenUtterance;
        if (analysisStore.Next < silenceAt)
        {
            LogSilence("armed", analysisStore.Next, body.End, silenceAt, pending.Count, open);
            return false;
        }

        if (pending.Count > 0 || open)
        {
            LogSilence("deferred", analysisStore.Next, body.End, silenceAt, pending.Count, open);
            return false;
        }

        LogSilence("fired", analysisStore.Next, body.End, silenceAt, pending.Count, open);
        if (session.FinishSilenceAt(analysisStore.Next, originalStore.Copy) is not { } silence)
        {
            return false;
        }

        await SubmitAsync(silence, cancellation);
        eligibleRecognitionStart = Math.Max(eligibleRecognitionStart, silence.Range.End);
        return true;
    }

    private void LogSilence(string state, long clock, long lastSpeech, long silenceAt, int pending, bool open)
    {
        var key = $"{state}:{lastSpeech}:{silenceAt}:{pending}:{open}";
        if (key == lastSilenceDiagnosticKey)
        {
            return;
        }

        lastSilenceDiagnosticKey = key;
        Log.Info($"dictation silence: {state} clock={clock} lastSpeech={lastSpeech} silenceAt={silenceAt} pending={pending} openUtterance={open}");
    }

    private void TrimStore(DictationSession session, Dictionary<long, RecognitionWork> pending)
    {
        if (segmenter.HasOpenUtterance)
        {
            return;
        }

        long? first = Math.Max(analysisStore.Start, analysisStore.Next - Segmenter.Frames(config.PrerollMs ?? 300) * Segmenter.FrameLength);
        if (session.RequiredAudioStart is long required)
        {
            first = Math.Min(first.Value, required);
        }

        foreach (var request in pending.Values)
        {
            first = first is long value ? Math.Min(value, request.Request.Range.Start) : request.Request.Range.Start;
        }

        var retainFrom = first ?? analysisStore.Next;
        originalStore.RetainFrom(retainFrom);
        analysisStore.RetainFrom(retainFrom);
        observer?.RetentionObserved(analysisStore.Start, analysisStore.Next, pending.Count);
    }

    private async Task SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        if (busy)
        {
            Log.Info("dictation handoff busy; external submission refused");
            return;
        }

        busy = true;
        try
        {
            var result = await handoff.SubmitAsync(audio, cancellation);
            observer?.HandoffSubmitted(audio, result);
            Log.Info($"dictation handoff: {result.Status} {result.Id} wav={result.Path ?? "-"} reason={audio.Reason} range={audio.Range.Start}..{audio.Range.End} {result.Message}");
        }
        finally
        {
            busy = false;
        }
    }

    private async Task PumpCaptureAsync(ChannelWriter<AnalysisFrame> writer, CancellationToken cancellation)
    {
        try
        {
            var emitted = new Queue<AnalysisFrame>();
            var processor = new NoiseProcessor(
                config.NoiseReduction,
                emitted.Enqueue,
                config.VadMinRMS ?? 0.005f);
            await foreach (var frame in capture.ReadFramesAsync(cancellation).WithCancellation(cancellation))
            {
                processor.Push(frame);
                while (emitted.TryDequeue(out var analysis))
                {
                    await writer.WriteAsync(analysis, cancellation);
                }
            }

            processor.Complete();
            observer?.NoiseProcessorCompleted(processor.Status);
            while (emitted.TryDequeue(out var analysis))
            {
                await writer.WriteAsync(analysis, cancellation);
            }

            writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
        }
    }

    private async Task PumpRecognitionAsync(ChannelReader<RecognitionWork> reader, ChannelWriter<RecognitionOutcome> writer, CancellationToken cancellation)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(cancellation))
            {
                try
                {
                    var recognition = await recognizer.RecognizeAsync(work.Request, cancellation);
                    await writer.WriteAsync(new RecognitionOutcome(work, recognition, null), cancellation);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await writer.WriteAsync(new RecognitionOutcome(work, null, ex), cancellation);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static float[] ToFloat(ReadOnlySpan<short> samples)
    {
        var values = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            values[i] = samples[i] / 32768f;
        }

        return values;
    }

    private static long MsToSamples(int ms) =>
        checked((long)Math.Round(ms * Segmenter.Rate / 1000.0, MidpointRounding.AwayFromZero));

    private sealed record RecognitionWork(RecognitionRequest Request);
    private sealed record RecognitionOutcome(RecognitionWork Work, RecognizedUtterance? Recognition, Exception? Error);
}

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

    public WinMmCapture()
        : this(new PInvokeWaveInNative())
    {
    }

    internal WinMmCapture(IWaveInNative native)
    {
        this.native = native;
        callback = OnWaveIn;
    }

    public static WinMmCapture Open()
    {
        var capture = new WinMmCapture();
        capture.Start();
        return capture;
    }

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
            var result = native.Open(out device, WaveMapper, ref format, callback, IntPtr.Zero, CallbackFunction);
            if (result != 0)
            {
                throw new InvalidOperationException($"WinMM could not open default PCM16/16000 mono input. waveInOpen={result}");
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

public sealed class SpeechPowerShellDictationRecognizer : IDictationRecognizer
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly VoiceSwitchConfig config;
    private readonly Action<RecognitionDiagnostic>? observe;

    public SpeechPowerShellDictationRecognizer(VoiceSwitchConfig config, Action<RecognitionDiagnostic>? observe = null)
    {
        this.config = config;
        this.observe = observe;
    }

    public async Task<RecognizedUtterance> RecognizeAsync(RecognitionRequest request, CancellationToken cancellation)
    {
        var psi = CreatePowerShell();
        psi.Environment["VOICE_SWITCH_LOCALE"] = config.EffectiveLocale;
        psi.Environment["VOICE_SWITCH_REQUEST_ID"] = request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["VOICE_SWITCH_REQUEST_START"] = request.Range.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["VOICE_SWITCH_REQUEST_END"] = request.Range.End.ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["VOICE_SWITCH_REQUEST_EXTENT"] = request.Extent.ToString();
        var pcm = ToBytes(request.Samples.AsSpan());
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start powershell.exe");
        RecognitionProcessIdentity? identity = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        long? childCpuMilliseconds = null;
        try
        {
            identity = ReadIdentity(process);
            observe?.Invoke(new RecognitionDiagnostic(request, identity.StartTimeUtc, 0, null, identity.ProcessId, Running: true, identity));
            using var killOnCancel = cancellation.Register(static state => KillProcess((Process)state!), process);
            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.BaseStream.WriteAsync(pcm, cancellation);
            process.StandardInput.Close();
            var timeout = TimeSpan.FromSeconds(Math.Max(10, pcm.Length / 32000.0 + 20));
            var waitTask = process.WaitForExitAsync(CancellationToken.None);
            if (await Task.WhenAny(waitTask, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false) != waitTask)
            {
                KillProcess(process);
                throw new TimeoutException("dictation recognizer timed out.");
            }

            await waitTask.ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
        }
        catch
        {
            KillProcess(process);
            throw;
        }
        finally
        {
            await RetainUntilProcessExitedAsync(
                () => process.HasExited,
                () => KillProcess(process)).ConfigureAwait(false);

            if (identity is not null)
            {
                await DrainCompletedPipeAsync(outputTask).ConfigureAwait(false);
                await DrainCompletedPipeAsync(errorTask).ConfigureAwait(false);
                childCpuMilliseconds = TryGetChildCpuMilliseconds(process);
                observe?.Invoke(new RecognitionDiagnostic(request, identity.StartTimeUtc, stopwatch.ElapsedMilliseconds, childCpuMilliseconds, identity.ProcessId, Running: false, identity));
            }
        }

        var output = await (outputTask ?? Task.FromResult("")).ConfigureAwait(false);
        var error = await (errorTask ?? Task.FromResult("")).ConfigureAwait(false);
        stopwatch.Stop();

        var line = output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()
            ?? "";
        var dto = JsonSerializer.Deserialize<RecognitionDto>(line, JsonOptions)
            ?? throw new InvalidOperationException(process.ExitCode == 0
                ? "dictation recognizer returned no JSON"
                : $"dictation recognizer failed {process.ExitCode}: {error}");
        if (dto.Type == "error")
        {
            throw new InvalidOperationException($"dictation recognizer failed {process.ExitCode}: {dto.Message ?? error}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dictation recognizer failed {process.ExitCode}: {dto.Message ?? error}");
        }

        if (dto.Id != request.Id)
        {
            throw new InvalidOperationException($"dictation recognizer returned id {dto.Id}, expected {request.Id}.");
        }

        var lexemes = (dto.Lexemes ?? [])
            .Select(item => new LexicalRun(item.Text ?? "", new SampleRange(item.Start, item.End)))
            .ToImmutableArray();
        if (lexemes.Any(item => item.Range.Start < request.Range.Start || item.Range.End > request.Range.End))
        {
            throw new InvalidOperationException("dictation recognizer returned lexical ranges outside the request.");
        }

        return new RecognizedUtterance(dto.Id, request.Extent, request.Range, dto.Text ?? "", lexemes, dto.Rejected);
    }

    internal static async Task RetainUntilProcessExitedAsync(Func<bool> hasExited, Action requestKill, TimeSpan? retryDelay = null)
    {
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        if (delay <= TimeSpan.Zero)
        {
            delay = TimeSpan.FromMilliseconds(250);
        }

        while (true)
        {
            try
            {
                if (hasExited())
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                requestKill();
            }
            catch
            {
            }

            try
            {
                if (hasExited())
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(delay, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static RecognitionProcessIdentity ReadIdentity(Process process)
    {
        var startTime = DateTime.SpecifyKind(process.StartTime.ToUniversalTime(), DateTimeKind.Utc);
        return new RecognitionProcessIdentity(process.Id, new DateTimeOffset(startTime));
    }

    private static long? TryGetChildCpuMilliseconds(Process process)
    {
        try
        {
            return (long)Math.Round(process.TotalProcessorTime.TotalMilliseconds, MidpointRounding.AwayFromZero);
        }
        catch
        {
            return null;
        }
    }

    private static async Task DrainCompletedPipeAsync(Task<string>? pipeTask)
    {
        if (pipeTask is null)
        {
            return;
        }

        try
        {
            _ = await pipeTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static ProcessStartInfo CreatePowerShell()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        return new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static byte[] ToBytes(ReadOnlySpan<short> samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private sealed record RecognitionDto(string? Type, string? Message, long Id, string? Text, bool Rejected, LexemeDto[] Lexemes);
    private sealed record LexemeDto(string? Text, long Start, long End);

private const string Script = """
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Speech
function Send-Json($obj) { $obj | ConvertTo-Json -Compress -Depth 6 }
$locale = $env:VOICE_SWITCH_LOCALE
$requestId = [int64]$env:VOICE_SWITCH_REQUEST_ID
$requestStart = [int64]$env:VOICE_SWITCH_REQUEST_START
$requestEnd = [int64]$env:VOICE_SWITCH_REQUEST_END
$inputStream = [Console]::OpenStandardInput()
$pcmStream = [System.IO.MemoryStream]::new()
$inputStream.CopyTo($pcmStream)
$pcm = $pcmStream.ToArray()
Add-Type -ReferencedAssemblies System.Speech -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;

public sealed class VoiceSwitchSapiCollector
{
    private readonly long requestId;
    private readonly long requestStart;
    private readonly long requestEnd;
    private readonly List<string> texts = new List<string>();
    private readonly List<VoiceSwitchLexeme> lexemes = new List<VoiceSwitchLexeme>();
    private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
    private bool rejected;
    private string error;

    public VoiceSwitchSapiCollector(long requestId, long requestStart, long requestEnd)
    {
        this.requestId = requestId;
        this.requestStart = requestStart;
        this.requestEnd = requestEnd;
    }

    public long Id { get { return requestId; } }
    public string Text { get { return string.Concat(texts); } }
    public bool Rejected { get { return rejected; } }
    public VoiceSwitchLexeme[] Lexemes { get { return lexemes.ToArray(); } }
    public string Error { get { return error; } }

    public void Run(RecognizerInfo info, byte[] pcm)
    {
        using (var engine = new SpeechRecognitionEngine(info))
        using (var stream = new MemoryStream(pcm, false))
        {
            engine.LoadGrammar(new DictationGrammar());
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechRecognitionRejected += OnRejected;
            engine.RecognizeCompleted += OnCompleted;
            var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
            engine.SetInputToAudioStream(stream, format);
            engine.RecognizeAsync(RecognizeMode.Multiple);
            if (!done.Wait(TimeSpan.FromSeconds(Math.Max(10.0, pcm.Length / 32000.0 + 10.0))))
            {
                rejected = true;
                error = "SAPI recognition did not complete before timeout.";
                try { engine.RecognizeAsyncCancel(); } catch { }
                done.Wait(TimeSpan.FromSeconds(2));
            }
        }
    }

    private void OnRecognized(object sender, SpeechRecognizedEventArgs args)
    {
        var result = args.Result;
        if (result == null)
        {
            rejected = true;
            return;
        }

        if (!string.IsNullOrEmpty(result.Text))
        {
            texts.Add(result.Text);
        }

        foreach (RecognizedWordUnit word in result.Words)
        {
            try
            {
                var wordAudio = result.GetAudioForWordRange(word, word);
                if (wordAudio == null || result.Audio == null)
                {
                    rejected = true;
                    continue;
                }

                var start = requestStart + TickSamples(result.Audio.AudioPosition.Ticks + wordAudio.AudioPosition.Ticks);
                var end = start + TickSamples(wordAudio.Duration.Ticks);
                if (start < requestStart || end > requestEnd || end < start)
                {
                    rejected = true;
                    continue;
                }

                lexemes.Add(new VoiceSwitchLexeme(word.Text, start, end));
            }
            catch
            {
                rejected = true;
            }
        }
    }

    private void OnRejected(object sender, SpeechRecognitionRejectedEventArgs args)
    {
        rejected = true;
    }

    private void OnCompleted(object sender, RecognizeCompletedEventArgs args)
    {
        if (args.Error != null)
        {
            error = args.Error.Message;
        }

        if (args.Cancelled)
        {
            rejected = true;
        }

        done.Set();
    }

    private static long TickSamples(long ticks)
    {
        return (long)Math.Round(((double)ticks) * 16000.0 / 10000000.0, MidpointRounding.AwayFromZero);
    }
}

public sealed class VoiceSwitchLexeme
{
    public VoiceSwitchLexeme(string text, long start, long end)
    {
        Text = text;
        Start = start;
        End = end;
    }

    public string Text { get; private set; }
    public long Start { get; private set; }
    public long End { get; private set; }
}
"@
$infos = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()
$info = $infos | Where-Object { $_.Culture.Name -eq $locale } | Select-Object -First 1
if ($null -eq $info) { Send-Json @{ type='error'; message=("No installed Windows speech recognizer for " + $locale) }; exit 2 }
try {
  $collector = [VoiceSwitchSapiCollector]::new($requestId, $requestStart, $requestEnd)
  $collector.Run($info, $pcm)
  if ($collector.Error) { Send-Json @{ type='error'; message=$collector.Error }; exit 3 }
  Send-Json @{ id=$collector.Id; text=$collector.Text; rejected=$collector.Rejected; lexemes=@($collector.Lexemes | ForEach-Object { @{ text=$_.Text; start=$_.Start; end=$_.End } }) }
} catch {
  Send-Json @{ type='error'; message=$_.Exception.Message }
  exit 3
}
""";
}

public sealed class RegisteredSuperwhisperHandoff : IDictationHandoff
{
    private readonly string root;
    private readonly Func<ProcessStartInfo, Process?> startProcess;
    private readonly Func<string, byte[], CancellationToken, Task> writeBytes;
    private readonly Func<string, string, CancellationToken, Task> writeText;
    private readonly Func<string, bool> deleteFile;
    private readonly bool dryRun;

    public RegisteredSuperwhisperHandoff(
        string root,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        bool dryRun = false,
        Func<string, byte[], CancellationToken, Task>? writeBytes = null,
        Func<string, string, CancellationToken, Task>? writeText = null,
        Func<string, bool>? deleteFile = null)
    {
        this.root = root;
        this.startProcess = startProcess ?? Process.Start;
        this.writeBytes = writeBytes ?? File.WriteAllBytesAsync;
        this.writeText = writeText ?? File.WriteAllTextAsync;
        this.deleteFile = deleteFile ?? TryDelete;
        this.dryRun = dryRun;
    }

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        if (dryRun)
        {
            return new HandoffResult(HandoffStatus.DryRunSuppressed, audio.SessionId, null, "dry-run kept audio in memory only");
        }

        Directory.CreateDirectory(root);
        ValidateOwnedDirectory(root);

        var wavPath = Path.Combine(root, $"{audio.SessionId:N}.wav");
        var manifestPath = Path.Combine(root, $"{audio.SessionId:N}.json");
        var manifest = new HandoffManifest(audio.SessionId, wavPath, Pcm16Wav.Sha256Hex(audio.Samples.AsSpan()), "DispatchingUnconfirmed", DateTimeOffset.UtcNow);
        var dispatched = false;
        FileStream gate;
        try
        {
            gate = OpenSynchronizationFile(root);
        }
        catch (IOException ex)
        {
            return new HandoffResult(HandoffStatus.Busy, audio.SessionId, null, ex.Message);
        }

        try
        {
            using var ownedGate = gate;
            TryDelete(ObsoleteLockPath(root));
            if (HasPending(root))
            {
                return new HandoffResult(HandoffStatus.Busy, audio.SessionId, null, "an unconfirmed owned Superwhisper handoff already exists");
            }

            await WriteManifestAtomicAsync(manifestPath, manifest, writeText, cancellation);
            await writeBytes(wavPath, Pcm16Wav.Encode(audio.Samples.AsSpan()), cancellation);
            var exe = ResolveSuperwhisperExecutable();
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("superwhisper://file//" + wavPath);
            _ = startProcess(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            dispatched = true;
            await WriteManifestAtomicAsync(manifestPath, manifest with { DispatchState = "SubmittedUnconfirmed" }, writeText, CancellationToken.None);
            return new HandoffResult(HandoffStatus.SubmittedUnconfirmed, audio.SessionId, wavPath, "submitted to registered Superwhisper file intake; user confirmation is required before cleanup");
        }
        catch (Exception ex) when (dispatched)
        {
            return new HandoffResult(HandoffStatus.SubmittedUnconfirmed, audio.SessionId, wavPath, $"handoff dispatch state is uncertain after launch; owned file is retained: {ex.Message}");
        }
        catch (Exception ex)
        {
            var wavDeleted = deleteFile(wavPath);
            if (wavDeleted)
            {
                deleteFile(manifestPath + ".tmp");
                deleteFile(manifestPath);
            }

            return new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, ex.Message);
        }
    }

    private static async Task WriteManifestAtomicAsync(
        string manifestPath,
        HandoffManifest manifest,
        Func<string, string, CancellationToken, Task> writeText,
        CancellationToken cancellation)
    {
        var tempPath = manifestPath + ".tmp";
        await writeText(tempPath, JsonSerializer.Serialize(manifest), cancellation);
        File.Move(tempPath, manifestPath, overwrite: true);
    }

    public static HandoffResult CompleteManual(string root, Guid id)
    {
        return CompleteManual(root, id, TryDelete);
    }

    public static HandoffResult CompleteManual(string root, Guid id, Func<string, bool> deleteFile)
    {
        var manifestPath = Path.Combine(root, $"{id:N}.json");
        var wavPath = Path.Combine(root, $"{id:N}.wav");
        FileStream gate;
        try
        {
            Directory.CreateDirectory(root);
            ValidateOwnedDirectory(root);
            gate = OpenSynchronizationFile(root);
        }
        catch (IOException ex)
        {
            return new HandoffResult(HandoffStatus.Busy, id, wavPath, $"owned handoff is still active: {ex.Message}");
        }

        using var ownedGate = gate;
        TryDelete(ObsoleteLockPath(root));
        if (!File.Exists(manifestPath))
        {
            return new HandoffResult(HandoffStatus.NotFound, id, null, "owned handoff manifest was not found");
        }

        HandoffManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<HandoffManifest>(File.ReadAllText(manifestPath));
        }
        catch (JsonException)
        {
        }

        if (manifest is not null)
        {
            if (manifest.Id != id)
            {
                throw new InvalidDataException("handoff manifest id does not match the requested id");
            }

            ValidateOwnedPath(root, manifest.WavPath);
            var expected = Path.GetFullPath(wavPath);
            if (!string.Equals(Path.GetFullPath(manifest.WavPath), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("handoff manifest path does not match the expected UUID-owned WAV path");
            }
        }

        ValidateOwnedPath(root, wavPath);
        if (!deleteFile(wavPath))
        {
            return new HandoffResult(HandoffStatus.CleanupFailed, id, wavPath, "manual completion acknowledged, but owned source cleanup failed");
        }

        if (!deleteFile(manifestPath))
        {
            return new HandoffResult(HandoffStatus.CleanupFailed, id, wavPath, "manual completion acknowledged, but owned state cleanup failed");
        }

        return new HandoffResult(HandoffStatus.CompletedManually, id, wavPath, "manual completion acknowledged; owned source file cleanup attempted");
    }

    private static string ResolveSuperwhisperExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Superwhisper.exe";
        }

        using var key = Registry.ClassesRoot.OpenSubKey(@"Applications\Superwhisper.exe\shell\open\command");
        var command = key?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("Superwhisper registered open command was not found.");
        }

        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                return trimmed[1..end];
            }
        }

        return trimmed.Split(' ', 2)[0];
    }

    private static void ValidateOwnedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("handoff manifest path is outside the owned directory");
        }

        ValidateOwnedDirectory(root);
        for (var current = fullPath; current.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current) ?? "")
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("handoff manifest path points at a reparse point");
                }
            }

            if (string.Equals(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    private static void ValidateOwnedDirectory(string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var current = fullRoot; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current) ?? "")
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("handoff owned directory path crosses a reparse point");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }
    }

    public static string PendingSummary(string root, DateTimeOffset now)
    {
        if (!Directory.Exists(root))
        {
            return "no pending dictation handoff";
        }

        var manifests = Directory.EnumerateFiles(root, "*.json").ToArray();
        if (manifests.Length == 0)
        {
            TryDelete(ObsoleteLockPath(root));
            return "no pending dictation handoff";
        }

        var manifestPath = manifests.OrderBy(path => File.GetCreationTimeUtc(path)).First();
        var id = Path.GetFileNameWithoutExtension(manifestPath);
        var wavPath = Path.Combine(root, $"{id}.wav");
        var created = new DateTimeOffset(File.GetCreationTimeUtc(manifestPath), TimeSpan.Zero);
        try
        {
            var manifest = JsonSerializer.Deserialize<HandoffManifest>(File.ReadAllText(manifestPath));
            if (manifest is not null)
            {
                id = manifest.Id.ToString("D");
                wavPath = manifest.WavPath;
                created = manifest.CreatedAt;
            }
        }
        catch (JsonException)
        {
        }

        var age = now - created;
        var warning = age >= TimeSpan.FromHours(24) ? " overdue; run --complete-handoff after confirming Superwhisper is done" : "";
        return $"pending dictation handoff: id {id}; wav {wavPath}; manifest {manifestPath}; age {age:g}; complete with --complete-handoff {id};{warning}";
    }

    private static bool HasPending(string root) =>
        Directory.EnumerateFiles(root, "*.json").Any();

    private static FileStream OpenSynchronizationFile(string root) =>
        new FileStream(SynchronizationPath(root), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private static string SynchronizationPath(string root) => Path.Combine(root, "pending.sync");

    private static string ObsoleteLockPath(string root) => Path.Combine(root, "pending.lock");

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record HandoffManifest(Guid Id, string WavPath, string Sha256, string DispatchState, DateTimeOffset CreatedAt);
}
