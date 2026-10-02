using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("SKIP requires native Windows powershell.exe and System.Speech voices");
    return 77;
}

var configPath = GetOption(args, "--config")
    ?? throw new ArgumentException("--config requires a path.");
var phrase = GetOption(args, "--phrase") ?? "音声入力、今日は晴れです";
var expect = GetOption(args, "--expect") ?? "音声入力";
var config = ConfigLoader.Load(configPath);
if (HasFlag(args, "--observer-throws"))
{
    return await RunObserverThrowsProbe(config, phrase);
}

if (HasFlag(args, "--cancel-after-start"))
{
    return await RunCancelAfterStartProbe(config, phrase);
}

var wavPath = Path.Combine(Path.GetTempPath(), $"voice-switch-native-dictation-{Guid.NewGuid():N}.wav");
try
{
    GenerateSyntheticWav(wavPath, phrase, config.EffectiveLocale);
    var samples = ReadPcm16Mono16k(wavPath);
    var request = new RecognitionRequest(
        1,
        RecognitionExtent.ClosedUtterance,
        new SampleRange(0, samples.Length),
        samples.ToImmutableArray());
    var recognizer = new SpeechPowerShellDictationRecognizer(config);
    var result = await recognizer.RecognizeAsync(request, CancellationToken.None);
    Console.OutputEncoding = Encoding.UTF8;
    Console.WriteLine($"recognized: {result.Text}");
    Console.WriteLine($"rejected: {result.HadRejectedSpeech}");
    Console.WriteLine($"lexemes: {result.Lexemes.Length}");
    foreach (var lexeme in result.Lexemes)
    {
        Console.WriteLine($"lexeme: {lexeme.Text} {lexeme.Range.Start}..{lexeme.Range.End}");
    }

    if (result.Id != request.Id || result.Source != request.Range)
    {
        Console.Error.WriteLine("native dictation probe: recognizer returned the wrong request identity or range");
        return 2;
    }

    if (!TextMatching.Normalize(result.Text).Contains(TextMatching.Normalize(expect), StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"native dictation probe: expected normalized text to contain {expect}");
        return 3;
    }

    if (result.Lexemes.Length == 0 || result.Lexemes.Any(item => item.Range.Start < 0 || item.Range.End > samples.Length || item.Range.End < item.Range.Start))
    {
        Console.Error.WriteLine("native dictation probe: lexical timings are missing or outside the source PCM range");
        return 4;
    }

    return 0;
}
finally
{
    try { if (File.Exists(wavPath)) File.Delete(wavPath); }
    catch { }
}

static async Task<int> RunObserverThrowsProbe(VoiceSwitchConfig config, string phrase)
{
    var wavPath = Path.Combine(Path.GetTempPath(), $"voice-switch-native-dictation-{Guid.NewGuid():N}.wav");
    RecognitionProcessIdentity? started = null;
    try
    {
        GenerateSyntheticWav(wavPath, phrase, config.EffectiveLocale);
        var samples = ReadPcm16Mono16k(wavPath);
        var request = new RecognitionRequest(2, RecognitionExtent.ClosedUtterance, new SampleRange(0, samples.Length), samples.ToImmutableArray());
        var recognizer = new SpeechPowerShellDictationRecognizer(config, diagnostic =>
        {
            if (diagnostic.Running && diagnostic.ChildProcess is { } identity)
            {
                started = identity;
                throw new InvalidOperationException("observer fixture failure after started identity");
            }
        });

        try
        {
            _ = await recognizer.RecognizeAsync(request, CancellationToken.None);
            Console.Error.WriteLine("native dictation probe: observer failure was not propagated");
            return 10;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("observer fixture failure", StringComparison.Ordinal))
        {
            if (started is null)
            {
                Console.Error.WriteLine("native dictation probe: observer did not record child identity");
                return 11;
            }

            Console.WriteLine($"started: {started.ProcessId} {started.StartTimeUtc:O}");
            if (!ProcessIdentityIsGone(started, TimeSpan.FromSeconds(10)))
            {
                Console.Error.WriteLine("native dictation probe: owned child remained alive after observer failure");
                return 12;
            }

            return 0;
        }
    }
    finally
    {
        try { if (File.Exists(wavPath)) File.Delete(wavPath); }
        catch { }
    }
}

static async Task<int> RunCancelAfterStartProbe(VoiceSwitchConfig config, string phrase)
{
    var wavPath = Path.Combine(Path.GetTempPath(), $"voice-switch-native-dictation-{Guid.NewGuid():N}.wav");
    RecognitionProcessIdentity? started = null;
    using var cts = new CancellationTokenSource();
    try
    {
        GenerateSyntheticWav(wavPath, phrase, config.EffectiveLocale);
        var samples = ReadPcm16Mono16k(wavPath);
        var request = new RecognitionRequest(3, RecognitionExtent.ClosedUtterance, new SampleRange(0, samples.Length), samples.ToImmutableArray());
        var recognizer = new SpeechPowerShellDictationRecognizer(config, diagnostic =>
        {
            if (diagnostic.Running && diagnostic.ChildProcess is { } identity)
            {
                started = identity;
                cts.Cancel();
            }
        });

        try
        {
            _ = await recognizer.RecognizeAsync(request, cts.Token);
            Console.Error.WriteLine("native dictation probe: cancellation unexpectedly succeeded");
            return 20;
        }
        catch (OperationCanceledException)
        {
            if (started is null)
            {
                Console.Error.WriteLine("native dictation probe: cancellation did not observe child identity");
                return 21;
            }

            Console.WriteLine($"cancelled: {started.ProcessId} {started.StartTimeUtc:O}");
            return ProcessIdentityIsGone(started, TimeSpan.FromSeconds(10)) ? 0 : 22;
        }
    }
    finally
    {
        try { if (File.Exists(wavPath)) File.Delete(wavPath); }
        catch { }
    }
}

static void GenerateSyntheticWav(string path, string phrase, string locale)
{
    var script = """
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Speech
$path = $env:VOICE_SWITCH_WAV_PATH
$phrase = $env:VOICE_SWITCH_SYNTH_PHRASE
$locale = $env:VOICE_SWITCH_LOCALE
$culture = [System.Globalization.CultureInfo]::GetCultureInfo($locale)
$format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
  $synth.SelectVoiceByHints(
    [System.Speech.Synthesis.VoiceGender]::NotSet,
    [System.Speech.Synthesis.VoiceAge]::NotSet,
    0,
    $culture)
  $synth.SetOutputToWaveFile($path, $format)
  $synth.Speak($phrase)
} finally {
  $synth.Dispose()
}
""";
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        CreateNoWindow = true,
        Environment =
        {
            ["VOICE_SWITCH_WAV_PATH"] = path,
            ["VOICE_SWITCH_SYNTH_PHRASE"] = phrase,
            ["VOICE_SWITCH_LOCALE"] = locale,
        },
    }) ?? throw new InvalidOperationException("failed to start powershell.exe");
    if (!process.WaitForExit(15000))
    {
        process.Kill(entireProcessTree: true);
        throw new TimeoutException("synthetic speech generation timed out");
    }

    var error = process.StandardError.ReadToEnd();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"synthetic speech generation failed {process.ExitCode}: {error}");
    }
}

static short[] ReadPcm16Mono16k(string path)
{
    using var reader = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
    {
        throw new InvalidDataException("synthetic WAV is not RIFF");
    }

    _ = reader.ReadInt32();
    if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
    {
        throw new InvalidDataException("synthetic WAV is not WAVE");
    }

    short? channels = null;
    int? rate = null;
    short? bits = null;
    byte[]? data = null;
    while (reader.BaseStream.Position < reader.BaseStream.Length)
    {
        var chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
        var length = reader.ReadInt32();
        if (chunk == "fmt ")
        {
            var formatTag = reader.ReadInt16();
            channels = reader.ReadInt16();
            rate = reader.ReadInt32();
            _ = reader.ReadInt32();
            _ = reader.ReadInt16();
            bits = reader.ReadInt16();
            reader.BaseStream.Position += length - 16;
            if (formatTag != 1)
            {
                throw new InvalidDataException("synthetic WAV is not PCM");
            }
        }
        else if (chunk == "data")
        {
            data = reader.ReadBytes(length);
        }
        else
        {
            reader.BaseStream.Position += length;
        }

        if ((length & 1) == 1)
        {
            reader.BaseStream.Position++;
        }
    }

    if (channels != 1 || rate != 16000 || bits != 16 || data is null || data.Length % 2 != 0)
    {
        throw new InvalidDataException("synthetic WAV must be PCM16 mono 16 kHz");
    }

    var samples = new short[data.Length / 2];
    Buffer.BlockCopy(data, 0, samples, 0, data.Length);
    return samples;
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

static bool HasFlag(string[] values, string name) =>
    values.Any(item => item.Equals(name, StringComparison.Ordinal));

static bool ProcessIdentityIsGone(RecognitionProcessIdentity identity, TimeSpan timeout)
{
    return SpinWait.SpinUntil(() =>
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
            {
                return true;
            }

            var startTime = DateTime.SpecifyKind(process.StartTime.ToUniversalTime(), DateTimeKind.Utc);
            return new DateTimeOffset(startTime) != identity.StartTimeUtc;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }, timeout);
}
