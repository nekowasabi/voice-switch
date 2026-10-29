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
        report.Observe("dictation recognition: complete id=5 text=\"rule=strict inputIdleSeconds=0.100 x\"");
        return report.Rules["strict"] == 1 && report.Contexts["strict:after_input"] == 1
            && report.Contexts["auto_b:idle"] == 1 && report.Contexts["followup:unknown"] == 1
            && report.MissingFields == 2 && report.MissLabels == 1 && report.MissContexts["auto_b:idle"] == 1
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
        if (!FilteredLocalDelivery() || !FilteredSuperwhisperDelivery()) return false;
        var history = new DictationHistory();
        var manual = new CountingHandoff { Status = HandoffStatus.FailedBeforeDispatch };
        var local = new LocalHistoryHandoff(manual, (_, _) => Task.FromResult("local words"), history);
        var audio = new DictationAudio(Guid.NewGuid(), new SampleRange(0, 2), ImmutableArray.Create<short>(1, 2),
            Origin: new(WakeRule.Followup, 2));
        var result = local.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Status != HandoffStatus.RecordedLocally || manual.Count != 1 || history.Select(audio.SessionId) != "local words") return false;
        var strict = audio with { SessionId = Guid.NewGuid(), Origin = new(WakeRule.Strict, 4) };
        if (local.SubmitAsync(strict, CancellationToken.None).GetAwaiter().GetResult().Status != HandoffStatus.FailedBeforeDispatch
            || manual.Count != 2 || history.Select(strict.SessionId) is not null) return false;
        var root = Path.Combine(Path.GetTempPath(), "voice-auto-" + Guid.NewGuid().ToString("N"));
        var launched = 0;
        try
        {
            var unverified = new RegisteredSuperwhisperHandoff(root, root, _ => { launched++; return null; },
                delay: _ => Task.CompletedTask, preferencesPath: Path.Combine(root, "prefs"), modesDir: root);
            var blocked = unverified.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            return blocked.Status == HandoffStatus.FailedBeforeDispatch && launched == 0
                && (!Directory.Exists(root) || Directory.GetFiles(root, "*.wav").Length == 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }


    public static bool WakeRules()
    {
        if (NoWakePolicy.OnSpeech(new(), 10, true) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), null, false) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), double.NaN, false) is not null
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), 0.2, false)?.Rule != WakeRule.AutoB
            || NoWakePolicy.OnSpeech(new(AutoIdle: true), -1, false) is not null
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
        async Task<(int Entries, int Manual, int Code, bool Leaked, bool Preroll, bool Replaced)> Run(double? idle, string? busy = null, bool failLocal = false, bool failRecognition = false, bool spokenWake = false, bool fragmented = false, bool filterInterjections = false, float? loopbackDb = null, int minHandoffMs = 0, int voiced = 1)
        {
            var frames = new List<PcmFrame>();
            void Add(int count, short value) => TestFrames.AddSquare(frames, count, value);
            Add(4, 0);
            if (fragmented) { for (var i = 0; i < 10; i++) { Add(2, 9000); Add(1, 0); } Add(30, 0); }
            else { Add(12, 9000); Add(22, 0); Add(12, 9000); Add(22, 0); }
            var config = new VoiceSwitchConfig(["wake"], "en-US", "unused", MaxSeconds: 2.5,
                HangoverMs: 120, PrerollMs: 60, MinSpeechMs: 90, VadMinRMS: 0.005f,
                Dictation: new(EndSilenceMs: 240, NoWakeWord: new(AutoIdle: true, FilterInterjections: filterInterjections, MinHandoffMs: minHandoffMs)));
            var manual = Unverified();
            var history = new DictationHistory();
            var captured = new List<DictationAudio>();
            var pasted = new List<string>();
            var handoff = new LocalHistoryHandoff(manual, (audio, _) => { captured.Add(audio); return failLocal ? Task.FromException<string>(new InvalidOperationException("PRIVATE_NO_WAKE_BODY")) : Task.FromResult(filterInterjections ? "あっ" : "local body"); }, history,
                filterInterjections ? (text, _) => { pasted.Add(text); return true; } : null);
            var recognizer = new ScriptedDictationRecognizer(request => failRecognition ? Task.FromException<RecognizedUtterance>(new InvalidOperationException("PRIVATE_NO_WAKE_BODY")) : Task.FromResult(new RecognizedUtterance(
                request.Id, request.Extent, request.Range, spokenWake ? "wake body" : "PRIVATE_NO_WAKE_BODY", spokenWake
                    ? [new LexicalRun("wake", new(request.Range.Start, request.Range.Start + request.Range.Length / 2)), new LexicalRun("body", new(request.Range.Start + request.Range.Length / 2, request.Range.End))]
                    : [new LexicalRun("PRIVATE_NO_WAKE_BODY", request.Range)])));
            var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, new[] { 22, 36, 56, 70 }),
                recognizer, handoff, false, inputIdleSeconds: () => idle, wakeReading: value => value,
                micInUseBy: _ => busy, loopbackLevels: loopbackDb is { } constantDb ? ConstantLoopback(constantDb) : null,
                voicedWindows: _ => voiced);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var previousOutput = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                var code = await runtime.RunAsync(timeout.Token);
                // Linux has no playback device, so without an injected trace the shadow fields must say so instead of vanishing.
                var expectedLevels = loopbackDb switch { null => " loopback=unknown micDb=", <= LoopbackShadow.SilentDb => " loopback=silent micDb=", _ => $" loopbackDb={loopbackDb:0.0} micDb=" };
                var lines = output.ToString().Split('\n');
                var ends = lines.Where(line => line.Contains("dictation session: end reason=")).ToArray();
                if (lines.Where(line => line.Contains("dictation session: automatic ") && line.Contains(" range=")).Concat(ends).Any(line => !line.Contains(expectedLevels))
                    || (idle == 4 && busy is null && ends.Length == 0)
                    || (loopbackDb is > LoopbackShadow.SilentDb && !ends.Any(line => line.Contains(" micOverLoopbackGe0=0."))))
                    throw new InvalidOperationException($"session logs lack '{expectedLevels}'");
                // Every automatic session is traceable by sid from its start through recognition, the end line and the handoff result,
                // and its recognitions describe the transcript's shape without the text.
                var sids = lines.Where(line => line.Contains("dictation session: automatic ") && line.Contains(" range="))
                    .Select(line => System.Text.RegularExpressions.Regex.Match(line, @" sid=([0-9a-f-]{36})").Groups[1].Value).ToArray();
                if (sids.Any(sid => sid.Length == 0
                        || (spokenWake
                            ? !lines.Any(line => line.Contains("dictation session: automatic replaced by wake word ") && line.Contains($" sid={sid}"))
                            : !lines.Any(line => line.Contains("dictation session: end reason=") && line.Contains($" sid={sid} ") && line.Contains(" seconds="))
                                || !lines.Any(line => (line.Contains("dictation handoff: ") || line.Contains("dictation handoff failed: ") || line.Contains("dictation dropped: ")) && line.Contains($" sid={sid}")))
                        || (!failRecognition && !lines.Any(line => line.Contains("dictation recognition: complete ") && line.Contains($" sid={sid}") && line.Contains($" textLen={(spokenWake ? 8 : 17)} filler=0 conf="))))
                    || lines.Any(line => line.Contains("dictation recognition: complete ") && line.Contains("PRIVATE_NO_WAKE_BODY")))
                    throw new InvalidOperationException($"session logs are not joined by sid (idle={idle} busy={busy} failLocal={failLocal} failRecognition={failRecognition} spokenWake={spokenWake} fragmented={fragmented} filter={filterInterjections})");
                if (minHandoffMs > 0 && !output.ToString().Contains("dictation dropped: automatic audio shorter than minHandoffMs"))
                    throw new InvalidOperationException("short automatic audio was not dropped before handoff");
                if (voiced == 0 && lines.Count(line => line.Contains("dictation dropped: automatic audio had no speech")) != 2)
                    throw new InvalidOperationException("automatic audio without speech was not dropped before handoff");
                if (busy is not null && lines.Count(line => line.Contains($"automatic wake blocked: {busy}")) != 1)
                    throw new InvalidOperationException("a blocked automatic wake must be logged once, not silently or per frame");
                if (filterInterjections)
                {
                    var retained = history.Snapshot();
                    if (spokenWake ? retained.Count == 0 || retained.Any(entry => entry.Text != "あっ" || entry.Origin.IsAutomatic)
                        || pasted.Count != retained.Count || pasted.Any(text => text != "あっ")
                        : retained.Count != 0 || pasted.Count != 0)
                        throw new InvalidOperationException("automatic filtering changed strict wake priority or transcript delivery");
                }
                var preroll = fragmented ? captured is [{ Range.Start: 960 }] : captured.Select((audio, index) => audio.Range == new SampleRange(index == 0 ? 960 : 17280, index == 0 ? 9600 : 25920)
                    && audio.Samples.Take(960).All(value => value == 0)
                    && audio.Samples.Skip(960).Take(5760).Select((value, index) => value == (index % 2 == 0 ? 9000 : -9000)).All(value => value)
                    && audio.Samples.Skip(6720).All(value => value == 0)).All(value => value);
                return (history.Snapshot().Count, manual.Count, code, output.ToString().Contains("PRIVATE_NO_WAKE_BODY"), preroll,
                    output.ToString().Contains("automatic replaced by wake word"));
            }
            finally { Console.SetOut(previousOutput); }
        }
        return !Run(4, failLocal: true).GetAwaiter().GetResult().Leaked
            && !Run(4, failRecognition: true).GetAwaiter().GetResult().Leaked
            && Run(4).GetAwaiter().GetResult() == (2, 2, 0, false, true, false)
            && Run(4, loopbackDb: -20).GetAwaiter().GetResult() == (2, 2, 0, false, true, false)
            && Run(4, loopbackDb: -100).GetAwaiter().GetResult() == (2, 2, 0, false, true, false)
            && Run(4, minHandoffMs: 1000).GetAwaiter().GetResult() == (0, 0, 0, false, true, false)
            && Run(4, voiced: 0).GetAwaiter().GetResult() == (0, 0, 0, false, true, false)
            && Run(null).GetAwaiter().GetResult() == (0, 0, 0, false, true, false)
            && Run(0).GetAwaiter().GetResult() == (2, 2, 0, false, true, false)
            && Run(0, spokenWake: true).GetAwaiter().GetResult() is (0, > 0, 0, _, _, true)
            && Run(4, "other recorder").GetAwaiter().GetResult() == (0, 0, 0, false, true, false)
            && Run(4, spokenWake: true).GetAwaiter().GetResult() is (0, > 0, 0, _, _, true)
            && Run(4, fragmented: true).GetAwaiter().GetResult() == (1, 1, 0, false, true, false)
            && Run(4, filterInterjections: true).GetAwaiter().GetResult() == (0, 2, 0, false, true, false)
            && Run(4, spokenWake: true, filterInterjections: true).GetAwaiter().GetResult() is (> 0, > 0, 0, false, _, true);
    }


    public static bool AutomaticWordAgreement()
    {
        async Task<(int Handed, bool Recording, string Log, DictationAudio? Audio)> Run(Func<ImmutableArray<short>, Task<int>> agreed, bool stopWord = false)
        {
            var frames = new List<PcmFrame>();
            void Add(int count, short value) => TestFrames.AddSquare(frames, count, value);
            Add(4, 0);
            if (stopWord) { Add(12, 9000); Add(10, 0); Add(12, 9000); Add(60, 0); }
            else { Add(70, 9000); Add(30, 0); }
            var config = new VoiceSwitchConfig(["wake"], "en-US", "unused", MaxSeconds: 2.5,
                HangoverMs: 120, PrerollMs: 60, MinSpeechMs: 90, VadMinRMS: 0.005f, StopWords: ["stop"],
                Dictation: new(EndSilenceMs: stopWord ? 1200 : 240, NoWakeWord: new(AutoIdle: true, MinHandoffMs: 0)));
            var handoff = new CountingHandoff();
            var recognizer = new ScriptedDictationRecognizer(request =>
            {
                var text = stopWord && request.Range.Start >= 20 * Segmenter.FrameLength ? "stop" : "body";
                return Task.FromResult(new RecognizedUtterance(request.Id, request.Extent, request.Range, text, [new LexicalRun(text, request.Range)]));
            });
            var phases = new PhaseRecorder();
            var runtime = new WindowsDictationRuntime(config, new FixturePcmCapture(frames, new[] { 20, 30, 40, 50, 60, 70, 80 }),
                recognizer, handoff, false, phases, inputIdleSeconds: () => 4, wakeReading: value => value,
                micInUseBy: _ => null, voicedWindows: _ => 1, agreedCharacters: agreed);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var previousOutput = Console.Out;
            using var output = new StringWriter();
            try
            {
                Console.SetOut(output);
                await runtime.RunAsync(timeout.Token);
            }
            finally { Console.SetOut(previousOutput); }
            return (handoff.Count, phases.Phases.Contains(DictationPhase.Recording), output.ToString(), handoff.Seen.LastOrDefault());
        }

        bool Before(string log, string first, string second) => log.IndexOf(first, StringComparison.Ordinal) is >= 0 and var at && at < log.IndexOf(second, StringComparison.Ordinal);
        var agreed = Run(_ => Task.FromResult(5)).GetAwaiter().GetResult();
        var cough = Run(_ => Task.FromResult(1)).GetAwaiter().GetResult();
        var broken = Run(_ => Task.FromException<int>(new InvalidOperationException("worker down"))).GetAwaiter().GetResult();
        var stopped = Run(_ => Task.FromResult(5), stopWord: true).GetAwaiter().GetResult();
        return agreed is (1, true, _, _) && Before(agreed.Log, "automatic confirmed by agreed words", "dictation phase: Recording")
               && !agreed.Log.Contains("dictation agreement:") && agreed.Audio?.Wake.IsAutomatic == true
            && cough is (0, false, _, _) && cough.Log.Contains("dictation agreement: shared=1 ")
               && cough.Log.Contains("dictation dropped: automatic audio had no agreed words")
            && broken is (1, true, _, _) && broken.Log.Contains("automatic confirmed by unavailable word agreement")
            && stopped.Handed == 1 && stopped.Audio is { Reason: FinishReason.StandaloneStop, Wake.IsAutomatic: false }
               && stopped.Log.Contains("automatic ended by StandaloneStop, pasting like a wake-word dictation");
    }

    // A playback trace at one level from a minute ago to a minute ahead, so every mic frame of the test pairs with a bin.
    private static LevelTrace ConstantLoopback(float db)
    {
        var trace = new LevelTrace(20_000);
        var now = Environment.TickCount64 / LoopbackShadow.BinMs * LoopbackShadow.BinMs;
        for (var at = now - 60_000; at < now + 60_000; at += LoopbackShadow.BinMs) trace.Add(at, db);
        return trace;
    }

    public static bool LoopbackShadowFields()
    {
        const long epoch = 1_000_000;
        // Nine 30 ms mic frames: four at -30 dBFS, then five at -10 dBFS (lower median -10).
        var mic = Enumerable.Range(0, 9).Select(k => ((long)k * Segmenter.FrameLength, k < 4 ? -30f : -10f)).ToArray();
        // Loopback bins over the lag-shifted window; playback starts where mic frame 3 is heard, so frames 0..2 pair with silence.
        var from = LoopbackShadow.LoopbackMs(0, epoch) - 2 * LoopbackShadow.BinMs;
        var to = LoopbackShadow.LoopbackMs(9 * Segmenter.FrameLength, epoch) + 2 * LoopbackShadow.BinMs;
        var onset = LoopbackShadow.LoopbackMs(3 * Segmenter.FrameLength, epoch);
        var loopback = new List<(long, float)>();
        for (var at = from; at < to; at += LoopbackShadow.BinMs) loopback.Add((at, at < onset ? -100f : -20f));
        var silent = loopback.Select(bin => (bin.Item1, -100f)).ToList();
        // Diffs: frame 3 gives -10, frames 4..8 give +10 -> median 10, 5 of 6 at or above 0.
        return LoopbackShadow.Fields(mic, loopback, epoch) == "loopbackDb=-20.0 micDb=-10.0 micOverLoopbackDb=10.0 micOverLoopbackGe0=0.83 micOverLoopbackN=6"
            && LoopbackShadow.Fields(mic, silent, epoch) == "loopback=silent micDb=-10.0"
            && LoopbackShadow.Fields(mic, null, epoch) == "loopback=unknown micDb=-10.0"
            && LoopbackShadow.Fields(mic, [], epoch) == "loopback=unknown micDb=-10.0"
            && LoopbackShadow.Fields([], loopback, epoch) == "loopbackDb=-20.0 micDb=- micOverLoopbackDb=- micOverLoopbackGe0=- micOverLoopbackN=0"
            && LoopbackShadow.Dbfs(new short[480]) == LoopbackShadow.FloorDb
            && Math.Abs(LoopbackShadow.Dbfs(Enumerable.Repeat((short)9000, 480).ToArray()) - -11.2f) < 0.05f;
    }

    public static bool TranscriptShapeFields() =>
        TranscriptShape.Length("あ、うん。 はい！") == 5 && TranscriptShape.IsFiller("あ、うん。 はい！")
        && TranscriptShape.Length("PRIVATE_NO_WAKE_BODY") == 17 && !TranscriptShape.IsFiller("PRIVATE_NO_WAKE_BODY")
        && TranscriptShape.IsFiller("ははは") && TranscriptShape.IsFiller("あっ") && TranscriptShape.IsFiller("えーっと") == false
        && !TranscriptShape.IsFiller("はい、了解") && !TranscriptShape.IsFiller("") && !TranscriptShape.IsFiller("。")
        && TranscriptShape.Length("") == 0;

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

    public static bool KeepsModeUntilRunFinishes()
    {
        static SuperwhisperRun Kind(string json) => SuperwhisperRuns.Classify(JsonDocument.Parse(json).RootElement);
        const string placeholder = """{"result":"","processingTime":0,"duration":0}""";
        const string heardNothing = """{"result":"","processingTime":18,"duration":3000}""";
        const string llmPending = """{"result":"ああなるほど。","processingTime":1006,"duration":6261,"languageModelKey":"claude-sonnet-5-5"}""";
        if (Kind(placeholder) != SuperwhisperRun.Unjudged || Kind(heardNothing) != SuperwhisperRun.HeardNothing
            || Kind("""{"result":"ああなるほど。","processingTime":1006,"duration":6261}""") != SuperwhisperRun.Transcribed
            || Kind("""{"result":"ああなるほど。","duration":6261,"llmResult":"なるほど。","languageModelProcessingTime":1398}""") != SuperwhisperRun.Polished
            || !SuperwhisperRuns.InFlight(JsonDocument.Parse(llmPending).RootElement)
            || SuperwhisperRuns.InFlight(JsonDocument.Parse(heardNothing).RootElement)) return false;

        var directory = Path.Combine(Path.GetTempPath(), "voice-keep-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(directory, "handoff");
        var modes = Path.Combine(directory, "modes");
        var recordings = Path.Combine(directory, "recordings");
        var prefs = Path.Combine(directory, "preferences.json");
        var meta = Path.Combine(recordings, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1).ToString(), "meta.json");
        Directory.CreateDirectory(modes);
        try
        {
            File.WriteAllText(Path.Combine(modes, "safe.json"), "{\"key\":\"safe\",\"autoPaste\":false}");
            File.WriteAllText(prefs, "{\"activeMode\":\"before\"}");
            var handoff = new RegisteredSuperwhisperHandoff(root, recordings, psi =>
            {
                var argument = psi.ArgumentList[0];
                if (argument.Contains("mode?key="))
                    File.WriteAllText(prefs, JsonSerializer.Serialize(new { activeMode = argument.Split("key=")[1] }));
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
                    File.WriteAllText(meta, placeholder);
                }
                return new Process();
            }, delay: _ => Task.CompletedTask, superwhisperMode: "safe", preferencesPath: prefs, modesDir: modes);
            var audio = new DictationAudio(Guid.NewGuid(), new(0, 2), ImmutableArray.Create<short>(1, 2), Origin: new(WakeRule.AutoB, 4));
            var journal = Path.Combine(root, "mode-restore.json");
            var result = handoff.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            if (result.Status != HandoffStatus.NoResult || !File.Exists(journal) || !File.ReadAllText(prefs).Contains("safe")) return false;
            var next = handoff.SubmitAsync(audio with { SessionId = Guid.NewGuid() }, CancellationToken.None).GetAwaiter().GetResult();
            if (next.Status != HandoffStatus.FailedBeforeDispatch || handoff.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult()
                || !File.Exists(journal)) return false;
            File.WriteAllText(meta, heardNothing);
            return handoff.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult()
                && !File.Exists(journal) && File.ReadAllText(prefs).Contains("before");
        }
        finally { Directory.Delete(directory, true); }
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
                delay: _ => Task.CompletedTask, preferencesPath: Path.Combine(root, "prefs"), modesDir: root);
            var audio = new DictationAudio(Guid.NewGuid(), new(0, 2), ImmutableArray.Create<short>(1, 2), Origin: new(WakeRule.AutoB, 4));
            var history = new DictationHistory();
            var pasted = false;
            var wrapper = new LocalHistoryHandoff(handoff, (_, _) => Task.FromResult("local fallback"), history, (_, _) => pasted = true);
            var result = wrapper.SubmitAsync(audio, CancellationToken.None).GetAwaiter().GetResult();
            return result.Status == HandoffStatus.RecordedLocally && !pasted && launched == 0
                && history.Select(audio.SessionId) == "local fallback" && Directory.GetFiles(root, "*.wav").Length == 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static bool ClearPendingHistory()
    {
        var history = new DictationHistory();
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wrapper = new LocalHistoryHandoff(Unverified(), (_, _) => pending.Task, history);
        var audio = new DictationAudio(Guid.NewGuid(), new(0, 1), ImmutableArray.Create<short>(1), Origin: new(WakeRule.AutoB, 4));
        var result = wrapper.SubmitAsync(audio, CancellationToken.None);
        history.Clear();
        pending.SetResult("old result");
        result.GetAwaiter().GetResult();
        if (history.Snapshot().Count != 0) return false;
        var next = new LocalHistoryHandoff(Unverified(), (_, _) => Task.FromResult("new result"), history);
        var id = Guid.NewGuid();
        next.SubmitAsync(audio with { SessionId = id }, CancellationToken.None).GetAwaiter().GetResult();
        if (history.Select(id) != "new result") return false;
        var cancelledId = Guid.NewGuid();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var cancelled = new LocalHistoryHandoff(Unverified(), (_, _) => delayed.Task, history)
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
                    superwhisperMode: "safe", preferencesPath: prefs, modesDir: modes);
                var audio = new DictationAudio(Guid.NewGuid(), new(0, 1), ImmutableArray.Create<short>(1), Origin: new(WakeRule.AutoB, 4));
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

    private static bool FilteredLocalDelivery()
    {
        async Task<bool> Run()
        {
            var path = Path.Combine(Path.GetTempPath(), "voice-filter-config-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                VoiceSwitchConfig Load(bool enabled)
                {
                    File.WriteAllText(path, "{\"wakeWords\":[\"wake\"],\"command\":\"unused\",\"dictation\":{\"noWakeWord\":{\"autoIdle\":true,\"filterInterjections\":" + (enabled ? "true" : "false") + "}}}");
                    return ConfigLoader.Load(path);
                }
                foreach (var enabled in new[] { false, true })
                {
                    var session = new DictationSession(Load(enabled));
                    if (!session.StartAutomatic(new(0, 2), new(WakeRule.AutoB, 4), 2)) return false;
                    var audio = session.Finish(FinishReason.FinishCommand, _ => [1, 2])!;
                    var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var history = new DictationHistory();
                    var changes = 0;
                    history.Changed += () => changes++;
                    var pasted = new List<string>();
                    var wrapper = new LocalHistoryHandoff(Unverified(), (_, _) => pending.Task, history,
                        (text, _) => { pasted.Add(text); return true; });
                    var result = wrapper.SubmitAsync(audio, CancellationToken.None);
                    _ = new DictationSession(Load(!enabled));
                    pending.SetResult(" あっ。\n");
                    if ((await result).Status != HandoffStatus.RecordedLocally
                        || history.Select(audio.SessionId) != (enabled ? null : " あっ。\n")
                        || history.Snapshot().Count != (enabled ? 0 : 1) || changes != (enabled ? 0 : 1)
                        || pasted.Count != 0) return false;
                    var strict = audio with { SessionId = Guid.NewGuid(), Origin = new(WakeRule.Strict, 0) };
                    await wrapper.SubmitAsync(strict, CancellationToken.None);
                    if (history.Select(strict.SessionId) != " あっ。\n" || !pasted.SequenceEqual([" あっ。\n"])) return false;
                }
                return true;
            }
            finally { File.Delete(path); }
        }
        return Run().GetAwaiter().GetResult();
    }

    private static bool FilteredSuperwhisperDelivery()
    {
        async Task<bool> Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "voice-filter-handoff-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(directory, "handoff");
            var modes = Path.Combine(directory, "modes");
            var recordings = Path.Combine(directory, "recordings");
            var prefs = Path.Combine(directory, "preferences.json");
            Directory.CreateDirectory(modes);
            File.WriteAllText(Path.Combine(modes, "safe.json"), "{\"key\":\"safe\",\"autoPaste\":false}");
            File.WriteAllText(prefs, "{\"activeMode\":\"before\"}");
            var history = DictationHistory.Shared;
            history.Clear();
            var changes = 0;
            void Changed() => changes++;
            history.Changed += Changed;
            try
            {
                var files = 0;
                var routes = new List<string>();
                var fallback = 0;
                var launched = new List<string>();
                var handoff = new RegisteredSuperwhisperHandoff(root, recordings, psi =>
                {
                    var argument = psi.ArgumentList[0];
                    launched.Add(argument);
                    if (argument.Contains("mode?key="))
                        File.WriteAllText(prefs, JsonSerializer.Serialize(new { activeMode = argument.Split("key=")[1] }));
                    else
                    {
                        files++;
                        var run = Path.Combine(recordings, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() + files).ToString());
                        Directory.CreateDirectory(run);
                        File.WriteAllText(Path.Combine(run, "meta.json"), JsonSerializer.Serialize(new { result = " ああ！\n" }));
                    }
                    return new Process();
                }, delay: _ => Task.CompletedTask, onTranscribed: text => { routes.Add(text); return Task.FromResult(RouteResult.NotRouted); },
                    superwhisperMode: "safe", preferencesPath: prefs, modesDir: modes);
                var wrapper = new LocalHistoryHandoff(handoff, (_, _) => { fallback++; return Task.FromResult("fallback"); });
                foreach (var rule in new[] { WakeRule.AutoB, WakeRule.Followup, WakeRule.RelaxedD2 })
                {
                    var audio = new DictationAudio(Guid.NewGuid(), new(0, 2), [1, 2],
                        Origin: new(rule, 4), TranscriptPolicy: new(true));
                    if ((await wrapper.SubmitAsync(audio, CancellationToken.None)).Status != HandoffStatus.RecordedLocally
                        || history.Select(audio.SessionId) is not null || history.Snapshot().Count != 0 || changes != 0) return false;
                }
                if (fallback != 0 || routes.Count != 0 || files != 3 || Directory.GetFiles(root, "*.wav").Length != 0
                    || SuperwhisperModes.ActiveMode(File.ReadAllText(prefs)) != "before"
                    || File.Exists(Path.Combine(root, "mode-restore.json"))) return false;
                var strict = new DictationAudio(Guid.NewGuid(), new(0, 2), [1, 2],
                    Origin: new(WakeRule.Strict, 0), TranscriptPolicy: new(true));
                if ((await wrapper.SubmitAsync(strict, CancellationToken.None)).Status != HandoffStatus.Transcribed
                    || history.Select(strict.SessionId) != " ああ！\n" || !routes.SequenceEqual([" ああ！\n"]) || changes != 1) return false;
                var duplicate = await wrapper.SubmitAsync(strict, CancellationToken.None);
                if (duplicate.Status != HandoffStatus.RecordedLocally || files != 4 || routes.Count != 1 || fallback != 0) return false;
                var disabled = strict with { SessionId = Guid.NewGuid(), Origin = new(WakeRule.AutoB, 4), TranscriptPolicy = default };
                return (await wrapper.SubmitAsync(disabled, CancellationToken.None)).Status == HandoffStatus.Transcribed
                    && history.Select(disabled.SessionId) == " ああ！\n" && changes == 2 && routes.Count == 1 && fallback == 0
                    && launched.Count(argument => argument.Contains("mode?key=")) == 8;
            }
            finally
            {
                history.Changed -= Changed;
                history.Clear();
                Directory.Delete(directory, true);
            }
        }
        return Run().GetAwaiter().GetResult();
    }

    private static CountingHandoff Unverified() => new() { Status = HandoffStatus.FailedBeforeDispatch };

    private sealed class CountingHandoff : IDictationHandoff
    {
        public int Count;
        public List<DictationAudio> Seen = [];
        public HandoffStatus Status = HandoffStatus.Transcribed;
        public Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
        {
            Count++;
            Seen.Add(audio);
            return Task.FromResult(new HandoffResult(Status, audio.SessionId, null, "manual"));
        }
    }
}
