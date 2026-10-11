using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed class LocalHistoryHandoff(
    IDictationHandoff manual,
    Func<DictationAudio, CancellationToken, Task<string>> transcribeLocal,
    DictationHistory? history = null) : IDictationHandoff
{
    private readonly DictationHistory history = history ?? DictationHistory.Shared;

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        if (!audio.Wake.IsAutomatic) return await manual.SubmitAsync(audio, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var text = await transcribeLocal(audio, cancellation);
        cancellation.ThrowIfCancellationRequested();
        var retained = history.Add(audio.SessionId, text, audio.Wake);
        return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, null,
            retained ? "local transcript added to history" : "empty or duplicate local transcript discarded");
    }

    public static IDictationHandoff Wrap(IDictationHandoff manual, VoiceSwitchConfig config) =>
        new LocalHistoryHandoff(manual, async (audio, cancellation) =>
        {
            await using var recognizer = new SpeechPowerShellDictationRecognizer(config);
            var result = await recognizer.RecognizeAsync(new RecognitionRequest(1, RecognitionExtent.ClosedUtterance,
                new SampleRange(0, audio.Samples.Length), audio.Samples), cancellation);
            return result.Text;
        });
}
