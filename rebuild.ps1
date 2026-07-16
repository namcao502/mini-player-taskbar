# Rebuild + hot-swap the deskband while it is loaded in Explorer.
# `dotnet build` compiles obj fine but cannot overwrite bin while the toolbar
# is enabled (Explorer locks the DLL). So: build -> kill Explorer -> copy the
# fresh obj DLL over the now-unlocked bin -> relaunch. No re-registration needed
# (CLSID and codebase path are stable). Re-enable the toolbar after it restarts.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$obj  = Join-Path $root 'obj\Release\net48\MiniPlayerBand.dll'
$bin  = Join-Path $root 'bin\Release\net48\MiniPlayerBand.dll'

dotnet build -c Release -v quiet   # bin copy step will fail while loaded; obj still builds

Stop-Process -Name explorer -Force
$ok = $false
for ($i = 0; $i -lt 100; $i++) {
  try { Copy-Item $obj $bin -Force -ErrorAction Stop; $ok = $true; break }
  catch { Start-Sleep -Milliseconds 50 }   # retry until we win the lock before Explorer reloads
}
if (-not $ok) { Write-Host 'FAILED to win the DLL lock'; exit 1 }
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer }
Write-Host "swapped, explorer restarted. DLL: $((Get-Item $bin).LastWriteTime)"
