using System.Collections.Immutable;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

internal static class NoWakeTests
{
    public static bool Telemetry()
    {
        var report = new WakeTelemetrySummary();
        report.Observe("dictation recognition: complete id=1 rule=strict inputIdleSeconds=0.250");
        report.Observe("dictation recognition: complete id=2 rule=auto_b inputIdleSeconds=3.000");
        report.Observe("dictation recognition: complete id=3 rule=followup inputIdleSeconds=-1");
        report.Observe("dictation recognition: complete id=4");
        report.Observe("wake label: MISS id=2 rule=auto_b inputIdleSeconds=3.000");
        return report.Rules["strict"] == 1 && report.Contexts["strict:after_input"] == 1
            && report.Contexts["auto_b:idle"] == 1 && report.Contexts["followup:unknown"] == 1
            && report.MissingFields == 1 && report.MissLabels == 1
            && new WakeOrigin(WakeRule.RelaxedD2, double.NaN).Fields == "rule=relaxed_d2 inputIdleSeconds=-1"
            && InputActivity.ElapsedSeconds(250, uint.MaxValue - 749) == 1;
    }

    public static bool PasteGuards()
    {
        for (var mask = 0; mask < 32; mask++)
        {
            var safety = new PasteSafety((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0, (mask & 16) != 0);
            if (safety.AllowsPaste != (mask == 31)) return false;
        }
        return true;
    }

    public static bool History()
    {
        var time = DateTimeOffset.Parse("2026-10-11T00:00:00Z");
        var history = new DictationHistory(() => time, capacity: 2, retention: TimeSpan.FromMinutes(30));
        var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
        var auto = new WakeOrigin(WakeRule.AutoB, 4);
        history.Add(first, "first", auto);
        history.Add(second, "second", auto);
        if (history.Add(second, "replacement", new(WakeRule.Strict, 0))) return false;
        history.Add(third, "third", auto);
        if (history.Select(first) is not null || history.Select(second) != "second"
            || string.Join(",", history.Snapshot().Select(entry => entry.Text)) != "third,second") return false;
        time += TimeSpan.FromMinutes(30);
        if (history.Select(third) is not null || history.Snapshot().Count != 0) return false;
        history.Add(first, "new", auto);
        history.Clear();
        return history.Snapshot().Count == 0 && history.Select(first) is null;
    }

    public static bool AutomaticDelivery()
    {
        var history = new DictationHistory();
        var manual = new CountingHandoff();
        var local = new LocalHistoryHandoff(manual, (_, _) => Task.FromResult("local words"), history);
        var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(1, 2),
            Origin: new(WakeRule.Followup, 2));
        var result = local.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Status != HandoffStatus.RecordedLocally || manual.Count != 0 || history.Select(audio.SessionId) != "local words") return false;
        var strict = audio with { SessionId = Guid.NewGuid(), Origin = new(WakeRule.Strict, 4) };
        if (local.SubmitAsync(strict, CancellationToken.None).GetAwaiter().GetResult().Status != HandoffStatus.Transcribed || manual.Count != 1) return false;
        var root = Path.Combine(Path.GetTempPath(), "voice-auto-" + Guid.NewGuid().ToString("N"));
        var launched = 0;
        var forbidden = new RegisteredSuperwhisperHandoff(root, root, _ => { launched++; return null; }, dryRun: true);
        var blocked = forbidden.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
        return blocked.Status == HandoffStatus.FailedBeforeDispatch && launched == 0 && !Directory.Exists(root);
    }


    public static bool WakeRules()
    {
        if (NoWakePolicy.OnSpeech(new(), 10, true) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), null, false) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), double.NaN, false) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), 2.999, false) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), 3, false)?.Rule != WakeRule.AutoB
            || NoWakePolicy.OnSpeech(new(Followup: true, AutoIdle: true), 4, true)?.Rule != WakeRule.Followup) return false;
        RecognizedUtterance Heard(string reading, RecognitionExtent extent = RecognitionExtent.ClosedUtterance) =>
            new(1, extent, new(0, 100), "candidate", [new LexicalRun("candidate", new(0, 100), reading)]);
        WakeWord[] wakes = [WakeWord.From("音声に入る", "おんせいにはいる")];
        if (NoWakePolicy.RelaxedWake(Heard("ほうせいにはいる"), wakes)?.Distance != 2
            || NoWakePolicy.RelaxedWake(Heard("ほうせいにはいるよ本文"), wakes) is not null
            || NoWakePolicy.RelaxedWake(Heard("ほうせいにはいる", RecognitionExtent.PrefixHead), wakes) is not null
            || NoWakePolicy.RelaxedWake(Heard("ほうせい"), [WakeWord.From("音声", "おんせい")]) is not null) return false;
        var config = new VoiceSwitchConfig(["wake"], null, "unused", Dictation: new(NoWakeWord: new(AutoIdle: true)));
        var session = new DictationSession(config);
        var origin = new WakeOrigin(WakeRule.AutoB, 5);
        if (!session.StartAutomatic(new(10, 100), origin, 100) || session.StartManual(100, 0)) return false;
        session.Apply(new(1, RecognitionExtent.ClosedUtterance, new(100, 200), "wake body",
            [new LexicalRun("wake", new(100, 150)), new LexicalRun("body", new(150, 200))]),
            range => Enumerable.Repeat((short)7, (int)range.Length).ToImmutableArray(), 200, new(WakeRule.Strict, 0));
        var audio = session.Finish(FinishReason.FinishCommand, range => Enumerable.Repeat((short)7, (int)range.Length).ToImmutableArray());
        return audio?.Wake == origin && audio.Range == new SampleRange(10, 200) && audio.Samples.Length == 190
            && session.Finish(FinishReason.FinishCommand, _ => []) is null
            && !new DictationSession(config with { Dictation = null }).StartAutomatic(new(0, 100), origin, 100);
    }

    public static bool AutomaticRuntime()
    {
        async Task<(int Entries, int Manual, int Code)> Run(double? idle, string? busy = null)
        {
            var frames = new List<PcmFrame>();
            void Add(int count, short value)
            {
                for (var i = 0; i < count; i++)
                    frames.Add(new(frames.Count * Segmenter.FrameLength,
                        Enumerable.Range(0, Segmenter.FrameLength).Select(j => (short)(j % 2 == 0 ? value : -value)).ToImmutableArray()));
            }
            Add(4, 0); Add(12, 9000); Add(22, 0); Add(12, 9000); Add(22, 0);
            var config = new VoiceSwitchConfig(["wake"], "en-US", "unused", MaxSeconds: 2.5,
                HangoverMs: 120, PrerollMs: 60, MinSpeechMs: 90, VadMinRMS: 0.005f,
                Dictation: new(EndSilenceMs: 240, NoWakeWord: new(AutoIdle: true)));
            var manual = new CountingHandoff();
            var history = new DictationHistory();
            var handoff = new LocalHistoryHandoff(manual, (_, _) => Task.FromResult("local body"), history);
            var recognizer = new ScriptedDictationRecognizer(request => Task.FromResult(new RecognizedUtterance(
                request.Id, request.Extent, request.Range, "body", [new LexicalRun("body", request.Range)])));
            var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, new[] { 22, 36, 56, 70 }),
                recognizer, handoff, false, inputIdleSeconds: () => idle, wakeReading: value => value,
                micInUseBy: _ => busy);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var code = await runtime.RunAsync(timeout.Token);
            return (history.Snapshot().Count, manual.Count, code);
        }
        return Run(4).GetAwaiter().GetResult() == (2, 0, 0)
            && Run(null).GetAwaiter().GetResult() == (0, 0, 0)
            && Run(0).GetAwaiter().GetResult() == (0, 0, 0)
            && Run(4, "other recorder").GetAwaiter().GetResult() == (0, 0, 0);
    }

    private sealed class CountingHandoff : IDictationHandoff
    {
        public int Count;
        public Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
        {
            Count++;
            return Task.FromResult(new HandoffResult(HandoffStatus.Transcribed, audio.SessionId, null, "manual"));
        }
    }
}
