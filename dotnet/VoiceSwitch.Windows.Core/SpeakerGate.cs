using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceSwitch.Windows.Core;

public static class SpeakerGate
{
    public const string ModelName = "3dspeaker_speech_campplus_sv_zh-cn_16k-common.onnx";
    // Measured 2026-10-16 on the user's superwhisper recordings vs FLEURS ja, CAM++ with a 5-clip mean enrollment:
    // ~1 s does not separate (EER 14-18 %), 3 s gives EER 2.9 % at 0.295, 6 s 0 % at 0.300. Shorter audio is not judged.
    public const double DefaultThreshold = 0.30;
    public const int MinSamples = 3 * 16000;
    public static string ModelPath => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "voice-switch", "models", ModelName);
    private static readonly object Gate = new();
    private static readonly Lazy<InferenceSession> Session = new(() =>
    {
        using var options = new SessionOptions { IntraOpNumThreads = 1 };
        return new InferenceSession(ModelPath, options);
    });
    public sealed record Enrollment(string Model, int SampleRate, string[] Sources, float[] Embedding);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static bool Accepts(ImmutableArray<short> samples, VoiceSwitchConfig config, Action<string>? log = null)
    {
        if (config.SpeakerEnrollment is not { } path) return true;
        log ??= Console.WriteLine;
        if (samples.Length < MinSamples)
        {
            log($"speaker gate: skipped, {samples.Length * 1000L / 16000} ms is under 3000 ms");
            return true;
        }
        var stage = "enrollment";
        try
        {
            var (enrolled, threshold) = Load(path, config, ref stage);
            var began = Stopwatch.GetTimestamp();
            var vector = Embedding(samples.Select(x => x / 32768f).ToArray());
            var ms = (long)Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            var score = Cosine(vector, enrolled);
            log(FormattableString.Invariant($"speaker gate: score={score:0.000} ms={ms}"));
            return score >= threshold;
        }
        catch (Exception error)
        {
            log($"speaker gate: {stage} unavailable, passing through: {error.Message}");
            return true;
        }
    }

    // The samples without each pause-delimited segment of 2 s+ that scores as another speaker; everything kept is untouched.
    // Measured 2026-10-16 (30 own+FLEURS+own mixes, 30 own-only clips): pauses of 400 ms, 2 s segments and 0.30 removed
    // a mean 86 % of the other voice, 0.1 % of own speech in mixes and none of the own-only clips.
    public static ImmutableArray<short> WithoutOtherSpeakers(ImmutableArray<short> samples, VoiceSwitchConfig config, Action<string>? log = null)
    {
        if (config.SpeakerEnrollment is not { } path) return samples;
        log ??= Console.WriteLine;
        var stage = "enrollment";
        List<(int Start, int End)> cuts;
        try
        {
            var (enrolled, threshold) = Load(path, config, ref stage);
            var floats = samples.Select(x => x / 32768f).ToArray();
            cuts = Segments(floats).Where(s => s.End - s.Start >= 2 * 16000
                && Cosine(Embedding(floats[s.Start..s.End]), enrolled) < threshold).ToList();
        }
        catch (Exception error)
        {
            log($"speaker gate: {stage} unavailable, passing through: {error.Message}");
            return samples;
        }
        if (cuts.Count == 0) return samples;
        var removed = cuts.Sum(c => c.End - c.Start);
        static long Ms(int count) => count * 1000L / 16000;
        // The user asked for this recording, so a cut that leaves almost nothing keeps all of it instead.
        if (samples.Length - removed < 16000)
        {
            log($"speaker gate: kept all {Ms(samples.Length)} ms, cutting {cuts.Count} segments would leave {Ms(samples.Length - removed)} ms");
            return samples;
        }
        log($"speaker gate: cut {Ms(removed)} ms of {Ms(samples.Length)} ms in {cuts.Count} segments");
        var kept = ImmutableArray.CreateBuilder<short>(samples.Length - removed);
        var at = 0;
        foreach (var (start, end) in cuts) { kept.AddRange(samples[at..start]); at = end; }
        kept.AddRange(samples[at..]);
        return kept.MoveToImmutable();
    }

    // 30 ms frames are voiced above a tenth of the 95th-percentile frame RMS; 400 ms unvoiced ends a segment.
    // Keep in step with SpeakerGate.swift; the measurement ran this rule in numpy (linear percentile).
    public static List<(int Start, int End)> Segments(float[] samples)
    {
        const int f = 480;
        var n = samples.Length / f;
        var segments = new List<(int Start, int End)>();
        if (n == 0) return segments;
        var rms = new double[n];
        for (var i = 0; i < n; i++)
        {
            var sum = 0.0;
            for (var k = i * f; k < (i + 1) * f; k++) sum += (double)samples[k] * samples[k];
            rms[i] = Math.Sqrt(sum / f);
        }
        var sorted = rms.Order().ToArray();
        var h = 0.95 * (n - 1); var lo = (int)h; var t = h - lo;
        var a = sorted[lo]; var b = sorted[Math.Min(lo + 1, n - 1)];
        var p95 = t < 0.5 ? a + (b - a) * t : b - (b - a) * (1 - t);
        int? start = null; var quiet = 0;
        for (var i = 0; i < n; i++)
        {
            if (rms[i] > 0.1 * p95)
            {
                start ??= i;
                quiet = 0;
            }
            else if (start is { } s)
            {
                quiet++;
                if (quiet * 30 >= 400) { segments.Add((s * f, (i - quiet + 1) * f)); start = null; }
            }
        }
        if (start is { } open) segments.Add((open * f, n * f));
        return segments;
    }

    private static (float[] Embedding, double Threshold) Load(string path, VoiceSwitchConfig config, ref string stage)
    {
        var enrolled = JsonSerializer.Deserialize<Enrollment>(File.ReadAllText(WindowsPaths.ExpandPath(path)), Json)
            ?? throw new InvalidDataException("empty enrollment");
        if (enrolled.Model != ModelName || enrolled.SampleRate != 16000)
            throw new InvalidDataException("model or sample rate mismatch");
        stage = "model";
        if (!File.Exists(ModelPath)) throw new FileNotFoundException(ModelPath);
        _ = Session.Value;
        stage = "speakerThreshold";
        var threshold = config.SpeakerThreshold ?? DefaultThreshold;
        if (!double.IsFinite(threshold) || threshold < -1 || threshold > 1)
            throw new InvalidDataException("invalid threshold");
        stage = "inference";
        return (enrolled.Embedding, threshold);
    }

    // Mean of the unit per-file embeddings, itself scaled to unit length.
    public static void Enroll(string output, IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) throw new ArgumentException("no input WAV");
        double[]? sum = null;
        foreach (var input in inputs)
        {
            var samples = Pcm16Wav.DecodeStrict(new FileInfo(input), int.MaxValue / 2).Select(x => x / 32768f).ToArray();
            if (samples.Length < MinSamples) throw new InvalidDataException($"{input} is under 3 s; short audio does not separate speakers");
            var vector = Embedding(samples);
            _ = Cosine(vector, vector);
            var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
            sum ??= new double[vector.Length];
            if (sum.Length != vector.Length) throw new InvalidDataException("embedding dimension mismatch");
            for (var i = 0; i < vector.Length; i++) sum[i] += vector[i] / norm;
        }
        var total = Math.Sqrt(sum!.Sum(x => x * x));
        var mean = sum!.Select(x => (float)(x / total)).ToArray();
        var data = JsonSerializer.Serialize(new Enrollment(ModelName, 16000, inputs.ToArray(), mean), Json);
        var temp = output + "." + Guid.NewGuid().ToString("N") + ".part";
        try { File.WriteAllText(temp, data); File.Move(temp, output, overwrite: true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) throw new InvalidDataException("embedding dimension mismatch");
        double dot = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (!float.IsFinite(a[i]) || !float.IsFinite(b[i])) throw new InvalidDataException("nonfinite embedding");
            dot += (double)a[i] * b[i]; aa += (double)a[i] * a[i]; bb += (double)b[i] * b[i];
        }
        if (aa <= 0 || bb <= 0) throw new InvalidDataException("zero embedding");
        var score = dot / Math.Sqrt(aa * bb);
        if (!double.IsFinite(score)) throw new InvalidDataException("nonfinite score");
        return score;
    }

    public static float[] Embedding(float[] samples)
    {
        var features = Fbank(samples);
        lock (Gate)
        {
            using var results = Session.Value.Run(new[] {
                NamedOnnxValue.CreateFromTensor("x", new DenseTensor<float>(features, new[] { 1, features.Length / 80, 80 })) });
            return results.First(x => x.Name == "embedding").AsEnumerable<float>().ToArray();
        }
    }

    // Upstream defaults: reflected edges, 25/10 ms, DC removal, preemphasis .97, Povey, mel80 (20–7600 Hz), CMN.
    // Keep this algorithm in step with SpeakerGate.swift.
    public static float[] Fbank(float[] samples)
    {
        if (samples.Length < 400 || samples.Any(x => !float.IsFinite(x))) throw new InvalidDataException("audio shorter than 25 ms or nonfinite");
        var frames = (samples.Length + 80) / 160;
        var low = 1127 * Math.Log(1 + 20.0 / 700); var high = 1127 * Math.Log(1 + 7600.0 / 700);
        var points = Enumerable.Range(0, 82).Select(i => low + i * (high - low) / 81).ToArray();
        var weights = Enumerable.Range(0, 80).Select(_ => new List<(int Bin, double Weight)>()).ToArray();
        for (var bin = 1; bin < 256; bin++)
        {
            var mel = 1127 * Math.Log(1 + (double)bin * 16000 / 512 / 700);
            for (var m = 0; m < 80; m++)
                if (mel > points[m] && mel < points[m + 2])
                    weights[m].Add((bin, mel <= points[m + 1] ? (mel - points[m]) / (points[m + 1] - points[m]) : (points[m + 2] - mel) / (points[m + 2] - points[m + 1])));
        }
        var window = Enumerable.Range(0, 400).Select(i => Math.Pow(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 399), 0.85)).ToArray();
        var features = new float[frames * 80];
        for (var frame = 0; frame < frames; frame++)
        {
            var real = new double[512]; var imag = new double[512];
            var offset = frame * 160 - 120;
            var input = Enumerable.Range(0, 400).Select(i => {
                var index = offset + i;
                while (index < 0 || index >= samples.Length)
                    index = index < 0 ? -index - 1 : 2 * samples.Length - 1 - index;
                return (double)samples[index];
            }).ToArray();
            var mean = input.Sum() / 400;
            for (var i = 0; i < 400; i++)
            {
                var x = input[i] - mean;
                var previous = input[Math.Max(0, i - 1)] - mean;
                real[i] = (x - 0.97 * previous) * window[i];
            }
            var j = 0;
            for (var i = 1; i < 512; i++)
            {
                var bit = 256;
                while ((j & bit) != 0) { j ^= bit; bit >>= 1; }
                j ^= bit;
                if (i < j) (real[i], real[j]) = (real[j], real[i]);
            }
            for (var size = 2; size <= 512; size *= 2)
            {
                var angle = -2 * Math.PI / size;
                for (var start = 0; start < 512; start += size)
                    for (var k = 0; k < size / 2; k++)
                    {
                        var c = Math.Cos(angle * k); var s = Math.Sin(angle * k);
                        var a = start + k; var b = a + size / 2;
                        var re = real[b] * c - imag[b] * s; var im = real[b] * s + imag[b] * c;
                        real[b] = real[a] - re; imag[b] = imag[a] - im;
                        real[a] += re; imag[a] += im;
                    }
            }
            for (var m = 0; m < 80; m++)
            {
                var energy = weights[m].Sum(w => (real[w.Bin] * real[w.Bin] + imag[w.Bin] * imag[w.Bin]) * w.Weight);
                features[frame * 80 + m] = (float)Math.Log(Math.Max(1.1920928955078125e-7, energy));
            }
        }
        for (var m = 0; m < 80; m++)
        {
            var mean = Enumerable.Range(0, frames).Sum(f => (double)features[f * 80 + m]) / frames;
            for (var frame = 0; frame < frames; frame++) features[frame * 80 + m] -= (float)mean;
        }
        return features;
    }

}
