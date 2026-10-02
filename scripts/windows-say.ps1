param(
  [Parameter(Mandatory = $true)][string]$Text,
  [Parameter(Mandatory = $true)][string]$Output,
  [string]$Locale = "ja-JP"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Add-Type -AssemblyName System.Speech

$culture = [System.Globalization.CultureInfo]::GetCultureInfo($Locale)
$format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(
  16000,
  [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen,
  [System.Speech.AudioFormat.AudioChannel]::Mono)

$synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
try {
  $voice = $synth.GetInstalledVoices($culture) | Select-Object -First 1
  if ($null -eq $voice) {
    throw "No installed System.Speech synthesis voice for $Locale."
  }

  $synth.SelectVoice($voice.VoiceInfo.Name)
  $parent = Split-Path -Parent $Output
  if ($parent) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
  }

  $synth.SetOutputToWaveFile($Output, $format)
  $synth.Speak($Text)
} finally {
  $synth.Dispose()
}
