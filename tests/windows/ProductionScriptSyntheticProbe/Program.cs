using System.Diagnostics;
using System.Reflection;
using System.Text;
using VoiceSwitch.Windows;
using VoiceSwitch.Windows.Core;

var configPath = GetOption(args, "--config")
    ?? throw new ArgumentException("--config requires a path.");
var phrase = GetOption(args, "--phrase") ?? "音声入力";
var simulateBug = args.Contains("--simulate-bug");
var config = ConfigLoader.Load(configPath);
var script = PatchInput(ProductionScript());
if (simulateBug)
{
    script = SimulateBug(script);
}

using var speech = Start(script, config, phrase);
if (!WaitReady(speech))
{
    Console.Error.WriteLine("probe: recognizer did not report ready");
    return 1;
}

var deadline = DateTime.UtcNow.AddSeconds(8);
while (DateTime.UtcNow < deadline)
{
    var line = speech.ReadLine(TimeSpan.FromMilliseconds(250));
    if (line is null)
    {
        continue;
    }

    Console.WriteLine(line);
    if (!RecognizerMessage.TryParse(line, out var message) || message.Type != "recognized" || string.IsNullOrEmpty(message.Text))
    {
        continue;
    }

    var decision = TextMatching.Decide(message.Text, config);
    Console.WriteLine($"probe-decision: kind={decision.Kind} text={decision.Text} reason={decision.Reason}");
    if (simulateBug)
    {
        return decision.Kind == "run-command" ? 4 : 0;
    }

    return decision.Kind == "run-command" ? 0 : 2;
}

Console.Error.WriteLine("probe: no recognized phrase before timeout");
return simulateBug ? 0 : 3;

static SpeechProcess Start(string script, VoiceSwitchConfig config, string phrase)
{
    var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -OutputFormat Text -EncodedCommand {encoded}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    psi.Environment["VOICE_SWITCH_LOCALE"] = config.EffectiveLocale;
    psi.Environment["VOICE_SWITCH_WAKE_WORDS"] = ConfigLoader.ToJsonArray(config.WakeWords.Concat(config.StopWords ?? []));
    psi.Environment["VOICE_SWITCH_SYNTH_PHRASE"] = phrase;
    var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start powershell.exe");
    return new SpeechProcess(process);
}

static bool WaitReady(SpeechProcess speech)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (DateTime.UtcNow < deadline)
    {
        var line = speech.ReadLine(TimeSpan.FromMilliseconds(250));
        if (line is null)
        {
            continue;
        }

        Console.WriteLine(line);
        if (line.Contains("recognizer ready:", StringComparison.Ordinal))
        {
            return true;
        }
    }

    return false;
}

static string ProductionScript()
{
    var field = typeof(SpeechPowerShell).GetField("Script", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingFieldException(nameof(SpeechPowerShell), "Script");
    return (string)(field.GetValue(null) ?? throw new InvalidOperationException("SpeechPowerShell.Script is null"));
}

static string SimulateBug(string script)
{
    const string fixedBlock = """
if ($env:VOICE_SWITCH_WAKE_WORDS) {
  $parsedWords = $env:VOICE_SWITCH_WAKE_WORDS | ConvertFrom-Json
  $words = @($parsedWords | ForEach-Object { [string]$_ })
}
""";
    const string oldBlock = """
if ($env:VOICE_SWITCH_WAKE_WORDS) { $words = @($env:VOICE_SWITCH_WAKE_WORDS | ConvertFrom-Json) }
""";

    var patched = script.Replace(fixedBlock, oldBlock);
    if (patched == script)
    {
        throw new InvalidOperationException("probe could not restore the old word parsing bug");
    }

    return patched;
}

static string PatchInput(string script)
{
    const string original = "$engine.SetInputToDefaultAudioDevice()";
    const string replacement = """
$synthStream = [System.IO.MemoryStream]::new()
$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
$synth.SelectVoiceByHints(
  [System.Speech.Synthesis.VoiceGender]::NotSet,
  [System.Speech.Synthesis.VoiceAge]::NotSet,
  0,
  $info.Culture)
$synth.SetOutputToWaveStream($synthStream)
$synth.Speak($env:VOICE_SWITCH_SYNTH_PHRASE)
$synth.Dispose()
$synthStream.Position = 0
$engine.SetInputToWaveStream($synthStream)
""";

    var patched = script.Replace(original, replacement);
    if (patched == script)
    {
        throw new InvalidOperationException("probe could not replace production input selection");
    }

    return patched;
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
