using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed class SpeechPowerShellDictationRecognizer : IAsyncDisposable
{
    public SpeechPowerShellDictationRecognizer(VoiceSwitchConfig config) =>
        throw new PlatformNotSupportedException("The portable policy test must inject its transcription delegate.");

    public Task<RecognizedUtterance> RecognizeAsync(RecognitionRequest request, CancellationToken cancellation) =>
        throw new PlatformNotSupportedException("The portable policy test cannot run SAPI.");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
