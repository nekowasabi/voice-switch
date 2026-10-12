using System.Collections.Immutable;
using System.Text.Json;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

public static class ConfigRegression
{
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "voice-config-policy-" + Guid.NewGuid().ToString("N") + ".json");
        var assertions = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            assertions++;
        }
        VoiceSwitchConfig Load(bool? filter)
        {
            var flag = filter is null ? "" : ",\"filterInterjections\":" + (filter.Value ? "true" : "false");
            File.WriteAllText(path, "{\"wakeWords\":[\"wake\"],\"command\":\"unused\",\"dictation\":{\"noWakeWord\":{\"autoIdle\":true,\"followup\":true,\"relaxedReadingDistance2\":true" + flag + "}}}");
            return ConfigLoader.Load(path);
        }
        DictationAudio Record(VoiceSwitchConfig config, WakeRule rule)
        {
            var session = new DictationSession(config);
            if (rule == WakeRule.Strict)
            {
                session.Apply(new(1, RecognitionExtent.ClosedUtterance, new(0, 4), "wake body",
                    [new LexicalRun("wake", new(0, 2)), new LexicalRun("body", new(2, 4))]), _ => [1, 2], 4);
            }
            else Check(session.StartAutomatic(new(0, 2), new(rule, 4), 2), "automatic session starts");
            return session.Finish(FinishReason.FinishCommand, _ => ImmutableArray.Create<short>(1, 2))
                ?? throw new InvalidOperationException("recording did not finish");
        }
        try
        {
            foreach (var rule in Enum.GetValues<WakeRule>())
            foreach (var enabled in new bool?[] { null, false, true })
            {
                var audio = Record(Load(enabled), rule);
                var history = new DictationHistory();
                var changed = 0;
                history.Changed += () => changed++;
                var pastes = new List<string>();
                var transcripts = 0;
                var wrapper = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) =>
                {
                    transcripts++;
                    return Task.FromResult(" あっ。 \n");
                }, history, (text, _) => { pastes.Add(text); return true; });
                var result = await wrapper.SubmitAsync(audio, CancellationToken.None);
                var expected = enabled == true && rule != WakeRule.Strict ? null : " あっ。 \n";
                Check(result.Status == HandoffStatus.RecordedLocally, "discard is successful processing");
                Check(history.Select(audio.SessionId) == expected, "config-driven interjection admission");
                Check(history.Snapshot().Select(entry => entry.Text).SequenceEqual(expected is null ? [] : new[] { expected }), "config-driven exact history");
                Check(changed == (expected is null ? 0 : 1), "discard has no history notification");
                Check(pastes.SequenceEqual(rule == WakeRule.Strict ? new[] { " あっ。 \n" } : []), "strict paste and automatic non-paste");
                Check(transcripts == 1, "local transcript requested once");
            }
            foreach (var enabled in new[] { false, true })
            {
                var audio = Record(Load(enabled), WakeRule.AutoB);
                var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var history = new DictationHistory();
                var wrapper = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => pending.Task, history);
                var result = wrapper.SubmitAsync(audio, CancellationToken.None);
                var next = Record(Load(!enabled), WakeRule.AutoB);
                pending.SetResult("ああ");
                await result;
                Check(history.Select(audio.SessionId) == (enabled ? null : "ああ"), "pending result keeps captured configuration");
                var nextWrapper = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => Task.FromResult("ああ"), history);
                await nextWrapper.SubmitAsync(next, CancellationToken.None);
                Check(history.Select(next.SessionId) == (enabled ? "ああ" : null), "next recording uses reloaded configuration");
            }
            var guarded = new DictationHistory();
            var retainedAudio = Record(Load(true), WakeRule.AutoB);
            var complete = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => Task.FromResult(" はい。\n"), guarded);
            await complete.SubmitAsync(retainedAudio, CancellationToken.None);
            Check(guarded.Select(retainedAudio.SessionId) == " はい。\n", "retained transcript is unchanged");
            var duplicate = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => Task.FromResult("うん"), guarded);
            await duplicate.SubmitAsync(retainedAudio, CancellationToken.None);
            Check(guarded.Select(retainedAudio.SessionId) == " はい。\n", "duplicate keeps original text");
            var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldAudio = Record(Load(true), WakeRule.AutoB);
            var old = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => delayed.Task, guarded).SubmitAsync(oldAudio, CancellationToken.None);
            guarded.Clear();
            delayed.SetResult("はい");
            await old;
            Check(guarded.Snapshot().Count == 0, "clear still invalidates pending result");
            using var cancellation = new CancellationTokenSource();
            var cancelledText = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new LocalHistoryHandoff(new UnavailableHandoff(), (_, _) => cancelledText.Task, guarded).SubmitAsync(oldAudio, cancellation.Token);
            cancellation.Cancel();
            cancelledText.SetResult("はい");
            try { await cancelled; throw new InvalidOperationException("cancelled result completed"); }
            catch (OperationCanceledException) { Check(guarded.Snapshot().Count == 0, "cancelled result stays out of history"); }
            Console.WriteLine($"PASS config-to-local-handoff {assertions} assertions");
        }
        finally { File.Delete(path); }
    }

    private sealed class UnavailableHandoff : IDictationHandoff
    {
        public Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation) =>
            Task.FromResult(new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "test provider unavailable"));
    }
}
