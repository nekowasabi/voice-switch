param(
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int[]]$FadeDb = @(0, -6, -12, -18, -24, -30)
)

# Wake word, then a body, with only the last mora of the wake attenuated: a weak, natural word ending.
$ErrorActionPreference = "Stop"
$say = Join-Path $PSScriptRoot "..\..\..\scripts\windows-say.ps1"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$wakes = @(
  @{ name = "onsei-nyuuryoku"; text = ([string][char]0x97F3 + [char]0x58F0 + [char]0x5165 + [char]0x529B); mora = 8 },
  @{ name = "onsei-ni-hairu"; text = ([string][char]0x97F3 + [char]0x58F0 + [char]0x306B + [char]0x5165 + [char]0x308B); mora = 8 },
  @{ name = "input"; text = ([string][char]0x30A4 + [char]0x30F3 + [char]0x30D7 + [char]0x30C3 + [char]0x30C8); mora = 5 },
  @{ name = "raito"; text = ([string][char]0x30E9 + [char]0x30A4 + [char]0x30C8); mora = 3 }
)
$body = [string][char]0x4ECA + [char]0x65E5 + [char]0x306F + [char]0x6674 + [char]0x308C + [char]0x3067 + [char]0x3059

function Read-Samples([string]$Path) {
  $bytes = [IO.File]::ReadAllBytes($Path); $offset = 12
  while ($offset + 8 -le $bytes.Length) {
    $id = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4); $len = [BitConverter]::ToInt32($bytes, $offset + 4)
    if ($id -eq "data") { $s = New-Object int16[] ($len / 2); [Buffer]::BlockCopy($bytes, $offset + 8, $s, 0, $len); return , $s }
    $offset += 8 + $len + ($len % 2)
  }
  throw "no data: $Path"
}

function Voiced([int16[]]$s) {
  $first = 0; $last = $s.Length - 1
  while ($first -lt $s.Length -and [Math]::Abs($s[$first]) -lt 300) { $first++ }
  while ($last -gt $first -and [Math]::Abs($s[$last]) -lt 300) { $last-- }
  # Keep 50 ms before the first loud sample: a soft vowel onset sits below the threshold.
  $first = [Math]::Max(0, $first - 800); $last = [Math]::Min($s.Length - 1, $last + 800)
  , [int16[]]$s[$first..$last]
}

function Write-Wav([string]$Path, [int16[]]$s) {
  $data = New-Object byte[] ($s.Length * 2); [Buffer]::BlockCopy($s, 0, $data, 0, $data.Length)
  $ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
  $w.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $w.Write([int](36 + $data.Length))
  $w.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt ")); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
  $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
  $w.Write([Text.Encoding]::ASCII.GetBytes("data")); $w.Write([int]$data.Length); $w.Write($data)
  [IO.File]::WriteAllBytes($Path, $ms.ToArray())
}

$tmpBody = Join-Path $OutDir "body.tmp.wav"
& $say -Text $body -Output $tmpBody
[int16[]]$bodySamples = Voiced (Read-Samples $tmpBody)
Remove-Item $tmpBody
foreach ($wake in $wakes) {
  $tmp = Join-Path $OutDir "$($wake.name).tmp.wav"
  & $say -Text $wake.text -Output $tmp
  [int16[]]$wakeSamples = Voiced (Read-Samples $tmp)
  Remove-Item $tmp
  # ponytail: the last mora is approximated as an equal share of the voiced length.
  $tailStart = [int]($wakeSamples.Length * ($wake.mora - 1) / $wake.mora)
  foreach ($db in $FadeDb) {
    $gain = [Math]::Pow(10, $db / 20.0)
    $faded = [int16[]]$wakeSamples.Clone()
    for ($i = $tailStart; $i -lt $faded.Length; $i++) { $faded[$i] = [int16]([Math]::Round($faded[$i] * $gain)) }
    $out = New-Object System.Collections.Generic.List[int16]
    $out.AddRange((New-Object int16[] 4800)); $out.AddRange($faded); $out.AddRange((New-Object int16[] 9600))
    $out.AddRange($bodySamples); $out.AddRange((New-Object int16[] 32000))
    Write-Wav (Join-Path $OutDir ("wakefade-{0}-{1}db.wav" -f $wake.name, [Math]::Abs($db))) $out.ToArray()
  }
}
Get-ChildItem $OutDir -Filter "wakefade-*.wav" | ForEach-Object Name

# Stop words: a clean wake and body, then the stop word with its last mora attenuated.
$stops = @(
  @{ name = "stop"; text = ([string][char]0x30B9 + [char]0x30C8 + [char]0x30C3 + [char]0x30D7); mora = 4 },
  @{ name = "nyuuryoku-stop"; text = ([string][char]0x5165 + [char]0x529B + [char]0x30B9 + [char]0x30C8 + [char]0x30C3 + [char]0x30D7); mora = 8 }
)
$tmpWake = Join-Path $OutDir "wake.tmp.wav"
& $say -Text $wakes[0].text -Output $tmpWake
[int16[]]$cleanWake = Voiced (Read-Samples $tmpWake)
Remove-Item $tmpWake
foreach ($stop in $stops) {
  $tmp = Join-Path $OutDir "$($stop.name).tmp.wav"
  & $say -Text $stop.text -Output $tmp
  [int16[]]$stopSamples = Voiced (Read-Samples $tmp)
  Remove-Item $tmp
  $tailStart = [int]($stopSamples.Length * ($stop.mora - 1) / $stop.mora)
  foreach ($db in $FadeDb) {
    $gain = [Math]::Pow(10, $db / 20.0)
    $faded = [int16[]]$stopSamples.Clone()
    for ($i = $tailStart; $i -lt $faded.Length; $i++) { $faded[$i] = [int16]([Math]::Round($faded[$i] * $gain)) }
    $out = New-Object System.Collections.Generic.List[int16]
    $out.AddRange((New-Object int16[] 4800)); $out.AddRange($cleanWake); $out.AddRange((New-Object int16[] 9600))
    $out.AddRange($bodySamples); $out.AddRange((New-Object int16[] 14400)); $out.AddRange($faded); $out.AddRange((New-Object int16[] 32000))
    Write-Wav (Join-Path $OutDir ("stopfade-{0}-{1}db.wav" -f $stop.name, [Math]::Abs($db))) $out.ToArray()
  }
}

# Short phrases that are not stop words, spoken where a stop word would be: measures stop false positives.
$negatives = (Get-Content -Raw -Encoding UTF8 (Join-Path $PSScriptRoot "stop-negatives.json")) | ConvertFrom-Json
$n = 0
foreach ($phrase in $negatives) {
  $tmp = Join-Path $OutDir "neg.tmp.wav"
  & $say -Text $phrase -Output $tmp
  [int16[]]$negSamples = Voiced (Read-Samples $tmp)
  Remove-Item $tmp
  $out = New-Object System.Collections.Generic.List[int16]
  $out.AddRange((New-Object int16[] 4800)); $out.AddRange($cleanWake); $out.AddRange((New-Object int16[] 9600))
  $out.AddRange($bodySamples); $out.AddRange((New-Object int16[] 14400)); $out.AddRange($negSamples); $out.AddRange((New-Object int16[] 32000))
  Write-Wav (Join-Path $OutDir ("stopneg-{0:d2}.wav" -f $n)) $out.ToArray()
  $n++
}
