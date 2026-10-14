using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed class LocalHistoryHandoff(
    IDictationHandoff manual,
    Func<DictationAudio, CancellationToken, Task<string>> transcribeLocal,
    DictationHistory? history = null,
    Func<string, nint, bool>? pasteManualFallback = null) : IDictationHandoff
{
    private readonly DictationHistory history = history ?? DictationHistory.Shared;

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        var result = audio.Wake.IsAutomatic && audio.Route == AutomaticRoute.Local
            ? new HandoffResult(HandoffStatus.FailedBeforeDispatch, audio.SessionId, null, "user still operating")
            : await manual.SubmitAsync(audio, cancellation);
        if (result.Status != HandoffStatus.FailedBeforeDispatch || audio.Action is not null
            || !audio.Wake.IsAutomatic && pasteManualFallback is null) return result;
        cancellation.ThrowIfCancellationRequested();
        var generation = history.Generation;
        var text = await transcribeLocal(audio, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var retained = history.Add(audio.SessionId, text, audio.Wake, generation, audio.TranscriptPolicy);
        if (retained && !audio.Wake.IsAutomatic) pasteManualFallback?.Invoke(text, audio.Target);
        return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null,
            retained ? "local transcript added to history" : "local transcript discarded");
    }

    public static IDictationHandoff Wrap(IDictationHandoff manual, VoiceSwitchConfig config, Func<string, nint, bool>? pasteManualFallback = null) =>
        new LocalHistoryHandoff(manual, async (audio, cancellation) =>
        {
            await using var recognizer = new SpeechPowerShellDictationRecognizer(config);
            var result = await recognizer.RecognizeAsync(new RecognitionRequest(1, RecognitionExtent.ClosedUtterance,
                new SampleRange(0, audio.Samples.Length), audio.Samples), cancellation);
            return result.Text;
        }, pasteManualFallback: pasteManualFallback);
}
