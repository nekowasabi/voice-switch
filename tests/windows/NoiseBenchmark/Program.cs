using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

var options = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var fixturesOut = GetOption(args, "--fixtures-out");
var cleanDir = GetOption(args, "--clean-dir");
var dspOut = GetOption(args, "--dsp-out");

if (fixturesOut is null && dspOut is null)
{
    throw new ArgumentException("specify --fixtures-out DIR and/or --dsp-out PATH.");
}

if (fixturesOut is not null)
{
    if (cleanDir is null)
    {
        throw new ArgumentException("--fixtures-out requires --clean-dir.");
    }

    Directory.CreateDirectory(fixturesOut);
    var manifest = GenerateFixtures(cleanDir, fixturesOut);
    await File.WriteAllTextAsync(Path.Combine(fixturesOut, "noise-fixtures.manifest.json"), JsonSerializer.Serialize(manifest, options));
}

if (dspOut is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dspOut))!);
    var result = RunDspBenchmark();
    await File.WriteAllTextAsync(dspOut, JsonSerializer.Serialize(result, options));
    Console.WriteLine($"DSP p95Per480Ms={result.P95Milliseconds:F4} allocations={result.AllocatedBytes} calibrated={result.Status.Calibrated}");
}

static string? GetOption(string[] values, string name)
{
    for (var i = 0; i < values.Length - 1; i++)
    {
        if (values[i] == name)
        {
            return values[i + 1];
        }
    }

    return null;
}

static FixtureManifest GenerateFixtures(string cleanDir, string outputDir)
{
    var cases = new List<FixtureCase>();
    foreach (var json in Directory.EnumerateFiles(cleanDir, "*-clean.json").Order(StringComparer.OrdinalIgnoreCase))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(json));
        var root = doc.RootElement;
        var wav = root.GetProperty("wav").GetString() ?? Path.ChangeExtension(json, ".wav");
        if (!OperatingSystem.IsWindows() && wav.Length > 2 && wav[1] == ':' && (wav[2] == '\\' || wav[2] == '/'))
        {
            wav = "/mnt/" + char.ToLowerInvariant(wav[0]) + "/" + wav[3..].Replace('\\', '/');
        }
        else if (!Path.IsPathRooted(wav))
        {
            wav = Path.Combine(Path.GetDirectoryName(json)!, wav);
        }

        var clean = Pcm16Wav.DecodeStrict(new FileInfo(wav), 600 * Pcm16Wav.Rate).ToArray();
        var segments = root.GetProperty("segments").EnumerateArray()
            .Select(item => new SegmentSpec(
                item.GetProperty("kind").GetString() ?? "",
                item.TryGetProperty("text", out var text) ? text.GetString() : null,
                item.GetProperty("start").GetInt32(),
                item.GetProperty("end").GetInt32()))
            .ToArray();
        var mask = SpeechMask(clean.Length, segments);
        var cleanCaseId = $"{root.GetProperty("name").GetString()}-clean-control";
        var cleanPath = Path.Combine(outputDir, cleanCaseId + ".wav");
        File.WriteAllBytes(cleanPath, Pcm16Wav.Encode(clean));
        cases.Add(WriteExpectation(outputDir, cleanCaseId, cleanPath, clean, null, null, null, segments, mask, commonScale: 1.0));

        foreach (var seed in new uint[] { 0xA11CE001u, 0xA11CE002u, 0xA11CE003u })
        foreach (var kind in new[] { "white", "fan", "hum" })
        foreach (var snr in new[] { 20, 10, 5 })
        {
            var generated = MixNoise(clean, mask, seed, kind, snr);
            var caseId = $"{root.GetProperty("name").GetString()}-{kind}-snr{snr}-seed{seed:X8}";
            var path = Path.Combine(outputDir, caseId + ".wav");
            File.WriteAllBytes(path, Pcm16Wav.Encode(generated.Mixed));
            cases.Add(WriteExpectation(outputDir, caseId, path, generated.Mixed, kind, seed, snr, segments, mask, generated.CommonScale));
        }

        var onset = MixNoise(clean, mask, 0xBEEFu, "white-midway", 10);
        var onsetCaseId = $"{root.GetProperty("name").GetString()}-white-midway-snr10-seed0000BEEF";
        var onsetPath = Path.Combine(outputDir, onsetCaseId + ".wav");
        File.WriteAllBytes(onsetPath, Pcm16Wav.Encode(onset.Mixed));
        cases.Add(WriteExpectation(outputDir, onsetCaseId, onsetPath, onset.Mixed, "white-midway", 0xBEEFu, 10, segments, mask, onset.CommonScale));
    }

    var quiet = NoiseOnlyControl(Pcm16Wav.Rate * 2, 250, 0x51A7E001u);
    var quietCase = Path.Combine(outputDir, "quiet-noise-control.wav");
    File.WriteAllBytes(quietCase, Pcm16Wav.Encode(quiet));
    var quietContractPath = Path.Combine(outputDir, "quiet-noise-control.harness-expectation.json");
    WriteHarnessExpectation(quietContractPath, new HarnessExpectation(true, 0, [], null));
    cases.Add(new FixtureCase("quiet-noise-control", quietCase, Path.ChangeExtension(quietCase, ".expectation.json"), Pcm16Wav.Sha256Hex(quiet), "white-noise-only-amplitude250", 0x51A7E001u, null, 1.0, [], quietContractPath));
    File.WriteAllText(Path.ChangeExtension(quietCase, ".expectation.json"), JsonSerializer.Serialize(cases[^1], new JsonSerializerOptions { WriteIndented = true }));
    return new FixtureManifest(DateTimeOffset.UtcNow, "xorshift32-box-muller", cases.ToArray());
}

static short[] NoiseOnlyControl(int length, int amplitude, uint seed)
{
    var rng = new XorShift32(seed);
    var samples = new short[length];
    for (var i = 0; i < samples.Length; i++)
    {
        samples[i] = (short)Math.Round(Math.Clamp(rng.NextGaussian() * amplitude, short.MinValue, short.MaxValue), MidpointRounding.AwayFromZero);
    }

    return samples;
}

static bool[] SpeechMask(int length, SegmentSpec[] segments)
{
    var mask = new bool[length];
    foreach (var segment in segments.Where(item => item.Kind == "speech"))
    {
        for (var i = Math.Max(0, segment.Start); i < Math.Min(length, segment.End); i++)
        {
            mask[i] = true;
        }
    }

    return mask;
}

static GeneratedMix MixNoise(short[] clean, bool[] mask, uint seed, string kind, int snr)
{
    var rng = new XorShift32(seed);
    var noise = new double[clean.Length];
    for (var i = 0; i < noise.Length; i++)
    {
        var active = kind.EndsWith("-midway", StringComparison.Ordinal) ? i >= noise.Length / 2 : true;
        noise[i] = active ? SampleNoise(kind, i, ref rng) : 0.0;
    }

    if (kind.StartsWith("fan", StringComparison.Ordinal))
    {
        var lowpass = 0.0;
        for (var i = 0; i < noise.Length; i++)
        {
            lowpass = 0.985 * lowpass + 0.015 * noise[i];
            noise[i] = lowpass;
        }
    }

    NormalizeOnMask(noise, mask);
    var speechRms = Math.Sqrt(clean.Where((_, i) => mask[i]).Select(sample => (sample / 32768.0) * (sample / 32768.0)).DefaultIfEmpty(0).Average());
    var noiseRms = speechRms * Math.Pow(10.0, -snr / 20.0);
    var mixed = new double[clean.Length];
    var peak = 0.0;
    for (var i = 0; i < mixed.Length; i++)
    {
        mixed[i] = clean[i] / 32768.0 + noise[i] * noiseRms;
        peak = Math.Max(peak, Math.Abs(mixed[i]));
    }

    var scale = peak > 32767.0 / 32768.0 ? (32767.0 / 32768.0) / peak : 1.0;
    var pcm = new short[mixed.Length];
    for (var i = 0; i < pcm.Length; i++)
    {
        pcm[i] = (short)Math.Round(mixed[i] * scale * 32768.0, MidpointRounding.AwayFromZero);
    }

    return new GeneratedMix(pcm, scale);
}

static double SampleNoise(string kind, int i, ref XorShift32 rng) =>
    kind.StartsWith("hum", StringComparison.Ordinal)
        ? 0.7 * Math.Sin(2.0 * Math.PI * 50.0 * i / Pcm16Wav.Rate)
            + 0.2 * Math.Sin(2.0 * Math.PI * 100.0 * i / Pcm16Wav.Rate)
            + 0.1 * Math.Sin(2.0 * Math.PI * 150.0 * i / Pcm16Wav.Rate)
        : rng.NextGaussian();

static void NormalizeOnMask(double[] values, bool[] mask)
{
    if (values.Length == 0)
    {
        return;
    }

    var rms = Math.Sqrt(values.Where((_, i) => mask[i]).Select(value => value * value).DefaultIfEmpty(0).Average());
    if (rms <= 0)
    {
        return;
    }

    for (var i = 0; i < values.Length; i++)
    {
        values[i] /= rms;
    }
}

static FixtureCase WriteExpectation(string outputDir, string caseId, string wav, short[] samples, string? noiseKind, uint? seed, int? snr, SegmentSpec[] segments, bool[] mask, double commonScale)
{
    var expectationPath = Path.Combine(outputDir, caseId + ".expectation.json");
    var expectedText = segments.Where(item => item.Kind == "speech" && !string.IsNullOrWhiteSpace(item.Text)).Select(item => item.Text!).ToArray();
    var harnessExpectationPath = Path.Combine(outputDir, caseId + ".harness-expectation.json");
    WriteHarnessExpectation(harnessExpectationPath, HarnessExpectationFor(caseId));
    var fixtureCase = new FixtureCase(caseId, wav, expectationPath, Pcm16Wav.Sha256Hex(samples), noiseKind, seed, snr, commonScale, expectedText, harnessExpectationPath);
    File.WriteAllText(expectationPath, JsonSerializer.Serialize(fixtureCase, new JsonSerializerOptions { WriteIndented = true }));
    return fixtureCase;
}

static HarnessExpectation HarnessExpectationFor(string caseId)
{
    var name = caseId.Split('-').TakeWhile(part => part is not "clean" and not "white" and not "fan" and not "hum").ToArray();
    var fixture = string.Join('-', name);
    return fixture switch
    {
        "wake-body-separate-stop" => new HarnessExpectation(true, 1, [FinishReason.StandaloneStop.ToString()], null),
        "embedded-stop" => new HarnessExpectation(true, 1, [FinishReason.Silence.ToString()], null),
        "natural-one-breath" => new HarnessExpectation(true, 1, [FinishReason.StandaloneStop.ToString()], null),
        "consecutive-sessions" => new HarnessExpectation(true, 2, [FinishReason.StandaloneStop.ToString(), FinishReason.StandaloneStop.ToString()], null),
        "intentional-silence" => new HarnessExpectation(true, 0, [], null),
        "late-stop-after-silence" => new HarnessExpectation(true, 1, [FinishReason.Silence.ToString()], null),
        _ => new HarnessExpectation(true, null, null, null)
    };
}

static void WriteHarnessExpectation(string path, HarnessExpectation expectation) =>
    File.WriteAllText(path, JsonSerializer.Serialize(expectation, new JsonSerializerOptions { WriteIndented = true }));

static DspBenchmarkResult RunDspBenchmark()
{
    var frames = new List<AnalysisFrame>();
    var processor = new NoiseProcessor(new NoiseReductionOptions(NoiseReductionMode.ConservativeWiener), frames.Add, 0.005);
    var block = new short[Segmenter.FrameLength];
    var elapsed = new List<double>();
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    for (var frame = 0; frame < 2200; frame++)
    {
        for (var i = 0; i < block.Length; i++)
        {
            var quiet = frame < 80 ? 80 * Math.Sin((frame * block.Length + i) * 2.0 * Math.PI / 53.0) : 0;
            var speech = frame >= 100 ? 4000 * Math.Sin((frame * block.Length + i) * 2.0 * Math.PI / 101.0) : 0;
            block[i] = (short)Math.Round(quiet + speech, MidpointRounding.AwayFromZero);
        }

        var sw = Stopwatch.StartNew();
        processor.Push(new PcmFrame(frame * Segmenter.FrameLength, block.ToImmutableArray()));
        sw.Stop();
        if (frame >= 200)
        {
            elapsed.Add(sw.Elapsed.TotalMilliseconds);
        }
    }

    processor.Complete();
    var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
    elapsed.Sort();
    return new DspBenchmarkResult(
        DateTimeOffset.UtcNow,
        elapsed.Count,
        Percentile(elapsed, 0.50),
        Percentile(elapsed, 0.95),
        Percentile(elapsed, 0.99),
        allocated,
        GC.GetTotalMemory(forceFullCollection: true),
        processor.Status,
        "proposed p95 < 3 ms per 480 samples; 30 ms publication lag is expected for 480-sample framing");
}

static double Percentile(IReadOnlyList<double> sorted, double p)
{
    if (sorted.Count == 0)
    {
        return 0;
    }

    var index = (int)Math.Ceiling(sorted.Count * p) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
}

sealed record FixtureManifest(DateTimeOffset CreatedAt, string Prng, FixtureCase[] Cases);
sealed record FixtureCase(string CaseId, string WavPath, string ExpectationPath, string WavSha256, string? NoiseKind, uint? Seed, int? SnrDb, double CommonScale, string[] ExpectedJapaneseSpeech, string HarnessExpectationPath);
sealed record HarnessExpectation(bool? ExpectExitSuccess, int? ExpectedOutputCount, string[]? ExpectedFinishReasons, object[]? ExpectedBodies);
sealed record SegmentSpec(string Kind, string? Text, int Start, int End);
sealed record GeneratedMix(short[] Mixed, double CommonScale);
sealed record DspBenchmarkResult(DateTimeOffset CreatedAt, int Samples, double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, long AllocatedBytes, long ManagedMemoryBytes, NoiseProcessorStatus Status, string Target);

struct XorShift32
{
    private uint state;
    private bool hasSpare;
    private double spare;

    public XorShift32(uint seed)
    {
        state = seed == 0 ? 1u : seed;
        hasSpare = false;
        spare = 0;
    }

    public double NextGaussian()
    {
        if (hasSpare)
        {
            hasSpare = false;
            return spare;
        }

        var u1 = Math.Max((NextUInt() + 0.5) / 4294967296.0, double.Epsilon);
        var u2 = (NextUInt() + 0.5) / 4294967296.0;
        var radius = Math.Sqrt(-2.0 * Math.Log(u1));
        var theta = 2.0 * Math.PI * u2;
        spare = radius * Math.Sin(theta);
        hasSpare = true;
        return radius * Math.Cos(theta);
    }

    private uint NextUInt()
    {
        var x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x == 0 ? 1u : x;
        return state;
    }
}
