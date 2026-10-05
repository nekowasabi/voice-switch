using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

public static class Program
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var options = CliOptions.Parse(args);

            if (options.Help)
            {
                PrintHelp();
                return 0;
            }

            if (options.SelfTest)
            {
                return SelfTest.Run();
            }

            var configPath = options.ConfigPath
                ?? Environment.GetEnvironmentVariable("VOICE_SWITCH_CONFIG")
                ?? WindowsPaths.DefaultConfigPath();

            if (options.Recognizers)
            {
                return SpeechPowerShell.RunRecognizerDiagnostics();
            }

            if (options.CheckDevice)
            {
                return SpeechPowerShell.RunDeviceDiagnostic(configPath);
            }

            var config = ConfigLoader.Load(configPath);
            if (options.CheckPaths.Count > 0)
            {
                var recognizer = new SpeechPowerShellDictationRecognizer(config);
                try
                {
                    return CheckMode.RunAsync(options.CheckPaths, config, recognizer, Console.Out).GetAwaiter().GetResult();
                }
                finally
                {
                    recognizer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }

            if (options.InputWavPath is not null && config.Dictation is null)
            {
                throw new ArgumentException("--input-wav requires a config with dictation.");
            }

            if (options.Fire)
            {
                CommandRunner.Run(config.Command);
                return 0;
            }

            if (config.Dictation is not null)
            {
                using var interrupt = new CancellationTokenSource();
                if (options.ListenSeconds is int seconds)
                {
                    interrupt.CancelAfter(TimeSpan.FromSeconds(seconds));
                }

                ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
                {
                    eventArgs.Cancel = true;
                    interrupt.Cancel();
                };
                Console.CancelKeyPress += cancelHandler;
                try
                {
                    var syntheticDryRun = SyntheticInputSuppressesExternalDispatch(options);
                    var effectiveDryRun = options.DryRun || syntheticDryRun;
                    IDictationHandoff handoff;
                    if (options.OutputDir is not null)
                    {
                        handoff = new LocalRecordingHandoff(options.OutputDir, options.InputWavPath);
                        Log.Info($"dictation synthetic output: recording WAV handoffs under {options.OutputDir}");
                    }
                    else
                    {
                        handoff = new RegisteredSuperwhisperHandoff(
                            RegisteredSuperwhisperHandoff.DefaultRoot(),
                            WindowsPaths.SuperwhisperRecordingsPath(config.Dictation),
                            dryRun: effectiveDryRun,
                            onTranscribed: TmuxPaneRouter.RouteAsync);
                    }

                    IPcmCapture capture;
                    if (options.InputWavPath is not null)
                    {
                        capture = new WavPcmCapture(options.InputWavPath, paced: !options.InputWavFast);
                        Log.Info(options.InputWavFast
                            ? $"dictation synthetic input: {options.InputWavPath} fast structural mode"
                            : $"dictation synthetic input: {options.InputWavPath} paced 480 samples / 30 ms");
                        if (syntheticDryRun)
                        {
                            Log.Info("dictation synthetic input: no --output-dir was supplied, so external dispatch is suppressed");
                        }
                    }
                    else
                    {
                        capture = WinMmCapture.Open();
                    }

                    return new WindowsDictationRuntime(
                            config,
                            capture,
                            new SpeechPowerShellDictationRecognizer(config),
                            handoff,
                            effectiveDryRun,
                            reloadConfig: new ConfigFile(configPath).ReloadIfChanged)
                        .RunAsync(interrupt.Token)
                        .GetAwaiter()
                        .GetResult();
                }
                finally
                {
                    Console.CancelKeyPress -= cancelHandler;
                }
            }

            var runtime = options.DryRun
                ? new ResidentRuntime(configPath, config, options.ListenSeconds, SpeechPowerShell.Start, CommandRunner.Run, _ => { }, true, CancellationToken.None)
                : new ResidentRuntime(configPath, config, options.ListenSeconds);
            return runtime.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex.Message);
            Log.Info($"hint: copy config.example.windows.json to {WindowsPaths.DefaultConfigPath()}");
            return 1;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
        voice-switch (Windows)

          voice-switch.exe                       start the tray app and listen for configured wake words
          voice-switch.exe --paused              start the tray app without opening the microphone
          voice-switch.exe --tray-command CMD    send status, start, pause, reload, or quit to the running tray app
          voice-switch.exe --listen-seconds 5    bounded console listener run for diagnostics
          voice-switch.exe --dry-run             listen and report decisions without running commands
          voice-switch.exe --recognizers         list installed Windows speech recognizers
          voice-switch.exe --check-device        open the default speech input once and report errors
          voice-switch.exe --check A.wav [B.wav]  print a VAD + recognizer verdict per utterance (PCM16 mono 16 kHz)
          voice-switch.exe --input-wav PATH      run dictation from PCM16 mono 16 kHz WAV, no microphone fallback
          voice-switch.exe --input-wav-fast      read --input-wav structurally without 30 ms pacing
          voice-switch.exe --output-dir PATH     record synthetic dictation WAV handoffs locally, no external launch
          voice-switch.exe --fire                run config command once
          voice-switch.exe --self-test           pure behavior tests, no mic and no command
          voice-switch.exe --help                this text

        Config: {WindowsPaths.DefaultConfigPath()}
        Default command: {PlatformDefaults.SuperwhisperToggle}
        Dictation handoff: {WindowsPaths.DefaultHandoffPath()}

        Incompatible flags: --dry-run cannot be combined with --fire.
        Synthetic input: --input-wav without --output-dir is a dry-run and never launches an external app.
        """);
    }

    public static bool SyntheticInputSuppressesExternalDispatch(CliOptions options) =>
        options.InputWavPath is not null && options.OutputDir is null;
}

// Mac check(_:cfg:): each file through the VAD and the recognizer, one verdict per utterance: "wake" for a wake word
// on its own, "dictate:<text after the wake word>" for a one-breath dictation, else the normalized text heard.
public static class CheckMode
{
    public static async Task<int> RunAsync(IReadOnlyList<string> paths, VoiceSwitchConfig config, IDictationRecognizer recognizer, TextWriter output)
    {
        long id = 0;
        foreach (var path in paths)
        {
            // A second of trailing silence closes the last utterance, as on Mac.
            var samples = Pcm16Wav.DecodeStrict(new FileInfo(path), maxSamples: 16000 * 600).AddRange(new short[16000]);
            var segmenter = new Segmenter(config);
            var verdicts = new List<string>();
            for (var end = Segmenter.FrameLength; end <= samples.Length; end += Segmenter.FrameLength)
            {
                var frame = new float[Segmenter.FrameLength];
                for (var i = 0; i < frame.Length; i++)
                {
                    frame[i] = samples[end - Segmenter.FrameLength + i] / 32768f;
                }

                if (segmenter.Push(frame) is not { } ev)
                {
                    continue;
                }

                var range = new SampleRange(end - ev.Samples.Length, end);
                var extent = ev.Kind == "head" ? RecognitionExtent.PrefixHead : RecognitionExtent.ClosedUtterance;
                var heard = await recognizer.RecognizeAsync(new RecognitionRequest(++id, extent, range, samples[(int)range.Start..(int)range.End]), CancellationToken.None);
                verdicts.Add(Verdict(heard, config));
            }

            output.WriteLine($"{path}\t[{string.Join(", ", verdicts.Select(verdict => $"\"{verdict}\""))}]");
        }

        return 0;
    }

    private static string Verdict(RecognizedUtterance heard, VoiceSwitchConfig config)
    {
        var wakes = config.WakeWords.Select(TextMatching.Normalize).OrderByDescending(wake => wake.Length).ToArray();
        var text = TextMatching.Normalize(heard.Text);
        if ((heard.Extent == RecognitionExtent.ClosedUtterance && wakes.Contains(text)) || DictationBoundaries.RejectedWake(heard, config.WakeWords) is not null)
        {
            return "wake";
        }

        if (DictationBoundaries.LeadingWake(heard, config.WakeWords) is not { BodyStart: not null })
        {
            return text;
        }

        var rest = text;
        while (wakes.FirstOrDefault(wake => rest.StartsWith(wake, StringComparison.Ordinal)) is { } wake)
        {
            rest = rest[wake.Length..];
        }

        return "dictate:" + rest;
    }
}

public sealed class ResidentRuntime
{
    private readonly string configPath;
    private VoiceSwitchConfig config;
    private readonly int? listenSeconds;
    private readonly Func<VoiceSwitchConfig, ISpeechProcess> startRecognizer;
    private readonly Action<string> runCommand;
    private readonly Action<RuntimeDecision> observeDecision;
    private readonly bool dryRun;
    private readonly CancellationToken cancellation;
    private readonly ConfigFile configFile;
    private readonly Func<IReadOnlyCollection<string>, string?> micInUseBy;

    public ResidentRuntime(string configPath, VoiceSwitchConfig config, int? listenSeconds)
        : this(configPath, config, listenSeconds, SpeechPowerShell.Start, CommandRunner.Run, _ => { }, false, CancellationToken.None)
    {
    }

    public ResidentRuntime(
        string configPath,
        VoiceSwitchConfig config,
        int? listenSeconds,
        Func<VoiceSwitchConfig, ISpeechProcess> startRecognizer,
        Action<string> runCommand,
        Action<RuntimeDecision> observeDecision,
        bool dryRun,
        CancellationToken cancellation,
        Func<IReadOnlyCollection<string>, string?>? micInUseBy = null)
    {
        this.configPath = configPath;
        this.micInUseBy = micInUseBy ?? MicInUse.By;
        this.config = config;
        this.listenSeconds = listenSeconds;
        this.startRecognizer = startRecognizer;
        this.runCommand = runCommand;
        this.observeDecision = observeDecision;
        this.dryRun = dryRun;
        this.cancellation = cancellation;
        configFile = new ConfigFile(configPath);
    }

    public int Run()
    {
        Log.Info($"voice-switch Windows: config {configPath}");
        Log.Info($"wake words: {string.Join(", ", config.WakeWords)}");
        Log.Info($"locale: {config.EffectiveLocale}");
        Log.Info($"command: {config.Command}");
        if (dryRun)
        {
            Log.Info("dry-run: commands are suppressed");
        }
        LogUnsupportedOptions();

        var recognizer = startRecognizer(config);
        var deadline = listenSeconds is null ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(listenSeconds.Value);
        using var interrupt = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            interrupt.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (!WaitUntilReady(recognizer, interrupt.Token))
            {
                return IsCancelled(interrupt.Token) ? 130 : 1;
            }

            while (!IsCancelled(interrupt.Token) && DateTime.UtcNow <= deadline)
            {
                var reloaded = ReloadIfChanged();
                if (reloaded == ConfigReload.RecognitionChanged)
                {
                    recognizer.Dispose();
                    recognizer = startRecognizer(config);
                    if (!WaitUntilReady(recognizer, interrupt.Token))
                    {
                        return IsCancelled(interrupt.Token) ? 130 : 1;
                    }
                }

                var line = recognizer.ReadLine(TimeSpan.FromMilliseconds(500));
                if (line is null)
                {
                    if (recognizer.HasExited)
                    {
                        Log.Info($"speech recognizer exited unexpectedly with code {recognizer.ExitCode}");
                        return 1;
                    }

                    continue;
                }

                if (!HandleRecognizerLine(line))
                {
                    return 1;
                }

                if (recognizer.HasExited)
                {
                    Log.Info($"speech recognizer exited unexpectedly with code {recognizer.ExitCode}");
                    return 1;
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            recognizer.Dispose();
        }

        if (IsCancelled(interrupt.Token))
        {
            Log.Info(interrupt.IsCancellationRequested ? "stopped by Ctrl+C" : "stopped");
            return 130;
        }

        Log.Info($"listener stopped after {listenSeconds} seconds");
        return 0;
    }

    private bool WaitUntilReady(ISpeechProcess recognizer, CancellationToken interrupt)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!IsCancelled(interrupt) && DateTime.UtcNow < until)
        {
            var line = recognizer.ReadLine(TimeSpan.FromMilliseconds(250));
            if (line is null)
            {
                if (recognizer.HasExited)
                {
                    Log.Info($"speech recognizer exited before ready with code {recognizer.ExitCode}");
                    return false;
                }

                continue;
            }

            if (!RecognizerMessage.TryParse(line, out var message))
            {
                Log.Info(line);
                continue;
            }

            if (message.Type == "error")
            {
                Log.Info($"speech error: {message.Message}");
                return false;
            }

            if (message.Type == "diagnostic")
            {
                Log.Info(message.Message ?? "");
                if ((message.Message ?? "").StartsWith("recognizer ready:", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        Log.Info("speech recognizer did not report ready within 10 seconds");
        return false;
    }

    private bool IsCancelled(CancellationToken interrupt) =>
        interrupt.IsCancellationRequested || cancellation.IsCancellationRequested;

    private ConfigReload ReloadIfChanged()
    {
        if (configFile.ReloadIfChanged() is not { } next)
        {
            return ConfigReload.Unchanged;
        }

        var recognitionChanged = config.RecognitionKey() != next.RecognitionKey();
        config = next;
        LogUnsupportedOptions();
        return recognitionChanged ? ConfigReload.RecognitionChanged : ConfigReload.Reloaded;
    }

    private void LogUnsupportedOptions()
    {
        foreach (var warning in config.UnsupportedWarnings())
        {
            Log.Info($"windows config warning: {warning}");
        }
    }

    private bool HandleRecognizerLine(string line)
    {
        if (!RecognizerMessage.TryParse(line, out var message))
        {
            Log.Info(line);
            return true;
        }

        if (message.Type == "error")
        {
            Log.Info($"speech error: {message.Message}");
            return false;
        }

        if (message.Type == "diagnostic")
        {
            Log.Info(message.Message ?? "");
            return true;
        }

        if (message.Type != "recognized" || string.IsNullOrEmpty(message.Text))
        {
            return true;
        }

        var decision = TextMatching.Decide(message.Text, config);
        observeDecision(decision);
        // Mac: the wake command toggles Superwhisper, so firing it while Superwhisper records would stop that recording.
        if (decision.Reason == "wake" && micInUseBy(config.SkipWhileMicInUseBy ?? []) is { } busy)
        {
            Log.Info($"heard: {decision.Text} -> skipped: {busy} is using the microphone");
            return true;
        }

        if (decision.Kind == "run-command" && decision.Command is not null)
        {
            Log.Info($"heard: {decision.Text} -> {decision.Reason}");
            if (dryRun)
            {
                Log.Info($"dry-run: decision {decision.Kind} reason={decision.Reason}; command suppressed");
            }
            else
            {
                runCommand(decision.Command);
            }
        }
        else
        {
            Log.Info($"heard: {decision.Text}");
        }

        return true;
    }
}

// Mac ConfigFile: checked between utterances so wake words and timings can be edited without a restart.
public sealed class ConfigFile(string path)
{
    private DateTime lastWrite = File.GetLastWriteTimeUtc(path);

    // A new valid config, or null when the file is unchanged or invalid (the caller keeps what it has).
    public VoiceSwitchConfig? ReloadIfChanged()
    {
        var write = File.GetLastWriteTimeUtc(path);
        if (write == lastWrite)
        {
            return null;
        }

        lastWrite = write;
        try
        {
            var next = ConfigLoader.Load(path);
            Log.Info($"config reloaded: {string.Join(", ", next.WakeWords)}");
            return next;
        }
        catch (Exception ex)
        {
            Log.Info($"config reload failed, keeping previous: {ex.Message}");
            return null;
        }
    }
}

public enum ConfigReload
{
    Unchanged,
    Reloaded,
    RecognitionChanged
}

public sealed record RecognizerMessage(string Type, string? Text, string? Message)
{
    public static bool TryParse(string line, out RecognizerMessage message)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            message = new RecognizerMessage(
                root.GetProperty("type").GetString() ?? "",
                root.TryGetProperty("text", out var text) ? text.GetString() : null,
                root.TryGetProperty("message", out var msg) ? msg.GetString() : null);
            return true;
        }
        catch
        {
            message = new RecognizerMessage("raw", null, line);
            return false;
        }
    }
}

public static class SpeechPowerShell
{
    public static SpeechProcess Start(VoiceSwitchConfig config)
    {
        var psi = CreatePowerShell();
        psi.Environment["VOICE_SWITCH_LOCALE"] = config.EffectiveLocale;
        psi.Environment["VOICE_SWITCH_WAKE_WORDS"] = ConfigLoader.ToJsonArray(config.WakeWords.Concat(config.StopWords ?? []));
        var process = ChildProcessJob.Start(psi);
        return new SpeechProcess(process);
    }

    public static int RunRecognizerDiagnostics()
    {
        using var proc = StartDiagnostic("recognizers");
        var lines = proc.DrainToConsole();
        if (lines.Count == 0)
        {
            Console.Error.WriteLine("speech diagnostic failed: child exited without recognizer output");
            return 1;
        }

        return proc.ExitCode;
    }

    public static int RunDeviceDiagnostic(string configPath)
    {
        var config = ConfigLoader.Load(configPath);
        using var proc = StartDiagnostic("device", config);
        var lines = proc.DrainToConsole();
        if (lines.Count == 0)
        {
            Console.Error.WriteLine("speech diagnostic failed: child exited before ready");
            return 1;
        }

        if (!lines.Any(line => line.Contains("recognizer ready:", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("speech diagnostic failed: recognizer did not report ready");
            return 1;
        }

        return proc.ExitCode;
    }

    private static SpeechProcess StartDiagnostic(string mode, VoiceSwitchConfig? config = null)
    {
        var psi = CreatePowerShell();
        psi.Environment["VOICE_SWITCH_MODE"] = mode;
        psi.Environment["VOICE_SWITCH_LOCALE"] = config?.EffectiveLocale ?? "ja-JP";
        psi.Environment["VOICE_SWITCH_WAKE_WORDS"] = ConfigLoader.ToJsonArray(config?.WakeWords ?? []);
        var process = ChildProcessJob.Start(psi);
        return new SpeechProcess(process);
    }

    private static ProcessStartInfo CreatePowerShell()
    {
        // Why: Use Windows PowerShell System.Speech instead of a NuGet package so WSL builds stay offline.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
        return new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private const string Script = """
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Speech
function Send-Json($obj) {
  $obj | ConvertTo-Json -Compress
}
$mode = $env:VOICE_SWITCH_MODE
$locale = $env:VOICE_SWITCH_LOCALE
$words = @()
if ($env:VOICE_SWITCH_WAKE_WORDS) {
  $parsedWords = $env:VOICE_SWITCH_WAKE_WORDS | ConvertFrom-Json
  $words = @($parsedWords | ForEach-Object { [string]$_ })
}
$infos = [System.Speech.Recognition.SpeechRecognitionEngine]::InstalledRecognizers()
if ($mode -eq 'recognizers') {
  if ($infos.Count -eq 0) {
    Send-Json @{ type='error'; message='No installed Windows speech recognizers were reported by System.Speech.' }
    exit 2
  }
  foreach ($info in $infos) {
    Send-Json @{ type='diagnostic'; message=($info.Culture.Name + ' ' + $info.Description) }
  }
  exit 0
}
$info = $infos | Where-Object { $_.Culture.Name -eq $locale } | Select-Object -First 1
if ($null -eq $info) {
  $installed = ($infos | ForEach-Object { $_.Culture.Name }) -join ', '
  Send-Json @{ type='error'; message=("No installed Windows speech recognizer for " + $locale + ". Installed: " + $installed) }
  exit 2
}
$engine = [System.Speech.Recognition.SpeechRecognitionEngine]::new($info)
try {
  if ($words.Count -gt 0) {
    $choices = [System.Speech.Recognition.Choices]::new()
    foreach ($word in $words) { [void]$choices.Add([string]$word) }
    $builder = [System.Speech.Recognition.GrammarBuilder]::new()
    $builder.Culture = $info.Culture
    $builder.Append($choices)
    $grammar = [System.Speech.Recognition.Grammar]::new($builder)
    $engine.LoadGrammar($grammar)
  }
  # Why: a choices-only grammar force-matches unrelated speech (e.g. while Superwhisper records) to the nearest
  # wake word at low confidence; firing on those toggles Superwhisper off mid-dictation.
  # ponytail: fixed threshold, calibrate from the logged conf= values.
  $minConfidence = 0.6
  $engine.SetInputToDefaultAudioDevice()
  Send-Json @{ type='diagnostic'; message=("recognizer ready: " + $info.Culture.Name + ' ' + $info.Description) }
  if ($mode -eq 'device') { exit 0 }
  while ($true) {
    $result = $engine.Recognize([TimeSpan]::FromSeconds(1))
    if ($null -ne $result) {
      $conf = [math]::Round($result.Confidence, 2)
      if ($result.Confidence -ge $minConfidence) {
        Send-Json @{ type='diagnostic'; message=("recognized: " + $result.Text + " conf=" + $conf) }
        Send-Json @{ type='recognized'; text=$result.Text; confidence=$result.Confidence }
      } else {
        Send-Json @{ type='diagnostic'; message=("ignored low confidence: " + $result.Text + " conf=" + $conf) }
      }
    }
  }
} catch {
  Send-Json @{ type='error'; message=$_.Exception.Message }
  exit 3
} finally {
  if ($null -ne $engine) { $engine.Dispose() }
}
""";
}

public interface ISpeechProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    string? ReadLine(TimeSpan timeout);
}

public sealed class SpeechProcess : ISpeechProcess
{
    private readonly Process process;
    private readonly CancellationTokenSource cancel = new();
    private readonly Queue<string> lines = new();
    private readonly object gate = new();
    private readonly Task outputPump;
    private readonly Task errorPump;

    public SpeechProcess(Process process)
    {
        this.process = process;
        outputPump = Task.Run(() => Pump(process.StandardOutput, false, cancel.Token));
        errorPump = Task.Run(() => Pump(process.StandardError, true, cancel.Token));
    }

    public bool HasExited => process.HasExited;
    public int ExitCode => process.HasExited ? process.ExitCode : 0;

    public string? ReadLine(TimeSpan timeout)
    {
        lock (gate)
        {
            if (lines.Count > 0)
            {
                return lines.Dequeue();
            }
        }

        var until = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < until)
        {
            lock (gate)
            {
                if (lines.Count > 0)
                {
                    return lines.Dequeue();
                }
            }

            Thread.Sleep(20);
        }

        return null;
    }

    public List<string> DrainToConsole()
    {
        var lines = new List<string>();
        while (!process.HasExited || HasQueuedLines())
        {
            var line = ReadLine(TimeSpan.FromMilliseconds(100));
            if (line is not null)
            {
                Console.WriteLine(line);
                lines.Add(line);
            }
        }

        process.WaitForExit();
        return lines;
    }

    public void Dispose()
    {
        cancel.Cancel();
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { }
        }

        process.Dispose();
        try { Task.WaitAll([outputPump, errorPump], TimeSpan.FromSeconds(1)); }
        catch { }
        cancel.Dispose();
    }

    private bool HasQueuedLines()
    {
        lock (gate)
        {
            return lines.Count > 0;
        }
    }

    private void Pump(StreamReader reader, bool isError, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                return;
            }

            lock (gate)
            {
                lines.Enqueue(isError ? JsonSerializer.Serialize(new { type = "error", message = line }) : line);
            }
        }
    }
}

public static class CommandRunner
{
    public static void Run(string command)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/c " + command)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }
}

public static class Log
{
    private static readonly object FileGate = new();

    public static void Info(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        AppendToFile(line);
    }

    public static void Fatal(string message)
    {
        var line = $"{DateTimeOffset.Now:O} fatal: {message}";
        Console.Error.WriteLine(line);
        AppendToFile(line);
    }

    private static void AppendToFile(string line)
    {
        // Why: the tray app has no console, so wake-word misses and startup failures are only diagnosable from a file.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            lock (FileGate)
            {
                var path = WindowsPaths.DefaultLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public static class SelfTest
{
    public static int Run()
    {
        var config = new VoiceSwitchConfig(["音声入力"], "ja_JP", "echo wake", StopWords: ["入力ストップ"], StopCommand: "echo stop");
        var wake = TextMatching.Decide("音声 入力。", config);
        var stop = TextMatching.Decide("入力ストップ", config);
        var miss = TextMatching.Decide("違います", config);
        if (wake is not { Kind: "run-command", Command: "echo wake" })
        {
            Console.Error.WriteLine("self-test: wake decision failed");
            return 1;
        }

        if (stop is not { Kind: "run-command", Command: "echo stop" })
        {
            Console.Error.WriteLine("self-test: stop decision failed");
            return 1;
        }

        if (miss.Kind != "ignore")
        {
            Console.Error.WriteLine("self-test: miss decision failed");
            return 1;
        }

        if (!VerifySegmenter())
        {
            Console.Error.WriteLine("self-test: segmenter decision failed");
            return 1;
        }

        Console.WriteLine("self-test: ok");
        return 0;
    }

    private static bool VerifySegmenter()
    {
        var config = new VoiceSwitchConfig(["test"], null, "true", MaxSeconds: 2.5, HangoverMs: 300, PrerollMs: 300, MinSpeechMs: 300, VadRatio: 3, VadMinRMS: 0.005f);
        var segmenter = new Segmenter(config);
        var quiet = Enumerable.Repeat(0.0001f, Segmenter.FrameLength).ToArray();
        var loud = Enumerable.Range(0, Segmenter.FrameLength)
            .Select(i => (float)(0.2 * Math.Sin(i * 0.5)))
            .ToArray();

        for (var i = 0; i < 7; i++)
        {
            segmenter.Push(quiet);
        }

        for (var i = 0; i < 17; i++)
        {
            segmenter.Push(loud);
        }

        for (var i = 0; i < 20; i++)
        {
            var ev = segmenter.Push(quiet);
            if (ev is { Kind: "utterance" })
            {
                return ev.Samples.Length > Segmenter.FrameLength;
            }
        }

        return false;
    }
}
