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
    private VoiceSwitchConfig config;
    private readonly IPcmCapture capture;
    private readonly IDictationRecognizer recognizer;
    private readonly IDictationHandoff handoff;
    private readonly IDictationRuntimeObserver? observer;
    private readonly bool dryRun;
    private readonly Func<nint> foregroundWindow;
    private readonly DictationHotkeys? hotkeys;
    private readonly Func<string?> readShortcuts;
    // Mac ConfigFile.reloadIfChanged: a new valid config, or null when the file is unchanged or invalid.
    private readonly Func<VoiceSwitchConfig?> reloadConfig;
    private readonly Func<nint, string?> windowProcess;
    private readonly Func<IReadOnlyCollection<string>, string?> micInUseBy;
    private readonly Func<string, string?>? wakeReading;
    private readonly SampleStore originalStore;
    private readonly SampleStore analysisStore;
    private readonly Segmenter segmenter;
    private long idleRetainSamples;
    private long nextRecognitionId;
    private long eligibleRecognitionStart;
    private Task? inflight;
    private bool lastVadSpeech;
    private string? lastSilenceDiagnosticKey;
    private DictationPhase shownPhase;
    private bool endedByUser;
    private nint target;
    // Mac heardSpeech: once the voice after the wake word is heard it stays heard, so a repeated wake word keeps 録音中.
    private bool heardAfterWake;
    // Frames before this sample index carry our own confirmation sound, which must not count as the text starting.
    private long deafUntil;
    // Sample positions where the VAD went from quiet to speech, oldest first, pruned with the store.
    private readonly List<long> speechOnsets = new();

    public WindowsDictationRuntime(
        VoiceSwitchConfig config,
        IPcmCapture capture,
        IDictationRecognizer recognizer,
        IDictationHandoff handoff,
        bool dryRun,
        IDictationRuntimeObserver? observer = null,
        Func<nint>? foregroundWindow = null,
        DictationHotkeys? hotkeys = null,
        Func<string?>? readShortcuts = null,
        Func<VoiceSwitchConfig?>? reloadConfig = null,
        Func<nint, string?>? windowProcess = null,
        Func<IReadOnlyCollection<string>, string?>? micInUseBy = null,
        Func<string, string?>? wakeReading = null)
    {
        this.wakeReading = wakeReading;
        config = WithWakeReadings(config);
        this.config = config;
        this.capture = capture;
        this.recognizer = recognizer;
        this.handoff = handoff;
        this.observer = observer;
        this.dryRun = dryRun;
        this.foregroundWindow = foregroundWindow ?? (() => 0);
        this.hotkeys = hotkeys;
        this.readShortcuts = readShortcuts ?? ReadSuperwhisperPreferences;
        this.reloadConfig = reloadConfig ?? (() => null);
        this.windowProcess = windowProcess ?? ProcessOfWindow;
        this.micInUseBy = micInUseBy ?? MicInUse.By;
        originalStore = new SampleStore(RetainedSamples(config, 10));
        analysisStore = new SampleStore(RetainedSamples(config, 10));
        // Mac parity: the segmenter caps at the top-level maxSeconds (2.5 s) so steady room noise is judged within seconds;
        // the body length is the session's dictation.maxSeconds. Capping here at 60 s left the VAD deaf for a minute per lock.
        segmenter = new Segmenter(config) { AdaptFloor = true };
        idleRetainSamples = RetainedSamples(config, 2);
    }

    private static long RetainedSamples(VoiceSwitchConfig config, double extraSeconds) =>
        checked((long)((config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60) + extraSeconds) * (long)Segmenter.Rate);

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
            LogTiming();
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
                        if (!ProcessFrame(frame, ref session, requests.Writer, pending, ref lastLiveSpeechEnd))
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
                            var leadingWake = DictationBoundaries.LeadingWake(observed, config.Wakes()) is not null;
                            var stopRange = DictationBoundaries.StandaloneStopRange(observed, config.StopWords ?? []);
                            // Mac logs "heard:" for what it transcribes while idle and never transcribes the body. Here a body longer
                            // than the segmenter cap arrives as PrefixHead and stays private; a closed utterance inside a session is
                            // a stop-word candidate, so its text, grammar and confidence are logged to make stop misses tunable.
                            var heard = (session.IsActive || session.IsAwaitingBody) && observed.Extent != RecognitionExtent.ClosedUtterance
                                ? ""
                                : $" text=\"{observed.Text}\" conf={(observed.Confidence is double conf ? conf.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "-")}{(observed.RejectedText is { } rejectedText ? $" rejectedText=\"{rejectedText}\"" : "")} grammar={(observed.FromStopGrammar ? "stop" : "dictation")} reading=\"{string.Join(' ', observed.Lexemes.Where(lexeme => lexeme.Reading is not null).Select(lexeme => lexeme.Reading))}\"{(observed.Alternates.IsDefaultOrEmpty ? "" : $" alts=\"{string.Join('|', observed.Alternates.Select(alternate => $"{alternate.Text}/{alternate.Reading}"))}\"")}";
                            Log.Info($"dictation recognition: complete id={outcome.Work.Request.Id} extent={outcome.Work.Request.Extent} range={outcome.Work.Request.Range.Start}..{outcome.Work.Request.Range.End} rejected={observed.HadRejectedSpeech} leadingWake={leadingWake} standaloneStop={stopRange is not null} stopRange={(stopRange is null ? "-" : $"{stopRange.Value.Start}..{stopRange.Value.End}")} pendingBefore={pendingBefore}{heard}");
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
                        var audio = session.Apply(outcome.Recognition!, originalStore.Copy, analysisStore.Next);
                        if (wasIdle && !session.IsActive && !session.IsAwaitingBody && outcome.Work.Request.Extent == RecognitionExtent.PrefixHead)
                        {
                            var floorBefore = segmenter.Floor;
                            segmenter.RebaseFloor();
                            Log.Info($"dictation vad: floor rebased id={outcome.Work.Request.Id} floor={floorBefore:0.0000}->{segmenter.Floor:0.0000} threshold={segmenter.Threshold:0.0000}");
                        }

                        if (wasIdle && (session.IsActive || session.IsAwaitingBody))
                        {
                            // Superwhisper pastes into whatever is frontmost, so remember where the user was when the wake word landed.
                            target = foregroundWindow();
                            if (SkipReason(target) is { } skipped)
                            {
                                Log.Info(skipped);
                                session = ResetSession(pending, outcome.Recognition!.Source.End);
                                TrimStore(session, pending);
                                continue;
                            }

                            hotkeys?.Begin(DictationHotkeys.Load(readShortcuts()));
                        }

                        var prefix = DictationBoundaries.LeadingWake(outcome.Recognition!, config.Wakes());
                        var byReading = prefix?.ByReading is { } readingWake ? $" via=reading d={prefix.Distance} wake=\"{readingWake.Reading}\"" : "";
                        if (wasIdle && session.IsAwaitingBody)
                        {
                            var via = prefix is not null ? byReading : $" via=rejected conf={outcome.Recognition!.Confidence:0.00} rejectedText=\"{outcome.Recognition.RejectedText}\"";
                            Log.Info($"dictation session: wake-only id={outcome.Work.Request.Id} source={outcome.Work.Request.Range.Start}..{outcome.Work.Request.Range.End}{via}");
                        }
                        else if (!wasActive && session.PendingBody is { } started)
                        {
                            Log.Info($"dictation session: body-start id={outcome.Work.Request.Id} range={started.Start}..{started.End}{byReading}");
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
                if (recognizer is IAsyncDisposable warmRecognizer)
                {
                    await warmRecognizer.DisposeAsync();
                }
            }
        }
    }

    private bool ProcessFrame(
        AnalysisFrame frame,
        ref DictationSession session,
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
            if (lastVadSpeech)
            {
                speechOnsets.Add(frame.Start);
            }
        }

        if (segmenter.LastWasSpeech)
        {
            lastLiveSpeechEnd = frame.Start + frame.Analysis.Length;
        }

        if (ev is null)
        {
            return true;
        }

        if (ev.Kind == "head")
        {
            Log.Info($"dictation vad: cap reached at={frame.Start} samples={ev.Samples.Length} rms={segmenter.LastRms:0.0000} floor={segmenter.Floor:0.0000} threshold={segmenter.Threshold:0.0000}");
        }

        // Mac checks between utterances: only here, with nothing open, can wake words and timings change under no one.
        if (!session.IsActive && !session.IsAwaitingBody && pending.Count == 0 && reloadConfig() is { } next)
        {
            ApplyConfig(next);
            session = new DictationSession(config);
        }

        return QueueRecognition(ev, frame.Start + frame.Analysis.Length, requests, pending);
    }

    private void ApplyConfig(VoiceSwitchConfig next)
    {
        next = WithWakeReadings(next);
        // The SAPI child and the noise processor were built from the old config; they change only on a restart.
        if (next.EffectiveLocale != config.EffectiveLocale || next.NoiseReduction != config.NoiseReduction)
        {
            Log.Info("dictation config: locale and noiseReduction changes apply after Reload or a restart");
        }

        config = next;
        segmenter.Config = next;
        originalStore.EnsureCapacity(RetainedSamples(next, 10));
        analysisStore.EnsureCapacity(RetainedSamples(next, 10));
        idleRetainSamples = RetainedSamples(next, 2);
        LogTiming();
    }

    private void LogTiming()
    {
        Log.Info($"dictation timing: startTimeoutMs={config.Dictation?.StartTimeoutMs ?? 3000} endSilenceMs={config.Dictation?.EndSilenceMs ?? 1200} hangoverMs={config.HangoverMs ?? 300} minSpeechMs={config.MinSpeechMs ?? 300} maxSeconds={config.Dictation?.MaxSeconds ?? config.MaxSeconds ?? 60} wakeReadings=\"{string.Join(',', config.Wakes().Select(wake => $"{wake.Text}={wake.Reading}"))}\"");
        if (DictationTimingGuard.ShouldWarnHighEndSilence(config.Dictation?.EndSilenceMs))
        {
            Log.Info($"dictation timing: endSilenceMs unusually high ({config.Dictation?.EndSilenceMs}); example is 2400");
        }

        if (DictationTimingGuard.ShouldWarnHighStartTimeout(config.Dictation?.StartTimeoutMs))
        {
            Log.Info($"dictation timing: startTimeoutMs unusually high ({config.Dictation?.StartTimeoutMs}); example is 3000");
        }
    }

    private VoiceSwitchConfig WithWakeReadings(VoiceSwitchConfig c) =>
        c with { WakeReadings = wakeReading is null ? ImeReadings.Of(c.WakeWords) : c.WakeWords.Select(wakeReading).ToArray() };

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

        if (command != DictationCommand.Finish)
        {
            return false;
        }

        DictationAudio? audio;
        if (session.IsActive)
        {
            audio = session.AdvanceSpeechTo(lastLiveSpeechEnd, originalStore.Copy) ?? session.Finish(FinishReason.FinishCommand, originalStore.Copy);
        }
        else if (HeardBodyStart(session) is long heardStart && HeardAfterWake(session, lastLiveSpeechEnd))
        {
            // Mac hands off what it heard at once; waiting for SAPI to confirm the body cost another 0.2-1 s.
            // The recognition still in flight goes stale with the reset below.
            audio = session.FinishHeard(heardStart, lastLiveSpeechEnd, originalStore.Copy);
        }
        else
        {
            return false;
        }

        endedByUser = true;
        if (audio is not null)
        {
            Log.Info($"dictation ended by hotkey after {audio.Range.Length * 1000 / (long)Segmenter.Rate} ms of audio");
            Submit(audio);
        }

        session = ResetSession(pending, analysisStore.Next);
        return true;
    }

    // Mac order: a recorder already taking the microphone first, then an excluded app in front.
    private string? SkipReason(nint window)
    {
        if (micInUseBy(config.SkipWhileMicInUseBy ?? []) is { } busy)
        {
            // Superwhisper would be recording this speech already, and its record toggle would stop it.
            return $"skipped: {busy} is using the microphone";
        }

        return config.Dictation?.ExcludeProcessNames is { Length: > 0 } excluded
            && windowProcess(window) is { } app
            && excluded.Any(name => string.Equals(Path.GetFileNameWithoutExtension(name), app, StringComparison.OrdinalIgnoreCase))
                ? $"dictation skipped: {app} is excluded"
                : null;
    }

    private static string? ProcessOfWindow(nint window)
    {
        if (window == 0 || !OperatingSystem.IsWindows() || GetWindowThreadProcessId(window, out var pid) == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

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

    // Where the text after a lone wake word starts: its first speech onset less the preroll (Mac keeps the same margin
    // so the first syllable is whole), never reaching back into the wake word. Null until speech follows the wake.
    private long? HeardBodyStart(DictationSession session)
    {
        if (session.AwaitingWakeEnd is not long wakeEnd || session.AwaitingWakeSourceEnd is not long sourceEnd)
        {
            return null;
        }

        var onset = speechOnsets.FindIndex(at => at >= sourceEnd);
        if (onset < 0)
        {
            return null;
        }

        var preroll = (long)Segmenter.Frames(config.PrerollMs ?? 300) * Segmenter.FrameLength;
        return Math.Max(Math.Max(speechOnsets[onset] - preroll, wakeEnd), originalStore.Start);
    }

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
        if (session.PendingBody is not { } body || session.SilenceDeadline is not long silenceAt || segmenter.LastWasSpeech)
        {
            return false;
        }

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
        if ((session.RequiredAudioStart ?? HeardBodyStart(session)) is long required)
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
        speechOnsets.RemoveAll(at => at < analysisStore.Start);
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
    public static string? CurrentDevice { get => Volatile.Read(ref currentDevice); private set => Volatile.Write(ref currentDevice, value); }
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

// Children started here die with voice-switch.exe, even when it crashes or is killed: we never close the job handle,
// so the kernel closes it at our exit and JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE ends every process still in the job.
public static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly Lazy<nint> Job = new(Create);

    public static Process Start(ProcessStartInfo psi)
    {
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {psi.FileName}");
        // A failure only loses the cleanup guarantee, so the child keeps running rather than failing recognition.
        if (OperatingSystem.IsWindows() && (Job.Value == 0 || !AssignProcessToJobObject(Job.Value, process.Handle)))
        {
            Log.Info($"child process job: pid={process.Id} not assigned (error {Marshal.GetLastWin32Error()}); it can outlive voice-switch");
        }

        return process;
    }

    private static nint Create()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var job = CreateJobObject(0, null);
        var info = new ExtendedLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose };
        if (job != 0 && SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            return job;
        }

        Log.Info($"child process job: not created (error {Marshal.GetLastWin32Error()})");
        if (job != 0)
        {
            CloseHandle(job);
        }

        return 0;
    }

    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION with JOBOBJECT_BASIC_LIMIT_INFORMATION and IO_COUNTERS inlined (144 bytes on x64).
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}

// One long-lived powershell.exe hosts SAPI for the whole run. A fresh child per request cost ~650 ms of PowerShell start,
// System.Speech load, C# compile and recognizer enumeration before any audio was heard; the warm child answers in ~100 ms.
public sealed class SpeechPowerShellDictationRecognizer : IDictationRecognizer, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly VoiceSwitchConfig config;
    private readonly Action<RecognitionDiagnostic>? observe;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SapiChild? child;

    public SpeechPowerShellDictationRecognizer(VoiceSwitchConfig config, Action<RecognitionDiagnostic>? observe = null)
    {
        this.config = config;
        this.observe = observe;
        // Warm up now so the first wake word after launch does not pay the child's startup; a failure surfaces on the first request.
        try
        {
            child = SapiChild.Start(config);
        }
        catch
        {
        }
    }

    public async Task<RecognizedUtterance> RecognizeAsync(RecognitionRequest request, CancellationToken cancellation)
    {
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (child is null || child.Process.HasExited)
            {
                if (child is { } exited)
                {
                    child = null;
                    await exited.KillAsync().ConfigureAwait(false);
                }

                child = SapiChild.Start(config);
            }

            var current = child;
            var identity = current.Identity;
            var stopwatch = Stopwatch.StartNew();
            var cpuBefore = TryGetChildCpuMilliseconds(current.Process);
            RecognitionDto dto;
            try
            {
                observe?.Invoke(new RecognitionDiagnostic(request, identity.StartTimeUtc, 0, null, identity.ProcessId, Running: true, identity));
                using var killOnCancel = cancellation.Register(static state => KillProcess((Process)state!), current.Process);
                var pcm = ToBytes(request.Samples.AsSpan());
                var timeout = TimeSpan.FromSeconds(Math.Max(10, pcm.Length / 32000.0 + 20));
                dto = await current.ExchangeAsync(request, pcm, timeout, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
            }
            catch
            {
                child = null;
                await current.KillAsync().ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                throw;
            }
            finally
            {
                var cpuAfter = TryGetChildCpuMilliseconds(current.Process);
                var childCpuMilliseconds = cpuBefore is long before && cpuAfter is long after ? after - before : cpuAfter;
                observe?.Invoke(new RecognitionDiagnostic(request, identity.StartTimeUtc, stopwatch.ElapsedMilliseconds, childCpuMilliseconds, identity.ProcessId, Running: false, identity));
            }

            Log.Info($"dictation recognition: stt id={request.Id} ms={stopwatch.ElapsedMilliseconds} audioMs={request.Samples.Length / 16}");
            return ToUtterance(request, dto);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (child is { } current)
            {
                child = null;
                await current.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static RecognizedUtterance ToUtterance(RecognitionRequest request, RecognitionDto dto)
    {
        if (dto.Id != request.Id)
        {
            throw new InvalidOperationException($"dictation recognizer returned id {dto.Id}, expected {request.Id}.");
        }

        var lexemes = (dto.Lexemes ?? [])
            .Select(item => new LexicalRun(item.Text ?? "", new SampleRange(item.Start, item.End), string.IsNullOrEmpty(item.Reading) ? null : item.Reading))
            .ToImmutableArray();
        if (lexemes.Any(item => item.Range.Start < request.Range.Start || item.Range.End > request.Range.End))
        {
            throw new InvalidOperationException("dictation recognizer returned lexical ranges outside the request.");
        }

        return new RecognizedUtterance(
            dto.Id,
            request.Extent,
            request.Range,
            dto.Text ?? "",
            lexemes,
            dto.Rejected,
            dto.Confidence >= 0 ? dto.Confidence : null,
            string.IsNullOrEmpty(dto.RejectedText) ? null : dto.RejectedText,
            dto.StopGrammar,
            (dto.Alternates ?? []).Select(item => new RecognitionAlternate(item.Text ?? "", item.Reading ?? "")).ToImmutableArray());
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

    private sealed class SapiChild
    {
        private readonly Stream stdin;
        private readonly StreamReader stdout;
        private readonly Task<string> stderr;

        private SapiChild(Process process)
        {
            Process = process;
            Identity = ReadIdentity(process);
            stdin = process.StandardInput.BaseStream;
            stdout = process.StandardOutput;
            // Drained from the start: a child blocked on a full stderr pipe would never answer.
            stderr = process.StandardError.ReadToEndAsync();
        }

        public Process Process { get; }
        public RecognitionProcessIdentity Identity { get; }

        public static SapiChild Start(VoiceSwitchConfig config)
        {
            var psi = CreatePowerShell();
            psi.Environment["VOICE_SWITCH_LOCALE"] = config.EffectiveLocale;
            psi.Environment["VOICE_SWITCH_STOP_WORDS"] = ConfigLoader.ToJsonArray(config.StopWords ?? []);
            var process = ChildProcessJob.Start(psi);
            Log.Info($"dictation recognizer: SAPI child started pid={process.Id}");
            return new SapiChild(process);
        }

        public async Task<RecognitionDto> ExchangeAsync(RecognitionRequest request, byte[] pcm, TimeSpan timeout, CancellationToken cancellation)
        {
            var header = new byte[28];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0), request.Id);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), request.Range.Start);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), request.Range.End);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), pcm.Length);
            await stdin.WriteAsync(header, cancellation).ConfigureAwait(false);
            await stdin.WriteAsync(pcm, cancellation).ConfigureAwait(false);
            await stdin.FlushAsync(cancellation).ConfigureAwait(false);
            while (true)
            {
                string? line;
                try
                {
                    line = await stdout.ReadLineAsync(cancellation).AsTask().WaitAsync(timeout, cancellation).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException("dictation recognizer timed out.");
                }

                if (line is null)
                {
                    var error = await ErrorTextAsync().ConfigureAwait(false);
                    throw new InvalidOperationException($"dictation recognizer exited {(Process.HasExited ? Process.ExitCode : -1)}: {error}");
                }

                RecognitionDto? dto;
                try
                {
                    dto = JsonSerializer.Deserialize<RecognitionDto>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (dto is null || dto.Type == "ready")
                {
                    continue;
                }

                if (dto.Type == "error")
                {
                    throw new InvalidOperationException($"dictation recognizer failed: {dto.Message}");
                }

                return dto;
            }
        }

        public async Task KillAsync()
        {
            KillProcess(Process);
            await RetainUntilProcessExitedAsync(() => Process.HasExited, () => KillProcess(Process)).ConfigureAwait(false);
            Process.Dispose();
        }

        // Closing stdin lets the child exit on its own; it is killed only if it lingers.
        public async ValueTask DisposeAsync()
        {
            try
            {
                stdin.Close();
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
            }

            await KillAsync().ConfigureAwait(false);
        }

        private async Task<string> ErrorTextAsync()
        {
            try
            {
                return (await stderr.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false)).Trim();
            }
            catch
            {
                return "";
            }
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

    private sealed record RecognitionDto(string? Type, string? Message, long Id, string? Text, bool Rejected, LexemeDto[] Lexemes, double Confidence = -1.0, string? RejectedText = null, bool StopGrammar = false, AlternateDto[]? Alternates = null);
    private sealed record LexemeDto(string? Text, long Start, long End, string? Reading = null);
    private sealed record AlternateDto(string? Text, string? Reading);

// Protocol: each request is a 28-byte little-endian header (int64 id, int64 start, int64 end, int32 pcmLength) followed by
// the PCM bytes; each answer is one JSON line. A "ready" line precedes the first answer; EOF on stdin ends the child.
private const string Script = """
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Speech
$locale = $env:VOICE_SWITCH_LOCALE
$stopWords = [string[]]@()
if ($env:VOICE_SWITCH_STOP_WORDS) {
  # Assigned before enumerating: piping ConvertFrom-Json straight into ForEach-Object hands over the array as one element.
  $parsedStopWords = $env:VOICE_SWITCH_STOP_WORDS | ConvertFrom-Json
  $stopWords = [string[]]@($parsedStopWords | ForEach-Object { [string]$_ })
}
$stdout = [Console]::OpenStandardOutput()
$reader = [System.IO.BinaryReader]::new([Console]::OpenStandardInput())
function Send-Json($obj) {
  $bytes = [System.Text.Encoding]::UTF8.GetBytes((($obj | ConvertTo-Json -Compress -Depth 6) + "`n"))
  $stdout.Write($bytes, 0, $bytes.Length)
  $stdout.Flush()
}
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
    private readonly List<string> rejectedTexts = new List<string>();
    private readonly List<VoiceSwitchLexeme> lexemes = new List<VoiceSwitchLexeme>();
    private readonly List<VoiceSwitchAlternate> alternates = new List<VoiceSwitchAlternate>();
    private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
    private bool rejected;
    private double confidence = -1.0;
    private string error;
    private Grammar stopGrammar;
    private int stopResults;
    private int dictationResults;

    public VoiceSwitchSapiCollector(long requestId, long requestStart, long requestEnd)
    {
        this.requestId = requestId;
        this.requestStart = requestStart;
        this.requestEnd = requestEnd;
    }

    public long Id { get { return requestId; } }
    public string Text { get { return string.Concat(texts); } }
    public string RejectedText { get { return string.Concat(rejectedTexts); } }
    public bool Rejected { get { return rejected; } }
    // Lowest confidence over the accepted and rejected results; -1 when SAPI returned none.
    public double Confidence { get { return confidence; } }
    public VoiceSwitchLexeme[] Lexemes { get { return lexemes.ToArray(); } }
    public VoiceSwitchAlternate[] Alternates { get { return alternates.ToArray(); } }
    public string Error { get { return error; } }
    // Every accepted result came from the stop-word grammar; a body phrase that also yielded a dictation result is not a stop.
    public bool StopGrammar { get { return stopResults > 0 && dictationResults == 0; } }

    public void Run(RecognizerInfo info, byte[] pcm, string[] stopWords)
    {
        using (var engine = new SpeechRecognitionEngine(info))
        using (var stream = new MemoryStream(pcm, false))
        {
            engine.LoadGrammar(new DictationGrammar());
            // Free dictation spells katakana stop words phonetically ("入力しTAP"), so the stop words are also offered as
            // a closed choice list; SAPI picks whichever grammar scores the utterance better.
            if (stopWords.Length > 0)
            {
                var builder = new GrammarBuilder(new Choices(stopWords));
                builder.Culture = info.Culture;
                stopGrammar = new Grammar(builder);
                stopGrammar.Name = "stop";
                engine.LoadGrammar(stopGrammar);
            }

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

        if (stopGrammar != null && ReferenceEquals(result.Grammar, stopGrammar))
        {
            stopResults++;
        }
        else
        {
            dictationResults++;
        }

        TrackConfidence(result);
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

                lexemes.Add(new VoiceSwitchLexeme(word.Text, word.LexicalForm ?? "", start, end));
            }
            catch
            {
                rejected = true;
            }
        }

        // Alternates are diagnostics only, so a failure reading them must not reject the recognized text.
        try
        {
            foreach (RecognizedPhrase alternate in result.Alternates)
            {
                if (alternates.Count >= 3)
                {
                    break;
                }

                if (alternate.Text == result.Text)
                {
                    continue;
                }

                var reading = new List<string>();
                foreach (RecognizedWordUnit unit in alternate.Words)
                {
                    reading.Add(unit.LexicalForm ?? "");
                }

                alternates.Add(new VoiceSwitchAlternate(alternate.Text, string.Concat(reading)));
            }
        }
        catch
        {
        }
    }

    private void OnRejected(object sender, SpeechRecognitionRejectedEventArgs args)
    {
        rejected = true;
        var result = args.Result;
        if (result == null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(result.Text))
        {
            rejectedTexts.Add(result.Text);
        }

        TrackConfidence(result);
    }

    private void TrackConfidence(RecognizedPhrase result)
    {
        confidence = confidence < 0 ? result.Confidence : Math.Min(confidence, result.Confidence);
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
    public VoiceSwitchLexeme(string text, string reading, long start, long end)
    {
        Text = text;
        Reading = reading;
        Start = start;
        End = end;
    }

    public string Text { get; private set; }
    public string Reading { get; private set; }
    public long Start { get; private set; }
    public long End { get; private set; }
}

public sealed class VoiceSwitchAlternate
{
    public VoiceSwitchAlternate(string text, string reading)
    {
        Text = text;
        Reading = reading;
    }

    public string Text { get; private set; }
    public string Reading { get; private set; }
}
"@
$infos = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()
$info = $infos | Where-Object { $_.Culture.Name -eq $locale } | Select-Object -First 1
if ($null -eq $info) { Send-Json @{ type='error'; message=("No installed Windows speech recognizer for " + $locale) }; exit 2 }
Send-Json @{ type='ready'; recognizer=$info.Description }
while ($true) {
  try {
    $requestId = $reader.ReadInt64()
    $requestStart = $reader.ReadInt64()
    $requestEnd = $reader.ReadInt64()
    $length = $reader.ReadInt32()
    $pcm = $reader.ReadBytes($length)
  } catch {
    exit 0
  }
  if ($pcm.Length -ne $length) { exit 0 }
  try {
    $collector = [VoiceSwitchSapiCollector]::new($requestId, $requestStart, $requestEnd)
    $collector.Run($info, $pcm, $stopWords)
    if ($collector.Error) { Send-Json @{ type='error'; id=$requestId; message=$collector.Error }; continue }
    Send-Json @{ id=$collector.Id; text=$collector.Text; rejected=$collector.Rejected; rejectedText=$collector.RejectedText; confidence=$collector.Confidence; stopGrammar=$collector.StopGrammar; lexemes=@($collector.Lexemes | ForEach-Object { @{ text=$_.Text; reading=$_.Reading; start=$_.Start; end=$_.End } }); alternates=@($collector.Alternates | ForEach-Object { @{ text=$_.Text; reading=$_.Reading } }) }
  } catch {
    Send-Json @{ type='error'; id=$requestId; message=$_.Exception.Message }
  }
}
""";
}
