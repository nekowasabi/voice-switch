using VoiceSwitch.Windows.Core;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Tray;
using System.Diagnostics;
using System.Reflection;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.IO.Pipes;

if (args is ["--child", var mode])
{
    return Child(mode);
}

var tests = new (string Name, Func<TestOutcome> Test)[]
{
    ("normalizes punctuation and spaces", () => Check(Normalizes())),
    ("decides wake and stop commands", () => Check(DecidesCommands())),
    ("segments one utterance from synthetic frames", () => Check(SegmentsSyntheticUtterance())),
    ("expands Windows environment paths", () => Check(ExpandsPaths())),
    ("parses CLI diagnostics mode", () => Check(ParsesCli())),
    ("rejects fire with dry-run", () => Check(RejectsFireDryRun())),
    ("rejects conflicting synthetic WAV flags", () => Check(RejectsConflictingSyntheticWavFlags())),
    ("program rejects synthetic WAV without dictation config", () => Check(ProgramRejectsSyntheticWavWithoutDictationConfig())),
    ("synthetic WAV without output dir suppresses external dispatch", () => Check(SyntheticWavWithoutOutputDirSuppressesExternalDispatch())),
    ("validates config boundary", () => Check(ValidatesConfig())),
    ("rejects numeric noise reduction mode", () => Check(RejectsNumericNoiseReductionMode())),
    ("recognition key changes only for recognizer inputs", () => Check(ComparesRecognitionKey())),
    ("reports unsupported Windows config options", () => Check(ReportsUnsupportedOptions())),
    ("dictation wake prefix trims exact body start", () => Check(DictationWakePrefixTrimsExactBodyStart())),
    ("dictation wake and stop boundaries are explicit", () => Check(DictationWakeAndStopBoundariesAreExplicit())),
    ("dictation lone wake waits for body", () => Check(DictationLoneWakeWaitsForBody())),
    ("dictation lone wake body then stop submits body", () => Check(DictationLoneWakeBodyThenStopSubmitsBody())),
    ("dictation repeated wake prefix keeps body", () => Check(DictationRepeatedWakePrefixKeepsBody())),
    ("dictation standalone stop trims at absolute stop start", () => Check(DictationStandaloneStopTrimsAtAbsoluteStart())),
    ("dictation prefix head stop is retained", () => Check(DictationPrefixHeadStopIsRetained())),
    ("dictation embedded stop is retained", () => Check(DictationEmbeddedStopIsRetained())),
    ("dictation rejected stop is retained", () => Check(DictationRejectedStopIsRetained())),
    ("dictation finish and cancel semantics", () => Check(DictationFinishAndCancelSemantics())),
    ("dictation terminal reasons are observable", () => Check(DictationTerminalReasonsAreObservable())),
    ("dictation runtime keeps body while recognition is delayed", () => Check(DictationRuntimeKeepsBodyWhileRecognitionIsDelayed())),
    ("dictation runtime keeps second body before delayed stop", () => Check(DictationRuntimeKeepsSecondBodyBeforeDelayedStop())),
    ("dictation runtime start timeout resets for next session", () => Check(DictationRuntimeStartTimeoutResetsForNextSession())),
    ("dictation runtime max uses live speech while recognition is delayed", () => Check(DictationRuntimeMaxUsesLiveSpeechWhileRecognitionIsDelayed())),
    ("dictation runtime stop after lone wake cancels empty session", () => Check(DictationRuntimeStopAfterLoneWakeCancelsEmptySession())),
    ("dictation runtime body before timeout waits for delayed recognition", () => Check(DictationRuntimeBodyBeforeTimeoutWaitsForDelayedRecognition())),
    ("dictation runtime recovers after long nonwake audio", () => Check(DictationRuntimeRecoversAfterLongNonwakeAudio())),
    ("dictation runtime waits for open stop utterance before silence", () => Check(DictationRuntimeWaitsForOpenStopUtteranceBeforeSilence())),
    ("dictation runtime short silence waits for open utterance", () => Check(DictationRuntimeShortSilenceWaitsForOpenUtterance())),
    ("dictation runtime ignores late stop after silence finish", () => Check(DictationRuntimeIgnoresLateStopAfterSilenceFinish())),
    ("dictation runtime keeps embedded stop in silence body", () => Check(DictationRuntimeKeepsEmbeddedStopInSilenceBody())),
    ("dictation runtime keeps active body embedded stop in separate utterance", () => Check(DictationRuntimeKeepsActiveBodyEmbeddedStopInSeparateUtterance())),
    ("dictation runtime capture fault disposes capture", () => Check(DictationRuntimeCaptureFaultDisposesCapture())),
    ("dictation runtime ignores stale queued recognition after reset", () => Check(DictationRuntimeIgnoresStaleQueuedRecognitionAfterReset())),
    ("dictation runtime preserves queued session after delayed stop", () => Check(DictationRuntimePreservesQueuedSessionAfterDelayedStop())),
    ("dictation phases follow lone wake, body and stop word", () => Check(DictationPhasesFollowLoneWakeBodyAndStop())),
    ("dictation phases follow one-breath dictation ended by silence", () => Check(DictationPhasesFollowOneBreathDictationEndedBySilence())),
    ("dictation phases follow lone wake ended by stop word", () => Check(DictationPhasesFollowLoneWakeEndedByStop())),
    ("dictation phases follow lone wake ended by start timeout", () => Check(DictationPhasesFollowLoneWakeStartTimeout())),
    ("dictation phases and foreground target across two stop-ended dictations", () => Check(DictationPhasesAndTargetAcrossTwoDictations())),
    ("dictation WinMM native layout and callback message", () => Check(DictationWinMmNativeLayoutAndInputDataMessage())),
    ("dictation WinMM dispose waits for worker before freeing buffers", () => Check(DictationWinMmDisposeWaitsForWorkerBeforeFreeingBuffers())),
    ("dictation sample store rejects discontinuity", () => Check(DictationSampleStoreRejectsDiscontinuity())),
    ("dictation stale recognition is safe error", () => Check(DictationStaleRecognitionIsSafeError())),
    ("dictation WAV bytes are PCM16 mono 16k", () => Check(DictationWavBytesAreExact())),
    ("dictation strict WAV parsing and source continuity", () => Check(DictationStrictWavParsingAndSourceContinuity())),
    ("dictation segmenter uses partial EOF sample duration", () => Check(DictationSegmenterUsesPartialEofSampleDuration())),
    ("dictation runtime finalizes body at synthetic EOF", () => Check(DictationRuntimeFinalizesBodyAtSyntheticEof())),
    ("dictation runtime keeps partial EOF PCM range exact", () => Check(DictationRuntimeKeepsPartialEofPcmRangeExact())),
    ("dictation local recording handoff writes byte-exact bodies", () => Check(DictationLocalRecordingHandoffWritesByteExactBodies())),
    ("dictation local recording handoff allows consecutive bodies", () => Check(DictationLocalRecordingHandoffAllowsConsecutiveBodies())),
    ("dictation synthetic capture cancellation disposes source", () => Check(DictationSyntheticCaptureCancellationDisposesSource())),
    ("noise processor off is bit exact", () => Check(NoiseProcessorOffIsBitExact())),
    ("noise processor conservative WOLA preserves unity boundaries", () => Check(NoiseProcessorConservativeWolaPreservesUnityBoundaries())),
    ("noise processor calibrated gain preserves conjugates", () => Check(NoiseProcessorCalibratedGainPreservesConjugates())),
    ("noise processor calibrated output remains real waveform", () => Check(NoiseProcessorCalibratedOutputRemainsRealWaveform())),
    ("noise processor partitions are stable", () => Check(NoiseProcessorPartitionsAreStable())),
    ("noise processor impulse index is stable", () => Check(NoiseProcessorImpulseIndexIsStable())),
    ("noise processor bounds retained state on long streams", () => Check(NoiseProcessorBoundsRetainedStateOnLongStreams())),
    ("noise processor handles high absolute indexes", () => Check(NoiseProcessorHandlesHighAbsoluteIndexes())),
    ("noise processor FFT matches naive DFT oracle", () => Check(NoiseProcessorFftMatchesNaiveDftOracle())),
    ("noise processor conservative gain is bounded", () => Check(NoiseProcessorConservativeGainIsBounded())),
    ("dictation noise analysis never replaces handoff source", () => Check(DictationNoiseAnalysisNeverReplacesHandoffSource())),
    ("dictation noise-on local recording is byte exact", () => Check(DictationNoiseOnLocalRecordingIsByteExact())),
    ("dictation handoff launches Superwhisper and deletes WAV after its result", () => Check(DictationHandoffTranscribesAndDeletesWav())),
    ("dictation handoff keeps WAV when Superwhisper writes no result", () => Check(DictationHandoffKeepsWavWithoutResult())),
    ("dictation handoff restores focus 20 times before polling", () => Check(DictationHandoffRestoresFocusBeforePolling())),
    ("superwhisper result lookup picks newest non-empty run", () => Check(SuperwhisperFindResultPicksNewestNonEmpty())),
    ("dictation handoff sweeps WAVs older than 10 minutes", () => Check(DictationHandoffSweepsOldFiles())),
    ("runtime launches Superwhisper for consecutive dictations", () => Check(RuntimeLaunchesSuperwhisperForConsecutiveDictations())),
    ("dictation handoff rejects unsupported intake path without writing", () => Check(DictationHandoffRejectsUnsupportedIntakePath())),
    ("dictation handoff launch failure deletes WAV", () => Check(DictationHandoffLaunchFailureDeletesWav())),
    ("dictation handoff accepts trusted ancestor junction only", DictationHandoffAcceptsTrustedAncestorJunctionOnly),
    ("handoff owned links never touch outside sentinel", HandoffOwnedLinksRejectSafely),
    ("runtime keeps listening after every handoff status", () => Check(RuntimeKeepsListeningAfterEveryHandoffStatus())),
    ("runtime drops dictation while handoff in flight and awaits it at EOF", () => Check(RuntimeDropsOverlappingDictationAndAwaitsInflightAtEof())),
    ("dictation dry-run keeps audio in memory", () => Check(DictationDryRunKeepsAudioInMemory())),
    ("production script parses configured words distinctly on Windows PowerShell", ProductionScriptParsesWordsOnWindowsPowerShell),
    ("resident fails on clean child exit before ready", () => Check(ResidentFailsOnCleanEarlyExit())),
    ("resident propagates child stderr as error", () => Check(ResidentPropagatesChildStderr())),
    ("resident bounded stop disposes child", () => Check(ResidentBoundedStopDisposesChild())),
    ("resident repeated start stop", () => Check(ResidentRepeatedStartStop())),
    ("resident reload restarts only valid recognition changes", ResidentReloadRestartRules),
    ("resident normal mode dispatches wake and stop commands", () => Check(ResidentNormalDispatchesCommands())),
    ("resident command mode suppresses idle stop but dispatches wake", ResidentCommandModeSuppressesIdleStopButDispatchesWake),
    ("resident dry-run observes wake and stop without commands", () => Check(ResidentDryRunSuppressesCommands())),
    ("resident cancellation stops child", () => Check(ResidentCancellationStopsChild())),
    ("dictation recognizer retains ownership until confirmed exit", () => Check(DictationRecognizerRetainsOwnershipUntilConfirmedExit())),
    ("tray options reject synthetic input without record-only", () => Check(TrayOptionsRejectSyntheticWithoutRecordOnly())),
    ("tray options reject numeric command", () => Check(TrayOptionsRejectNumericCommand())),
    ("single exe routes bare and tray-only launches to the tray", () => Check(SingleExeRoutesTrayLaunches())),
    ("production tray factory rejects unsafe synthetic source", () => Check(ProductionTrayFactoryRejectsUnsafeSyntheticSource())),
    ("tray supervisor starts paused and start is idempotent", () => Check(TraySupervisorStartsPausedAndStartIsIdempotent())),
    ("tray supervisor invalid reload keeps running generation", () => Check(TraySupervisorInvalidReloadKeepsRunningGeneration())),
    ("tray supervisor reload switches dictation to command mode", () => Check(TraySupervisorReloadSwitchesToCommandMode())),
    ("tray supervisor valid reload stops old before new", () => Check(TraySupervisorValidReloadStopsOldBeforeNew())),
    ("tray supervisor retains run after stop failure", () => Check(TraySupervisorRetainsRunAfterStopFailure())),
    ("tray supervisor retries quit after stop failure", () => Check(TraySupervisorRetriesQuitAfterStopFailure())),
    ("tray supervisor disposes faulted completion and restarts", () => Check(TraySupervisorDisposesFaultedCompletionAndRestarts())),
    ("tray supervisor refreshes child identity without state change", () => Check(TraySupervisorRefreshesChildIdentityWithoutStateChange())),
    ("tray supervisor rejects stale run during snapshot race", () => Check(TraySupervisorRejectsStaleRunDuringSnapshotRace())),
    ("tray command identity does not acquire lease", () => Check(TrayCommandIdentityDoesNotAcquireLease())),
    ("tray IPC no-server error is actionable", TrayIpcNoServerErrorIsActionable),
    ("tray IPC rejects bad client then serves status", TrayIpcRejectsBadClientThenServesStatus),
    ("tray IPC times out unread response then serves status", TrayIpcTimesOutUnreadResponseThenServesStatus)
};

var failed = 0;
var skipped = 0;
foreach (var (name, test) in tests)
{
    var outcome = test();
    if (outcome.Status == TestStatus.Pass)
    {
        Console.WriteLine($"PASS {name}");
        continue;
    }

    if (outcome.Status == TestStatus.Skip)
    {
        Console.WriteLine($"SKIP {name}: {outcome.Message}");
        skipped++;
        continue;
    }

    Console.Error.WriteLine($"FAIL {name}");
    if (!string.IsNullOrWhiteSpace(outcome.Message))
    {
        Console.Error.WriteLine(outcome.Message);
    }
    failed++;
}

return failed == 0 ? 0 : 1;

static TestOutcome Check(bool ok) => ok ? TestOutcome.Pass() : TestOutcome.Fail();

static bool Normalizes() =>
    TextMatching.Normalize(" 音声 入力。") == "音声入力";

static bool DecidesCommands()
{
    var config = new VoiceSwitchConfig(["音声入力"], "ja_JP", "wake", StopWords: ["入力ストップ"], StopCommand: "stop");
    return TextMatching.Decide("音声入力", config).Command == "wake"
        && TextMatching.Decide("入力ストップ", config).Command == "stop"
        && TextMatching.Decide("別の言葉", config).Kind == "ignore";
}

static bool SegmentsSyntheticUtterance()
{
    var config = new VoiceSwitchConfig(["test"], null, "true", MaxSeconds: 2.5, HangoverMs: 300, PrerollMs: 300, MinSpeechMs: 300, VadRatio: 3, VadMinRMS: 0.005f);
    var segmenter = new Segmenter(config);
    var quiet = Enumerable.Repeat(0.0001f, Segmenter.FrameLength).ToArray();
    var loud = Enumerable.Range(0, Segmenter.FrameLength)
        .Select(i => (float)(0.2 * Math.Sin(i * 0.5)))
        .ToArray();

    for (var i = 0; i < 7; i++)
    {
        segmenter.Push(quiet);
    }

    for (var i = 0; i < 17; i++)
    {
        segmenter.Push(loud);
    }

    for (var i = 0; i < 20; i++)
    {
        var ev = segmenter.Push(quiet);
        if (ev is { Kind: "utterance" })
        {
            return ev.Samples.Length > Segmenter.FrameLength;
        }
    }

    return false;
}

static bool ExpandsPaths()
{
    Environment.SetEnvironmentVariable("LOCALAPPDATA", @"C:\Users\me\AppData\Local");
    return WindowsPaths.ExpandPath(@"%LOCALAPPDATA%\voice-switch")
        == @"C:\Users\me\AppData\Local\voice-switch";
}

static bool ParsesCli()
{
    var options = CliOptions.Parse(["--config", "c.json", "--listen-seconds", "5", "--dry-run", "--recognizers"]);
    try
    {
        CliOptions.Parse(["--listen-seconds", "-1"]);
        return false;
    }
    catch (ArgumentException)
    {
        return options.ConfigPath == "c.json" && options.ListenSeconds == 5 && options.DryRun && options.Recognizers;
    }
}

static bool RejectsFireDryRun()
{
    try
    {
        CliOptions.Parse(["--dry-run", "--fire"]);
        return false;
    }
    catch (ArgumentException ex)
    {
        return ex.Message.Contains("--dry-run");
    }
}

static bool RejectsConflictingSyntheticWavFlags()
{
    var checks = new[]
    {
        new[] { "--input-wav", "in.wav", "--fire" },
        ["--input-wav", "in.wav", "--recognizers"],
        ["--input-wav", "in.wav", "--check-device"],
        ["--input-wav-fast"],
        ["--output-dir", "out"],
    };
    return checks.All(args =>
    {
        try
        {
            _ = CliOptions.Parse(args);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    });
}

static bool ProgramRejectsSyntheticWavWithoutDictationConfig()
{
    using var temp = RuntimeTemp();
    var marker = Path.Combine(temp.Dir, "command-ran");
    File.WriteAllText(temp.ConfigPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        wakeWords = new[] { "音声入力" },
        locale = "ja_JP",
        command = $"cmd /c echo sentinel > \"{marker}\"",
        stopWords = new[] { "入力ストップ" },
        stopCommand = "cmd /c echo stop"
    }));
    var missingWav = Path.Combine(temp.Dir, "missing.wav");
    var (code, output) = CaptureConsole(() => VoiceSwitch.Windows.Program.Run(["--config", temp.ConfigPath, "--input-wav", missingWav]));
    return code == 1
        && output.Contains("--input-wav requires a config with dictation", StringComparison.Ordinal)
        && !File.Exists(marker);
}

static bool SyntheticWavWithoutOutputDirSuppressesExternalDispatch()
{
    var options = CliOptions.Parse(["--input-wav", "input.wav"]);
    var withOutput = CliOptions.Parse(["--input-wav", "input.wav", "--output-dir", "out"]);
    return VoiceSwitch.Windows.Program.SyntheticInputSuppressesExternalDispatch(options)
        && !options.DryRun
        && !VoiceSwitch.Windows.Program.SyntheticInputSuppressesExternalDispatch(withOutput);
}

static bool ValidatesConfig()
{
    return Rejects("""{"wakeWords":["a"],"command":""}""", "command")
        && Rejects("""{"wakeWords":["。"],"command":"wake"}""", "wakeWords[0]")
        && Rejects("""{"wakeWords":["a"],"locale":"no_such_locale","command":"wake"}""", "locale");
}

static bool RejectsNumericNoiseReductionMode() =>
    Rejects("""{"wakeWords":["a"],"command":"wake","noiseReduction":{"mode":2}}""", "Config JSON is invalid");

static bool ComparesRecognitionKey()
{
    var baseConfig = new VoiceSwitchConfig(["音声入力"], "ja_JP", "wake", StopWords: ["入力ストップ"]);
    var commandOnly = baseConfig with { Command = "different" };
    var wakeChanged = baseConfig with { WakeWords = ["別"] };
    return baseConfig.RecognitionKey() == commandOnly.RecognitionKey()
        && baseConfig.RecognitionKey() != wakeChanged.RecognitionKey();
}

static bool ReportsUnsupportedOptions()
{
    var config = new VoiceSwitchConfig(["音声入力"], "ja_JP", "wake", SkipWhileMicInUseBy: ["superwhisper"], Dictation: new DictationConfig());
    var warnings = config.UnsupportedWarnings().ToArray();
    return !warnings.Any(w => w.Contains("dictation is parsed"))
        && warnings.Any(w => w.Contains("skipWhileMicInUseBy"));
}

static bool DictationWakePrefixTrimsExactBodyStart()
{
    var config = DictationConfig();
    var session = new DictationSession(config);
    var store = StoreWithRamp(0, 24000);
    var rec = Recognized(1, RecognitionExtent.PrefixHead, 0, 24000, "音声入力今日は晴れです", false,
        Run("音声", 0, 4000), Run("入力", 4000, 10000), Run("今日", 13300, 15700), Run("は", 15700, 17100), Run("晴れ", 17100, 19300), Run("です", 19300, 22000));
    session.Apply(rec, store.Copy);
    return session.IsActive
        && session.PendingBody == new SampleRange(13300, 24000)
        && session.Events.Single().Range == new SampleRange(13300, 24000);
}

static bool DictationWakeAndStopBoundariesAreExplicit()
{
    var splitWake = Recognized(1, RecognitionExtent.PrefixHead, 0, 8000, "音声入力本文", false,
        Run("音声", 0, 2000), Run("入力", 2000, 4000), Run("本文", 5000, 8000));
    var fusedWake = Recognized(2, RecognitionExtent.PrefixHead, 0, 8000, "音声入力本文", false,
        Run("音声入力本文", 0, 8000));
    var standaloneStop = Recognized(3, RecognitionExtent.ClosedUtterance, 10000, 14000, "入力ストップ", false,
        Run("入力", 10500, 12000), Run("ストップ", 12000, 13500));
    var embeddedStop = Recognized(4, RecognitionExtent.ClosedUtterance, 15000, 22000, "今日は入力ストップです", false,
        Run("今日は", 15000, 17000), Run("入力", 17000, 18500), Run("ストップ", 18500, 20000), Run("です", 20000, 22000));
    var prefixStop = Recognized(5, RecognitionExtent.PrefixHead, 23000, 27000, "入力ストップ", false,
        Run("入力", 23000, 24500), Run("ストップ", 24500, 26000));

    return DictationBoundaries.LeadingWake(splitWake, ["音声入力"]) == new WakePrefix(4000, 5000)
        && DictationBoundaries.LeadingWake(fusedWake, ["音声入力"]) is null
        && DictationBoundaries.StandaloneStopRange(standaloneStop, ["入力ストップ"]) == new SampleRange(10500, 13500)
        && DictationBoundaries.StandaloneStopRange(embeddedStop, ["入力ストップ"]) is null
        && DictationBoundaries.StandaloneStopRange(prefixStop, ["入力ストップ"]) is null;
}

static bool DictationLoneWakeWaitsForBody()
{
    var config = DictationConfig();
    var session = new DictationSession(config);
    var store = StoreWithRamp(0, 30000);
    session.Apply(Recognized(1, RecognitionExtent.ClosedUtterance, 0, 10000, "音声入力", false, Run("音声", 0, 4000), Run("入力", 4000, 10000)), store.Copy);
    var before = session.IsAwaitingBody && !session.IsActive;
    session.Apply(Recognized(2, RecognitionExtent.ClosedUtterance, 13000, 20000, "今日は晴れです", false, Run("今日", 13000, 15400), Run("は", 15400, 16000), Run("晴れです", 16000, 20000)), store.Copy);
    return before && session.IsActive && session.PendingBody == new SampleRange(13000, 20000);
}

static bool DictationLoneWakeBodyThenStopSubmitsBody()
{
    var config = DictationConfig();
    var session = new DictationSession(config);
    var store = StoreWithRamp(0, 40000);
    session.Apply(Recognized(1, RecognitionExtent.ClosedUtterance, 0, 10000, "音声入力", false, Run("音声", 0, 4000), Run("入力", 4000, 10000)), store.Copy);
    session.Apply(Recognized(2, RecognitionExtent.ClosedUtterance, 13000, 20000, "今日は晴れです", false, Run("今日", 13000, 15400), Run("は", 15400, 16000), Run("晴れです", 16000, 20000)), store.Copy);
    var audio = session.Apply(Recognized(3, RecognitionExtent.ClosedUtterance, 23000, 26000, "入力ストップ", false, Run("入力", 23000, 24500), Run("ストップ", 24500, 26000)), store.Copy);
    return audio is not null
        && audio.Range == new SampleRange(13000, 23000)
        && session.Events.Select(ev => ev.Kind).SequenceEqual(["started", "submitted"])
        && session.Events.Last().Reason == FinishReason.StandaloneStop;
}

static bool DictationRepeatedWakePrefixKeepsBody()
{
    var config = DictationConfig();
    var session = new DictationSession(config);
    var store = StoreWithRamp(0, 30000);
    session.Apply(Recognized(1, RecognitionExtent.PrefixHead, 0, 22000, "音声入力音声入力本文", false,
        Run("音声", 0, 3000), Run("入力", 3000, 6000), Run("音声", 7000, 10000), Run("入力", 10000, 13000), Run("本文", 17000, 22000)), store.Copy);
    return session.IsActive && session.PendingBody == new SampleRange(17000, 22000);
}

static bool DictationStandaloneStopTrimsAtAbsoluteStart()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    var audio = session.Apply(Recognized(2, RecognitionExtent.ClosedUtterance, 19000, 27000, "入力ストップ", false, Run("入力", 21000, 23000), Run("ストップ", 23000, 26000)), store.Copy);
    return audio is not null
        && audio.Range == new SampleRange(10000, 21000)
        && audio.Samples.Length == 11000
        && audio.Samples[0] == 10000
        && audio.Samples[^1] == 20999;
}

static bool DictationPrefixHeadStopIsRetained()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    var audio = session.Apply(Recognized(2, RecognitionExtent.PrefixHead, 19000, 27000, "入力ストップ", false, Run("入力", 21000, 23000), Run("ストップ", 23000, 26000)), store.Copy);
    return audio is null && session.PendingBody == new SampleRange(10000, 27000);
}

static bool DictationEmbeddedStopIsRetained()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    var audio = session.Apply(Recognized(2, RecognitionExtent.ClosedUtterance, 21000, 30000, "入力ストップではありません", false, Run("入力", 21000, 23000), Run("ストップ", 23000, 26000), Run("ではありません", 26000, 30000)), store.Copy);
    return audio is null && session.PendingBody == new SampleRange(10000, 30000);
}

static bool DictationRejectedStopIsRetained()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    var audio = session.Apply(Recognized(2, RecognitionExtent.ClosedUtterance, 21000, 26000, "入力ストップ", true, Run("入力", 21000, 23000), Run("ストップ", 23000, 26000)), store.Copy);
    return audio is null && session.PendingBody == new SampleRange(10000, 26000);
}

static bool DictationFinishAndCancelSemantics()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    var finished = session.Finish(FinishReason.Silence, store.Copy);
    var cancelled = new DictationSession(config);
    cancelled.Cancel();
    return finished is not null
        && finished.Range == new SampleRange(10000, 20000)
        && cancelled.Events.Single().Reason == FinishReason.CancelCommand;
}

static bool DictationTerminalReasonsAreObservable()
{
    var config = DictationConfig();
    var silence = StartedSession(config, out var silenceStore);
    _ = silence.Finish(FinishReason.Silence, silenceStore.Copy);

    var finish = StartedSession(config, out var finishStore);
    _ = finish.Finish(FinishReason.FinishCommand, finishStore.Copy);

    var timeout = new DictationSession(config);
    var timeoutStore = StoreWithRamp(0, 12000);
    timeout.Apply(Recognized(1, RecognitionExtent.ClosedUtterance, 0, 10000, "音声入力", false, Run("音声", 0, 4000), Run("入力", 4000, 10000)), timeoutStore.Copy);
    _ = timeout.AdvanceTo(60000, timeoutStore.Copy);

    var maxConfig = config with { Dictation = config.Dictation! with { MaxSeconds = 0.5 } };
    var max = StartedSession(maxConfig, out var maxStore);
    _ = max.AdvanceTo(18000, maxStore.Copy);

    return silence.Events.Last().Reason == FinishReason.Silence
        && finish.Events.Last().Reason == FinishReason.FinishCommand
        && timeout.Events.Single().Reason == FinishReason.StartTimeout
        && max.Events.Last().Reason == FinishReason.MaximumDuration
        && max.Events.Last().Range == new SampleRange(10000, 18000);
}

static bool DictationRuntimeKeepsBodyWhileRecognitionIsDelayed()
{
    var config = DictationConfig() with
    {
        HangoverMs = 300,
        PrerollMs = 90,
        MinSpeechMs = 300,
        VadMinRMS = 0.005f,
        Dictation = DictationConfig().Dictation! with { EndSilenceMs = 5000, MaxSeconds = 10 }
    };
    var frames = TwoUtteranceFrames();
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        await Task.Delay(150);
        if (request.Id == 1)
        {
            var bodyStart = request.Range.Start + Segmenter.FrameLength * 4;
            return new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力本文",
                ImmutableArray.Create(
                    Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("本文", bodyStart, request.Range.End)));
        }

        return new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "入力ストップ",
            ImmutableArray.Create(
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("ストップ", request.Range.Start + Segmenter.FrameLength * 2, request.Range.Start + Segmenter.FrameLength * 3)));
    });
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, delayAfterFrame: 24), recognizer, handoff, dryRun: true);

    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var firstStopRequest = recognizer.Requests.Single(request => request.Id == 2);
    var expectedStart = recognizer.Requests.Single(request => request.Id == 1).Range.Start + Segmenter.FrameLength * 4;
    var expectedEnd = firstStopRequest.Range.Start + Segmenter.FrameLength;
    return code == 0
        && audio is not null
        && audio.Range == new SampleRange(expectedStart, expectedEnd)
        && audio.Samples.Length == expectedEnd - expectedStart
        && recognizer.Requests.Count == 2;
}

static bool DictationRuntimeKeepsSecondBodyBeforeDelayedStop()
{
    var config = DictationConfig() with
    {
        HangoverMs = 300,
        PrerollMs = 90,
        MinSpeechMs = 300,
        VadMinRMS = 0.005f,
        Dictation = DictationConfig().Dictation! with { EndSilenceMs = 5000, MaxSeconds = 10 }
    };
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        await Task.Delay(request.Id == 1 ? 250 : 20);
        return request.Id switch
        {
            1 => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力本文A",
                ImmutableArray.Create(
                    Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("本文A", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End))),
            2 => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "本文B",
                ImmutableArray.Create(Run("本文B", request.Range.Start + Segmenter.FrameLength, request.Range.End))),
            _ => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "入力ストップ",
                ImmutableArray.Create(
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("ストップ", request.Range.Start + Segmenter.FrameLength * 2, request.Range.Start + Segmenter.FrameLength * 3)))
        };
    });
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(ThreeUtteranceFrames()), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var bodyStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    var stopStart = recognizer.Requests[2].Range.Start + Segmenter.FrameLength;
    return code == 0
        && audio is not null
        && audio.Range == new SampleRange(bodyStart, stopStart)
        && audio.Samples.Length == stopStart - bodyStart
        && recognizer.Requests.Count == 3;
}

static bool DictationRuntimeStartTimeoutResetsForNextSession()
{
    var config = DictationConfig() with
    {
        HangoverMs = 300,
        PrerollMs = 90,
        MinSpeechMs = 300,
        VadMinRMS = 0.005f,
        Dictation = DictationConfig().Dictation! with { StartTimeoutMs = 300, EndSilenceMs = 1200, MaxSeconds = 10 }
    };
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Id switch
    {
        1 => new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.End))),
        2 => new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End))),
        _ => new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "入力ストップ",
            ImmutableArray.Create(
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("ストップ", request.Range.Start + Segmenter.FrameLength * 2, request.Range.Start + Segmenter.FrameLength * 3)))
    }));
    var handoff = new RecordingDictationHandoff();
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 22, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, delayAfterFrame: 24), recognizer, handoff, dryRun: true);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 1
        && recognizer.Requests.Count == 3;
}

static bool DictationRuntimeMaxUsesLiveSpeechWhileRecognitionIsDelayed()
{
    var config = DictationConfig() with
    {
        HangoverMs = 300,
        PrerollMs = 90,
        MinSpeechMs = 300,
        VadMinRMS = 0.005f,
        Dictation = DictationConfig().Dictation! with { EndSilenceMs = 5000, MaxSeconds = 0.6 }
    };
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 50, loud: true);
    AddFrames(frames, 12, loud: false);
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        await Task.Delay(250);
        var bodyStart = request.Range.Start + Segmenter.FrameLength * 4;
        return new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力長い本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("長い本文", bodyStart, request.Range.End)));
    });
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests.Single().Range.Start + Segmenter.FrameLength * 4;
    var expectedEnd = expectedStart + (long)Math.Round(0.6 * Segmenter.Rate, MidpointRounding.AwayFromZero);
    return code == 0
        && audio is not null
        && audio.Range == new SampleRange(expectedStart, expectedEnd)
        && audio.Samples.Length == expectedEnd - expectedStart;
}

static bool DictationRuntimeStopAfterLoneWakeCancelsEmptySession()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 5000, maxSeconds: 10);
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Id == 1
        ? new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.End)))
        : new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "入力ストップ",
            ImmutableArray.Create(
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("ストップ", request.Range.Start + Segmenter.FrameLength * 2, request.Range.Start + Segmenter.FrameLength * 3)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(TwoUtteranceFrames()), recognizer, handoff, dryRun: true);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 0
        && recognizer.Requests.Count == 2;
}

static bool DictationRuntimeBodyBeforeTimeoutWaitsForDelayedRecognition()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 1200, endSilenceMs: 5000, maxSeconds: 10);
    var frames = new List<PcmFrame>();
    AddFrames(frames, 20, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 20, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        if (request.Id is 1 or 2)
        {
            await Task.Delay(500);
        }

        return request.Id switch
        {
            1 => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力",
                ImmutableArray.Create(Run("音声入力", request.Range.Start, request.Range.End))),
            2 => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "本文",
                ImmutableArray.Create(Run("本文", request.Range.Start + Segmenter.FrameLength, request.Range.End))),
            _ => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "入力ストップ",
                ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))
        };
    });
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    if (code != 0 || handoff.Submissions.Count != 1 || recognizer.Requests.Count != 3)
    {
        Console.Error.WriteLine($"body-before-timeout detail: code={code} submissions={handoff.Submissions.Count} requests={recognizer.Requests.Count}");
        foreach (var request in recognizer.Requests)
        {
            Console.Error.WriteLine($"request {request.Id}: {request.Extent} {request.Range.Start}..{request.Range.End}");
        }
    }

    return code == 0
        && handoff.Submissions.Count == 1
        && recognizer.Requests.Count == 3;
}

static bool DictationRuntimeRecoversAfterLongNonwakeAudio()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 5000, maxSeconds: 0.5);
    var frames = new List<PcmFrame>();
    AddFrames(frames, 80, loud: true);
    AddFrames(frames, 14, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Range.End <= 94 * Segmenter.FrameLength
        ? new RecognizedUtterance(request.Id, request.Extent, request.Range, "雑談", ImmutableArray.Create(Run("雑談", request.Range.Start, request.Range.End)))
        : request.Range.End <= 118 * Segmenter.FrameLength
            ? new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力本文",
                ImmutableArray.Create(
                    Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End)))
            : new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "入力ストップ",
                ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 1
        && recognizer.Requests.Count >= 3
        && handoff.Submissions[0].Range.Start >= 94 * Segmenter.FrameLength
        && handoff.Submissions[0].Range.End <= 118 * Segmenter.FrameLength;
}

static bool DictationRuntimeWaitsForOpenStopUtteranceBeforeSilence()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 1200, maxSeconds: 10);
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 30, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 50, loud: false);
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        if (request.Id == 2)
        {
            await Task.Delay(250);
        }

        return request.Id == 1
            ? new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力本文",
                ImmutableArray.Create(
                    Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))
            : new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "入力ストップ",
                ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)));
    });
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    var expectedStopStart = recognizer.Requests[1].Range.Start + Segmenter.FrameLength;
    return code == 0
        && audio is not null
        && audio.Reason == FinishReason.StandaloneStop
        && audio.Range == new SampleRange(expectedStart, expectedStopStart)
        && audio.Samples.Length == expectedStopStart - expectedStart
        && recognizer.Requests.Count == 2;
}

static bool DictationRuntimeIgnoresLateStopAfterSilenceFinish()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 1200, maxSeconds: 10);
    var beforeLateStop = new List<PcmFrame>();
    AddFrames(beforeLateStop, 3, loud: false);
    AddFrames(beforeLateStop, 12, loud: true);
    AddFrames(beforeLateStop, 60, loud: false);
    var lateStop = new List<PcmFrame>(beforeLateStop);
    AddFrames(lateStop, 12, loud: true);
    AddFrames(lateStop, 30, loud: false);
    lateStop = lateStop.Skip(beforeLateStop.Count).ToList();
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Id == 1
        ? new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))
        : new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "入力ストップ",
            ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))));
    using var silenceSubmitted = new ManualResetEventSlim(false);
    var handoff = new RecordingDictationHandoff(audio =>
    {
        if (audio.Reason == FinishReason.Silence)
        {
            silenceSubmitted.Set();
        }
    });
    var capture = new GatedPcmCapture(beforeLateStop, lateStop, silenceSubmitted);
    var runtime = new WindowsDictationRuntime(config, capture, recognizer, handoff, dryRun: true);
    var code = RunWithCapturedConsole(runtime, TimeSpan.FromSeconds(5), out var output);
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    return code == 0
        && audio is not null
        && audio.Reason == FinishReason.Silence
        && audio.Range == new SampleRange(expectedStart, recognizer.Requests[0].Range.End)
        && audio.Samples.Length == recognizer.Requests[0].Range.End - expectedStart
        && handoff.Submissions.Count == 1
        && recognizer.Requests.Count == 2
        && capture.GateWasReached
        && silenceSubmitted.IsSet
        && DictationDiagnosticsArePrivate(output);
}

static bool DictationRuntimeKeepsEmbeddedStopInSilenceBody()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 1200, maxSeconds: 10);
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 50, loud: false);
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
        request.Id,
        request.Extent,
        request.Range,
        "音声入力今日は入力ストップ晴れです",
        ImmutableArray.Create(
            Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
            Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
            Run("今日は入力ストップ晴れです", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests.Single().Range.Start + Segmenter.FrameLength * 4;
    return code == 0
        && audio is not null
        && audio.Reason == FinishReason.Silence
        && audio.Range.Start == expectedStart
        && audio.Range.End == recognizer.Requests.Single().Range.End;
}

static bool DictationRuntimeKeepsActiveBodyEmbeddedStopInSeparateUtterance()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 600, maxSeconds: 10);
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 30, loud: false);
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Id == 1
        ? new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))
        : new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "今日は入力ストップ晴れです",
            ImmutableArray.Create(
                Run("今日は", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("入力", request.Range.Start + Segmenter.FrameLength * 2, request.Range.Start + Segmenter.FrameLength * 3),
                Run("ストップ", request.Range.Start + Segmenter.FrameLength * 3, request.Range.Start + Segmenter.FrameLength * 4),
                Run("晴れです", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    return code == 0
        && recognizer.Requests.Count == 2
        && audio is not null
        && audio.Reason == FinishReason.Silence
        && audio.Range.Start == expectedStart
        && audio.Range.End == recognizer.Requests[1].Range.End
        && handoff.Submissions.Count == 1;
}

static bool DictationRuntimeShortSilenceWaitsForOpenUtterance()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 100, maxSeconds: 10);
    var prefix = new List<PcmFrame>();
    AddFrames(prefix, 3, loud: false);
    AddFrames(prefix, 12, loud: true);
    AddFrames(prefix, 12, loud: false);
    AddFrames(prefix, 12, loud: true);
    AddFrames(prefix, 1, loud: false);
    var suffix = new List<PcmFrame>(prefix);
    AddFrames(suffix, 11, loud: false);
    suffix = suffix.Skip(prefix.Count).ToList();
    using var bodyApplied = new ManualResetEventSlim(false);
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(request.Id == 1
        ? new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))
        : new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "入力ストップ",
            ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))));
    var handoff = new RecordingDictationHandoff();
    var capture = new GatedPcmCapture(prefix, suffix, bodyApplied);
    var runtime = new WindowsDictationRuntime(config, capture, recognizer, handoff, dryRun: true);
    var code = RunWithCapturedConsole(
        runtime,
        TimeSpan.FromSeconds(5),
        out var output,
        line =>
        {
            if (line.Contains("dictation session: body-start", StringComparison.Ordinal))
            {
                bodyApplied.Set();
            }
        });
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    var expectedStopStart = recognizer.Requests[1].Range.Start + Segmenter.FrameLength;
    return code == 0
        && audio is not null
        && audio.Reason == FinishReason.StandaloneStop
        && audio.Range == new SampleRange(expectedStart, expectedStopStart)
        && audio.Samples.Length == expectedStopStart - expectedStart
        && handoff.Submissions.Count == 1
        && recognizer.Requests.Count == 2
        && capture.GateWasReached
        && bodyApplied.IsSet
        && DictationDiagnosticsArePrivate(output);
}

static bool DictationRuntimeCaptureFaultDisposesCapture()
{
    var capture = new ThrowingPcmCapture();
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(), capture, new ScriptedDictationRecognizer(request => Task.FromResult(
        new RecognizedUtterance(request.Id, request.Extent, request.Range, "", []))), new RecordingDictationHandoff(), dryRun: true);
    try
    {
        _ = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
        return false;
    }
    catch (AggregateException ex) when (ex.InnerException is InvalidOperationException inner)
    {
        return inner.Message.Contains("fixture capture failure", StringComparison.Ordinal)
            && capture.Disposed;
    }
    catch (InvalidOperationException ex)
    {
        return ex.Message.Contains("fixture capture failure", StringComparison.Ordinal)
            && capture.Disposed;
    }
}

static bool DictationRuntimeIgnoresStaleQueuedRecognitionAfterReset()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 5000, maxSeconds: 10);
    var recognizer = new ScriptedDictationRecognizer(request =>
    {
        if (request.Id == 1)
        {
            throw new InvalidOperationException("fixture recognizer failure");
        }

        return Task.FromResult(new RecognizedUtterance(
            request.Id,
            request.Extent,
            request.Range,
            "音声入力本文",
            ImmutableArray.Create(
                Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End))));
    });
    var handoff = new RecordingDictationHandoff();
    var frames = new List<PcmFrame>();
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 1
        && handoff.Submissions[0].Range.Start >= recognizer.Requests[1].Range.Start;
}

static bool DictationRuntimePreservesQueuedSessionAfterDelayedStop()
{
    var config = DictationRuntimeTestConfig(startTimeoutMs: 3000, endSilenceMs: 5000, maxSeconds: 10);
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        if (request.Id == 2)
        {
            await Task.Delay(400);
        }

        return request.Id switch
        {
            1 or 3 => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "音声入力本文",
                ImmutableArray.Create(
                    Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                    Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
                    Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End))),
            _ => new RecognizedUtterance(
                request.Id,
                request.Extent,
                request.Range,
                "入力ストップ",
                ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))
        };
    });
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 2
        && recognizer.Requests.Count == 4;
}

static bool DictationPhasesFollowLoneWakeBodyAndStop()
{
    var observer = new PhaseRecorder();
    var handoff = new RecordingDictationHandoff();
    // Pauses after the wake utterance and after the first body frames, so each phase is published before the next batch lands.
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 5000), new FixturePcmCapture(ThreeUtteranceFrames(), [24, 28]), Recognizing("wake", "body", "stop"), handoff, dryRun: true, observer);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Single().Reason == FinishReason.StandaloneStop
        && observer.Phases.SequenceEqual([DictationPhase.Waiting, DictationPhase.Recording, DictationPhase.Ended]);
}

static bool DictationPhasesFollowOneBreathDictationEndedBySilence()
{
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 50, loud: false);
    var observer = new PhaseRecorder();
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 1200), new FixturePcmCapture(frames, delayAfterFrame: 24), Recognizing("wakebody"), handoff, dryRun: true, observer);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Single().Reason == FinishReason.Silence
        && observer.Phases.SequenceEqual([DictationPhase.Recording, DictationPhase.Idle]);
}

static bool DictationPhasesFollowLoneWakeEndedByStop()
{
    var observer = new PhaseRecorder();
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 5000), new FixturePcmCapture(TwoUtteranceFrames(), [24, 28]), Recognizing("wake", "stop"), handoff, dryRun: true, observer);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 0
        && observer.Phases.SequenceEqual([DictationPhase.Waiting, DictationPhase.Recording, DictationPhase.Ended]);
}

static bool DictationPhasesFollowLoneWakeStartTimeout()
{
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 40, loud: false);
    var observer = new PhaseRecorder();
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(startTimeoutMs: 300), new FixturePcmCapture(frames, delayAfterFrame: 24), Recognizing("wake"), handoff, dryRun: true, observer);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Count == 0
        && observer.Phases.SequenceEqual([DictationPhase.Waiting, DictationPhase.Idle]);
}

static bool DictationPhasesAndTargetAcrossTwoDictations()
{
    var frames = ThreeUtteranceFrames().ToList();
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    var observer = new PhaseRecorder();
    var handoff = new RecordingDictationHandoff();
    var window = 0x41;
    var runtime = new WindowsDictationRuntime(
        DictationRuntimeTestConfig(endSilenceMs: 5000),
        new FixturePcmCapture(frames, [24, 48, 72]),
        Recognizing("wakebody", "stop", "wakebody", "stop"),
        handoff,
        dryRun: true,
        observer,
        foregroundWindow: () => ++window);
    return RunWithTimeout(runtime, TimeSpan.FromSeconds(5)) == 0
        && handoff.Submissions.Select(audio => audio.Target).SequenceEqual([(nint)0x42, (nint)0x43])
        && handoff.Submissions.All(audio => audio.Reason == FinishReason.StandaloneStop)
        && observer.Phases.SequenceEqual([DictationPhase.Recording, DictationPhase.Ended, DictationPhase.Recording, DictationPhase.Ended]);
}

static RecognizedUtterance Utterance(RecognitionRequest request, string kind) => kind switch
{
    "wake" => new(request.Id, request.Extent, request.Range, "音声入力", [Run("音声入力", request.Range.Start, request.Range.End)]),
    "body" => new(request.Id, request.Extent, request.Range, "本文", [Run("本文", request.Range.Start + Segmenter.FrameLength, request.Range.End)]),
    "stop" => new(request.Id, request.Extent, request.Range, "入力ストップ", [Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)]),
    _ => new(request.Id, request.Extent, request.Range, "音声入力本文", [Run("音声入力", request.Range.Start, request.Range.Start + Segmenter.FrameLength), Run("本文", request.Range.Start + Segmenter.FrameLength, request.Range.End)])
};

// kinds[i] answers request i+1; the last kind repeats for any later request.
static ScriptedDictationRecognizer Recognizing(params string[] kinds) =>
    new(request => Task.FromResult(Utterance(request, kinds[Math.Min((int)request.Id, kinds.Length) - 1])));

static bool DictationWinMmNativeLayoutAndInputDataMessage() =>
    DictationWinMmUsesInputDataCallbackMessage()
    && WinMmCapture.WaveFormatSizeForTest() == 18
    && WinMmCapture.WaveHeaderSizeForTest() == 48;

static bool DictationWinMmUsesInputDataCallbackMessage() =>
    WinMmCapture.IsInputDataMessageForTest(0x3C0)
    && !WinMmCapture.IsInputDataMessageForTest(0x3BD);

static bool DictationWinMmDisposeWaitsForWorkerBeforeFreeingBuffers()
{
    var native = new FakeWaveInNative { BlockRequeue = true };
    var capture = WinMmCapture.OpenForTest(native);
    native.SignalOneBuffer();
    if (!native.RequeueEntered.Wait(TimeSpan.FromSeconds(2)))
    {
        return false;
    }

    var dispose = capture.DisposeAsync().AsTask();
    Thread.Sleep(100);
    var didNotResetOrFreeWhileWorkerBlocked = native.ResetCount == 0 && native.UnprepareCount == 0 && native.CloseCount == 0;
    native.ReleaseRequeue.Set();
    if (!dispose.Wait(TimeSpan.FromSeconds(2)))
    {
        return false;
    }

    return didNotResetOrFreeWhileWorkerBlocked
        && !native.AddBufferAfterFinalReset
        && !native.PrematureUnprepare
        && native.ResetCount == 1
        && native.UnprepareCount == 4
        && native.CloseCount == 1;
}

static bool DictationSampleStoreRejectsDiscontinuity()
{
    var store = new SampleStore();
    store.Append(0, [1, 2, 3]);
    try
    {
        store.Append(4, [5]);
        return false;
    }
    catch (InvalidOperationException)
    {
        return true;
    }
}

static bool DictationStaleRecognitionIsSafeError()
{
    var config = DictationConfig();
    var session = StartedSession(config, out var store);
    _ = session.Finish(FinishReason.FinishCommand, store.Copy);
    _ = session.Apply(Recognized(99, RecognitionExtent.ClosedUtterance, 21000, 22000, "遅延", false, Run("遅延", 21000, 22000)), store.Copy);
    return session.Events.Last().Reason == FinishReason.StaleRecognition;
}

static bool DictationWavBytesAreExact()
{
    var wav = Pcm16Wav.Encode([1, -2]);
    return Encoding.ASCII.GetString(wav, 0, 4) == "RIFF"
        && Encoding.ASCII.GetString(wav, 8, 4) == "WAVE"
        && BitConverter.ToInt32(wav, 24) == 16000
        && BitConverter.ToInt32(wav, 28) == 32000
        && BitConverter.ToInt16(wav, 32) == 2
        && BitConverter.ToInt16(wav, 34) == 16
        && Encoding.ASCII.GetString(wav, 36, 4) == "data"
        && BitConverter.ToInt32(wav, 40) == 4
        && BitConverter.ToInt16(wav, 44) == 1
        && BitConverter.ToInt16(wav, 46) == -2;
}

static bool DictationStrictWavParsingAndSourceContinuity()
{
    using var temp = RuntimeTemp();
    var wavPath = Path.Combine(temp.Dir, "input.wav");
    File.WriteAllBytes(wavPath, Pcm16Wav.Encode(Enumerable.Range(0, Segmenter.FrameLength * 2 + 17).Select(i => (short)i).ToArray()));
    var decoded = Pcm16Wav.DecodeStrict(new FileInfo(wavPath), Segmenter.FrameLength * 3);
    var capture = new WavPcmCapture(wavPath, paced: false);
    var frames = CollectFrames(capture).ToArray();
    var malformed = Path.Combine(temp.Dir, "bad.wav");
    File.WriteAllBytes(malformed, Encoding.ASCII.GetBytes("not a wav file"));
    var rejected = false;
    try
    {
        _ = Pcm16Wav.DecodeStrict(new FileInfo(malformed), Segmenter.FrameLength);
    }
    catch (InvalidDataException)
    {
        rejected = true;
    }

    return rejected
        && decoded.Length == Segmenter.FrameLength * 2 + 17
        && frames.Length == 3
        && frames[0].Start == 0
        && frames[1].Start == Segmenter.FrameLength
        && frames[2].Start == Segmenter.FrameLength * 2
        && frames[2].Samples.Length == 17;
}

static bool DictationSegmenterUsesPartialEofSampleDuration()
{
    var config = new VoiceSwitchConfig(["test"], null, "true", MaxSeconds: 2.5, HangoverMs: 300, PrerollMs: 0, MinSpeechMs: 300, VadRatio: 3, VadMinRMS: 0.005f);
    var shortSegmenter = new Segmenter(config);
    var fullSegmenter = new Segmenter(config);
    var loud = Enumerable.Repeat(0.2f, Segmenter.FrameLength).ToArray();

    for (var i = 0; i < 9; i++)
    {
        _ = shortSegmenter.Push(loud);
    }

    _ = shortSegmenter.Push([0.2f]);
    for (var i = 0; i < 10; i++)
    {
        _ = fullSegmenter.Push(loud);
    }

    var tooShort = shortSegmenter.Flush();
    var exact = fullSegmenter.Flush();
    return tooShort is null
        && exact is { Kind: "utterance" }
        && exact.Samples.Length == Segmenter.FrameLength * 10;
}

static bool DictationRuntimeFinalizesBodyAtSyntheticEof()
{
    using var temp = RuntimeTemp();
    var wavPath = Path.Combine(temp.Dir, "eof.wav");
    var samples = new List<short>();
    samples.AddRange(Enumerable.Repeat((short)1, Segmenter.FrameLength * 3));
    samples.AddRange(Enumerable.Repeat((short)8000, Segmenter.FrameLength * 12));
    File.WriteAllBytes(wavPath, Pcm16Wav.Encode(samples.ToArray()));
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
        request.Id,
        request.Extent,
        request.Range,
        "音声入力本文",
        ImmutableArray.Create(
            Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
            Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
            Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(
        DictationRuntimeTestConfig(endSilenceMs: 5000),
        new WavPcmCapture(wavPath, paced: false),
        recognizer,
        handoff,
        dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    return code == 0
        && recognizer.Requests.Count == 1
        && audio is not null
        && audio.Reason == FinishReason.Silence
        && audio.Range.Start == recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4
        && audio.Range.End == recognizer.Requests[0].Range.End;
}

static bool DictationRuntimeKeepsPartialEofPcmRangeExact()
{
    using var temp = RuntimeTemp();
    var wavPath = Path.Combine(temp.Dir, "partial-eof.wav");
    var samples = new List<short>();
    samples.AddRange(Enumerable.Repeat((short)1, Segmenter.FrameLength * 3));
    samples.AddRange(Enumerable.Range(0, Segmenter.FrameLength * 12 + 1).Select(i => (short)(1000 + i)));
    File.WriteAllBytes(wavPath, Pcm16Wav.Encode(samples.ToArray()));
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
        request.Id,
        request.Extent,
        request.Range,
        "音声入力本文",
        ImmutableArray.Create(
            Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
            Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
            Run("本文", request.Range.Start + Segmenter.FrameLength * 4, request.Range.End)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(
        DictationRuntimeTestConfig(endSilenceMs: 5000),
        new WavPcmCapture(wavPath, paced: false),
        recognizer,
        handoff,
        dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var expectedStart = recognizer.Requests[0].Range.Start + Segmenter.FrameLength * 4;
    var expectedSamples = samples.Skip((int)expectedStart).Take(samples.Count - (int)expectedStart).ToArray();
    return code == 0
        && audio is not null
        && audio.Range == new SampleRange(expectedStart, samples.Count)
        && audio.Samples.SequenceEqual(expectedSamples);
}

static bool DictationLocalRecordingHandoffWritesByteExactBodies()
{
    using var temp = RuntimeTemp();
    var output = Path.Combine(temp.Dir, "out");
    var samples = ImmutableArray.Create<short>(1, -2, 300);
    var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(100, 103), samples, FinishReason.StandaloneStop);
    var result = new LocalRecordingHandoff(output, "source.wav").SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
    var wav = Directory.EnumerateFiles(output, "*.wav").Single();
    var metadata = Directory.EnumerateFiles(output, "*.json").Single();
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadata));
    var root = doc.RootElement;
    return result.Status == HandoffStatus.RecordedLocally
        && File.ReadAllBytes(wav).SequenceEqual(Pcm16Wav.Encode(samples.AsSpan()))
        && root.GetProperty("SourceStart").GetInt64() == 100
        && root.GetProperty("SourceEnd").GetInt64() == 103
        && root.GetProperty("SourcePcmSha256").GetString() == Pcm16Wav.Sha256Hex(samples.AsSpan())
        && root.GetProperty("FinishReason").GetString() == FinishReason.StandaloneStop.ToString();
}

static bool DictationLocalRecordingHandoffAllowsConsecutiveBodies()
{
    using var temp = RuntimeTemp();
    var output = Path.Combine(temp.Dir, "out");
    var handoff = new LocalRecordingHandoff(output);
    var first = handoff.SubmitAsync(new DictationAudio(Guid.NewGuid(), new SampleRange(0, 1), ImmutableArray.Create<short>(11)), CancellationToken.None).GetAwaiter().GetResult();
    Thread.Sleep(2);
    var second = handoff.SubmitAsync(new DictationAudio(Guid.NewGuid(), new SampleRange(2, 4), ImmutableArray.Create<short>(22, 33)), CancellationToken.None).GetAwaiter().GetResult();
    return first.Status == HandoffStatus.RecordedLocally
        && second.Status == HandoffStatus.RecordedLocally
        && Directory.EnumerateFiles(output, "*.wav").Count() == 2
        && Directory.EnumerateFiles(output, "*.json").Count() == 2;
}

static bool NoiseProcessorOffIsBitExact()
{
    var samples = ImmutableArray.Create<short>(short.MinValue, -123, 0, 123, short.MaxValue);
    var frames = new List<AnalysisFrame>();
    var processor = new NoiseProcessor(new NoiseReductionOptions(), frames.Add, 0.005);
    processor.Push(new PcmFrame(0, samples[..2]));
    processor.Push(new PcmFrame(2, samples[2..]));
    processor.Complete();
    processor.Complete();
    var rejected = false;
    try
    {
        processor.Push(new PcmFrame(samples.Length, ImmutableArray.Create<short>(1)));
    }
    catch (InvalidOperationException)
    {
        rejected = true;
    }

    var original = frames.SelectMany(item => item.Original).ToArray();
    var analysis = frames.SelectMany(item => item.Analysis).ToArray();
    return rejected
        && frames.Select(item => item.Start).SequenceEqual([0L, 2L])
        && original.SequenceEqual(samples)
        && analysis.SequenceEqual(samples);
}

static bool NoiseProcessorConservativeWolaPreservesUnityBoundaries()
{
    foreach (var length in new[] { 0, 1, 159, 160, 161, 319, 320, 321, 479, 480, 481, 960, 1237 })
    {
        var samples = SeededSamples(length, 0x12345678u);
        var output = RunNoiseProcessor(samples, [997], out _);
        if (output.Length != samples.Length || !WithinOneLsb(output, samples))
        {
            return false;
        }
    }

    var zero = ImmutableArray.CreateRange(Enumerable.Repeat((short)0, 481));
    return RunNoiseProcessor(zero, [17], out _).All(sample => sample == 0);
}

static bool NoiseProcessorCalibratedGainPreservesConjugates()
{
    var processor = new NoiseProcessor(new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener), _ => { }, 0.005);
    var type = typeof(NoiseProcessor);
    var real = (double[])type.GetField("real", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    var imag = (double[])type.GetField("imag", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    var power = (double[])type.GetField("power", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    var noisePsd = (double[])type.GetField("noisePsd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    var previousEnhancedPower = (double[])type.GetField("previousEnhancedPower", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    var previousGain = (double[])type.GetField("previousGain", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
    type.GetField("calibrated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(processor, true);

    const int bin = 37;
    real[bin] = 3.0;
    imag[bin] = 2.0;
    real[NoiseProcessor.FftLength - bin] = 3.0;
    imag[NoiseProcessor.FftLength - bin] = -2.0;
    power[bin] = 2.0;
    noisePsd[bin] = 1.0;
    previousEnhancedPower[bin] = 4.0;

    type.GetMethod("ApplyGain", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(processor, [false]);

    const double expectedGain = 3.88 / 4.88;
    var expectedHistory = expectedGain * expectedGain * power[bin];
    var conjugatesMatch = Math.Abs(real[bin] - real[NoiseProcessor.FftLength - bin]) < 1e-12
        && Math.Abs(imag[bin] + imag[NoiseProcessor.FftLength - bin]) < 1e-12
        && Math.Abs(previousGain[bin] - expectedGain) < 1e-12
        && Math.Abs(previousEnhancedPower[bin] - expectedHistory) < 1e-12;

    NoiseProcessor.FftForTest(real, imag, inverse: true);
    var imaginaryResidual = imag.Max(value => Math.Abs(value));
    return conjugatesMatch && imaginaryResidual < 1e-12;
}

static bool NoiseProcessorCalibratedOutputRemainsRealWaveform()
{
    var samples = new short[Segmenter.FrameLength * 95];
    for (var i = 0; i < samples.Length; i++)
    {
        samples[i] = (short)Math.Round(50 * Math.Sin(i * 2.0 * Math.PI / 47.0), MidpointRounding.AwayFromZero);
        if (i >= Segmenter.FrameLength * 70)
        {
            samples[i] = (short)Math.Round(samples[i] + 4000 * Math.Sin(i * 2.0 * Math.PI / 80.0), MidpointRounding.AwayFromZero);
        }
    }

    var frames = new List<AnalysisFrame>();
    var processor = new NoiseProcessor(new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener), frames.Add, 0.005);
    for (var offset = 0; offset < samples.Length; offset += 777)
    {
        var count = Math.Min(777, samples.Length - offset);
        processor.Push(new PcmFrame(offset, samples.AsSpan(offset, count).ToArray().ToImmutableArray()));
    }

    processor.Complete();
    var output = frames.SelectMany(frame => frame.Analysis).ToArray();
    var original = frames.SelectMany(frame => frame.Original).ToArray();
    return processor.Status.Calibrated
        && processor.Status.Saturations == 0
        && output.Length == samples.Length
        && original.SequenceEqual(samples)
        && output.Skip(Segmenter.FrameLength * 75).Any(sample => sample != 0)
        && output.Skip(Segmenter.FrameLength * 75).Zip(samples.Skip(Segmenter.FrameLength * 75), (a, b) => a != b).Any(changed => changed);
}

static bool NoiseProcessorPartitionsAreStable()
{
    var samples = CalibratedPartitionSamples();
    var whole = RunNoiseProcessorFrames(samples, new[] { samples.Length }, out var wholeStatus);
    if (!wholeStatus.Calibrated)
    {
        return false;
    }

    foreach (var partitions in new[]
    {
        new[] { 1 },
        new[] { 7 },
        new[] { 159 },
        new[] { 160 },
        new[] { 320 },
        new[] { 480 },
        new[] { 997 },
        new[] { 1, 7, 159, 160, 320, 480, 997, 31 }
    })
    {
        var chunked = RunNoiseProcessorFrames(samples, partitions, out var status);
        if (!status.Calibrated || chunked.Count != whole.Count)
        {
            return false;
        }

        for (var i = 0; i < whole.Count; i++)
        {
            if (chunked[i].Start != whole[i].Start
                || !chunked[i].Original.SequenceEqual(whole[i].Original)
                || !chunked[i].Analysis.SequenceEqual(whole[i].Analysis))
            {
                return false;
            }
        }
    }

    return true;
}

static ImmutableArray<short> CalibratedPartitionSamples()
{
    var samples = new short[Segmenter.FrameLength * 170 + 37];
    for (var i = 0; i < samples.Length; i++)
    {
        var quiet = 70 * Math.Sin(i * 2.0 * Math.PI / 53.0);
        var speech = i >= Segmenter.FrameLength * 100 ? 4000 * Math.Sin(i * 2.0 * Math.PI / 101.0) : 0;
        samples[i] = (short)Math.Round(quiet + speech, MidpointRounding.AwayFromZero);
    }

    return samples.ToImmutableArray();
}

static bool NoiseProcessorHandlesHighAbsoluteIndexes()
{
    const long start = (long)int.MaxValue + 12345;
    const int impulseIndex = 777;
    var samples = Enumerable.Repeat((short)0, 2000).ToArray();
    samples[impulseIndex] = 20000;
    var frames = RunNoiseProcessorFrames(samples.ToImmutableArray(), new[] { 1, 997, 53 }, out var status, start);
    var original = frames.SelectMany(frame => frame.Original).ToArray();
    var analysis = frames.SelectMany(frame => frame.Analysis).ToArray();
    var starts = frames.Select(frame => frame.Start).ToArray();
    var maxIndex = 0;
    for (var i = 1; i < analysis.Length; i++)
    {
        if (Math.Abs(analysis[i]) > Math.Abs(analysis[maxIndex]))
        {
            maxIndex = i;
        }
    }

    return status.ProcessedSamples == samples.Length
        && status.InternalStateHighWaterSamples <= NoiseProcessor.InternalStateSampleBudget
        && starts.First() == start
        && starts.Last() == start + Segmenter.FrameLength * (starts.Length - 1L)
        && original.SequenceEqual(samples)
        && maxIndex == impulseIndex
        && Math.Abs(analysis[impulseIndex] - samples[impulseIndex]) <= 1;
}

static bool NoiseProcessorBoundsRetainedStateOnLongStreams()
{
    const int totalFrames = 10 * 60 * 16000 / Segmenter.FrameLength;
    var emitted = 0L;
    var rawUnchanged = true;
    var processor = new NoiseProcessor(new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener), frame =>
    {
        for (var i = 0; i < frame.Original.Length; i++)
        {
            if (frame.Original[i] != LongStreamSample(frame.Start + i))
            {
                rawUnchanged = false;
            }
        }

        emitted += frame.Original.Length;
    }, 0.005);

    for (var frame = 0; frame < totalFrames; frame++)
    {
        var start = frame * Segmenter.FrameLength;
        var samples = Enumerable.Range(0, Segmenter.FrameLength)
            .Select(i => LongStreamSample(start + i))
            .ToImmutableArray();
        processor.Push(new PcmFrame(start, samples));
    }

    processor.Complete();
    var status = processor.Status;
    return rawUnchanged
        && emitted == (long)totalFrames * Segmenter.FrameLength
        && status.InternalStateHighWaterSamples <= NoiseProcessor.InternalStateSampleBudget
        && status.RetainedInputSamples <= NoiseProcessor.InternalStateSampleBudget
        && status.RetainedOlaSamples <= NoiseProcessor.InternalStateSampleBudget;
}

static bool NoiseProcessorImpulseIndexIsStable()
{
    foreach (var index in new[] { 0, 1, 159, 160, 161, 319, 320, 777, 959 })
    {
        var samples = Enumerable.Repeat((short)0, 1000).ToArray();
        samples[index] = 20000;
        var output = RunNoiseProcessor(samples.ToImmutableArray(), [37], out _);
        var maxIndex = 0;
        for (var i = 1; i < output.Length; i++)
        {
            if (Math.Abs(output[i]) > Math.Abs(output[maxIndex]))
            {
                maxIndex = i;
            }
        }

        if (maxIndex != index || Math.Abs(output[index] - samples[index]) > 1)
        {
            return false;
        }
    }

    return true;
}

static short LongStreamSample(long index) =>
    (short)Math.Round(60 * Math.Sin(index * 2.0 * Math.PI / 53.0), MidpointRounding.AwayFromZero);

static bool NoiseProcessorFftMatchesNaiveDftOracle()
{
    var real = new double[NoiseProcessor.FftLength];
    var imag = new double[NoiseProcessor.FftLength];
    for (var i = 0; i < 8; i++)
    {
        real[i] = i - 3;
        imag[i] = (i % 3) - 1;
    }

    var expected = NaiveDft(real, imag, inverse: false);
    NoiseProcessor.FftForTest(real, imag, inverse: false);
    for (var k = 0; k < 16; k++)
    {
        if (Math.Abs(real[k] - expected.Real[k]) > 1e-9 || Math.Abs(imag[k] - expected.Imag[k]) > 1e-9)
        {
            return false;
        }
    }

    NoiseProcessor.FftForTest(real, imag, inverse: true);
    for (var i = 0; i < 8; i++)
    {
        if (Math.Abs(real[i] - (i - 3)) > 1e-9 || Math.Abs(imag[i] - ((i % 3) - 1)) > 1e-9)
        {
            return false;
        }
    }

    return true;
}

static bool NoiseProcessorConservativeGainIsBounded()
{
    var samples = new short[Segmenter.FrameLength * 100];
    for (var i = 0; i < samples.Length; i++)
    {
        var quietNoise = (short)(((i * 1103515245 + 12345) >> 16) % 100);
        samples[i] = quietNoise;
        if (i >= Segmenter.FrameLength * 70)
        {
            samples[i] = (short)(quietNoise + 5000 * Math.Sin(i * 2.0 * Math.PI / 97.0));
        }
    }

    var output = RunNoiseProcessor(samples.ToImmutableArray(), [480], out var status);
    return status.Calibrated
        && status.Saturations == 0
        && output.Length == samples.Length
        && output.All(sample => sample >= short.MinValue && sample <= short.MaxValue)
        && output.Skip(Segmenter.FrameLength * 75).Zip(samples.Skip(Segmenter.FrameLength * 75), (a, b) => a != b).Any(changed => changed);
}

static bool DictationNoiseAnalysisNeverReplacesHandoffSource()
{
    var config = DictationRuntimeTestConfig() with
    {
        NoiseReduction = new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener),
        Dictation = new DictationConfig(EndSilenceMs: 300, MaxSeconds: 10, StartTimeoutMs: 3000)
    };
    var frames = new List<PcmFrame>();
    AddNoiseCalibrationFrames(frames, 90);
    AddFrames(frames, 16, loud: true);
    AddFrames(frames, 20, loud: false);
    var source = frames.SelectMany(frame => frame.Samples).ToArray();
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
        request.Id,
        request.Extent,
        request.Range,
        "音声入力本文",
        ImmutableArray.Create(
            Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
            Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
            Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End)))));
    var handoff = new RecordingDictationHandoff();
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, handoff, dryRun: true);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var audio = handoff.Submissions.SingleOrDefault();
    var request = recognizer.Requests.SingleOrDefault();
    if (code != 0 || audio is null || request is null)
    {
        return false;
    }

    var requestOriginal = source.Skip((int)request.Range.Start).Take(request.Samples.Length).ToArray();
    var handoffOriginal = source.Skip((int)audio.Range.Start).Take(audio.Samples.Length).ToArray();
    return !request.Samples.SequenceEqual(requestOriginal)
        && audio.Samples.SequenceEqual(handoffOriginal);
}

static bool DictationNoiseOnLocalRecordingIsByteExact()
{
    using var temp = RuntimeTemp();
    var output = Path.Combine(temp.Dir, "out");
    var config = DictationRuntimeTestConfig() with
    {
        NoiseReduction = new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener),
        Dictation = new DictationConfig(EndSilenceMs: 300, MaxSeconds: 10, StartTimeoutMs: 3000)
    };
    var frames = new List<PcmFrame>();
    AddNoiseCalibrationFrames(frames, 90);
    AddFrames(frames, 16, loud: true);
    AddFrames(frames, 20, loud: false);
    var source = frames.SelectMany(frame => frame.Samples).ToArray();
    var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
        request.Id,
        request.Extent,
        request.Range,
        "音声入力本文",
        ImmutableArray.Create(
            Run("音声", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
            Run("入力", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 2),
            Run("本文", request.Range.Start + Segmenter.FrameLength * 3, request.Range.End)))));
    var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames), recognizer, new LocalRecordingHandoff(output), dryRun: false);
    var code = RunWithTimeout(runtime, TimeSpan.FromSeconds(5));
    var request = recognizer.Requests.SingleOrDefault();
    var wav = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.wav").SingleOrDefault() : null;
    var metadata = Directory.Exists(output) ? Directory.EnumerateFiles(output, "*.json").SingleOrDefault() : null;
    if (code != 0 || request is null || wav is null || metadata is null)
    {
        return false;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metadata));
    var root = doc.RootElement;
    var sourceStart = root.GetProperty("SourceStart").GetInt64();
    var sourceEnd = root.GetProperty("SourceEnd").GetInt64();
    var expected = source.Skip((int)sourceStart).Take((int)(sourceEnd - sourceStart)).ToArray();
    var recorded = Pcm16Wav.DecodeStrict(new FileInfo(wav), expected.Length + 1).ToArray();
    return !request.Samples.SequenceEqual(source.Skip((int)request.Range.Start).Take(request.Samples.Length))
        && recorded.SequenceEqual(expected)
        && root.GetProperty("SourcePcmSha256").GetString() == Pcm16Wav.Sha256Hex(expected.AsSpan())
        && root.GetProperty("OutputWavSha256").GetString() == Pcm16Wav.Sha256Hex(File.ReadAllBytes(wav));
}

static void AddNoiseCalibrationFrames(List<PcmFrame> frames, int count)
{
    for (var i = 0; i < count; i++)
    {
        var start = frames.Count * Segmenter.FrameLength;
        var samples = Enumerable.Range(0, Segmenter.FrameLength)
            .Select(j => (short)Math.Round(70 * Math.Sin((start + j) * 2.0 * Math.PI / 53.0), MidpointRounding.AwayFromZero))
            .ToImmutableArray();
        frames.Add(new PcmFrame(start, samples));
    }
}

static ImmutableArray<short> RunNoiseProcessor(ImmutableArray<short> samples, int[] partitions, out NoiseProcessorStatus status, long start = 0)
{
    var frames = RunNoiseProcessorFrames(samples, partitions, out status, start);
    return frames.SelectMany(item => item.Analysis).ToImmutableArray();
}

static List<AnalysisFrame> RunNoiseProcessorFrames(ImmutableArray<short> samples, int[] partitions, out NoiseProcessorStatus status, long start = 0)
{
    var frames = new List<AnalysisFrame>();
    var processor = new NoiseProcessor(new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener), frames.Add, 0.005);
    processor.Reset(start);
    var offset = 0;
    var partitionIndex = 0;
    while (offset < samples.Length)
    {
        var size = Math.Min(partitions[partitionIndex % partitions.Length], samples.Length - offset);
        processor.Push(new PcmFrame(start + offset, samples.AsSpan(offset, size).ToArray().ToImmutableArray()));
        offset += size;
        partitionIndex++;
    }

    processor.Complete();
    status = processor.Status;
    return frames;
}

static ImmutableArray<short> SeededSamples(int length, uint seed)
{
    var values = new short[length];
    var state = seed == 0 ? 1u : seed;
    for (var i = 0; i < values.Length; i++)
    {
        state ^= state << 13;
        state ^= state >> 17;
        state ^= state << 5;
        values[i] = (short)((int)(state % 20001) - 10000);
    }

    return values.ToImmutableArray();
}

static bool WithinOneLsb(IReadOnlyList<short> actual, IReadOnlyList<short> expected)
{
    if (actual.Count != expected.Count)
    {
        return false;
    }

    for (var i = 0; i < actual.Count; i++)
    {
        if (Math.Abs(actual[i] - expected[i]) > 1)
        {
            return false;
        }
    }

    return true;
}

static (double[] Real, double[] Imag) NaiveDft(double[] real, double[] imag, bool inverse)
{
    var outputReal = new double[NoiseProcessor.FftLength];
    var outputImag = new double[NoiseProcessor.FftLength];
    var sign = inverse ? 1.0 : -1.0;
    for (var k = 0; k < NoiseProcessor.FftLength; k++)
    {
        for (var n = 0; n < NoiseProcessor.FftLength; n++)
        {
            var angle = sign * 2.0 * Math.PI * k * n / NoiseProcessor.FftLength;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            outputReal[k] += real[n] * cos - imag[n] * sin;
            outputImag[k] += real[n] * sin + imag[n] * cos;
        }
    }

    if (inverse)
    {
        for (var i = 0; i < outputReal.Length; i++)
        {
            outputReal[i] /= NoiseProcessor.FftLength;
            outputImag[i] /= NoiseProcessor.FftLength;
        }
    }

    return (outputReal, outputImag);
}

static bool DictationSyntheticCaptureCancellationDisposesSource()
{
    using var temp = RuntimeTemp();
    var wavPath = Path.Combine(temp.Dir, "long.wav");
    File.WriteAllBytes(wavPath, Pcm16Wav.Encode(Enumerable.Repeat((short)1, Segmenter.FrameLength * 40).ToArray()));
    var capture = new WavPcmCapture(wavPath, paced: true);
    var runtime = new WindowsDictationRuntime(
        DictationRuntimeTestConfig(),
        capture,
        new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(request.Id, request.Extent, request.Range, "", []))),
        new RecordingDictationHandoff(),
        dryRun: true);
    using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
    var code = runtime.RunAsync(cancel.Token).GetAwaiter().GetResult();
    return code == 0 && capture.DisposedForTest;
}

static bool DictationHandoffTranscribesAndDeletesWav()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    var recordings = Path.Combine(temp.Dir, "recordings");
    ProcessStartInfo? seen = null;
    var handoff = FakeSuperwhisper(root, recordings, psi =>
    {
        seen = psi;
        WriteSuperwhisperMeta(recordings, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), """{"llmResult":"こんにちは"}""");
        return Process.GetCurrentProcess();
    });
    var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(1, 2));
    HandoffResult? result = null;
    var (_, output) = CaptureConsole(() => { result = SubmitHandoff(handoff, audio); return 0; });
    var wav = Path.Combine(root, $"{audio.SessionId:N}.wav");
    return result!.Status == HandoffStatus.Transcribed
        && output.Contains("dictation: 5 chars in ", StringComparison.Ordinal)
        && !File.Exists(wav)
        && seen!.ArgumentList.SequenceEqual(["superwhisper://file//" + Path.GetFullPath(wav)]);
}

static bool DictationHandoffKeepsWavWithoutResult()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    var recordings = Path.Combine(temp.Dir, "recordings");
    WriteSuperwhisperMeta(recordings, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60).ToString(), """{"result":"older run"}""");
    var launches = 0;
    var handoff = FakeSuperwhisper(root, recordings, _ => { launches++; return Process.GetCurrentProcess(); });
    var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(5, 6));
    HandoffResult? result = null;
    var (_, output) = CaptureConsole(() => { result = SubmitHandoff(handoff, audio); return 0; });
    var wav = Path.Combine(root, $"{audio.SessionId:N}.wav");
    return result!.Status == HandoffStatus.NoResult
        && launches == 1
        && result.Path == wav
        && output.Contains($"dictation: no superwhisper result within 30 s, kept {wav}", StringComparison.Ordinal)
        && Pcm16Wav.DecodeStrict(new FileInfo(wav), 100).SequenceEqual(audio.Samples);
}

static bool DictationHandoffRestoresFocusBeforePolling()
{
    using var temp = RuntimeTemp();
    var recordings = Path.Combine(temp.Dir, "recordings");
    var targets = new List<nint>();
    var handoff = FakeSuperwhisper(Path.Combine(temp.Dir, "handoff"), recordings, _ => Process.GetCurrentProcess(), target =>
    {
        targets.Add(target);
        if (targets.Count == 20)
        {
            WriteSuperwhisperMeta(recordings, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), """{"result":"ok"}""");
        }
    });
    var result = SubmitHandoff(handoff, new DictationAudio(Guid.NewGuid(), new SampleRange(0, 1), ImmutableArray.Create<short>(1), Target: 0x1234));
    return result.Status == HandoffStatus.Transcribed
        && targets.Count == 20
        && targets.All(target => target == 0x1234);
}

static bool SuperwhisperFindResultPicksNewestNonEmpty()
{
    using var temp = RuntimeTemp();
    var recordings = Path.Combine(temp.Dir, "recordings");
    WriteSuperwhisperMeta(recordings, "100", """{"result":"old"}""");
    WriteSuperwhisperMeta(recordings, "105", """{"llmResult":"","result":"b"}""");
    WriteSuperwhisperMeta(recordings, "107", """{"llmResult":"half""");
    WriteSuperwhisperMeta(recordings, "abc", """{"result":"not a run"}""");
    return RegisteredSuperwhisperHandoff.FindResult(recordings, 103) == "b"
        && RegisteredSuperwhisperHandoff.FindResult(recordings, 99) == "b"
        && RegisteredSuperwhisperHandoff.FindResult(recordings, 108) is null
        && RegisteredSuperwhisperHandoff.FindResult(Path.Combine(temp.Dir, "missing"), 0) is null;
}

static bool DictationHandoffSweepsOldFiles()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    Directory.CreateDirectory(root);
    var old = Path.Combine(root, "old.wav");
    var fresh = Path.Combine(root, "fresh.wav");
    File.WriteAllBytes(old, [1]);
    File.WriteAllBytes(fresh, [2]);
    File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-11));
    _ = new RegisteredSuperwhisperHandoff(root, Path.Combine(temp.Dir, "recordings"));
    return !File.Exists(old) && File.Exists(fresh);
}

static bool RuntimeLaunchesSuperwhisperForConsecutiveDictations()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    var recordings = Path.Combine(temp.Dir, "recordings");
    var launches = 0;
    var handoff = FakeSuperwhisper(root, recordings, _ =>
    {
        var run = Interlocked.Increment(ref launches);
        WriteSuperwhisperMeta(recordings, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + run).ToString(), $$"""{"result":"run {{run}}"}""");
        return Process.GetCurrentProcess();
    });
    var recognize = TwoDictationRecognizer();
    // The second dictation must arrive after the first handoff finished, or the runtime drops it by design.
    var recognizer = new ScriptedDictationRecognizer(async request =>
    {
        if (request.Id == 3)
        {
            while (Volatile.Read(ref launches) == 0 || Directory.EnumerateFiles(root, "*.wav").Any()) await Task.Delay(10);
            await Task.Delay(100);
        }

        return await recognize.RecognizeAsync(request, CancellationToken.None);
    });
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 5000), new WavPcmCapture(TwoDictationWav(temp.Dir), paced: false), recognizer, handoff, dryRun: false);
    var code = RunWithCapturedConsole(runtime, TimeSpan.FromSeconds(10), out var output);
    return code == 0
        && launches == 2
        && output.Split('\n').Count(line => line.Contains("dictation handoff: Transcribed")) == 2
        && !Directory.EnumerateFiles(root, "*.wav").Any();
}

static bool DictationHandoffRejectsUnsupportedIntakePath()
{
    using var temp = RuntimeTemp();
    var launches = 0;
    var root = Path.Combine(temp.Dir, "sp ace#%日本語");
    var handoff = FakeSuperwhisper(root, Path.Combine(temp.Dir, "recordings"), _ => { launches++; return Process.GetCurrentProcess(); });
    var result = SubmitHandoff(handoff, new DictationAudio(Guid.NewGuid(), new SampleRange(0, 3), ImmutableArray.Create<short>(1, 2, 3)));
    return result.Status == HandoffStatus.FailedBeforeDispatch
        && launches == 0
        && !Directory.Exists(root)
        && result.Message.Contains("ASCII path", StringComparison.Ordinal);
}

static bool DictationHandoffLaunchFailureDeletesWav()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    var handoff = FakeSuperwhisper(root, Path.Combine(temp.Dir, "recordings"), _ => throw new InvalidOperationException("boom"));
    var result = SubmitHandoff(handoff, new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(1, 2)));
    return result.Status == HandoffStatus.FailedBeforeDispatch
        && result.Message == "boom"
        && !Directory.EnumerateFileSystemEntries(root).Any();
}

static RegisteredSuperwhisperHandoff FakeSuperwhisper(string root, string recordings, Func<ProcessStartInfo, Process?> start, Action<nint>? restoreFocus = null) =>
    new(root, recordings, start, restoreFocus: restoreFocus, delay: _ => Task.CompletedTask);

static void WriteSuperwhisperMeta(string recordings, string run, string json)
{
    var dir = Path.Combine(recordings, run);
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "meta.json"), json);
}

static TestOutcome DictationHandoffAcceptsTrustedAncestorJunctionOnly()
{
    if (!OperatingSystem.IsWindows())
    {
        return TestOutcome.Skip("requires native Windows junctions");
    }

    using var temp = RuntimeTemp();
    var target = Path.Combine(temp.Dir, "target");
    var junction = Path.Combine(temp.Dir, "junction");
    var rootJunction = Path.Combine(temp.Dir, "root-junction");
    var childRoot = Path.Combine(temp.Dir, "child-root");
    var childJunction = "";
    Directory.CreateDirectory(target);
    try
    {
        if (!CreateJunction(junction, target))
        {
            return TestOutcome.Skip("mklink /J is unavailable in this environment");
        }

        var launches = 0;
        var body = ImmutableArray.Create<short>(321, 654);
        var recordings = Path.Combine(temp.Dir, "recordings");
        var handoff = FakeSuperwhisper(Path.Combine(junction, "handoff"), recordings, _ =>
        {
            launches++;
            return Process.GetCurrentProcess();
        });
        var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), body);
        var result = SubmitHandoff(handoff, audio);
        var wavPath = Path.Combine(target, "handoff", $"{audio.SessionId:N}.wav");
        var retained = File.Exists(wavPath)
            && Pcm16Wav.DecodeStrict(new FileInfo(wavPath), 60 * 16000).SequenceEqual(body);

        var outside = Path.Combine(temp.Dir, "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel");
        File.WriteAllText(sentinel, "unchanged");
        var rejectedLaunches = 0;
        var rootResult = CreateJunction(rootJunction, outside)
            ? SubmitHandoff(FakeSuperwhisper(rootJunction, recordings, _ => { rejectedLaunches++; return Process.GetCurrentProcess(); }), new DictationAudio(Guid.NewGuid(), new SampleRange(0, 1), ImmutableArray.Create<short>(7)))
            : new HandoffResult(HandoffStatus.NoResult, Guid.Empty, null, "root junction not created");

        Directory.CreateDirectory(childRoot);
        var childAudio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 1), ImmutableArray.Create<short>(8));
        childJunction = Path.Combine(childRoot, $"{childAudio.SessionId:N}.wav");
        var childResult = CreateJunction(childJunction, outside)
            ? SubmitHandoff(FakeSuperwhisper(childRoot, recordings, _ => { rejectedLaunches++; return Process.GetCurrentProcess(); }), childAudio)
            : new HandoffResult(HandoffStatus.NoResult, Guid.Empty, null, "child junction not created");

        return result.Status == HandoffStatus.NoResult
            && launches == 1
            && retained
            && rootResult.Status == HandoffStatus.FailedBeforeDispatch
            && childResult.Status == HandoffStatus.FailedBeforeDispatch
            && rejectedLaunches == 0
            && File.ReadAllText(sentinel) == "unchanged"
            && Directory.GetFileSystemEntries(outside).Length == 1
            ? TestOutcome.Pass()
            : TestOutcome.Fail($"ancestor={result.Status} launches={launches} retained={retained} root={rootResult.Status}/{rootResult.Message} child={childResult.Status}/{childResult.Message}");
    }
    finally
    {
        DeleteJunctionIfOwned(junction, temp.Dir);
        DeleteJunctionIfOwned(rootJunction, temp.Dir);
        if (!string.IsNullOrEmpty(childJunction))
        {
            DeleteJunctionIfOwned(childJunction, temp.Dir);
        }
    }
}

static bool CreateJunction(string junction, string target)
{
    using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
    }) ?? throw new InvalidOperationException("failed to start mklink");
    return process.WaitForExit(5000) && process.ExitCode == 0;
}

static void DeleteJunctionIfOwned(string junction, string ownerRoot)
{
    var fullOwner = Path.GetFullPath(ownerRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullJunction = Path.GetFullPath(junction);
    if (!fullJunction.StartsWith(fullOwner, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(junction))
    {
        return;
    }

    var attributes = File.GetAttributes(junction);
    if ((attributes & FileAttributes.ReparsePoint) != 0)
    {
        Directory.Delete(junction, recursive: false);
    }
}

static HandoffResult SubmitHandoff(IDictationHandoff handoff, DictationAudio audio) =>
    handoff.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();

static TestOutcome HandoffOwnedLinksRejectSafely()
{
    if (OperatingSystem.IsWindows()) return TestOutcome.Skip("portable symlink test; native junction test covers Windows");
    using var temp = RuntimeTemp();
    var sentinel = Path.Combine(temp.Dir, "sentinel");
    const string contents = "outside must remain unchanged";
    File.WriteAllText(sentinel, contents);
    var launches = 0;
    var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(6, 8));
    var recordings = Path.Combine(temp.Dir, "recordings");
    File.SetLastWriteTimeUtc(sentinel, DateTime.UtcNow.AddHours(-1));
    foreach (var name in new[] { $"{audio.SessionId:N}.wav", "stale.wav" })
    {
        var root = Path.Combine(temp.Dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, name);
        File.CreateSymbolicLink(link, sentinel);
        var result = SubmitHandoff(FakeSuperwhisper(root, recordings, _ => { launches++; return Process.GetCurrentProcess(); }), audio);
        if (result.Status != HandoffStatus.FailedBeforeDispatch || !File.Exists(sentinel) || File.ReadAllText(sentinel) != contents
            || launches != 0) return TestOutcome.Fail(name);
    }
    var outside = Path.Combine(temp.Dir, "outside");
    Directory.CreateDirectory(outside);
    var rootLink = Path.Combine(temp.Dir, "root-link");
    Directory.CreateSymbolicLink(rootLink, outside);
    var outsideOld = Path.Combine(outside, "old.wav");
    File.WriteAllBytes(outsideOld, [1]);
    File.SetLastWriteTimeUtc(outsideOld, DateTime.UtcNow.AddHours(-1));
    var rejected = SubmitHandoff(FakeSuperwhisper(rootLink, recordings, _ => { launches++; return Process.GetCurrentProcess(); }), audio);
    var ancestorRoot = Path.Combine(rootLink, "owned");
    var accepted = SubmitHandoff(FakeSuperwhisper(ancestorRoot, recordings, _ => { launches++; return Process.GetCurrentProcess(); }), audio);
    return rejected.Status == HandoffStatus.FailedBeforeDispatch && accepted.Status == HandoffStatus.NoResult
        && launches == 1 && File.ReadAllText(sentinel) == contents && File.Exists(outsideOld)
        && Pcm16Wav.DecodeStrict(new FileInfo(accepted.Path!), 100).SequenceEqual(audio.Samples)
        ? TestOutcome.Pass() : TestOutcome.Fail("root / ancestor policy");
}

static bool RuntimeKeepsListeningAfterEveryHandoffStatus()
{
    using var temp = RuntimeTemp();
    var wav = TwoDictationWav(temp.Dir);
    foreach (var status in Enum.GetValues<HandoffStatus>())
    {
        var capture = new WavPcmCapture(wav, paced: false);
        var handoff = new FixedResultHandoff(status);
        var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 5000), capture, TwoDictationRecognizer(), handoff, dryRun: false);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (runtime.RunAsync(cancel.Token).GetAwaiter().GetResult() != 0 || !capture.DisposedForTest || handoff.Count != 2) return false;
    }
    return true;
}

static bool RuntimeDropsOverlappingDictationAndAwaitsInflightAtEof()
{
    using var temp = RuntimeTemp();
    var handoff = new GatedHandoff();
    var runtime = new WindowsDictationRuntime(DictationRuntimeTestConfig(endSilenceMs: 5000), new WavPcmCapture(TwoDictationWav(temp.Dir), paced: false), TwoDictationRecognizer(), handoff, dryRun: false);
    var code = RunWithCapturedConsole(runtime, TimeSpan.FromSeconds(10), out var output, line =>
    {
        if (line.Contains("dictation dropped: previous one still in flight")) _ = handoff.ReleaseAfterAsync(TimeSpan.FromMilliseconds(300));
    });
    return code == 0
        && handoff.Count == 1
        && handoff.Released
        && output.Split('\n').Count(line => line.Contains("dictation dropped: previous one still in flight")) == 1
        && output.Split('\n').Count(line => line.Contains("dictation handoff: DryRunSuppressed")) == 1;
}

static string TwoDictationWav(string dir)
{
    var wav = Path.Combine(dir, "input.wav");
    var frames = ThreeUtteranceFrames().ToList();
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    File.WriteAllBytes(wav, Pcm16Wav.Encode(frames.SelectMany(frame => frame.Samples).ToArray()));
    return wav;
}

static ScriptedDictationRecognizer TwoDictationRecognizer() =>
    new(request => Task.FromResult(request.Id % 2 == 1
        ? new RecognizedUtterance(request.Id, request.Extent, request.Range, "音声入力本文",
            ImmutableArray.Create(Run("音声入力", request.Range.Start, request.Range.Start + Segmenter.FrameLength),
                Run("本文", request.Range.Start + Segmenter.FrameLength, request.Range.End)))
        : new RecognizedUtterance(request.Id, request.Extent, request.Range, "入力ストップ",
            ImmutableArray.Create(Run("入力ストップ", request.Range.Start + Segmenter.FrameLength, request.Range.Start + Segmenter.FrameLength * 3)))));

static bool DictationDryRunKeepsAudioInMemory()
{
    using var temp = RuntimeTemp();
    var root = Path.Combine(temp.Dir, "handoff");
    var launches = 0;
    var handoff = new RegisteredSuperwhisperHandoff(root, Path.Combine(temp.Dir, "recordings"), _ =>
    {
        launches++;
        throw new InvalidOperationException("must not launch");
    }, dryRun: true);
    var result = handoff.SubmitAsync(new DictationAudio(Guid.NewGuid(), new SampleRange(0, 1), ImmutableArray.Create<short>(1)), CancellationToken.None).GetAwaiter().GetResult();
    return result.Status == HandoffStatus.DryRunSuppressed
        && result.Path is null
        && launches == 0
        && !Directory.Exists(root);
}

static VoiceSwitchConfig DictationConfig() =>
    new(["音声入力"], "ja_JP", "wake", StopWords: ["入力ストップ"], StopCommand: "stop", Dictation: new DictationConfig(StartTimeoutMs: 3000, EndSilenceMs: 1200, MaxSeconds: 60));

static VoiceSwitchConfig DictationRuntimeTestConfig(int startTimeoutMs = 3000, int endSilenceMs = 1200, double maxSeconds = 60) =>
    DictationConfig() with
    {
        HangoverMs = 300,
        PrerollMs = 90,
        MinSpeechMs = 300,
        VadMinRMS = 0.005f,
        Dictation = DictationConfig().Dictation! with { StartTimeoutMs = startTimeoutMs, EndSilenceMs = endSilenceMs, MaxSeconds = maxSeconds }
    };

static DictationSession StartedSession(VoiceSwitchConfig config, out SampleStore store)
{
    store = StoreWithRamp(0, 50000);
    var session = new DictationSession(config);
    session.Apply(Recognized(1, RecognitionExtent.PrefixHead, 0, 20000, "音声入力本文", false, Run("音声", 0, 4000), Run("入力", 4000, 10000), Run("本文", 10000, 20000)), store.Copy);
    return session;
}

static SampleStore StoreWithRamp(long start, int length)
{
    var store = new SampleStore();
    var samples = Enumerable.Range((int)start, length).Select(value => (short)value).ToArray();
    store.Append(start, samples);
    return store;
}

static LexicalRun Run(string text, long start, long end) => new(text, new SampleRange(start, end));

static RecognizedUtterance Recognized(long id, RecognitionExtent extent, long start, long end, string text, bool rejected, params LexicalRun[] runs) =>
    new(id, extent, new SampleRange(start, end), text, runs.ToImmutableArray(), rejected);

static IReadOnlyList<PcmFrame> TwoUtteranceFrames()
{
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    return frames;
}

static IReadOnlyList<PcmFrame> ThreeUtteranceFrames()
{
    var frames = new List<PcmFrame>();
    AddFrames(frames, 3, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    AddFrames(frames, 12, loud: true);
    AddFrames(frames, 12, loud: false);
    return frames;
}

static void AddFrames(List<PcmFrame> frames, int count, bool loud)
{
    AddFramesWithSample(frames, count, loud ? (short)8000 : (short)1);
}

static void AddFramesWithSample(List<PcmFrame> frames, int count, short sample)
{
    for (var i = 0; i < count; i++)
    {
        var start = frames.Count * Segmenter.FrameLength;
        var samples = Enumerable.Range(0, Segmenter.FrameLength)
            .Select(_ => sample)
            .ToImmutableArray();
        frames.Add(new PcmFrame(start, samples));
    }
}

static int RunWithTimeout(WindowsDictationRuntime runtime, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource();
    var task = runtime.RunAsync(cts.Token);
    if (!task.Wait(timeout))
    {
        cts.Cancel();
        task.Wait(TimeSpan.FromSeconds(1));
        throw new TimeoutException("dictation runtime fixture did not finish");
    }

    return task.GetAwaiter().GetResult();
}

static IReadOnlyList<PcmFrame> CollectFrames(IPcmCapture capture)
{
    var frames = new List<PcmFrame>();
    var reader = capture.ReadFramesAsync(CancellationToken.None).GetAsyncEnumerator();
    try
    {
        while (reader.MoveNextAsync().AsTask().GetAwaiter().GetResult())
        {
            frames.Add(reader.Current);
        }
    }
    finally
    {
        reader.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    return frames;
}

static int RunWithCapturedConsole(
    WindowsDictationRuntime runtime,
    TimeSpan timeout,
    out string output,
    Action<string>? observeLine = null)
{
    var previous = Console.Out;
    using var writer = new CapturingTextWriter(observeLine);
    Console.SetOut(writer);
    try
    {
        return RunWithTimeout(runtime, timeout);
    }
    finally
    {
        Console.SetOut(previous);
        output = writer.ToString();
    }
}

static bool DictationDiagnosticsArePrivate(string output) =>
    !output.Contains("音声入力本文", StringComparison.Ordinal)
    && !output.Contains("入力ストップ", StringComparison.Ordinal)
    && !output.Contains("samples=", StringComparison.OrdinalIgnoreCase)
    && !output.Contains("pcm=", StringComparison.OrdinalIgnoreCase);

static TestOutcome ProductionScriptParsesWordsOnWindowsPowerShell()
{
    if (!OperatingSystem.IsWindows())
    {
        return TestOutcome.Skip("requires native Windows powershell.exe");
    }

    try
    {
        return RunProductionWordParse(["音声入力", "入力ストップ"], ["音声入力", "入力ストップ"])
            && RunProductionWordParse(["音声入力"], ["音声入力"])
            && RunProductionWordParse([], [])
            ? TestOutcome.Pass()
            : TestOutcome.Fail("production script did not preserve configured word array values");
    }
    catch (Exception ex)
    {
        return TestOutcome.Fail(ex.Message);
    }
}

static bool RunProductionWordParse(string[] words, string[] expected)
{
    var script = ProductionScriptPrefix() + """

Send-Json @{ type='words'; count=$words.Count; values=@($words | ForEach-Object { [string]$_ }) }
exit 0
""";
    var line = RunPowerShell(script, ConfigLoader.ToJsonArray(words));
    using var doc = System.Text.Json.JsonDocument.Parse(line);
    var root = doc.RootElement;
    var actual = root.GetProperty("values").EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
    return root.GetProperty("type").GetString() == "words"
        && root.GetProperty("count").GetInt32() == expected.Length
        && actual.SequenceEqual(expected);
}

static string ProductionScriptPrefix()
{
    var script = ProductionScript();
    const string marker = "$infos =";
    var index = script.IndexOf(marker, StringComparison.Ordinal);
    if (index < 0)
    {
        throw new InvalidOperationException("production script prefix marker not found");
    }

    return script[..index];
}

static string ProductionScript()
{
    var field = typeof(SpeechPowerShell).GetField("Script", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(nameof(SpeechPowerShell), "Script");
    return (string)(field.GetValue(null) ?? throw new InvalidOperationException("SpeechPowerShell.Script is null"));
}

static string RunPowerShell(string script, string wordsJson)
{
    var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
    var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
        UseShellExecute = false,
        CreateNoWindow = true,
        Environment =
        {
            ["VOICE_SWITCH_LOCALE"] = "ja-JP",
            ["VOICE_SWITCH_WAKE_WORDS"] = wordsJson,
        },
    }) ?? throw new InvalidOperationException("failed to start powershell.exe");

    if (!process.WaitForExit(5000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("powershell.exe did not exit within 5 seconds");
    }

    var output = process.StandardOutput.ReadToEnd().Trim();
    var error = process.StandardError.ReadToEnd().Trim();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"powershell.exe exited {process.ExitCode}: {error}");
    }

    return output.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).Last();
}

static bool ResidentFailsOnCleanEarlyExit()
{
    using var temp = RuntimeTemp();
    var runtime = CreateRuntime(temp, 2, _ => StartChild("exit-clean"));
    return runtime.Run() == 1;
}

static bool ResidentPropagatesChildStderr()
{
    using var temp = RuntimeTemp();
    var runtime = CreateRuntime(temp, 2, _ => StartChild("stderr-error"));
    return runtime.Run() == 1;
}

static bool ResidentBoundedStopDisposesChild()
{
    using var temp = RuntimeTemp();
    int? childId = null;
    var runtime = CreateRuntime(temp, 1, _ =>
    {
        var speech = StartChild("ready-wait");
        childId = speech.Process.Id;
        return speech;
    });
    var code = runtime.Run();
    return code == 0 && childId is not null && ProcessIsGone(childId.Value, TimeSpan.FromSeconds(2));
}

static bool ResidentRepeatedStartStop()
{
    using var temp = RuntimeTemp();
    for (var i = 0; i < 2; i++)
    {
        var runtime = CreateRuntime(temp, 1, _ => StartChild("ready-wait"));
        if (runtime.Run() != 0)
        {
            return false;
        }
    }

    return true;
}

static TestOutcome ResidentReloadRestartRules()
{
    using var temp = RuntimeTemp();
    var starts = 0;
    using var firstReady = new ManualResetEventSlim(false);
    using var invalidObserved = new ManualResetEventSlim(false);
    using var secondStarted = new ManualResetEventSlim(false);
    Exception? updaterError = null;
    Task? updater = null;
    var runtime = CreateRuntime(temp, 2, config =>
    {
        var start = Interlocked.Increment(ref starts);
        if (start == 1)
        {
            updater = Task.Run(() =>
            {
                try
                {
                    if (!firstReady.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException("first recognizer did not report ready");
                    }

                    WriteConfigWithNewTimestamp(temp.ConfigPath, """{"wakeWords":["。"],"locale":"ja_JP","command":"wake"}""");
                    if (!invalidObserved.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException("invalid config reload was not observed");
                    }

                    WriteConfigWithNewTimestamp(temp.ConfigPath, """{"wakeWords":["別"],"locale":"ja_JP","command":"wake"}""");
                    if (!secondStarted.Wait(TimeSpan.FromSeconds(2)))
                    {
                        throw new TimeoutException("valid config reload did not restart recognizer");
                    }
                }
                catch (Exception ex)
                {
                    updaterError = ex;
                }
            });
        }
        else if (start == 2)
        {
            secondStarted.Set();
        }

        return StartChild("ready-wait", line =>
        {
            if (start == 1 && line.Contains("recognizer ready:", StringComparison.Ordinal))
            {
                firstReady.Set();
            }
        });
    });

    var (code, output) = CaptureConsole(
        () => runtime.Run(),
        line =>
        {
            if (line.Contains("config reload failed", StringComparison.Ordinal))
            {
                invalidObserved.Set();
            }
        });
    if (updater is not null && !updater.Wait(TimeSpan.FromSeconds(5)))
    {
        return TestOutcome.Fail("config updater task did not finish");
    }

    return code == 0 && starts == 2 && updaterError is null
        ? TestOutcome.Pass()
        : TestOutcome.Fail($"code={code} starts={starts} updater={updaterError?.Message ?? "ok"} output={output}");
}

static bool ResidentDryRunSuppressesCommands()
{
    using var temp = RuntimeTemp();
    var commands = new List<string>();
    var decisions = new List<RuntimeDecision>();
    var runtime = CreateRuntime(
        temp,
        1,
        _ => StartChild("ready-recognize-wake-stop"),
        commands.Add,
        decisions.Add,
        dryRun: true);

    return runtime.Run() == 0
        && commands.Count == 0
        && decisions.Select(decision => decision.Reason).SequenceEqual(["wake", "stop"]);
}

static bool ResidentNormalDispatchesCommands()
{
    using var temp = RuntimeTemp();
    var commands = new List<string>();
    var decisions = new List<RuntimeDecision>();
    var runtime = CreateRuntime(
        temp,
        1,
        _ => StartChild("ready-recognize-wake-stop"),
        commands.Add,
        decisions.Add);

    return runtime.Run() == 0
        && commands.SequenceEqual(["wake", "stop"])
        && decisions.Select(decision => decision.Reason).SequenceEqual(["wake", "stop"]);
}

static TestOutcome ResidentCommandModeSuppressesIdleStopButDispatchesWake()
{
    using var missingStopTemp = RuntimeTemp();
    WriteCommandConfig(missingStopTemp.ConfigPath, stopCommand: null);
    var missingStopCommands = new List<string>();
    var missingStopDecisions = new List<RuntimeDecision>();
    var missingStopRuntime = CreateRuntime(
        missingStopTemp,
        1,
        _ => StartChild("ready-recognize-stop"),
        missingStopCommands.Add,
        missingStopDecisions.Add);
    var missingStopCode = missingStopRuntime.Run();

    using var legacyToggleTemp = RuntimeTemp();
    WriteCommandConfig(legacyToggleTemp.ConfigPath, PlatformDefaults.SuperwhisperToggle);
    var legacyToggleCommands = new List<string>();
    var legacyToggleDecisions = new List<RuntimeDecision>();
    var legacyToggleRuntime = CreateRuntime(
        legacyToggleTemp,
        1,
        _ => StartChild("ready-recognize-stop"),
        legacyToggleCommands.Add,
        legacyToggleDecisions.Add);
    var legacyToggleCode = legacyToggleRuntime.Run();

    using var wakeTemp = RuntimeTemp();
    WriteCommandConfig(wakeTemp.ConfigPath, stopCommand: null);
    var wakeCommands = new List<string>();
    var wakeDecisions = new List<RuntimeDecision>();
    var wakeRuntime = CreateRuntime(
        wakeTemp,
        1,
        _ => StartChild("ready-recognize-wake"),
        wakeCommands.Add,
        wakeDecisions.Add);
    var wakeCode = wakeRuntime.Run();

    var missingConfig = missingStopTemp.Load();
    var legacyConfig = legacyToggleTemp.Load();
    var pureWake = TextMatching.Decide("音声入力", missingConfig);
    var pureMissingStop = TextMatching.Decide("入力ストップ", missingConfig);
    var pureLegacyStop = TextMatching.Decide("入力ストップ", legacyConfig);
    var ok = missingStopCode == 0
        && legacyToggleCode == 0
        && wakeCode == 0
        && pureWake is { Kind: "run-command", Reason: "wake" }
        && pureMissingStop.Kind == "ignore"
        && pureLegacyStop.Kind == "ignore"
        && missingStopCommands.Count == 0
        && legacyToggleCommands.Count == 0
        && wakeCommands.SequenceEqual(["wake"]);
    return ok
        ? TestOutcome.Pass()
        : TestOutcome.Fail($"pureWake={pureWake.Kind}/{pureWake.Reason} missingPure={pureMissingStop.Kind}/{pureMissingStop.Reason} legacyPure={pureLegacyStop.Kind}/{pureLegacyStop.Reason} missingCode={missingStopCode} legacyCode={legacyToggleCode} missingCommands=[{string.Join(',', missingStopCommands)}] legacyCommands=[{string.Join(',', legacyToggleCommands)}] missingDecisions=[{string.Join(',', missingStopDecisions.Select(decision => decision.Reason))}] legacyDecisions=[{string.Join(',', legacyToggleDecisions.Select(decision => decision.Reason))}] wakeCode={wakeCode} wakeCommands=[{string.Join(',', wakeCommands)}]");
}

static bool ResidentCancellationStopsChild()
{
    using var temp = RuntimeTemp();
    using var cancel = new CancellationTokenSource();
    int? childId = null;
    var runtime = CreateRuntime(temp, null, _ =>
    {
        var speech = StartChild("ready-wait");
        childId = speech.Process.Id;
        return speech;
    }, cancellation: cancel.Token);

    var task = Task.Run(runtime.Run);
    SpinWait.SpinUntil(() => childId is not null, TimeSpan.FromSeconds(2));
    cancel.Cancel();
    task.Wait(TimeSpan.FromSeconds(5));
    return task.IsCompleted
        && task.Result == 130
        && childId is not null
        && ProcessIsGone(childId.Value, TimeSpan.FromSeconds(2));
}

static bool DictationRecognizerRetainsOwnershipUntilConfirmedExit()
{
    var exited = false;
    var disposed = false;
    var killCount = 0;
    var hasExitedReads = 0;
    var ended = 0;
    var owner = Task.Run(async () =>
    {
        try
        {
            await SpeechPowerShellDictationRecognizer.RetainUntilProcessExitedAsync(
                () =>
                {
                    if (Interlocked.Increment(ref hasExitedReads) <= 2)
                    {
                        throw new InvalidOperationException("transient observation failure");
                    }

                    return Volatile.Read(ref exited);
                },
                () =>
                {
                    Interlocked.Increment(ref killCount);
                    throw new InvalidOperationException("fixture kill failure");
                },
                TimeSpan.FromMilliseconds(10));
            Interlocked.Increment(ref ended);
        }
        finally
        {
            Volatile.Write(ref disposed, true);
        }
    });

    var retained = SpinWait.SpinUntil(() => Volatile.Read(ref killCount) >= 2, TimeSpan.FromSeconds(1))
        && !owner.IsCompleted
        && !Volatile.Read(ref disposed)
        && Volatile.Read(ref ended) == 0;
    Volatile.Write(ref exited, true);
    return retained
        && owner.Wait(TimeSpan.FromSeconds(1))
        && Volatile.Read(ref disposed)
        && Volatile.Read(ref ended) == 1;
}

static bool TrayOptionsRejectSyntheticWithoutRecordOnly()
{
    try
    {
        _ = TrayOptions.Parse(["--config", "config.json", "--input-wav", "fixture.wav"]);
        return false;
    }
    catch (ArgumentException ex)
    {
        return ex.Message.Contains("--record-only", StringComparison.Ordinal);
    }
}

static bool SingleExeRoutesTrayLaunches() =>
    CliOptions.IsTrayLaunch([])
    && CliOptions.IsTrayLaunch(["--config", "c.json"])
    && CliOptions.IsTrayLaunch(["--config", "c.json", "--paused"])
    && CliOptions.IsTrayLaunch(["--tray-command", "quit"])
    && CliOptions.IsTrayLaunch(["--config", "c.json", "--input-wav", "in.wav", "--record-only", "out"])
    && !CliOptions.IsTrayLaunch(["--self-test"])
    && !CliOptions.IsTrayLaunch(["--config", "c.json", "--dry-run", "--listen-seconds", "5"])
    && !CliOptions.IsTrayLaunch(["--config", "c.json", "--input-wav", "in.wav"])
    && !CliOptions.IsTrayLaunch(["--recognizers"]);

static bool TrayOptionsRejectNumericCommand()
{
    try
    {
        _ = TrayOptions.Parse(["--config", "config.json", "--tray-command", "0"]);
        return false;
    }
    catch (ArgumentException ex)
    {
        return ex.Message.Contains("--tray-command", StringComparison.Ordinal);
    }
}

static bool ProductionTrayFactoryRejectsUnsafeSyntheticSource()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var config = ConfigLoader.Load(temp.ConfigPath);
    var factory = new ProductionRuntimeFactory(temp.ConfigPath);
    return Throws<ArgumentException>(() => factory.StartAsync(config, new TrayInputSource("in.wav", null), CancellationToken.None).GetAwaiter().GetResult(), "--record-only")
        && Throws<ArgumentException>(() => factory.StartAsync(config, new TrayInputSource(null, Path.Combine(temp.Dir, "out")), CancellationToken.None).GetAwaiter().GetResult(), "--input-wav")
        && Throws<ArgumentException>(() => factory.StartAsync(config with { Dictation = null }, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), CancellationToken.None).GetAwaiter().GetResult(), "requires a config with dictation");
}

static bool TraySupervisorStartsPausedAndStartIsIdempotent()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory();
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    var initialPaused = supervisor.Snapshot.State == TrayState.Paused;
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var running = supervisor.Snapshot.State == TrayState.Listening && factory.Started.Count == 1;
    supervisor.PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
    return initialPaused
        && running
        && supervisor.Snapshot.State == TrayState.Paused
        && factory.Started.Single().StopCount == 1;
}

static bool TraySupervisorInvalidReloadKeepsRunningGeneration()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory();
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var generation = supervisor.Snapshot.Generation;
    File.WriteAllText(temp.ConfigPath, """{"wakeWords":["。"],"command":"wake","dictation":{}}""");
    supervisor.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
    return supervisor.Snapshot.State == TrayState.Listening
        && supervisor.Snapshot.Generation == generation
        && supervisor.Snapshot.LastError is not null
        && factory.Started.Count == 1
        && factory.Started.Single().StopCount == 0;
}

static bool TraySupervisorReloadSwitchesToCommandMode()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory();
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource(null, null), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var generation = supervisor.Snapshot.Generation;
    File.WriteAllText(temp.ConfigPath, """{"wakeWords":["音声入力"],"command":"wake"}""");
    supervisor.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
    return supervisor.Snapshot.State == TrayState.Listening
        && supervisor.Snapshot.Generation == generation + 1
        && supervisor.Snapshot.LastError is null
        && factory.Started.Count == 2
        && factory.Started[0].StopCount == 1;
}

static bool TraySupervisorValidReloadStopsOldBeforeNew()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory();
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var first = factory.Started.Single();
    WriteTrayDictationConfig(temp.ConfigPath, "別の起動語");
    supervisor.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
    return supervisor.Snapshot.State == TrayState.Listening
        && factory.Started.Count == 2
        && first.StopCount == 1
        && factory.Started[1].StopCount == 0
        && factory.Started[0].StopOrder < factory.Started[1].StartOrder;
}

static bool TraySupervisorRetainsRunAfterStopFailure()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory { ThrowOnStop = true };
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    supervisor.PauseAsync(CancellationToken.None).GetAwaiter().GetResult();
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    return supervisor.Snapshot.State == TrayState.Error
        && supervisor.Snapshot.OwnedChildProcessId == 1234
        && factory.Started.Count == 1
        && factory.Started.Single().StopCount == 1;
}

static bool TraySupervisorRetriesQuitAfterStopFailure()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory { ThrowOnStop = true };
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    Throws<TimeoutException>(() => supervisor.QuitAsync(CancellationToken.None).GetAwaiter().GetResult(), "runtime did not confirm termination");
    var run = factory.Started.Single();
    var retained = supervisor.Snapshot.State == TrayState.Error
        && supervisor.Snapshot.OwnedChildProcessId == 1234
        && run.StopCount == 1;
    run.ThrowOnStop = false;
    supervisor.QuitAsync(CancellationToken.None).GetAwaiter().GetResult();
    return retained
        && supervisor.Snapshot.State == TrayState.Quitting
        && supervisor.Snapshot.OwnedChildProcessId is null
        && run.StopCount == 2
        && run.DisposeCount == 1;
}

static bool TraySupervisorDisposesFaultedCompletionAndRestarts()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory { FaultCompletionOnStart = true };
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var observed = SpinWait.SpinUntil(
        () => supervisor.Snapshot.State == TrayState.Error && supervisor.Snapshot.OwnedChildProcessId is null,
        TimeSpan.FromSeconds(2));
    factory.FaultCompletionOnStart = false;
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    return observed
        && factory.Started.Count == 2
        && factory.Started[0].DisposeCount == 1
        && supervisor.Snapshot.State == TrayState.Listening
        && supervisor.Snapshot.OwnedChildProcessId == 1234;
}

static bool TraySupervisorRefreshesChildIdentityWithoutStateChange()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var factory = new FakeTrayRuntimeFactory { InitialOwnedChild = null };
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var generation = supervisor.Snapshot.Generation;
    var run = factory.Started.Single();
    var readsBeforeIdentityChanges = run.OwnedChildReadCount;
    var firstStart = DateTimeOffset.UtcNow.AddSeconds(-10);
    var first = new RecognitionProcessIdentity(4321, firstStart);
    var second = new RecognitionProcessIdentity(4321, firstStart.AddMilliseconds(1));

    run.OwnedChild = first;
    var firstSnapshot = supervisor.Snapshot;
    run.OwnedChild = null;
    var clearedSnapshot = supervisor.Snapshot;
    run.OwnedChild = second;
    var secondSnapshot = supervisor.Snapshot;

    return firstSnapshot.State == TrayState.Listening
        && firstSnapshot.Generation == generation
        && firstSnapshot.OwnedChildProcessId == first.ProcessId
        && firstSnapshot.OwnedChildProcessStartTimeUtc == first.StartTimeUtc
        && clearedSnapshot.OwnedChildProcessId is null
        && clearedSnapshot.OwnedChildProcessStartTimeUtc is null
        && secondSnapshot.Generation == generation
        && secondSnapshot.OwnedChildProcessId == second.ProcessId
        && secondSnapshot.OwnedChildProcessStartTimeUtc == second.StartTimeUtc
        && run.OwnedChildReadCount - readsBeforeIdentityChanges == 3;
}

static bool TraySupervisorRejectsStaleRunDuringSnapshotRace()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var first = new RecognitionProcessIdentity(1001, DateTimeOffset.UtcNow.AddSeconds(-20));
    var second = new RecognitionProcessIdentity(2002, DateTimeOffset.UtcNow.AddSeconds(-10));
    var factory = new FakeTrayRuntimeFactory { InitialOwnedChild = first };
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), factory);
    supervisor.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    var oldRun = factory.Started.Single();
    using var blockOldIdentityRead = new ManualResetEventSlim(false);
    oldRun.BlockOwnedChildRead = blockOldIdentityRead;
    oldRun.OwnedChildReadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var readTask = Task.Run(() => supervisor.Snapshot);
    if (!oldRun.OwnedChildReadStarted.Task.Wait(TimeSpan.FromSeconds(1)))
    {
        return false;
    }

    factory.InitialOwnedChild = second;
    WriteTrayDictationConfig(temp.ConfigPath, "別の起動語");
    supervisor.ReloadAsync(CancellationToken.None).GetAwaiter().GetResult();
    blockOldIdentityRead.Set();
    if (!readTask.Wait(TimeSpan.FromSeconds(1)))
    {
        return false;
    }

    var observed = readTask.Result;
    var current = supervisor.Snapshot;
    return factory.Started.Count == 2
        && observed.Generation == current.Generation
        && observed.OwnedChildProcessId == second.ProcessId
        && observed.OwnedChildProcessStartTimeUtc == second.StartTimeUtc;
}

static bool TrayCommandIdentityDoesNotAcquireLease()
{
    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    var originalLocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", temp.Dir);
    Environment.SetEnvironmentVariable("LOCALAPPDATA", temp.Dir);
    try
    {
        using var commandIdentity = TraySingleInstance.Identify(temp.ConfigPath);
        var lockExistedBeforeHost = File.Exists(commandIdentity.LockPath);
        using var host = TraySingleInstance.Acquire(temp.ConfigPath);
        using var duplicateHost = TraySingleInstance.Acquire(temp.ConfigPath);
        return !commandIdentity.IsOwner
            && !lockExistedBeforeHost
            && host.IsOwner
            && !duplicateHost.IsOwner
            && commandIdentity.Key == host.Key
            && commandIdentity.PipeName == host.PipeName
            && commandIdentity.LockPath == host.LockPath
            && duplicateHost.PipeName == host.PipeName;
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", originalLocalAppData);
    }
}

static TestOutcome TrayIpcNoServerErrorIsActionable()
{
    if (!OperatingSystem.IsWindows())
    {
        return TestOutcome.Skip("requires native Windows named pipe client errors");
    }

    try
    {
        TrayIpcServer.SendAsync(
            "voice-switch-test-missing-" + Guid.NewGuid().ToString("N"),
            TrayCommand.ShowStatus,
            TimeSpan.FromMilliseconds(100)).GetAwaiter().GetResult();
        return TestOutcome.Fail("missing tray host command unexpectedly succeeded");
    }
    catch (TimeoutException ex)
    {
        return ex.Message.Contains("Start voice-switch.exe first", StringComparison.Ordinal)
            && ex.Message.Contains(nameof(TrayCommand.ShowStatus), StringComparison.Ordinal)
            ? TestOutcome.Pass()
            : TestOutcome.Fail(ex.Message);
    }
}

static TestOutcome TrayIpcRejectsBadClientThenServesStatus()
{
    if (!OperatingSystem.IsWindows())
    {
        return TestOutcome.Skip("requires native Windows named pipe permissions");
    }

    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), new FakeTrayRuntimeFactory());
    var pipe = "voice-switch-test-" + Guid.NewGuid().ToString("N");
    var server = new TrayIpcServer(pipe, supervisor);
    try
    {
        using (var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            client.Connect(2000);
            Thread.Sleep(2600);
        }

        var bad = SendRawIpc(pipe, "0");
        var good = TrayIpcServer.SendAsync(pipe, TrayCommand.ShowStatus, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        using var badDocument = JsonDocument.Parse(bad);
        using var goodDocument = JsonDocument.Parse(good);
        return badDocument.RootElement.GetProperty("ok").GetBoolean() == false
            && goodDocument.RootElement.GetProperty("Snapshot").GetProperty("State").GetString() == nameof(TrayState.Paused)
            ? TestOutcome.Pass()
            : TestOutcome.Fail("bad client was not rejected before valid status");
    }
    finally
    {
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static TestOutcome TrayIpcTimesOutUnreadResponseThenServesStatus()
{
    if (!OperatingSystem.IsWindows())
    {
        return TestOutcome.Skip("requires native Windows named pipe permissions");
    }

    using var temp = RuntimeTemp();
    WriteTrayDictationConfig(temp.ConfigPath, "音声入力");
    var supervisor = new TrayRuntimeSupervisor(temp.ConfigPath, new TrayInputSource("in.wav", Path.Combine(temp.Dir, "out")), new FakeTrayRuntimeFactory());
    var pipe = "voice-switch-test-" + Guid.NewGuid().ToString("N");
    var diagnosticsCalls = 0;
    var server = new TrayIpcServer(pipe, supervisor, diagnostics: () =>
    {
        var snapshot = supervisor.Snapshot;
        return Interlocked.Increment(ref diagnosticsCalls) == 1
            ? new TrayDiagnostics(snapshot, [new TrayMenuDiagnostic("oversized", new string('x', 8 * 1024 * 1024), true)], true, "checked", Environment.ProcessId)
            : new TrayDiagnostics(snapshot, [], true, "checked", Environment.ProcessId);
    });

    NamedPipeClientStream? unreadClient = null;
    TestOutcome outcome;
    try
    {
        unreadClient = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        unreadClient.Connect(2000);
        unreadClient.Write(Encoding.UTF8.GetBytes(nameof(TrayCommand.ShowStatus) + "\n"));
        Thread.Sleep(2600);

        var good = TrayIpcServer.SendAsync(pipe, TrayCommand.ShowStatus, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        using var goodDocument = JsonDocument.Parse(good);
        outcome = Volatile.Read(ref diagnosticsCalls) >= 2
            && goodDocument.RootElement.GetProperty("Snapshot").GetProperty("State").GetString() == nameof(TrayState.Paused)
            ? TestOutcome.Pass()
            : TestOutcome.Fail("unread client blocked subsequent status response");
    }
    catch (Exception ex)
    {
        outcome = TestOutcome.Fail(ex.Message);
    }

    var dispose = server.DisposeAsync().AsTask();
    var disposed = dispose.Wait(TimeSpan.FromSeconds(5));
    unreadClient?.Dispose();
    return disposed
        ? outcome
        : TestOutcome.Fail("server dispose did not finish after unread client timeout");
}

static bool ProcessIsGone(int processId, TimeSpan timeout)
{
    return SpinWait.SpinUntil(() =>
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }, timeout);
}

static bool Rejects(string json, string message)
{
    var path = Path.Combine(Path.GetTempPath(), $"voice-switch-{Guid.NewGuid():N}.json");
    File.WriteAllText(path, json);
    try
    {
        ConfigLoader.Load(path);
        return false;
    }
    catch (InvalidDataException ex)
    {
        return ex.Message.Contains(message);
    }
    finally
    {
        File.Delete(path);
    }
}

static bool Throws<T>(Action action, string message) where T : Exception
{
    try
    {
        action();
        return false;
    }
    catch (T ex)
    {
        return ex.Message.Contains(message, StringComparison.Ordinal);
    }
}

static string SendRawIpc(string pipeName, string line)
{
    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    client.Connect(2000);
    using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
    using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
    writer.WriteLine(line);
    return reader.ReadLine() ?? "";
}

static TestRuntime RuntimeTemp()
{
    var dir = Path.Combine(Path.GetTempPath(), $"voice-switch-runtime-{Guid.NewGuid():N}");
    Directory.CreateDirectory(dir);
    var configPath = Path.Combine(dir, "config.json");
    File.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new
    {
        wakeWords = new[] { "音声入力" },
        locale = "ja_JP",
        command = "wake",
        stopWords = new[] { "入力ストップ" },
        stopCommand = "stop"
    }));
    return new TestRuntime(dir, configPath);
}

static void WriteTrayDictationConfig(string path, string wakeWord)
{
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new
    {
        wakeWords = new[] { wakeWord },
        locale = "ja_JP",
        command = "wake",
        stopWords = new[] { "入力ストップ" },
        stopCommand = "stop",
        dictation = new { startTimeoutMs = 3000, endSilenceMs = 1200, maxSeconds = 60 }
    }));
}

static (int Code, string Output) CaptureConsole(Func<int> action, Action<string>? observeLine = null)
{
    var originalOut = Console.Out;
    var originalError = Console.Error;
    using var writer = new CapturingTextWriter(observeLine);
    try
    {
        Console.SetOut(writer);
        Console.SetError(writer);
        var code = action();
        return (code, writer.ToString());
    }
    finally
    {
        Console.SetOut(originalOut);
        Console.SetError(originalError);
    }
}

static ResidentRuntime CreateRuntime(
    TestRuntime temp,
    int? listenSeconds,
    Func<VoiceSwitchConfig, ISpeechProcess> startRecognizer,
    Action<string>? runCommand = null,
    Action<RuntimeDecision>? observeDecision = null,
    bool dryRun = false,
    CancellationToken cancellation = default) =>
    new(temp.ConfigPath, temp.Load(), listenSeconds, startRecognizer, runCommand ?? (_ => { }), observeDecision ?? (_ => { }), dryRun, cancellation);

static void WriteConfigWithNewTimestamp(string path, string json)
{
    var previous = File.GetLastWriteTimeUtc(path);
    File.WriteAllText(path, json);
    for (var i = 1; File.GetLastWriteTimeUtc(path) <= previous && i <= 20; i++)
    {
        File.SetLastWriteTimeUtc(path, previous.AddMilliseconds(i));
    }
}

static void WriteCommandConfig(string path, string? stopCommand)
{
    var properties = new Dictionary<string, object?>
    {
        ["wakeWords"] = new[] { "音声入力" },
        ["locale"] = "ja_JP",
        ["command"] = "wake",
        ["stopWords"] = new[] { "入力ストップ" }
    };
    if (stopCommand is not null)
    {
        properties["stopCommand"] = stopCommand;
    }

    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(properties));
}

static TestSpeechProcess StartChild(string mode, Action<string>? observeLine = null)
{
    var dll = Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("test assembly path not found");
    var host = Environment.ProcessPath ?? "dotnet";
    var isDotnetHost = Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
    var arguments = isDotnetHost ? $"\"{dll}\" --child {mode}" : $"--child {mode}";
    var process = Process.Start(new ProcessStartInfo(host, arguments)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8,
        StandardErrorEncoding = System.Text.Encoding.UTF8,
        UseShellExecute = false,
        CreateNoWindow = true,
    }) ?? throw new InvalidOperationException("child failed to start");
    return new TestSpeechProcess(process, observeLine);
}

static int Child(string mode)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    return mode switch
    {
        "exit-clean" => 0,
        "stderr-error" => ChildStderrError(),
        "ready-wait" => ChildReadyWait(),
        "ready-recognize-wake-stop" => ChildReadyRecognizeWakeStop(),
        "ready-recognize-stop" => ChildReadyRecognize("入力ストップ", 0.88),
        "ready-recognize-wake" => ChildReadyRecognize("音声入力", 0.99),
        _ => 2,
    };
}

static int ChildStderrError()
{
    Console.Error.WriteLine("fixture real error");
    return 3;
}

static int ChildReadyWait()
{
    Console.WriteLine("{\"type\":\"diagnostic\",\"message\":\"recognizer ready: ja-JP fixture\"}");
    while (true)
    {
        Thread.Sleep(100);
    }
}

static int ChildReadyRecognizeWakeStop()
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("{\"type\":\"diagnostic\",\"message\":\"recognizer ready: ja-JP fixture\"}");
    Console.WriteLine("{\"type\":\"recognized\",\"text\":\"音声入力\",\"confidence\":0.99}");
    Console.WriteLine("{\"type\":\"recognized\",\"text\":\"入力ストップ\",\"confidence\":0.88}");
    Console.Out.Flush();
    Thread.Sleep(1500);
    return 0;
}

static int ChildReadyRecognize(string text, double confidence)
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.WriteLine("{\"type\":\"diagnostic\",\"message\":\"recognizer ready: ja-JP fixture\"}");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { type = "recognized", text, confidence }));
    Console.Out.Flush();
    Thread.Sleep(1500);
    return 0;
}
