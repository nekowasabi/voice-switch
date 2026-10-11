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
