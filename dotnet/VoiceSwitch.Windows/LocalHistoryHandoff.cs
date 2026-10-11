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
        if (!audio.Wake.IsAutomatic)
        {
            var result = await manual.SubmitAsync(audio, cancellation);
            if (result.Status != HandoffStatus.FailedBeforeDispatch || audio.Action is not null || pasteManualFallback is null) return result;
        }
        cancellation.ThrowIfCancellationRequested();
        var generation = history.Generation;
        var text = await transcribeLocal(audio, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var retained = history.Add(audio.SessionId, text, audio.Wake, generation);
        if (retained && !audio.Wake.IsAutomatic) pasteManualFallback?.Invoke(text, audio.Target);
        return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null,
            retained ? "local transcript added to history" : "empty or duplicate local transcript discarded");
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
