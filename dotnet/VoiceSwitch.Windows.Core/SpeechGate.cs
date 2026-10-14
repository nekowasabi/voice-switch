using System.Collections.Immutable;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceSwitch.Windows.Core;

/// Silero VAD v5 over a whole 16 kHz recording. macOS runs the same model and rule in SpeechGate.swift.
public static class SpeechGate
{
    public const float Threshold = 0.5f;
    private const int Window = 512;
    private const int Context = 64;

    private static readonly Lazy<InferenceSession> Session = new(() =>
    {
        using var stream = typeof(SpeechGate).Assembly.GetManifestResourceStream("silero_vad.onnx")
            ?? throw new InvalidOperationException("silero_vad.onnx missing from VoiceSwitch.Windows.Core");
        using var model = new MemoryStream();
        stream.CopyTo(model);
        using var options = new SessionOptions { IntraOpNumThreads = 1 };
        return new InferenceSession(model.ToArray(), options);
    });
    private static readonly object Gate = new();

    /// Number of 32 ms windows Silero scores at or above Threshold; a recording with none holds no voice.
    public static int VoicedWindows(ImmutableArray<short> samples)
    {
        lock (Gate)
        {
            var session = Session.Value;
            var state = new float[2 * 128];
            var input = new float[Context + Window];
            var sr = new DenseTensor<long>(new[] { 16000L }, new[] { 1 });
            var voiced = 0;
            for (var start = 0; start < samples.Length; start += Window)
            {
                // Silero expects each window prefixed with the previous window's last 64 samples; a short tail is zero-padded.
                Array.Copy(input, Window, input, 0, Context);
                var count = Math.Min(Window, samples.Length - start);
                for (var i = 0; i < Window; i++)
                    input[Context + i] = i < count ? samples[start + i] / 32768f : 0f;
                using var results = session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>((float[])input.Clone(), new[] { 1, Context + Window })),
                    NamedOnnxValue.CreateFromTensor("state", new DenseTensor<float>(state, new[] { 2, 1, 128 })),
                    NamedOnnxValue.CreateFromTensor("sr", sr),
                });
                if (results.First(r => r.Name == "output").AsEnumerable<float>().First() >= Threshold) voiced++;
                state = results.First(r => r.Name == "stateN").AsEnumerable<float>().ToArray();
            }
            return voiced;
        }
    }
}
