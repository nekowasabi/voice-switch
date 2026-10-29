using System.Collections.Immutable;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

internal static class StopWindowTests
{
    public static bool VerdictNamesTheStopKind()
    {
        string[] stops = ["stop"];
        RecognizedUtterance Heard(RecognitionExtent extent, long end, params (string Text, long Start, long End)[] runs) =>
            new(1, extent, new(0, end), string.Concat(runs.Select(run => run.Text)),
                runs.Select(run => new LexicalRun(run.Text, new(run.Start, run.End))).ToImmutableArray());
        var alone = Heard(RecognitionExtent.ClosedUtterance, 8000, ("stop", 1000, 4000));
        var trailing = Heard(RecognitionExtent.ClosedUtterance, 8000, ("body", 0, 2000), ("stop", 3000, 6000));
        var quietWindow = Heard(RecognitionExtent.PrefixHead, 20000, ("body", 0, 2000), ("stop", 3000, 6000));
        var probe = Heard(RecognitionExtent.Probe, 8000, ("body", 0, 2000), ("stop", 3000, 6000));
        var tail = Heard(RecognitionExtent.ClosedUtterance, 8000, ("body", 0, 2000)) with
        {
            TailStop = true, TailStopConfidence = 0.9, TailStopWord = "stop", TailStopStart = 3000, TailStopEnd = 6000
        };
        return DictationBoundaries.FindStop(alone, stops, false, recording: false) == new StopVerdict(StopKind.Standalone, new(1000, 4000))
            && DictationBoundaries.FindStop(trailing, stops, false, recording: false) is null
            && DictationBoundaries.FindStop(trailing, stops, false, recording: true) == new StopVerdict(StopKind.Trailing, new(3000, 6000))
            && DictationBoundaries.FindStop(quietWindow, stops, false, recording: true) == new StopVerdict(StopKind.Window, new(3000, 6000))
            && DictationBoundaries.FindStop(probe, stops, false, recording: true) is { Kind: StopKind.Heard, Ends: false }
            && DictationBoundaries.FindStop(probe, stops, false, recording: false) is null
            && DictationBoundaries.FindStop(tail, stops, true, recording: false) == new StopVerdict(StopKind.Tail, new(3000, 6000), 0.9)
            && DictationBoundaries.FindStop(tail, stops, false, recording: false) is null;
    }

    // L4: an over-cap head queued while the dictation waited for its body completes after Finish reset the session.
    // It is still that body's audio, so its text must stay out of the log.
    public static bool StaleBodyHeadStaysPrivate()
    {
        var frames = new List<PcmFrame>();
        TestFrames.AddSquare(frames, 4, 0);
        TestFrames.AddSquare(frames, 12, 9000);
        TestFrames.AddSquare(frames, 10, 0);
        TestFrames.AddSquare(frames, 110, 9000);
        TestFrames.AddSquare(frames, 30, 0);
        var config = new VoiceSwitchConfig(["wake"], "en-US", "unused", MaxSeconds: 2.5,
            HangoverMs: 120, PrerollMs: 60, MinSpeechMs: 90, VadMinRMS: 0.005f, StopWords: ["stop"],
            Dictation: new(StartTimeoutMs: 10000, EndSilenceMs: 10000));
        var hotkeys = new DictationHotkeys();
        var recognizer = new ScriptedDictationRecognizer(async request =>
        {
            if (request.Extent != RecognitionExtent.PrefixHead)
                return new RecognizedUtterance(request.Id, request.Extent, request.Range, "wake", [new LexicalRun("wake", request.Range)]);
            await Task.Delay(300);
            return new RecognizedUtterance(request.Id, request.Extent, request.Range, "PRIVATE_BODY", [new LexicalRun("PRIVATE_BODY", request.Range)]);
        });
        var observer = new FinishOnHead(hotkeys);
        var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, [24]), recognizer, new RecordingDictationHandoff(),
            dryRun: true, observer, hotkeys: hotkeys, readShortcuts: () => null, wakeReading: value => value, micInUseBy: _ => null);
        var previousOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            runtime.RunAsync(timeout.Token).GetAwaiter().GetResult();
        }
        finally { Console.SetOut(previousOutput); }
        var log = output.ToString();
        var head = log.Split('\n').FirstOrDefault(line => line.Contains("dictation recognition: complete ") && line.Contains("extent=PrefixHead"));
        return observer.Pressed && log.Contains("dictation ended by hotkey") && log.Contains("dictation recognition: stale ")
            && head is not null && head.Contains(" textLen=11 ") && !log.Contains("PRIVATE_BODY");
    }

    private sealed class FinishOnHead(DictationHotkeys hotkeys) : IDictationRuntimeObserver
    {
        public bool Pressed { get; private set; }

        public void RecognitionQueued(RecognitionRequest request, int pending, long retainedStart, long retainedEnd)
        {
            if (request.Extent != RecognitionExtent.PrefixHead || Pressed) return;
            Pressed = true;
            hotkeys.OnKey(0x20, KeyMods.Control, down: true, nowMs: 0);
        }

        public void RecognitionCompleted(RecognitionRequest request, RecognizedUtterance? recognition, Exception? error) { }
        public void HandoffSubmitted(DictationAudio audio, HandoffResult result) { }
        public void RetentionObserved(long retainedStart, long retainedEnd, int pending) { }
        public void NoiseProcessorCompleted(NoiseProcessorStatus status) { }
    }
}
