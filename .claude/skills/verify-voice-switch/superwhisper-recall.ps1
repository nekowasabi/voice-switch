param(
  [Parameter(Mandatory = $true)][string]$Cases,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [string]$ModeKey = "new-mode-1",
  [int[]]$PadMs = @(0),
  [int]$TimeoutSeconds = 90
)

# Sends each case WAV through the real Superwhisper file intake and counts expected phrases missing from rawResult.
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$app = Join-Path $env:LOCALAPPDATA "com.superwhisper.app"
$recordings = Join-Path $app "recordings"
$preferences = Join-Path $app "preferences.json"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Get-ActiveMode { ((Get-Content -Raw -Encoding UTF8 $preferences) | ConvertFrom-Json).activeMode }

function Get-SuperwhisperExe {
  $command = (Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\Applications\Superwhisper.exe\shell\open\command").'(default)'
  if ($command.StartsWith('"')) { return $command.Substring(1, $command.IndexOf('"', 1) - 1) }
  return $command.Split(' ')[0]
}

function Invoke-Superwhisper([string]$Argument) {
  Start-Process -FilePath $script:exe -ArgumentList $Argument -WindowStyle Hidden | Out-Null
}

function Set-Mode([string]$Key) {
  if ((Get-ActiveMode) -eq $Key) { return }
  Invoke-Superwhisper ("superwhisper://mode?key=" + [Uri]::EscapeDataString($Key))
  for ($i = 0; $i -lt 50; $i++) { Start-Sleep -Milliseconds 100; if ((Get-ActiveMode) -eq $Key) { return } }
  throw "Superwhisper did not switch to mode $Key"
}

function Write-PaddedWav([string]$Source, [string]$Target, [int]$Ms) {
  $bytes = [IO.File]::ReadAllBytes($Source)
  $offset = 12
  $data = $null
  $rate = 16000; $channels = 1; $bits = 16
  while ($offset + 8 -le $bytes.Length) {
    $id = [Text.Encoding]::ASCII.GetString($bytes, $offset, 4)
    $len = [BitConverter]::ToInt32($bytes, $offset + 4)
    if ($id -eq "fmt ") {
      $channels = [BitConverter]::ToInt16($bytes, $offset + 10)
      $rate = [BitConverter]::ToInt32($bytes, $offset + 12)
      $bits = [BitConverter]::ToInt16($bytes, $offset + 22)
    } elseif ($id -eq "data") {
      $data = New-Object byte[] $len
      [Array]::Copy($bytes, $offset + 8, $data, 0, $len)
    }
    $offset += 8 + $len + ($len % 2)
  }
  if ($null -eq $data -or $bits -ne 16) { throw "PCM16 WAV required: $Source" }
  $pad = New-Object byte[] ([int]($rate * $channels * 2 * $Ms / 1000))
  $body = New-Object byte[] ($pad.Length + $data.Length)
  [Array]::Copy($data, 0, $body, $pad.Length, $data.Length)
  $stream = [IO.MemoryStream]::new()
  $writer = [IO.BinaryWriter]::new($stream)
  $writer.Write([Text.Encoding]::ASCII.GetBytes("RIFF")); $writer.Write([int](36 + $body.Length))
  $writer.Write([Text.Encoding]::ASCII.GetBytes("WAVEfmt ")); $writer.Write([int]16); $writer.Write([int16]1)
  $writer.Write([int16]$channels); $writer.Write([int]$rate); $writer.Write([int]($rate * $channels * 2))
  $writer.Write([int16]($channels * 2)); $writer.Write([int16]16)
  $writer.Write([Text.Encoding]::ASCII.GetBytes("data")); $writer.Write([int]$body.Length); $writer.Write($body)
  [IO.File]::WriteAllBytes($Target, $stream.ToArray())
}

function Normalize([string]$Text) { if ($null -eq $Text) { return "" } ($Text -replace "[\s\p{P}]", "") }

$script:exe = Get-SuperwhisperExe
$caseList = (Get-Content -Raw -Encoding UTF8 $Cases) | ConvertFrom-Json
$previousMode = Get-ActiveMode
$results = @()
try {
  Set-Mode $ModeKey
  foreach ($case in $caseList) {
    foreach ($ms in $PadMs) {
      $name = "$($case.name)-pad$ms"
      $wav = Join-Path $OutDir "$name.wav"
      Write-PaddedWav $case.wav $wav $ms
      $before = @(Get-ChildItem -Directory $recordings | ForEach-Object Name)
      Invoke-Superwhisper ("superwhisper://file//" + $wav)
      $meta = $null
      $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
      while ($null -eq $meta -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 300
        foreach ($dir in Get-ChildItem -Directory $recordings | Where-Object { $before -notcontains $_.Name }) {
          $path = Join-Path $dir.FullName "meta.json"
          if (Test-Path $path) {
            try { $candidate = (Get-Content -Raw -Encoding UTF8 $path) | ConvertFrom-Json } catch { continue }
            if ($candidate.rawResult -or $candidate.result) { $meta = $candidate }
          }
        }
      }
      $raw = if ($null -eq $meta) { $null } else { $meta.rawResult }
      $missing = @()
      foreach ($phrase in $case.phrases) {
        $hit = $false
        foreach ($alt in $phrase) { if ((Normalize $raw).Contains((Normalize $alt))) { $hit = $true } }
        if (-not $hit) { $missing += ($phrase -join "|") }
      }
      $result = [pscustomobject]@{
        case = $case.name; padMs = $ms; phrases = @($case.phrases).Count; dropped = $missing.Count
        missing = $missing; model = $(if ($meta) { $meta.modelKey } else { $null }); rawResult = $raw; llmResult = $(if ($meta) { $meta.llmResult } else { $null })
        timedOut = ($null -eq $meta)
      }
      $results += $result
      Write-Host ("{0} pad={1}ms model={2} dropped={3}/{4} missing={5}" -f $case.name, $ms, $result.model, $missing.Count, @($case.phrases).Count, ($missing -join ","))
    }
  }
} finally {
  try { Set-Mode $previousMode } catch { Write-Warning "mode restore failed: $($_.Exception.Message)" }
}

$summary = foreach ($ms in $PadMs) {
  $rows = @($results | Where-Object padMs -eq $ms)
  [pscustomobject]@{ padMs = $ms; cases = $rows.Count; phrases = ($rows | Measure-Object phrases -Sum).Sum
    dropped = ($rows | Measure-Object dropped -Sum).Sum; timedOut = @($rows | Where-Object timedOut).Count }
}
$results | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $OutDir "results.json")
$summary | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $OutDir "summary.json")
foreach ($row in $summary) { Write-Host ("KPI pad={0}ms dropped={1}/{2} timedOut={3}" -f $row.padMs, $row.dropped, $row.phrases, $row.timedOut) }
if (@($summary | Where-Object { $_.dropped -gt 0 -or $_.timedOut -gt 0 }).Count -gt 0) { exit 1 }
exit 0
