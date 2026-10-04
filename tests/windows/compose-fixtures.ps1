param(
  [Parameter(Mandatory = $true)][string]$OutputDir,
  [string]$Locale = "ja-JP"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$say = Join-Path $repo "scripts\windows-say.ps1"
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

function Read-Pcm16Mono16k($Path) {
  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ($bytes.Length -lt 44) { throw "WAV too short: $Path" }
  $riff = [System.Text.Encoding]::ASCII.GetString($bytes, 0, 4)
  $wave = [System.Text.Encoding]::ASCII.GetString($bytes, 8, 4)
  if ($riff -ne "RIFF" -or $wave -ne "WAVE") { throw "Not a RIFF/WAVE file: $Path" }
  $offset = 12
  $okFmt = $false
  $pcm = $null
  while ($offset + 8 -le $bytes.Length) {
    $chunk = [System.Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
    $len = [BitConverter]::ToInt32($bytes, $offset + 4)
    $offset += 8
    if ($offset + $len -gt $bytes.Length) { throw "Bad WAV chunk length: $Path" }
    if ($chunk -eq "fmt ") {
      $formatTag = [BitConverter]::ToInt16($bytes, $offset)
      $channels = [BitConverter]::ToInt16($bytes, $offset + 2)
      $rate = [BitConverter]::ToInt32($bytes, $offset + 4)
      $bits = [BitConverter]::ToInt16($bytes, $offset + 14)
      $okFmt = ($formatTag -eq 1 -and $channels -eq 1 -and $rate -eq 16000 -and $bits -eq 16)
    } elseif ($chunk -eq "data") {
      $pcm = New-Object byte[] $len
      [Array]::Copy($bytes, $offset, $pcm, 0, $len)
    }
    $offset += $len
    if (($len % 2) -eq 1) { $offset += 1 }
  }
  if (-not $okFmt -or $null -eq $pcm -or ($pcm.Length % 2) -ne 0) {
    throw "Fixture WAV must be PCM16 mono 16 kHz: $Path"
  }
  $samples = New-Object Int16[] ($pcm.Length / 2)
  [Buffer]::BlockCopy($pcm, 0, $samples, 0, $pcm.Length)
  return $samples
}

function Write-Wav($Path, [Int16[]]$Samples) {
  $dataLength = $Samples.Length * 2
  $stream = [System.IO.File]::Create($Path)
  $writer = [System.IO.BinaryWriter]::new($stream, [System.Text.Encoding]::ASCII)
  try {
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
    $writer.Write([int](36 + $dataLength))
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
    $writer.Write([int]16)
    $writer.Write([int16]1)
    $writer.Write([int16]1)
    $writer.Write([int]16000)
    $writer.Write([int]32000)
    $writer.Write([int16]2)
    $writer.Write([int16]16)
    $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
    $writer.Write([int]$dataLength)
    foreach ($sample in $Samples) { $writer.Write([int16]$sample) }
  } finally {
    $writer.Dispose()
    $stream.Dispose()
  }
}

function Silence($Ms, $NoiseAmplitude, $Seed) {
  $count = [int][Math]::Round(16000 * $Ms / 1000.0)
  $samples = New-Object Int16[] $count
  if ($NoiseAmplitude -le 0) { return $samples }
  $rng = [System.Random]::new($Seed)
  for ($i = 0; $i -lt $samples.Length; $i++) {
    $samples[$i] = [int16]$rng.Next(-$NoiseAmplitude, $NoiseAmplitude + 1)
  }
  return $samples
}

function Stable-Fnv1a32($Text) {
  [uint64]$hash = 2166136261
  foreach ($ch in $Text.ToCharArray()) {
    $hash = $hash -bxor [uint64][int][char]$ch
    $hash = ($hash * [uint64]16777619) -band [uint64]4294967295
  }
  return [uint32]$hash
}

function Convert-UInt32ToInt32Seed([uint32]$Value) {
  if ($Value -le [uint32][int]::MaxValue) {
    return [int]$Value
  }

  return [int]([int64]$Value - 4294967296)
}

function Assert-StableFnv1a32Vectors() {
  $vectors = @(
    @{ text = ""; hash = [uint32]2166136261 },
    @{ text = "a"; hash = [uint32]3826002220 },
    @{ text = "foobar"; hash = [uint32]3214735720 }
  )
  foreach ($vector in $vectors) {
    $actual = Stable-Fnv1a32 $vector["text"]
    if ($actual -ne $vector["hash"]) {
      throw "FNV-1a 32-bit vector failed for '$($vector['text'])': expected $($vector['hash']), got $actual"
    }
  }
}

function Stable-Seed($Name, $Index) {
  $seed = ([uint64](Stable-Fnv1a32 $Name) -bxor ([uint64]$Index -band [uint64]4294967295)) -band [uint64]4294967295
  return (Convert-UInt32ToInt32Seed ([uint32]$seed))
}

Assert-StableFnv1a32Vectors

function Jp([int[]]$CodePoints) {
  $builder = [System.Text.StringBuilder]::new()
  foreach ($codePoint in $CodePoints) {
    [void]$builder.Append([char]::ConvertFromUtf32($codePoint))
  }
  return $builder.ToString()
}

function Trim-KnownSyntheticZeroPadding([Int16[]]$Samples) {
  $first = 0
  while ($first -lt $Samples.Length -and $Samples[$first] -eq 0) { $first += 1 }
  $last = $Samples.Length - 1
  while ($last -ge $first -and $Samples[$last] -eq 0) { $last -= 1 }
  if ($first -gt $last) {
    return [pscustomobject]@{ Samples = $Samples; Leading = 0; Trailing = 0; Original = $Samples.Length }
  }

  $count = $last - $first + 1
  $trimmed = New-Object Int16[] $count
  [Array]::Copy($Samples, $first, $trimmed, 0, $count)
  return [pscustomobject]@{
    Samples = $trimmed
    Leading = $first
    Trailing = $Samples.Length - $last - 1
    Original = $Samples.Length
  }
}

function Append-Samples([System.Collections.Generic.List[Int16]]$Target, [Int16[]]$Samples) {
  foreach ($sample in $Samples) { $Target.Add($sample) }
}

function Build-Fixture($Name, $Parts, $NoiseAmplitude) {
  $samples = [System.Collections.Generic.List[Int16]]::new()
  $segments = @()
  $index = 0
  foreach ($part in $Parts) {
    $start = $samples.Count
    $originalSamples = $null
    $trimmedLeadingZeros = 0
    $trimmedTrailingZeros = 0
    if ($part.kind -eq "speech") {
      $tmp = Join-Path $OutputDir "$Name-$index.tmp.wav"
      & $say -Text $part.text -Output $tmp -Locale $Locale
      $speech = Read-Pcm16Mono16k $tmp
      $trim = Trim-KnownSyntheticZeroPadding $speech
      $originalSamples = $trim.Original
      $trimmedLeadingZeros = $trim.Leading
      $trimmedTrailingZeros = $trim.Trailing
      Append-Samples $samples $trim.Samples
      Remove-Item $tmp -Force
    } else {
      Append-Samples $samples (Silence $part.ms $NoiseAmplitude (Stable-Seed $Name $index))
    }
    $end = $samples.Count
    $segments += [pscustomobject]@{
      kind = $part.kind
      text = $(if ($part.kind -eq "speech") { $part.text } else { $null })
      start = $start
      end = $end
      explicitSilenceMs = $(if ($part.kind -eq "silence") { $part.ms } else { $null })
      originalSamples = $originalSamples
      trimmedLeadingZeros = $trimmedLeadingZeros
      trimmedTrailingZeros = $trimmedTrailingZeros
      noiseAmplitude = $(if ($part.kind -eq "silence") { $NoiseAmplitude } else { 0 })
    }
    $index += 1
  }

  $suffix = if ($NoiseAmplitude -eq 0) { "clean" } else { "silence-noise$NoiseAmplitude" }
  $wav = Join-Path $OutputDir "$Name-$suffix.wav"
  $json = Join-Path $OutputDir "$Name-$suffix.json"
  Write-Wav $wav $samples.ToArray()
  [pscustomobject]@{
    name = $Name
    locale = $Locale
    sampleRate = 16000
    channels = 1
    bitsPerSample = 16
    noiseAmplitude = $NoiseAmplitude
    noiseMode = $(if ($NoiseAmplitude -eq 0) { "none" } else { "silence-only" })
    wav = $wav
    segments = $segments
  } | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 $json
}

$WakeBody = Jp @(0x97F3,0x58F0,0x5165,0x529B,0x20,0x4ECA,0x65E5,0x306F,0x6674,0x308C,0x3067,0x3059)
$Stop = Jp @(0x5165,0x529B,0x30B9,0x30C8,0x30C3,0x30D7)
$EmbeddedStop = Jp @(0x97F3,0x58F0,0x5165,0x529B,0x20,0x4ECA,0x65E5,0x306F,0x5165,0x529B,0x30B9,0x30C8,0x30C3,0x30D7,0x3067,0x306F,0x3042,0x308A,0x307E,0x305B,0x3093)
$OneBreath = Jp @(0x97F3,0x58F0,0x5165,0x529B,0x20,0x4ECA,0x65E5,0x306F,0x6674,0x308C,0x3067,0x3059,0x20,0x5165,0x529B,0x30B9,0x30C8,0x30C3,0x30D7)

$fixtures = @(
  @{
    name = "wake-body-separate-stop"
    parts = @(
      @{ kind = "silence"; ms = 300 },
      @{ kind = "speech"; text = $WakeBody },
      @{ kind = "silence"; ms = 900 },
      @{ kind = "speech"; text = $Stop },
      @{ kind = "silence"; ms = 1200 }
    )
  },
  @{
    name = "embedded-stop"
    parts = @(
      @{ kind = "silence"; ms = 300 },
      @{ kind = "speech"; text = $EmbeddedStop },
      @{ kind = "silence"; ms = 1200 }
    )
  },
  @{
    name = "natural-one-breath"
    parts = @(
      @{ kind = "silence"; ms = 300 },
      @{ kind = "speech"; text = $OneBreath },
      @{ kind = "silence"; ms = 1200 }
    )
  },
  @{
    name = "consecutive-sessions"
    parts = @(
      @{ kind = "silence"; ms = 300 },
      @{ kind = "speech"; text = $WakeBody },
      @{ kind = "silence"; ms = 900 },
      @{ kind = "speech"; text = $Stop },
      @{ kind = "silence"; ms = 600 },
      @{ kind = "speech"; text = $WakeBody },
      @{ kind = "silence"; ms = 900 },
      @{ kind = "speech"; text = $Stop },
      @{ kind = "silence"; ms = 1200 }
    )
  },
  @{
    name = "intentional-silence"
    parts = @(
      @{ kind = "silence"; ms = 2500 }
    )
  },
  @{
    name = "late-stop-after-silence"
    parts = @(
      @{ kind = "silence"; ms = 300 },
      @{ kind = "speech"; text = $WakeBody },
      @{ kind = "silence"; ms = 1500 },
      @{ kind = "speech"; text = $Stop },
      @{ kind = "silence"; ms = 1200 }
    )
  }
)

foreach ($fixture in $fixtures) {
  Build-Fixture $fixture.name $fixture.parts 0
  Build-Fixture $fixture.name $fixture.parts 2
}
