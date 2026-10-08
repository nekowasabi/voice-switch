using System.Diagnostics;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

if (args.Contains("--self-test-validation"))
{
    return await SelfTestValidation();
}

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SKIP requires native Windows SAPI");
    return 77;
}

var jsonOptions = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };
var configPath = GetOption(args, "--config")
    ?? throw new ArgumentException("--config requires a path.");
var wavPath = GetOption(args, "--wav")
    ?? throw new ArgumentException("--wav requires a path.");
var outputDir = GetOption(args, "--output-dir")
    ?? throw new ArgumentException("--output-dir requires a path.");
var evidencePath = GetOption(args, "--evidence")
    ?? Path.Combine(outputDir, "evidence.json");
var expectPath = GetOption(args, "--expect");
var fast = args.Contains("--fast");
var cancelAfterMs = GetIntOption(args, "--cancel-after-ms");

Directory.CreateDirectory(outputDir);
var config = ConfigLoader.Load(configPath);
var sourceSamples = Pcm16Wav.DecodeStrict(new FileInfo(wavPath), WavPcmCapture.DefaultMaxInputSeconds * Pcm16Wav.Rate);
var expectation = expectPath is null
    ? null
    : ReadExpectation(expectPath, jsonOptions);
var observer = new HarnessObserver(config);
using var cts = new CancellationTokenSource();
if (cancelAfterMs is int ms)
{
    cts.CancelAfter(ms);
}

var parent = Process.GetCurrentProcess();
var parentCpuBefore = parent.TotalProcessorTime;
var stopwatch = Stopwatch.StartNew();
var code = await new WindowsDictationRuntime(
        config,
        new WavPcmCapture(wavPath, paced: !fast),
        new SpeechPowerShellDictationRecognizer(config, observer.RecordSapiTiming),
        new LocalRecordingHandoff(outputDir, wavPath),
        dryRun: false,
        observer)
    .RunAsync(cts.Token);
stopwatch.Stop();
parent.Refresh();
var parentCpuMs = (long)Math.Round((parent.TotalProcessorTime - parentCpuBefore).TotalMilliseconds, MidpointRounding.AwayFromZero);

var outputFiles = Directory.EnumerateFiles(outputDir, "*.wav")
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .Select(path => new OutputFile(path, new FileInfo(path).Length, Pcm16Wav.Sha256Hex(File.ReadAllBytes(path))))
    .ToArray();
var metadataFiles = Directory.EnumerateFiles(outputDir, "*.json")
    .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(evidencePath), StringComparison.OrdinalIgnoreCase)
        && (expectPath is null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectPath), StringComparison.OrdinalIgnoreCase)))
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
var metadata = metadataFiles
    .Select(path => ReadMetadata(path, sourceSamples))
    .ToArray();
var validation = ValidateExpectation(expectation, code, metadata);
var evidence = new Evidence(
    DateTimeOffset.UtcNow,
    configPath,
    wavPath,
    Pcm16Wav.Sha256Hex(sourceSamples.AsSpan()),
    sourceSamples.Length,
    Math.Round(sourceSamples.Length / (double)Pcm16Wav.Rate, 3),
    outputDir,
    fast,
    cancelAfterMs,
    code,
    stopwatch.ElapsedMilliseconds,
    parentCpuMs,
    observer.PendingHighWater,
    observer.RetainedSampleHighWater,
    outputFiles,
    metadata,
    observer.RecognitionRequests.ToArray(),
    observer.RecognitionOutcomes.ToArray(),
    observer.Handoffs.ToArray(),
    observer.NoiseStatus,
    expectation is null ? "not-checked" : validation.Failures.Length == 0 ? "passed" : "failed",
    validation.Failures);
await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(evidence, jsonOptions));
if (validation.Failures.Length > 0)
{
    foreach (var failure in validation.Failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 2;
}

return code;

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

static int? GetIntOption(string[] values, string name)
{
    var raw = GetOption(values, name);
    return raw is null ? null : int.Parse(raw);
}

static Expectation ReadExpectation(string path, JsonSerializerOptions options)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    EnsureKnownProperties(doc.RootElement, path, ["ExpectExitSuccess", "ExpectedOutputCount", "ExpectedFinishReasons", "ExpectedBodies"]);
    if (doc.RootElement.TryGetProperty("ExpectedBodies", out var bodies) && bodies.ValueKind == JsonValueKind.Array)
    {
        foreach (var body in bodies.EnumerateArray())
        {
            EnsureKnownProperties(body, path, ["SourceStart", "SourceEnd", "SourcePcmSha256"]);
        }
    }

    return doc.RootElement.Deserialize<Expectation>(options)
        ?? throw new InvalidDataException($"expectation manifest is empty: {path}");
}

static void EnsureKnownProperties(JsonElement element, string path, string[] names)
{
    if (element.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException($"expectation manifest must be an object: {path}");
    }

    var allowed = new HashSet<string>(names, StringComparer.Ordinal);
    foreach (var property in element.EnumerateObject())
    {
        if (!allowed.Contains(property.Name))
        {
            throw new InvalidDataException($"expectation manifest contains unknown key '{property.Name}': {path}");
        }
    }
}

static MetadataEvidence ReadMetadata(string path, System.Collections.Immutable.ImmutableArray<short> sourceSamples)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;
    var sourceStart = root.GetProperty("SourceStart").GetInt64();
    var sourceEnd = root.GetProperty("SourceEnd").GetInt64();
    var wavPath = root.GetProperty("WavPath").GetString() ?? "";
    var slice = sourceSamples.AsSpan((int)sourceStart, (int)(sourceEnd - sourceStart));
    var sliceHash = Pcm16Wav.Sha256Hex(slice);
    var expectedOutputBytes = Pcm16Wav.Encode(slice);
    var actualOutputBytes = File.ReadAllBytes(wavPath);
    var actualOutputSamples = Pcm16Wav.DecodeStrict(new FileInfo(wavPath), Math.Max(1, slice.Length + 1));
    return new MetadataEvidence(
        path,
        wavPath,
        root.GetProperty("FinishReason").GetString() ?? "",
        sourceStart,
        sourceEnd,
        root.GetProperty("SampleCount").GetInt32(),
        root.GetProperty("SourcePcmSha256").GetString() ?? "",
        sliceHash,
        root.GetProperty("OutputWavSha256").GetString() ?? "",
        Pcm16Wav.Sha256Hex(expectedOutputBytes),
        Pcm16Wav.Sha256Hex(actualOutputBytes),
        actualOutputSamples.Length,
        Pcm16Wav.Sha256Hex(actualOutputSamples.AsSpan()));
}

static ValidationResult ValidateExpectation(Expectation? expectation, int code, MetadataEvidence[] metadata)
{
    if (expectation is null)
    {
        return new ValidationResult([]);
    }

    var failures = new List<string>();
    if (expectation.ExpectExitSuccess is bool exitSuccess && (code == 0) != exitSuccess)
    {
        failures.Add($"expected exit success={exitSuccess}, actual exitCode={code}");
    }

    if (expectation.ExpectedOutputCount is int outputCount && metadata.Length != outputCount)
    {
        failures.Add($"expected {outputCount} outputs, actual {metadata.Length}");
    }

    if (expectation.ExpectedFinishReasons is { } reasons
        && !metadata.Select(item => item.FinishReason).SequenceEqual(reasons, StringComparer.Ordinal))
    {
        failures.Add($"expected finish reasons [{string.Join(", ", reasons)}], actual [{string.Join(", ", metadata.Select(item => item.FinishReason))}]");
    }

    if (expectation.ExpectedBodies is { } bodies)
    {
        if (metadata.Length != bodies.Length)
        {
            failures.Add($"expected {bodies.Length} body entries, actual {metadata.Length}");
        }

        for (var i = 0; i < Math.Min(metadata.Length, bodies.Length); i++)
        {
            var actual = metadata[i];
            var expected = bodies[i];
            if (expected.SourceStart is long start && actual.SourceStart != start)
            {
                failures.Add($"body {i} sourceStart expected {start}, actual {actual.SourceStart}");
            }

            if (expected.SourceEnd is long end && actual.SourceEnd != end)
            {
                failures.Add($"body {i} sourceEnd expected {end}, actual {actual.SourceEnd}");
            }

            if (expected.SourcePcmSha256 is { } hash && !string.Equals(actual.SourcePcmSha256, hash, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"body {i} source hash expected {hash}, actual {actual.SourcePcmSha256}");
            }

            if (!string.Equals(actual.SourcePcmSha256, actual.ComputedSourcePcmSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"body {i} source hash does not match input slice");
            }

            if (actual.SampleCount != actual.SourceEnd - actual.SourceStart)
            {
                failures.Add($"body {i} sample count does not match source range");
            }

            if (!string.Equals(actual.OutputWavSha256, actual.ExpectedOutputWavSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"body {i} output WAV hash does not match input slice");
            }

            if (!string.Equals(actual.OutputWavSha256, actual.ActualOutputWavSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"body {i} output WAV sidecar hash does not match actual output file");
            }

            if (actual.ActualOutputSampleCount != actual.SampleCount)
            {
                failures.Add($"body {i} output sample count does not match sidecar sample count");
            }

            if (!string.Equals(actual.ActualOutputPcmSha256, actual.SourcePcmSha256, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"body {i} output PCM hash does not match source slice");
            }
        }
    }

    return new ValidationResult(failures.ToArray());
}

static async Task<int> SelfTestValidation()
{
    var root = Path.Combine(Path.GetTempPath(), "voice-switch-harness-self-test-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(root);
        var sourceSamples = Enumerable.Range(0, 32).Select(i => (short)(1000 + i)).ToArray().ToImmutableArray();
        var body = sourceSamples.AsSpan(10, 6).ToArray().ToImmutableArray();
        var expectedHash = Pcm16Wav.Sha256Hex(body.AsSpan());
        var handoff = new LocalRecordingHandoff(root, "source.wav");
        var result = await handoff.SubmitAsync(new DictationAudio(Guid.NewGuid(), new SampleRange(10, 16), body, FinishReason.StandaloneStop), CancellationToken.None);
        var metadataPath = Directory.EnumerateFiles(root, "*.json").Single();
        var metadata = ReadMetadata(metadataPath, sourceSamples);
        var expectation = new Expectation(
            ExpectExitSuccess: true,
            ExpectedOutputCount: 1,
            ExpectedFinishReasons: [FinishReason.StandaloneStop.ToString()],
            ExpectedBodies: [new ExpectedBody(10, 16, expectedHash)]);
        var clean = ValidateExpectation(expectation, 0, [metadata]);
        if (result.Status != HandoffStatus.RecordedLocally || clean.Failures.Length != 0)
        {
            Console.Error.WriteLine("clean handoff validation failed");
            foreach (var failure in clean.Failures)
            {
                Console.Error.WriteLine(failure);
            }

            return 1;
        }

        var originalWav = File.ReadAllBytes(metadata.WavPath);
        await File.WriteAllBytesAsync(metadata.WavPath, Pcm16Wav.Encode(Enumerable.Repeat((short)7, body.Length).ToArray()));
        var alteredFile = ValidateExpectation(expectation, 0, [ReadMetadata(metadataPath, sourceSamples)]);
        await File.WriteAllBytesAsync(metadata.WavPath, originalWav);
        if (!alteredFile.Failures.Any(failure => failure.Contains("actual output file", StringComparison.Ordinal)
            || failure.Contains("output PCM hash", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("altered output WAV was not rejected");
            return 1;
        }

        var sidecar = await File.ReadAllTextAsync(metadataPath);
        await File.WriteAllTextAsync(metadataPath, sidecar.Replace(expectedHash, new string('0', expectedHash.Length), StringComparison.Ordinal));
        var alteredSidecar = ValidateExpectation(expectation, 0, [ReadMetadata(metadataPath, sourceSamples)]);
        if (!alteredSidecar.Failures.Any(failure => failure.Contains("source hash expected", StringComparison.Ordinal)
            || failure.Contains("source hash does not match", StringComparison.Ordinal)
            || failure.Contains("output WAV hash", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("altered sidecar was not rejected");
            return 1;
        }

        Console.WriteLine("PASS harness validation rejects altered output WAV and sidecar hashes");
        return 0;
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

sealed class HarnessObserver : IDictationRuntimeObserver
{
    private readonly VoiceSwitchConfig config;
    private readonly ConcurrentDictionary<long, RecognitionDiagnostic> timings = new();

    public HarnessObserver(VoiceSwitchConfig config)
    {
        this.config = config;
    }

    public ConcurrentQueue<RequestEvidence> RecognitionRequests { get; } = new();
    public ConcurrentQueue<RecognitionEvidence> RecognitionOutcomes { get; } = new();
    public ConcurrentQueue<HandoffEvidence> Handoffs { get; } = new();
    public NoiseProcessorStatus? NoiseStatus { get; private set; }
    public int PendingHighWater { get; private set; }
    public long RetainedSampleHighWater { get; private set; }

    public void RecordSapiTiming(RecognitionDiagnostic diagnostic)
    {
        timings[diagnostic.Request.Id] = diagnostic;
    }

    public void RecognitionQueued(RecognitionRequest request, int pending, long retainedStart, long retainedEnd)
    {
        PendingHighWater = Math.Max(PendingHighWater, pending);
        RetainedSampleHighWater = Math.Max(RetainedSampleHighWater, retainedEnd - retainedStart);
        RecognitionRequests.Enqueue(new RequestEvidence(request.Id, request.Extent.ToString(), request.Range.Start, request.Range.End, request.Samples.Length, pending, retainedStart, retainedEnd));
    }

    public void RecognitionCompleted(RecognitionRequest request, RecognizedUtterance? recognition, Exception? error)
    {
        timings.TryGetValue(request.Id, out var timing);
        var stop = recognition is null ? null : DictationBoundaries.StandaloneStopRange(recognition, config.StopWords ?? []);
        RecognitionOutcomes.Enqueue(new RecognitionEvidence(
            request.Id,
            request.Extent.ToString(),
            request.Range.Start,
            request.Range.End,
            recognition?.Text,
            recognition?.HadRejectedSpeech,
            recognition is not null && DictationBoundaries.LeadingWake(recognition, config.WakeWords) is not null,
            stop is not null,
            stop?.Start,
            stop?.End,
            recognition?.Lexemes.Select(item => new LexemeEvidence(item.Text, item.Range.Start, item.Range.End)).ToArray() ?? [],
            error?.Message,
            timing?.ElapsedMilliseconds,
            timing?.ChildCpuMilliseconds));
    }

    public void HandoffSubmitted(DictationAudio audio, HandoffResult result)
    {
        Handoffs.Enqueue(new HandoffEvidence(result.Status.ToString(), result.Id, result.Path, audio.Reason.ToString(), audio.Range.Start, audio.Range.End, audio.Samples.Length));
    }

    public void RetentionObserved(long retainedStart, long retainedEnd, int pending)
    {
        PendingHighWater = Math.Max(PendingHighWater, pending);
        RetainedSampleHighWater = Math.Max(RetainedSampleHighWater, retainedEnd - retainedStart);
    }

    public void NoiseProcessorCompleted(NoiseProcessorStatus status)
    {
        NoiseStatus = status;
    }
}

sealed record Expectation(
    bool? ExpectExitSuccess = null,
    int? ExpectedOutputCount = null,
    string[]? ExpectedFinishReasons = null,
    ExpectedBody[]? ExpectedBodies = null);

sealed record ExpectedBody(long? SourceStart = null, long? SourceEnd = null, string? SourcePcmSha256 = null);

sealed record Evidence(
    DateTimeOffset CreatedAt,
    string ConfigPath,
    string WavPath,
    string SourcePcmSha256,
    int SourceSamples,
    double AudioSeconds,
    string OutputDir,
    bool Fast,
    int? CancelAfterMs,
    int ExitCode,
    long ElapsedMilliseconds,
    long ParentCpuMilliseconds,
    int PendingHighWater,
    long RetainedSampleHighWater,
    OutputFile[] Outputs,
    MetadataEvidence[] Metadata,
    RequestEvidence[] RecognitionRequests,
    RecognitionEvidence[] RecognitionOutcomes,
    HandoffEvidence[] Handoffs,
    NoiseProcessorStatus? NoiseStatus,
    string ExpectationStatus,
    string[] ExpectationFailures);

sealed record OutputFile(string Path, long Bytes, string Sha256);
sealed record MetadataEvidence(string Path, string WavPath, string FinishReason, long SourceStart, long SourceEnd, int SampleCount, string SourcePcmSha256, string ComputedSourcePcmSha256, string OutputWavSha256, string ExpectedOutputWavSha256, string ActualOutputWavSha256, int ActualOutputSampleCount, string ActualOutputPcmSha256);
sealed record RequestEvidence(long Id, string Extent, long Start, long End, int Samples, int Pending, long RetainedStart, long RetainedEnd);
sealed record RecognitionEvidence(long Id, string Extent, long Start, long End, string? Text, bool? Rejected, bool LeadingWake, bool StandaloneStop, long? StopStart, long? StopEnd, LexemeEvidence[] Lexemes, string? Error, long? ElapsedMilliseconds, long? ChildCpuMilliseconds);
sealed record LexemeEvidence(string Text, long Start, long End);
sealed record HandoffEvidence(string Status, Guid Id, string? Path, string FinishReason, long SourceStart, long SourceEnd, int SampleCount);
sealed record ValidationResult(string[] Failures);
