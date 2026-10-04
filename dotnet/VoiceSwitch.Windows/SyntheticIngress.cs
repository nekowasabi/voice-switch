using System.Collections.Immutable;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public sealed class WavPcmCapture : IPcmCapture
{
    public const int DefaultFrameSamples = Segmenter.FrameLength;
    public const int DefaultPaceMilliseconds = 30;
    public const int DefaultMaxInputSeconds = 600;
    private readonly ImmutableArray<short> samples;
    private readonly bool paced;
    private bool disposed;

    public WavPcmCapture(string path, bool paced, int maxInputSeconds = DefaultMaxInputSeconds)
    {
        if (maxInputSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInputSeconds));
        }

        samples = Pcm16Wav.DecodeStrict(new FileInfo(path), checked(maxInputSeconds * (int)Segmenter.Rate));
        this.paced = paced;
    }

    public async IAsyncEnumerable<PcmFrame> ReadFramesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
    {
        for (var offset = 0; offset < samples.Length; offset += DefaultFrameSamples)
        {
            cancellation.ThrowIfCancellationRequested();
            var length = Math.Min(DefaultFrameSamples, samples.Length - offset);
            yield return new PcmFrame(offset, samples.AsSpan(offset, length).ToArray().ToImmutableArray());
            if (paced && offset + length < samples.Length)
            {
                await Task.Delay(DefaultPaceMilliseconds, cancellation);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ValueTask.CompletedTask;
    }

    internal bool DisposedForTest => disposed;
}

public sealed class LocalRecordingHandoff : IDictationHandoff
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string outputDir;
    private readonly string? sourcePath;

    public LocalRecordingHandoff(string outputDir, string? sourcePath = null)
    {
        this.outputDir = outputDir;
        this.sourcePath = sourcePath;
    }

    public async Task<HandoffResult> SubmitAsync(DictationAudio audio, CancellationToken cancellation)
    {
        Directory.CreateDirectory(outputDir);
        var wavBytes = Pcm16Wav.Encode(audio.Samples.AsSpan());
        var stem = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{audio.SessionId:N}";
        var wavPath = Path.Combine(outputDir, stem + ".wav");
        var jsonPath = Path.Combine(outputDir, stem + ".json");
        await File.WriteAllBytesAsync(wavPath, wavBytes, cancellation);
        var metadata = new LocalRecordingMetadata(
            audio.SessionId,
            DateTimeOffset.UtcNow,
            sourcePath,
            wavPath,
            audio.Range.Start,
            audio.Range.End,
            audio.Samples.Length,
            Pcm16Wav.Sha256Hex(audio.Samples.AsSpan()),
            Pcm16Wav.Sha256Hex(wavBytes),
            audio.Reason.ToString());
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(metadata, JsonOptions), cancellation);
        return new HandoffResult(HandoffStatus.RecordedLocally, audio.SessionId, wavPath, $"recorded WAV and metadata to {outputDir}");
    }

    private sealed record LocalRecordingMetadata(
        Guid Id,
        DateTimeOffset CreatedAt,
        string? SourcePath,
        string WavPath,
        long SourceStart,
        long SourceEnd,
        int SampleCount,
        string SourcePcmSha256,
        string OutputWavSha256,
        string FinishReason);
}
