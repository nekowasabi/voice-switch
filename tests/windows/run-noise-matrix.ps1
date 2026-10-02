param(
  [Parameter(Mandatory = $true)][string]$ReleaseDir,
  [Parameter(Mandatory = $true)][string]$OutputRoot,
  [int]$PerCaseTimeoutSeconds = 45,
  [int]$OverallTimeoutMinutes = 90,
  [int]$MaxCases = 0,
  [string[]]$CaseId = @(),
  [switch]$FastStructural
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
if ($PSVersionTable.PSVersion.Major -lt 7) {
  throw "run-noise-matrix.ps1 requires PowerShell 7+ because it uses ProcessStartInfo.ArgumentList and async pipe draining."
}
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$fixtures = Join-Path $OutputRoot "fixtures"
$runs = Join-Path $OutputRoot "runs"
$configs = Join-Path $OutputRoot "configs"
function Initialize-FreshDirectory([string]$Path) {
  if (Test-Path $Path) {
    if (@(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
      throw "output directory must be fresh and empty: $Path"
    }
  } else {
    New-Item -ItemType Directory -Path $Path | Out-Null
  }
}

Initialize-FreshDirectory $OutputRoot
New-Item -ItemType Directory -Path $fixtures, $runs, $configs | Out-Null

function Normalize-Japanese([string]$Text) {
  if ($null -eq $Text) { return "" }
  return ([regex]::Replace($Text, "[\s\p{P}\p{S}]", "")).ToLowerInvariant()
}

function Get-Cer([string]$Expected, [string]$Actual) {
  $a = Normalize-Japanese $Expected
  $b = Normalize-Japanese $Actual
  if ($a.Length -eq 0) { return $(if ($b.Length -eq 0) { 0.0 } else { 1.0 }) }
  $prev = 0..$b.Length
  for ($i = 1; $i -le $a.Length; $i++) {
    $curr = New-Object int[] ($b.Length + 1)
    $curr[0] = $i
    for ($j = 1; $j -le $b.Length; $j++) {
      $cost = $(if ($a[$i - 1] -eq $b[$j - 1]) { 0 } else { 1 })
      $curr[$j] = [Math]::Min([Math]::Min($curr[$j - 1] + 1, $prev[$j] + 1), $prev[$j - 1] + $cost)
    }
    $prev = $curr
  }
  return [Math]::Round($prev[$b.Length] / [double]$a.Length, 6)
}

function Write-Config($Path, $Mode) {
  $base = Get-Content (Join-Path $repo "config.example.windows-dictation.json") -Raw | ConvertFrom-Json
  $base | Add-Member -Force -NotePropertyName noiseReduction -NotePropertyValue ([pscustomobject]@{
    mode = $Mode
    maxAttenuationDb = 6
  })
  $base | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $Path
}

function Invoke-Case($Case, [string]$Mode, [string]$ConfigPath, [string]$RunRoot) {
  $caseOut = Join-Path $RunRoot $Mode
  if (Test-Path $caseOut) { throw "case evidence directory already exists: $caseOut" }
  New-Item -ItemType Directory -Path $caseOut | Out-Null
  $evidence = Join-Path $caseOut "evidence.json"
  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = "dotnet"
  $psi.ArgumentList.Add((Join-Path $ReleaseDir "harness\SyntheticDictationRuntimeHarness.dll"))
  foreach ($arg in @("--config", $ConfigPath, "--wav", $Case.WavPath, "--output-dir", $caseOut, "--evidence", $evidence, "--expect", $Case.HarnessExpectationPath)) {
    $psi.ArgumentList.Add($arg)
  }
  if ($FastStructural) {
    $psi.ArgumentList.Add("--fast")
  }
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $stdoutTask = $p.StandardOutput.ReadToEndAsync()
  $stderrTask = $p.StandardError.ReadToEndAsync()
  $timedOut = $false
  try {
    if (-not $p.WaitForExit($PerCaseTimeoutSeconds * 1000)) {
      $timedOut = $true
      try { $p.Kill($true) } catch {}
      if (-not $p.WaitForExit(5000)) {
        throw "timeout $Mode $($Case.CaseId); killed process did not exit within bounded wait"
      }
    }
  } finally {
    $stdoutTask.GetAwaiter().GetResult() | Set-Content -Encoding UTF8 (Join-Path $caseOut "stdout.txt")
    $stderrTask.GetAwaiter().GetResult() | Set-Content -Encoding UTF8 (Join-Path $caseOut "stderr.txt")
    $p.Dispose()
  }
  if ($timedOut) {
    throw "timeout $Mode $($Case.CaseId)"
  }
  $ev = Get-Content $evidence -Raw | ConvertFrom-Json
  $actual = (($ev.RecognitionOutcomes | ForEach-Object { $_.Text }) -join "")
  $expected = ($Case.ExpectedJapaneseSpeech -join "")
  $expectedContract = Get-Content $Case.HarnessExpectationPath -Raw | ConvertFrom-Json
  $contractFailures = @($ev.ExpectationFailures)
  $handoffFinishReasons = @($ev.Handoffs | ForEach-Object { $_.FinishReason })
  $finishedByStandaloneStop = [bool]($handoffFinishReasons | Where-Object { $_ -eq "StandaloneStop" } | Select-Object -First 1)
  $recognizedStandaloneStop = [bool]($ev.RecognitionOutcomes | Where-Object { $_.StandaloneStop } | Select-Object -First 1)
  [pscustomobject]@{
    caseId = $Case.CaseId
    mode = $Mode
    exitCode = $p.ExitCode
    evidence = $evidence
    harnessExpectation = $Case.HarnessExpectationPath
    contractVerdict = $ev.ExpectationStatus
    contractFailures = $contractFailures
    expectedOutputCount = $expectedContract.ExpectedOutputCount
    expectedFinishReasons = $expectedContract.ExpectedFinishReasons
    actualText = $actual
    expectedText = $expected
    normalizedCer = Get-Cer $expected $actual
    wakeSuccess = [bool]($ev.RecognitionOutcomes | Where-Object { $_.LeadingWake } | Select-Object -First 1)
    recognizedStandaloneStop = $recognizedStandaloneStop
    standaloneStop = $finishedByStandaloneStop
    falseStopOnEmbedded = ($Case.CaseId -like "*embedded-stop*" -and $finishedByStandaloneStop)
    handoffCount = @($ev.Handoffs).Count
    handoffFinishReasons = $handoffFinishReasons
    outputCount = @($ev.Outputs).Count
    elapsedMilliseconds = $ev.ElapsedMilliseconds
    parentCpuMilliseconds = $ev.ParentCpuMilliseconds
    retainedSampleHighWater = $ev.RetainedSampleHighWater
    noiseStatus = $ev.NoiseStatus
  }
}

function Select-MatrixCases {
  param([object[]]$AllCases)

  if ($CaseId.Count -gt 0) {
    $selected = foreach ($id in $CaseId) {
      $match = @($AllCases | Where-Object { $_.CaseId -eq $id })
      if ($match.Count -ne 1) { throw "case id not found or ambiguous: $id" }
      $match[0]
    }
    return @($selected)
  }

  if ($MaxCases -le 0) {
    return @($AllCases)
  }

  $patterns = @(
    "wake-body-separate-stop-clean-control",
    "embedded-stop-clean-control",
    "consecutive-sessions-clean-control",
    "intentional-silence-clean-control",
    "late-stop-after-silence-clean-control",
    "*-white-snr10-*",
    "*-fan-snr10-*",
    "*-hum-snr10-*"
  )
  $selected = [System.Collections.Generic.List[object]]::new()
  foreach ($pattern in $patterns) {
    if ($selected.Count -ge $MaxCases) { break }
    $selectedIds = @($selected | ForEach-Object { $_.CaseId })
    $case = $AllCases | Where-Object { ($_.CaseId -like $pattern) -and ($selectedIds -notcontains $_.CaseId) } | Select-Object -First 1
    if ($null -ne $case) { $selected.Add($case) }
  }

  foreach ($case in $AllCases) {
    if ($selected.Count -ge $MaxCases) { break }
    if (-not ($selected | Where-Object { $_.CaseId -eq $case.CaseId })) {
      $selected.Add($case)
    }
  }

  return @($selected)
}

function Write-Summary($Path, $Results, [string]$Status) {
  [pscustomobject]@{
    createdAt = [DateTimeOffset]::UtcNow
    releaseDir = $ReleaseDir
    outputRoot = $OutputRoot
    perCaseTimeoutSeconds = $PerCaseTimeoutSeconds
    overallTimeoutMinutes = $OverallTimeoutMinutes
    inputPacing = $(if ($FastStructural) { "fast structural mode" } else { "paced production mode" })
    manifest = (Join-Path $fixtures "noise-fixtures.manifest.json")
    selectedCaseIds = @($cases | ForEach-Object { $_.CaseId })
    status = $Status
    results = $Results
    gate = "Zero new false stops/body source loss must be checked from paired evidence. RMS reduction alone is not recognition improvement."
  } | ConvertTo-Json -Depth 12 | Set-Content -Encoding UTF8 $Path
}

$offConfig = Join-Path $configs "off.config.json"
$treatmentConfig = Join-Path $configs "conservative-wiener.config.json"
Write-Config $offConfig "Off"
Write-Config $treatmentConfig "ConservativeWiener"

$benchmarkDll = Join-Path $ReleaseDir "noise-benchmark\NoiseBenchmark.dll"
$cleanFixtures = Join-Path $ReleaseDir "fixtures"
if (-not (Test-Path $cleanFixtures)) {
  & (Join-Path $PSScriptRoot "compose-fixtures.ps1") -OutputDir $cleanFixtures | Out-Null
}
dotnet $benchmarkDll `
  --fixtures-out $fixtures `
  --clean-dir $cleanFixtures `
  --dsp-out (Join-Path $OutputRoot "dsp-benchmark.json")

$manifest = Get-Content (Join-Path $fixtures "noise-fixtures.manifest.json") -Raw | ConvertFrom-Json
$cases = @(Select-MatrixCases -AllCases @($manifest.Cases))
$deadline = [DateTime]::UtcNow.AddMinutes($OverallTimeoutMinutes)
$results = [System.Collections.Generic.List[object]]::new()
$summaryPath = Join-Path $OutputRoot "noise-matrix-summary.json"
$orderSeed = [uint32]3235823838

foreach ($case in $cases) {
  if ([DateTime]::UtcNow -gt $deadline) { throw "overall timeout before $($case.CaseId)" }
  $caseRoot = Join-Path $runs $case.CaseId
  New-Item -ItemType Directory -Force -Path $caseRoot | Out-Null
  $orderSeed = $orderSeed -bxor ($orderSeed -shl 13)
  $orderSeed = $orderSeed -bxor ($orderSeed -shr 17)
  $orderSeed = $orderSeed -bxor ($orderSeed -shl 5)
  $modes = $(if (($orderSeed -band 1) -eq 0) { @("Off", "ConservativeWiener") } else { @("ConservativeWiener", "Off") })
  foreach ($mode in $modes) {
    $config = $(if ($mode -eq "Off") { $offConfig } else { $treatmentConfig })
    $results.Add((Invoke-Case $case $mode $config $caseRoot))
    Write-Summary $summaryPath $results "partial"
  }
}

Write-Summary $summaryPath $results "complete"
$regressions = @(
  $results | Where-Object {
    $_.falseStopOnEmbedded -or
    ($_.caseId -like "*clean-control" -and $_.contractVerdict -eq "failed") -or
    ($null -ne $_.expectedOutputCount -and $_.outputCount -gt $_.expectedOutputCount) -or
    (@($_.contractFailures) | Where-Object {
      $_ -match "hash|sample count|source range|output PCM"
    })
  }
)
if ($regressions.Count -gt 0) {
  $regressions | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $OutputRoot "noise-matrix-regressions.json")
  throw "noise matrix integrity/false-stop/safety regression count: $($regressions.Count)"
}
Write-Host "noise matrix summary: $(Join-Path $OutputRoot "noise-matrix-summary.json")"
