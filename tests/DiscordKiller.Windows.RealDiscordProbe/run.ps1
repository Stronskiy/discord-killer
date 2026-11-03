$ErrorActionPreference = 'Stop'
$status = 'FAIL'
$reason = 'Setup did not complete.'
$vendor = '(?i)(?:^|,\s*)CN=Discord,?\s+Inc\.?(?:,|$)'
$installer = Join-Path $env:RUNNER_TEMP 'discord-e2e-setup.exe'
$root = Join-Path $env:LOCALAPPDATA 'Discord'
function Start-Isolated([string] $Path, [string] $Arguments) {
  $info = [Diagnostics.ProcessStartInfo]::new()
  $info.FileName = $Path
  $info.Arguments = $Arguments
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  foreach ($key in @($info.Environment.Keys)) {
    if ($key -match '(?i)TOKEN|SECRET|PASSWORD|CREDENTIAL|AUTH|^GITHUB_|^ACTIONS_') { [void]$info.Environment.Remove($key) }
  }
  $p = [Diagnostics.Process]::Start($info)
  if (-not $p) { throw 'Could not start isolated process.' }
  [void]$p.StandardOutput.ReadToEndAsync()
  [void]$p.StandardError.ReadToEndAsync()
  return $p
}
function Assert-Signed([string] $Path) {
  $signature = Get-AuthenticodeSignature -LiteralPath $Path
  if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notmatch $vendor) {
    throw 'Expected valid Discord Inc. signature was not verified.'
  }
}
try {
  if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Only a disposable GitHub-hosted runner is allowed.' }
  Write-Host "OS: $((Get-CimInstance Win32_OperatingSystem).Caption). This does not verify Windows 11 desktop behavior."
  Write-Host 'No account, screenshots, output capture, or binary artifacts are used.'
  if (@(Get-Process -Name Discord -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Pre-existing Discord process; refusing to proceed.' }
  if (Test-Path -LiteralPath $root) { throw 'Pre-existing Discord install; refusing to proceed.' }
  Invoke-WebRequest -Uri 'https://discordapp.com/api/download?platform=win' -OutFile $installer -TimeoutSec 90
  Assert-Signed $installer
  $setup = Start-Isolated $installer '-s'
  if (-not $setup.WaitForExit(120000)) { throw 'Discord installer timed out.' }
  if ($setup.ExitCode -ne 0) { throw 'Discord installer returned a nonzero exit code.' }
  $deadline = [DateTime]::UtcNow.AddSeconds(45)
  $exe = $null
  do {
    if (Test-Path -LiteralPath $root) {
      $exe = Get-ChildItem -LiteralPath $root -Directory -Filter 'app-*' | Sort-Object LastWriteTime -Descending | ForEach-Object { Join-Path $_.FullName 'Discord.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    }
    if (-not $exe) { Start-Sleep -Seconds 2 }
  } while (-not $exe -and [DateTime]::UtcNow -lt $deadline)
  if (-not $exe) { throw 'Installed Discord executable was not found.' }
  Assert-Signed $exe
  if (@(Get-Process -Name Discord -ErrorAction SilentlyContinue).Count -eq 0) { $null = Start-Isolated $exe '' }
  # A freshly installed Discord may restart its window process while updating.
  # Let startup settle before the probe retains its original process handles.
  Start-Sleep -Seconds 45
  $probe = 'tests/DiscordKiller.Windows.RealDiscordProbe/bin/Release/net10.0-windows/DiscordKiller.Windows.RealDiscordProbe.dll'
  $app = Join-Path $env:RUNNER_TEMP 'discord-killer-e2e/discord-killer.exe'
  dotnet $probe $app $exe
  switch ($LASTEXITCODE) {
    0 { $status = 'PASS'; $reason = 'The original Discord process handles exited and the application reported closure.' }
    2 { $status = 'UNVERIFIED'; $reason = 'An E2E prerequisite or enabled shutdown was unavailable; this is not a pass.' }
    default { $status = 'FAIL'; $reason = 'The actual Discord shutdown check failed.' }
  }
} catch {
  $reason = "Diagnostic setup failed: $($_.Exception.GetType().Name): $($_.Exception.Message)"
} finally {
  Write-Host "STATUS=$status"
  Write-Host "DETAIL=$reason"
  Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value "Real Discord E2E: **$status**. $reason Tested on a hosted Windows Server runner; Windows 11 desktop behavior remains unverified."
}
# A skipped/unverified diagnostic is explicitly non-green, so it cannot be mistaken for proof.
if ($status -ne 'PASS') { exit 1 }
