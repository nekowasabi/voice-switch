using System.Diagnostics;
using System.Text.Json;
using VoiceSwitch.Windows.Core;

namespace VoiceSwitch.Windows;

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

    // Why: Use Windows PowerShell System.Speech instead of a NuGet package so WSL builds stay offline.
    internal static ProcessStartInfo CreatePowerShell() => PowerShellScript.StartInfo("wake.ps1", Script);

    internal const string Script = """
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
