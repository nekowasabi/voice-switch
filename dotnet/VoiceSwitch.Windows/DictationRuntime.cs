using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
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
    // Called on the runtime thread after a frame or recognition batch changes the phase; Ended -> Idle is not reported.
    void PhaseChanged(DictationPhase phase) { }
    // Lone wake word only. Return true when a sound was played so the runtime ignores the microphone briefly.
    bool PlayWakeSound() => false;
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
    private readonly Func<nint> foregroundWindow;
    private readonly DictationHotkeys? hotkeys;
    private readonly Func<string?> readShortcuts;
    private readonly SampleStore originalStore;
    private readonly SampleStore analysisStore;
    private readonly Segmenter segmenter;
    private readonly long idleRetainSamples;
    private long nextRecognitionId;
    private long eligibleRecognitionStart;
    private Task? inflight;
    private bool lastVadSpeech;
    private string? lastSilenceDiagnosticKey;
    private DictationPhase shownPhase;
    private bool endedByUser;
    // Finish pressed while the body was heard but not yet recognized; honored once the body makes the session active.
    private bool finishRequested;
    private nint target;
    // Mac heardSpeech: once the voice after the wake word is heard it stays heard, so a repeated wake word keeps 録音中.
    private bool heardAfterWake;
    // Frames before this sample index carry our own confirmation sound, which must not count as the text starting.
    private long deafUntil;

    public WindowsDictationRuntime(
        VoiceSwitchConfig config,
        IPcmCapture capture,
        IDictationRecognizer recognizer,
        IDictationHandoff handoff,
        bool dryRun,
        IDictationRuntimeObserver? observer = null,
        Func<nint>? foregroundWindow = null,
        DictationHotkeys? hotkeys = null,
        Func<string?>? readShortcuts = null)
    {
        this.config = config;
        this.capture = capture;
        this.recognizer = recognizer;
        this.handoff = handoff;
        this.observer = observer;
        this.dryRun = dryRun;
        this.foregroundWindow = foregroundWindow ?? (() => 0);
        this.hotkeys = hotkeys;
        this.readShortcuts = readShortcuts ?? ReadSuperwhisperPreferences;
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
            Log.Info("dictation: Windows PCM capture, finite SAPI recognition, and Superwhisper file handoff are enabled");
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

                        if (ApplyHotkey(ref session, pending, lastLiveSpeechEnd))
                        {
                            TrimStore(session, pending);
                            continue;
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
                            Submit(advanced);
                            session = ResetSession(pending, advanced.Range.End);
                            TrimStore(session, pending);
                            continue;
                        }

                        if (TryFinishSilence(session, pending))
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
                        if (wasIdle && (session.IsActive || session.IsAwaitingBody))
                        {
                            // Superwhisper pastes into whatever is frontmost, so remember where the user was when the wake word landed.
                            target = foregroundWindow();
                            hotkeys?.Begin(DictationHotkeys.Load(readShortcuts()));
                        }

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
                        else if (wasAwaiting && stopBeforeApply is not null && session.IsTerminal)
                        {
                            Log.Info($"dictation session: stop-while-waiting id={outcome.Work.Request.Id}");
                            endedByUser = true;
                        }

                        if (audio is not null)
                        {
                            if (audio.Reason == FinishReason.StandaloneStop && stopBeforeApply is { } stop)
                            {
                                Log.Info($"dictation session: standalone-stop id={outcome.Work.Request.Id} trimExcluded={audio.Range.End}..{stop.End}");
                            }

                            Submit(audio);
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
                                Submit(advanced);
                                session = ResetSession(pending, advanced.Range.End);
                            }
                            else if (TryFinishSilence(session, pending))
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
                        Submit(eof);
                        session = ResetSession(pending, eof.Range.End);
                        TrimStore(session, pending);
                    }

                    if (inflight is not null)
                    {
                        await inflight.WaitAsync(linked.Token);
                    }

                    break;
                }

                PublishPhase(session, lastLiveSpeechEnd);
            }

            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return 0;
        }
        finally
        {
            // The EOF break above skips the loop bottom, so a stop word in the last batch is still reported here.
            PublishPhase(endedByUser ? DictationPhase.Ended : DictationPhase.Idle);
            // Pause or quit mid-dictation must give Superwhisper its shortcuts back (Mac: Hotkeys.end() on the empty frame).
            hotkeys?.End();
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
            && (!segmenter.HasOpenUtterance || segmenter.IsSkipping)
            && analysisStore.Next - analysisStore.Start > idleRetainSamples)
        {
            segmenter.Reset();
            var keepFrom = Math.Max(analysisStore.Start, analysisStore.Next - Segmenter.Frames(config.PrerollMs ?? 300) * Segmenter.FrameLength);
            originalStore.RetainFrom(keepFrom);
            analysisStore.RetainFrom(keepFrom);
        }

        originalStore.Append(frame.Start, frame.Original.AsSpan());
        analysisStore.Append(frame.Start, frame.Analysis.AsSpan());
        segmenter.AdaptFloor = !(session.IsActive || session.IsAwaitingBody);
        // Only the VAD goes deaf; the stores keep the real audio so SAPI and the preroll stay intact.
        var ev = segmenter.Push(frame.Start < deafUntil ? new float[frame.Analysis.Length] : ToFloat(frame.Analysis.AsSpan()));
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
        target = 0;
        finishRequested = false;
        heardAfterWake = false;
        hotkeys?.End();
        return new DictationSession(config);
    }

    // True when a hotkey closed the session, which is then already reset.
    private bool ApplyHotkey(ref DictationSession session, Dictionary<long, RecognitionWork> pending, long lastLiveSpeechEnd)
    {
        if (hotkeys is null)
        {
            return false;
        }

        var command = hotkeys.Take();
        if (command == DictationCommand.Cancel && (session.IsActive || session.IsAwaitingBody))
        {
            session.Cancel(FinishReason.CancelCommand);
            Log.Info("dictation cancelled");
            session = ResetSession(pending, analysisStore.Next);
            return true;
        }

        if (command == DictationCommand.Finish && session.IsAwaitingBody && HeardAfterWake(session, lastLiveSpeechEnd))
        {
            Log.Info("dictation finish requested before the body was recognized");
            finishRequested = true;
        }

        if ((command != DictationCommand.Finish && !finishRequested) || !session.IsActive)
        {
            return false;
        }

        var audio = session.AdvanceSpeechTo(lastLiveSpeechEnd, originalStore.Copy) ?? session.Finish(FinishReason.FinishCommand, originalStore.Copy);
        endedByUser = true;
        if (audio is not null)
        {
            Log.Info($"dictation ended by hotkey after {audio.Range.Length * 1000 / (long)Segmenter.Rate} ms of audio");
            Submit(audio);
        }

        session = ResetSession(pending, analysisStore.Next);
        return true;
    }

    private static string? ReadSuperwhisperPreferences()
    {
        try
        {
            var path = WindowsPaths.SuperwhisperPreferencesPath();
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PublishPhase(DictationSession session, long lastLiveSpeechEnd)
    {
        PublishPhase(endedByUser ? DictationPhase.Ended
            : session.IsActive ? DictationPhase.Recording
            : session.IsAwaitingBody ? (HeardAfterWake(session, lastLiveSpeechEnd) ? DictationPhase.Recording : DictationPhase.Waiting)
            : DictationPhase.Idle);
    }

    private bool HeardAfterWake(DictationSession session, long lastLiveSpeechEnd) =>
        heardAfterWake |= session.AwaitingWakeSourceEnd is long wakeEnd && lastLiveSpeechEnd > wakeEnd;

    private void PublishPhase(DictationPhase phase)
    {
        endedByUser = false;
        // Ended hides itself after a moment; the Idle that follows it is not a change.
        if (phase == shownPhase || (shownPhase == DictationPhase.Ended && phase == DictationPhase.Idle))
        {
            return;
        }

        // Only a lone wake word: in a one-breath dictation the user is already talking and the sound would be recorded.
        if (phase == DictationPhase.Waiting && observer?.PlayWakeSound() == true)
        {
            deafUntil = analysisStore.Next + MsToSamples(600);
        }

        shownPhase = phase;
        Log.Info($"dictation phase: {phase}");
        observer?.PhaseChanged(phase);
    }

    private bool TryFinishSilence(DictationSession session, Dictionary<long, RecognitionWork> pending)
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

        Submit(silence);
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

    // Mac parity: one handoff at a time so a recordings-folder result is never attributed to the wrong dictation,
    // and the loop keeps listening while Superwhisper transcribes.
    private void Submit(DictationAudio audio)
    {
        endedByUser |= audio.Reason is FinishReason.StandaloneStop or FinishReason.FinishCommand;
        audio = audio with { Target = target };
        if (inflight is { IsCompleted: false })
        {
            Log.Info($"dictation dropped: previous one still in flight reason={audio.Reason} range={audio.Range.Start}..{audio.Range.End}");
            return;
        }

        inflight = RunHandoffAsync(audio);
    }

    // Not tied to the loop token: pausing or quitting must not abort a handoff Superwhisper is already reading.
    private async Task RunHandoffAsync(DictationAudio audio)
    {
        try
        {
            var result = await handoff.SubmitAsync(audio, CancellationToken.None);
            observer?.HandoffSubmitted(audio, result);
            Log.Info($"dictation handoff: {result.Status} {result.Id} wav={result.Path ?? "-"} reason={audio.Reason} range={audio.Range.Start}..{audio.Range.End} {result.Message}");
        }
        catch (Exception ex)
        {
            Log.Info($"dictation handoff failed: {ex.Message} reason={audio.Reason} range={audio.Range.Start}..{audio.Range.End}");
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
