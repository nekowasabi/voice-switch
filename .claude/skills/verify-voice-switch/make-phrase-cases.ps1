param(
  [Parameter(Mandatory = $true)][string]$Spec,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int]$LeadMs = 300
)

# Speaks each spec part with Windows TTS, joins the parts with the given pauses, and writes <OutDir>\cases.json
# in the shape superwhisper-recall.ps1 reads.
$ErrorActionPreference = "Stop"
$say = Join-Path $PSScriptRoot "..\..\..\scripts\windows-say.ps1"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Read-Pcm([string]$Path) {
  $bytes = [IO.File]::ReadAllBytes($Path)
  $offset = 12
  while ($offset + 8 -le $bytes.Length) {
    $id = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
    $len = [BitConverter]::ToInt32($bytes, $offset + 4)
    if ($id -eq "data") { $data = New-Object byte[] $len; [Array]::Copy($bytes, $offset + 8, $data, 0, $len); return $data }
    $offset += 8 + $len + ($len % 2)
  }
  throw "no data chunk: $Path"
}

function Silence([int]$Ms) { New-Object byte[] (32 * $Ms) }

$cases = @()
foreach ($case in ((Get-Content -Raw -Encoding UTF8 $Spec) | ConvertFrom-Json)) {
  $body = [IO.MemoryStream]::new()
  $lead = Silence $LeadMs; $body.Write($lead, 0, $lead.Length)
  $phrases = @()
  $index = 0
  foreach ($part in $case.parts) {
    if ($part.pauseMs) { $gap = Silence $part.pauseMs; $body.Write($gap, 0, $gap.Length); continue }
    $tmp = Join-Path $OutDir "$($case.name)-$index.tmp.wav"
    & $say -Text $part.text -Output $tmp
    $pcm = Read-Pcm $tmp
    $body.Write($pcm, 0, $pcm.Length)
    Remove-Item $tmp
    $phrases += , @($part.expect)
    $index++
  }
  $tail = Silence 600; $body.Write($tail, 0, $tail.Length)
  $data = $body.ToArray()
  $wav = Join-Path $OutDir "$($case.name).wav"
  $stream = [IO.MemoryStream]::new()
  $w = [IO.BinaryWriter]::new($stream)
  $w.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $w.Write([int](36 + $data.Length))
  $w.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt ")); $w.Write([int]16); $w.Write([int16]1); $w.Write([int16]1)
  $w.Write([int]16000); $w.Write([int]32000); $w.Write([int16]2); $w.Write([int16]16)
  $w.Write([Text.Encoding]::ASCII.GetBytes("data")); $w.Write([int]$data.Length); $w.Write($data)
  [IO.File]::WriteAllBytes($wav, $stream.ToArray())
  $cases += [pscustomobject]@{ name = $case.name; wav = $wav; phrases = $phrases }
}
$cases | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $OutDir "cases.json")
Write-Host "wrote $($cases.Count) cases to $(Join-Path $OutDir 'cases.json')"
