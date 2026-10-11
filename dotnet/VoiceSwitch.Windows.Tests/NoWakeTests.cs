using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
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
            && report.MissingFields == 1 && report.MissLabels == 1 && report.MissContexts["auto_b:idle"] == 1
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
        time -= TimeSpan.FromSeconds(1);
        if (history.Select(first) is not null || history.Add(first, new string('x', 100_001), auto)) return false;
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
        if (NoWakePolicy.RelaxedWake(Heard("hexxo"), [WakeWord.From("hello", "hello")]) is not null
            || NoWakePolicy.RelaxedWake(Heard("ほうせいにはいる"), wakes)?.Distance != 2
            || NoWakePolicy.RelaxedWake(Heard("ほうせいにはいるよ本文"), wakes) is not null
            || NoWakePolicy.RelaxedWake(Heard("ほうせいにはいる", RecognitionExtent.PrefixHead), wakes) is not null
            || NoWakePolicy.RelaxedWake(Heard("ほうせい"), [WakeWord.From("音声", "おんせい")]) is not null) return false;
        var config = new VoiceSwitchConfig(["wake"], null, "unused", Dictation: new(NoWakeWord: new(AutoIdle: true)));
        var session = new DictationSession(config);
        var origin = new WakeOrigin(WakeRule.AutoB, 5);
        if (session.StartAutomatic(new(10, 100), new(WakeRule.AutoB, null), 100)
            || session.StartAutomatic(new(10, 100), new(WakeRule.AutoB, double.NaN), 100)
            || !session.StartAutomatic(new(10, 100), origin, 100) || session.StartManual(100, 0)) return false;
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
        async Task<(int Entries, int Manual, int Code, bool Leaked, bool Preroll)> Run(double? idle, string? busy = null, bool failLocal = false, bool failRecognition = false)
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
            var captured = new List<DictationAudio>();
            var handoff = new LocalHistoryHandoff(manual, (audio, _) => { captured.Add(audio); return failLocal ? Task.FromException<string>(new InvalidOperationException("PRIVATE_NO_WAKE_BODY")) : Task.FromResult("local body"); }, history);
            var recognizer = new ScriptedDictationRecognizer(request => failRecognition ? Task.FromException<RecognizedUtterance>(new InvalidOperationException("PRIVATE_NO_WAKE_BODY")) : Task.FromResult(new RecognizedUtterance(
                request.Id, request.Extent, request.Range, "PRIVATE_NO_WAKE_BODY", [new LexicalRun("PRIVATE_NO_WAKE_BODY", request.Range)])));
            var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, new[] { 22, 36, 56, 70 }),
                recognizer, handoff, false, inputIdleSeconds: () => idle, wakeReading: value => value,
                micInUseBy: _ => busy);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var previousOutput = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                var code = await runtime.RunAsync(timeout.Token);
                var preroll = captured.Select((audio, index) => audio.Range == new SampleRange(index == 0 ? 960 : 17280, index == 0 ? 9600 : 25920)
                    && audio.Samples.Take(960).All(value => value == 0)
                    && audio.Samples.Skip(960).Take(5760).Select((value, index) => value == (index % 2 == 0 ? 9000 : -9000)).All(value => value)
                    && audio.Samples.Skip(6720).All(value => value == 0)).All(value => value);
                return (history.Snapshot().Count, manual.Count, code, output.ToString().Contains("PRIVATE_NO_WAKE_BODY"), preroll);
            }
            finally { Console.SetOut(previousOutput); }
        }
        return !Run(4, failLocal: true).GetAwaiter().GetResult().Leaked
            && !Run(4, failRecognition: true).GetAwaiter().GetResult().Leaked
            && Run(4).GetAwaiter().GetResult() == (2, 0, 0, false, true)
            && Run(null).GetAwaiter().GetResult() == (0, 0, 0, false, true)
            && Run(0).GetAwaiter().GetResult() == (0, 0, 0, false, true)
            && Run(4, "other recorder").GetAwaiter().GetResult() == (0, 0, 0, false, true);
    }


    public static bool ModeRecovery()
    {
        var state = new ModeRestore("before", "temporary");
        if (state.Decide("temporary") != VoiceSwitch.Windows.Core.ModeRecovery.Restore
            || state.Decide("user-choice") != VoiceSwitch.Windows.Core.ModeRecovery.Clear
            || state.Decide(null) != VoiceSwitch.Windows.Core.ModeRecovery.Unknown) return false;
        var root = Path.Combine(Path.GetTempPath(), "voice-mode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var prefs = Path.Combine(root, "preferences.json");
            var journal = Path.Combine(root, "mode-restore.json");
            File.WriteAllText(prefs, "{\"activeMode\":\"temporary\"}");
            File.WriteAllText(journal, JsonSerializer.Serialize(state));
            var launches = new List<string>();
            var handoff = new RegisteredSuperwhisperHandoff(root, root, psi =>
            {
                var argument = psi.ArgumentList[0];
                launches.Add(argument);
                File.WriteAllText(prefs, JsonSerializer.Serialize(new { activeMode = Uri.UnescapeDataString(argument.Split("key=")[1]) }));
                return new Process();
            }, delay: _ => Task.CompletedTask, preferencesPath: prefs, modesDir: root);
            if (!handoff.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult()
                || launches.Count != 1 || launches[0] != "superwhisper://mode?key=before" || File.Exists(journal)) return false;
            File.WriteAllText(prefs, "{\"activeMode\":\"user-choice\"}");
            File.WriteAllText(journal, JsonSerializer.Serialize(state));
            if (!handoff.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult() || launches.Count != 1) return false;
            File.WriteAllText(journal, "broken");
            return !handoff.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult() && File.Exists(journal);
        }
        finally { Directory.Delete(root, true); }
    }

    public static bool ControlledMode()
    {
        string[] safe = ["{\"key\":\"safe\",\"name\":\"voice\",\"autoPaste\":false}"];
        if (!SuperwhisperModes.HasAutoPasteDisabled("voice", safe)
            || SuperwhisperModes.HasAutoPasteDisabled("safe", ["{\"key\":\"safe\"}"])
            || SuperwhisperModes.HasAutoPasteDisabled("safe", [.. safe, .. safe])) return false;
        var root = Path.Combine(Path.GetTempPath(), "voice-controlled-" + Guid.NewGuid().ToString("N"));
        var launched = 0;
        try
        {
            var handoff = new RegisteredSuperwhisperHandoff(root, root, _ => { launched++; return new Process(); },
                delay: _ => Task.CompletedTask, preferencesPath: Path.Combine(root, "prefs"), modesDir: root, requireControlledMode: true);
            var audio = new DictationAudio(Guid.NewGuid(), new(0, 2), ImmutableArray.Create<short>(1, 2));
            var history = new DictationHistory();
            var pasted = false;
            var wrapper = new LocalHistoryHandoff(handoff, (_, _) => Task.FromResult("manual fallback"), history,
                (text, _) => pasted = text == "manual fallback" && history.Select(audio.SessionId) == text);
            var result = wrapper.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            return result.Status == HandoffStatus.RecordedLocally && pasted && launched == 0
                && history.Select(audio.SessionId) == "manual fallback" && Directory.GetFiles(root, "*.wav").Length == 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static bool ClearPendingHistory()
    {
        var history = new DictationHistory();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new LocalHistoryHandoff(new CountingHandoff(), (_, _) => pending.Task, history);
        var audio = new DictationAudio(Guid.NewGuid(), new(0, 1), ImmutableArray.Create<short>(1), Origin: new(WakeRule.AutoB, 4));
        var result = wrapper.SubmitAsync(audio, CancellationToken.None);
        history.Clear();
        pending.SetResult("old result");
        result.GetAwaiter().GetResult();
        if (history.Snapshot().Count != 0) return false;
        var next = new LocalHistoryHandoff(new CountingHandoff(), (_, _) => Task.FromResult("new result"), history);
        var id = Guid.NewGuid();
        next.SubmitAsync(audio with { SessionId = id }, CancellationToken.None).GetAwaiter().GetResult();
        if (history.Select(id) != "new result") return false;
        var cancelledId = Guid.NewGuid();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new LocalHistoryHandoff(new CountingHandoff(), (_, _) => delayed.Task, history)
            .SubmitAsync(audio with { SessionId = cancelledId }, cancellation.Token);
        cancellation.Cancel();
        delayed.SetResult("discard after pause");
        try { cancelled.GetAwaiter().GetResult(); return false; }
        catch (OperationCanceledException) { return history.Select(cancelledId) is null; }
    }


    public static bool OverlappingHandoffs()
    {
        async Task<bool> Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "voice-overlap-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(directory, "handoff");
            Directory.CreateDirectory(directory);
            try
            {
                var modes = Path.Combine(directory, "modes"); Directory.CreateDirectory(modes);
                File.WriteAllText(Path.Combine(modes, "safe.json"), "{\"key\":\"safe\",\"autoPaste\":false}");
                var prefs = Path.Combine(directory, "preferences.json");
                File.WriteAllText(prefs, "{\"activeMode\":\"before\"}");
                var recordings = Path.Combine(directory, "recordings");
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var launches = new List<string>();
                var files = 0;
                var handoff = new RegisteredSuperwhisperHandoff(root, recordings, psi =>
                {
                    var argument = psi.ArgumentList[0]; launches.Add(argument);
                    if (argument.Contains("mode?key="))
                        File.WriteAllText(prefs, JsonSerializer.Serialize(new { activeMode = argument.Split("key=")[1] }));
                    else
                    {
                        files++;
                        var run = Path.Combine(recordings, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + files).ToString());
                        Directory.CreateDirectory(run);
                        File.WriteAllText(Path.Combine(run, "meta.json"), "{\"result\":\"result\"}");
                        started.TrySetResult();
                    }
                    return new Process();
                }, delay: _ => files == 1 && !release.Task.IsCompleted ? release.Task : Task.CompletedTask,
                    superwhisperMode: "safe", preferencesPath: prefs, modesDir: modes, requireControlledMode: true);
                var audio = new DictationAudio(Guid.NewGuid(), new(0, 1), ImmutableArray.Create<short>(1));
                var first = handoff.SubmitAsync(audio, CancellationToken.None);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var second = handoff.SubmitAsync(audio with { SessionId = Guid.NewGuid() }, CancellationToken.None);
                if (files != 1 || second.IsCompleted) { release.TrySetResult(); return false; }
                release.SetResult();
                var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
                var duplicate = await handoff.SubmitAsync(audio, CancellationToken.None);
                return results.All(result => result.Status == HandoffStatus.Transcribed)
                    && files == 2 && duplicate.Status == HandoffStatus.RecordedLocally
                    && launches.Where(value => value.Contains("mode?key=")).SequenceEqual([
                        "superwhisper://mode?key=safe", "superwhisper://mode?key=before",
                        "superwhisper://mode?key=safe", "superwhisper://mode?key=before"])
                    && !File.Exists(Path.Combine(root, "mode-restore.json"));
            }
            finally { Directory.Delete(directory, true); }
        }
        return Run().GetAwaiter().GetResult();
    }


    public static bool FreshResultsOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "voice-fresh-" + Guid.NewGuid().ToString("N"));
        try
        {
            var old = Path.Combine(root, "100"); Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "meta.json"), "{\"result\":\"previous words\"}");
            if (RegisteredSuperwhisperHandoff.FindResult(root, 99) != "previous words"
                || RegisteredSuperwhisperHandoff.FindResult(root, 99, new HashSet<string> { "100" }) is not null) return false;
            var next = Path.Combine(root, "101"); Directory.CreateDirectory(next);
            File.WriteAllText(Path.Combine(next, "meta.json"), "{\"result\":\"new words\"}");
            return RegisteredSuperwhisperHandoff.FindResult(root, 99, new HashSet<string> { "100" }) == "new words";
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
