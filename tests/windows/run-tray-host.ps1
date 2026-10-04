param(
  [Parameter(Mandatory = $true)][string]$ReleaseDir,
  [Parameter(Mandatory = $true)][string]$OutputRoot,
  [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
if ($PSVersionTable.PSVersion.Major -lt 7) {
  throw "run-tray-host.ps1 requires PowerShell 7+."
}

$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$tray = Join-Path $ReleaseDir "voice-switch.exe"
if (-not (Test-Path $tray)) { throw "tray executable not found: $tray" }

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
$RunRoot = Join-Path $OutputRoot ("run with spaces " + [DateTimeOffset]::UtcNow.ToString("yyyyMMddTHHmmssfffZ"))
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$fixtures = Join-Path $RunRoot "fixtures with spaces"
& (Join-Path $PSScriptRoot "compose-fixtures.ps1") -OutputDir $fixtures | Out-Null
$fixture = Join-Path $fixtures "consecutive-sessions-clean.wav"

function Write-TrayConfig($Path, [string]$Wake = "音声入力") {
  @{
    wakeWords = @($Wake)
    locale = "ja_JP"
    command = "cmd /c exit 0"
    stopWords = @("入力ストップ")
    stopCommand = "cmd /c exit 0"
    dictation = @{
      startTimeoutMs = 3000
      endSilenceMs = 1200
      maxSeconds = 60
    }
  } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $Path
}

function Start-Tray($Config, $Wav, $OutDir) {
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $stdout = Join-Path $OutDir "host.stdout.txt"
  $stderr = Join-Path $OutDir "host.stderr.txt"
  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = $tray
  foreach ($arg in @("--config", $Config, "--paused", "--input-wav", $Wav, "--record-only", $OutDir)) { $psi.ArgumentList.Add($arg) }
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $p | Add-Member -NotePropertyName StdoutTask -NotePropertyValue $p.StandardOutput.ReadToEndAsync()
  $p | Add-Member -NotePropertyName StderrTask -NotePropertyValue $p.StandardError.ReadToEndAsync()
  $p | Add-Member -NotePropertyName StdoutPath -NotePropertyValue $stdout
  $p | Add-Member -NotePropertyName StderrPath -NotePropertyValue $stderr
  $p | Add-Member -NotePropertyName ExitCodePath -NotePropertyValue (Join-Path $OutDir "host.exitcode.txt")
  return $p
}

function Save-TrayProcessOutput($Handle) {
  if (-not $Handle.HasExited) { return }
  try { $Handle.StdoutTask.GetAwaiter().GetResult() | Set-Content -Encoding UTF8 $Handle.StdoutPath } catch {}
  try { $Handle.StderrTask.GetAwaiter().GetResult() | Set-Content -Encoding UTF8 $Handle.StderrPath } catch {}
  try { ([string]$Handle.ExitCode) | Set-Content -Encoding UTF8 $Handle.ExitCodePath } catch {}
}

function Invoke-TrayRaw($Config, $Command, [int]$TimeoutMilliseconds = 15000) {
  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = $tray
  foreach ($arg in @("--config", $Config, "--tray-command", $Command)) { $psi.ArgumentList.Add($arg) }
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $outTask = $p.StandardOutput.ReadToEndAsync()
  $errTask = $p.StandardError.ReadToEndAsync()
  if (-not $p.WaitForExit($TimeoutMilliseconds)) {
    try { $p.Kill($true) } catch {}
    throw "tray command timeout: $Command"
  }
  $stdout = $outTask.GetAwaiter().GetResult().Trim()
  $stderr = $errTask.GetAwaiter().GetResult().Trim()
  return [pscustomobject]@{ ExitCode = $p.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

function Invoke-Tray($Config, $Command) {
  $result = Invoke-TrayRaw $Config $Command
  if ($result.ExitCode -ne 0) { throw "tray command failed $Command exit=$($result.ExitCode) stderr=$($result.Stderr)" }
  return $result.Stdout | ConvertFrom-Json
}

function Start-TrayCommand($Config, $Command, $OutDir, [string]$Name) {
  New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
  $psi = [System.Diagnostics.ProcessStartInfo]::new()
  $psi.FileName = $tray
  foreach ($arg in @("--config", $Config, "--tray-command", $Command)) { $psi.ArgumentList.Add($arg) }
  $psi.RedirectStandardOutput = $true
  $psi.RedirectStandardError = $true
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $p = [System.Diagnostics.Process]::Start($psi)
  $p | Add-Member -NotePropertyName StdoutTask -NotePropertyValue $p.StandardOutput.ReadToEndAsync()
  $p | Add-Member -NotePropertyName StderrTask -NotePropertyValue $p.StandardError.ReadToEndAsync()
  $p | Add-Member -NotePropertyName StdoutPath -NotePropertyValue (Join-Path $OutDir "$Name.stdout.txt")
  $p | Add-Member -NotePropertyName StderrPath -NotePropertyValue (Join-Path $OutDir "$Name.stderr.txt")
  $p | Add-Member -NotePropertyName ExitCodePath -NotePropertyValue (Join-Path $OutDir "$Name.exitcode.txt")
  return $p
}

function Stop-OwnedTray($Handle, [string]$Config, [string]$Name) {
  try { Invoke-Tray $Config "quit" | Out-Null } catch {}
  if (-not $Handle.WaitForExit(15000)) {
    $failure = Join-Path $RunRoot "$Name-cleanup-failure.txt"
    "owned tray process did not exit after IPC quit; recovering with Kill(true)" | Set-Content -Encoding UTF8 $failure
    try { $Handle.Kill($true) } catch {}
    try { $Handle.WaitForExit(5000) | Out-Null } catch {}
    Save-TrayProcessOutput $Handle
    throw "$Name cleanup failed; recovery kill recorded at $failure"
  }

  Save-TrayProcessOutput $Handle
}

function Wait-State($Config, [string[]]$States, $HostProcess = $null, [string]$Name = "tray") {
  $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
  $lastError = $null
  do {
    if ($null -ne $HostProcess -and $HostProcess.HasExited) {
      Save-TrayProcessOutput $HostProcess
      throw "$Name exited while waiting for tray state $($States -join ', '); exit=$($HostProcess.ExitCode); stdout=$($HostProcess.StdoutPath); stderr=$($HostProcess.StderrPath)"
    }

    try {
      $status = Invoke-Tray $Config "status"
    } catch {
      $lastError = $_.Exception.Message
      Start-Sleep -Milliseconds 300
      continue
    }

    if ($States -contains $status.Snapshot.State) { return $status }
    Start-Sleep -Milliseconds 300
  } while ([DateTime]::UtcNow -lt $deadline)
  throw "timeout waiting for tray state: $($States -join ', '); last status error: $lastError"
}

function Assert($Condition, $Message) {
  if (-not $Condition) { throw $Message }
}

$config = Join-Path $RunRoot "owned config.json"
$recordings = Join-Path $RunRoot "owned recordings"
Write-TrayConfig $config
$raceConfig = Join-Path $RunRoot "command-first config.json"
$raceRecordings = Join-Path $RunRoot "command-first recordings"
Write-TrayConfig $raceConfig
$noServer = Invoke-TrayRaw $raceConfig "status" 12000
Assert ($noServer.ExitCode -eq 2) "status without a tray host returned exit=$($noServer.ExitCode)"
Assert ($noServer.Stderr -match "Start voice-switch.exe first") "status without a tray host was not actionable: $($noServer.Stderr)"
$raceClient = Start-TrayCommand $raceConfig "status" (Join-Path $RunRoot "command-first client") "status-first"
Start-Sleep -Milliseconds 300
$raceHost = Start-Tray $raceConfig $fixture $raceRecordings
try {
  Assert ($raceClient.WaitForExit(15000)) "status-first command did not finish after host startup"
  Save-TrayProcessOutput $raceClient
  Assert ($raceClient.ExitCode -eq 0) "status-first command failed; stderr=$($raceClient.StderrPath)"
  $raceStatus = (Get-Content -Raw $raceClient.StdoutPath).Trim() | ConvertFrom-Json
  Assert ($raceStatus.Snapshot.State -eq "Paused") "status-first command did not receive Paused"
  Assert (-not $raceHost.HasExited) "command-first host exited after serving status-first"
  Wait-State $raceConfig @("Paused") $raceHost "command-first host" | Out-Null
} finally {
  if ($raceHost.HasExited) {
    Save-TrayProcessOutput $raceHost
  } else {
    Stop-OwnedTray $raceHost $raceConfig "command-first-host"
  }
}
$hostProcess = Start-Tray $config $fixture $recordings
try {
  $status = Wait-State $config @("Paused") $hostProcess "main tray host"
  Assert $status.NotifyIconVisible "NotifyIcon.Visible was false"
  Assert (($status.MenuItems | Where-Object Name -eq "start").Enabled) "start menu item was not enabled while paused"
  Assert (($status.MenuItems | Where-Object Name -eq "pause").Enabled -eq $false) "pause menu item was enabled while paused"

  Invoke-Tray $config "start" | Out-Null
  Invoke-Tray $config "start" | Out-Null
  $finished = Wait-State $config @("Finished") $hostProcess "main tray host"
  Assert (@(Get-ChildItem $recordings -Filter *.wav).Count -eq 2) "first run did not record two local WAV outputs"
  Assert ($finished.ShellRegistration -ne "registered" -or $finished.NotifyIconVisible) "shell registration reported but managed icon was hidden"

  Invoke-Tray $config "pause" | Out-Null
  Invoke-Tray $config "start" | Out-Null
  Wait-State $config @("Finished") $hostProcess "main tray host" | Out-Null
  Assert (@(Get-ChildItem $recordings -Filter *.wav).Count -eq 4) "resume run did not repeat two local outputs"

  "{ `"wakeWords`": [`"。`"], `"command`": `"wake`", `"dictation`": {} }" | Set-Content -Encoding UTF8 $config
  $invalid = Invoke-Tray $config "reload"
  Assert ($null -ne $invalid.Snapshot.LastError) "invalid reload did not preserve a visible error"
  Write-TrayConfig $config "別の起動語"
  Invoke-Tray $config "reload" | Out-Null

  $beforeDuplicate = @(Get-ChildItem $recordings -Filter *.wav).Count
  $dupPsi = [System.Diagnostics.ProcessStartInfo]::new()
  $dupPsi.FileName = $tray
  foreach ($arg in @("--config", $config)) { $dupPsi.ArgumentList.Add($arg) }
  $dupPsi.UseShellExecute = $false
  $dupPsi.CreateNoWindow = $true
  $dup = [System.Diagnostics.Process]::Start($dupPsi)
  if (-not $dup.WaitForExit(10000)) {
    try { $dup.Kill($true) } catch {}
    throw "duplicate launch did not exit"
  }
  Assert (@(Get-ChildItem $recordings -Filter *.wav).Count -eq $beforeDuplicate) "duplicate launch created output"

  $badConfig = Join-Path $RunRoot "bad-config.json"
  $badOut = Join-Path $RunRoot "bad-recordings"
  Write-TrayConfig $badConfig
  $badHost = Start-Tray $badConfig (Join-Path $RunRoot "missing.wav") $badOut
  try {
    Wait-State $badConfig @("Paused") $badHost "missing-wav host" | Out-Null
    Invoke-Tray $badConfig "start" | Out-Null
    $badStatus = Wait-State $badConfig @("Error") $badHost "missing-wav host"
    $badStatusPath = Join-Path $RunRoot "missing-wav-status.json"
    $badStatus | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $badStatusPath
    Assert ($badStatus.Snapshot.State -eq "Error") "missing WAV did not enter Error state"
    Assert ($badStatus.Snapshot.LastError -eq "input WAV was not found.") "missing WAV did not surface the product error contract; see $badStatusPath"
    Assert ($null -eq $badStatus.Snapshot.OwnedChildProcessId) "missing WAV left a child process identity; see $badStatusPath"
    Assert ($null -eq $badStatus.Snapshot.OwnedChildProcessStartTimeUtc) "missing WAV left a child process start time; see $badStatusPath"
    Assert (($badStatus.MenuItems | Where-Object Name -eq "error").Enabled) "missing WAV did not enable the recent-error menu; see $badStatusPath"
    Assert (-not (Test-Path $badOut) -or @(Get-ChildItem $badOut -Filter *.wav).Count -eq 0) "missing WAV run created output"
  } finally {
    Stop-OwnedTray $badHost $badConfig "missing-wav"
  }

  $quitConfig = Join-Path $RunRoot "quit-config.json"
  $quitOut = Join-Path $RunRoot "quit-recordings"
  Write-TrayConfig $quitConfig
  $quitHost = Start-Tray $quitConfig $fixture $quitOut
  $quitHostFailure = $null
  try {
    Wait-State $quitConfig @("Paused") $quitHost "quit host" | Out-Null
    Invoke-Tray $quitConfig "start" | Out-Null
    Invoke-Tray $quitConfig "quit" | Out-Null
    Assert ($quitHost.WaitForExit(15000)) "quit while processing did not stop the host"
  } catch {
    $quitHostFailure = $_
    throw
  } finally {
    if ($quitHost.HasExited) {
      Save-TrayProcessOutput $quitHost
    } else {
      try {
        Stop-OwnedTray $quitHost $quitConfig "quit-host"
      } catch {
        if ($null -eq $quitHostFailure) { throw }
        Write-Warning "quit-host cleanup failed while preserving original failure: $($_.Exception.Message)"
      }
    }
  }

  Invoke-Tray $config "quit" | Out-Null
  Assert ($hostProcess.WaitForExit(15000)) "main tray host did not exit after quit"
  Save-TrayProcessOutput $hostProcess
  $summary = [pscustomobject]@{
    createdAt = [DateTimeOffset]::UtcNow
    outputRoot = $RunRoot
    shellRegistration = $finished.ShellRegistration
    notifyIconVisible = $finished.NotifyIconVisible
    finalOutputCount = @(Get-ChildItem $recordings -Filter *.wav).Count
    duplicateExitCode = $dup.ExitCode
  }
  $summary | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $RunRoot "tray-host-summary.json")
  Write-Host "tray host summary: $(Join-Path $RunRoot "tray-host-summary.json")"
} finally {
  if ($hostProcess.HasExited) {
    Save-TrayProcessOutput $hostProcess
  } else {
    Stop-OwnedTray $hostProcess $config "main-host"
  }
}
