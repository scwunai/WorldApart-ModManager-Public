param(
  [int]$WaitSec = 60,
  [switch]$KeepRunning,
  [string]$Mode = 'full',
  [int]$FallbackMs = 0,
  [int]$Notify = 1
)
$G = '<G>'
$INJ = "$G\KIMI\injector"

Write-Output '=== 1. kill leftovers ==='
Get-Process WorldApart*,wastart -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Write-Output '=== 2. clear logs ==='
Remove-Item "$INJ\kimi_inject.log" -ErrorAction SilentlyContinue
Remove-Item "$G\KIMI\probe_dump.txt" -ErrorAction SilentlyContinue
Remove-Item "$G\kimi_probe.txt" -ErrorAction SilentlyContinue
Remove-Item "$G\BepInEx\LogOutput.log" -ErrorAction SilentlyContinue
Remove-Item "$G\doorstop_*.log" -ErrorAction SilentlyContinue

Write-Output '=== 3. env + launch ==='
$env:KIMI_PROBE_LOG = "$G\KIMI\probe_dump.txt"
$env:KIMI_MODE = $Mode
$env:KIMI_FALLBACK_MS = "$FallbackMs"
$env:KIMI_USE_NOTIFY = "$Notify"
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G

$t = 0
while ($t -lt $WaitSec) {
  Start-Sleep 5
  $t += 5
  $ps = Get-Process WorldApart*,wastart -ErrorAction SilentlyContinue
  $names = ($ps | ForEach-Object { "$($_.ProcessName):$($_.Id)" }) -join ', '
  Write-Output "[t=$t s] procs: $names"
}

Write-Output '=== 4. process tree ==='
Get-CimInstance Win32_Process -Filter "Name like 'WorldApart%' or Name like 'wastart%'" |
  Select-Object ProcessId, ParentProcessId, Name | Format-Table -AutoSize | Out-String | Write-Output

Write-Output '=== 5. inject log ==='
if (Test-Path "$INJ\kimi_inject.log") { Get-Content "$INJ\kimi_inject.log" } else { Write-Output 'NO_INJECT_LOG' }

Write-Output '=== 6. BepInEx log ==='
if (Test-Path "$G\BepInEx\LogOutput.log") { Get-Content "$G\BepInEx\LogOutput.log" -TotalCount 60 } else { Write-Output 'NO_BEPINEX_LOG' }

Write-Output '=== 7. doorstop logs ==='
Get-ChildItem "$G\doorstop_*.log" -ErrorAction SilentlyContinue | ForEach-Object { Write-Output "--- $($_.Name) ---"; Get-Content $_.FullName }

Write-Output '=== 8.5 visible windows / error dialogs ==='
powershell -NoProfile -ExecutionPolicy Bypass -File "$G\KIMI\check_windows.ps1" 2>&1 | Out-String | Write-Output

Write-Output '=== 9. interop dir ==='
if (Test-Path "$G\BepInEx\interop") { Get-ChildItem "$G\BepInEx\interop" | Select-Object Name,Length | Format-Table -AutoSize | Out-String } else { Write-Output 'NO_INTEROP_DIR' }

if (-not $KeepRunning) {
  Write-Output '=== 9. cleanup ==='
  Get-Process WorldApart*,wastart -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
