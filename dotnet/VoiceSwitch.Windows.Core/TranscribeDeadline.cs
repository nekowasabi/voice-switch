namespace VoiceSwitch.Windows.Core;

/// <summary>
/// Shared STT wall-clock budget: max(10, audioSeconds + 20).
/// Matches Mac <c>transcribeDeadlineSeconds</c> and RESEARCH hyp 2.
/// </summary>
public static class TranscribeDeadline
{
    /// <summary>Seconds allowed for one recognition given audio duration in seconds.</summary>
    public static double Seconds(double audioSeconds) => Math.Max(10, audioSeconds + 20);

    /// <summary>16-bit mono PCM at 16 kHz is 32_000 bytes per second of audio.</summary>
    public static double SecondsFromPcmBytes(int pcmByteLength) => Seconds(pcmByteLength / 32000.0);
}
