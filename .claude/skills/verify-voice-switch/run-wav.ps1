param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Config,
  [Parameter(Mandatory = $true)][string]$Wav,
  [Parameter(Mandatory = $true)][string]$OutDir,
  [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $Exe
$psi.Arguments = "--config `"$Config`" --input-wav `"$Wav`" --output-dir `"$(Join-Path $OutDir 'handoffs')`""
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.StandardOutputEncoding = [System.Text.Encoding]::UTF8
$p = [System.Diagnostics.Process]::Start($psi)
[string]$p.Id | Set-Content -Encoding ASCII (Join-Path $OutDir "pid.txt")
$out = $p.StandardOutput.ReadToEndAsync()
$err = $p.StandardError.ReadToEndAsync()
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
  # Only the process this script started is killed.
  try { $p.Kill($true) } catch {}
  "timeout after $TimeoutSeconds s; killed pid $($p.Id)" | Set-Content -Encoding UTF8 (Join-Path $OutDir "timeout.txt")
}
$p.WaitForExit()
[System.IO.File]::WriteAllText((Join-Path $OutDir "stdout.txt"), $out.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false))
[System.IO.File]::WriteAllText((Join-Path $OutDir "stderr.txt"), $err.GetAwaiter().GetResult(), [System.Text.UTF8Encoding]::new($false))
[string]$p.ExitCode | Set-Content -Encoding ASCII (Join-Path $OutDir "exitcode.txt")
exit $p.ExitCode
