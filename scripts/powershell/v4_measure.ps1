param(
  [Parameter(Mandatory=$true)][string]$Scenario,
  [int]$SteadySec = 200,
  [switch]$NoInjector
)
$ErrorActionPreference = 'Continue'
$G = '<G>'
$OUT = "$G\KIMI\v4_measure_$Scenario.txt"
$FRAMELOG = "$G\KIMI\v4_frames_$Scenario.log"

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
"@

# NOTE: EnumWindows-based enumeration stopped seeing the game window in this
# environment (works from .NET's MainWindowHandle, returns nothing from a
# PowerShell scriptblock delegate), so use Get-Process/MainWindowTitle instead.
function Get-GamePid($title) {
  $p = Get-Process WorldApart_kimi -EA SilentlyContinue |
       Where-Object { $_.MainWindowTitle -eq $title } | Select-Object -First 1
  if ($p) { return $p.Id }
  return 0
}

function Log($s) {
  Write-Output $s
  Add-Content -Path $OUT -Value $s -Encoding UTF8
}

"=== scenario $Scenario  $(Get-Date -Format 'HH:mm:ss')  NoInjector=$NoInjector SteadySec=$SteadySec ===" |
  Set-Content -Path $OUT -Encoding UTF8
Remove-Item $FRAMELOG -EA SilentlyContinue

Log 'kill leftovers'
Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
Get-Process WorldApart* -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 2

$env:MOD_FRAME_LOG = $FRAMELOG
$env:MOD_FRAME_TAG = $Scenario

if ($NoInjector) { $exe = "$G\WorldApart_kimi.exe" } else { $exe = "$G\WorldApart.exe" }
Log "launching $exe"
$t0 = Get-Date
Start-Process $exe -WorkingDirectory $G | Out-Null

# wait for the game's own main window
$pid2 = 0; $waited = 0
while ($waited -lt 240) {
  Start-Sleep -Milliseconds 500
  $waited += 0.5
  $p = Get-GamePid 'WorldApart'
  if ($p -ne 0) { $pid2 = $p; break }
}
$startupMs = [int]((Get-Date) - $t0).TotalMilliseconds
if ($pid2 -eq 0) { Log "STARTUP: main window NOT seen within 240s"; exit 1 }
Log "STARTUP: main window title 'WorldApart' pid=$pid2 after ${startupMs}ms"

# The Unity player stops rendering while unfocused, so keep the game foreground
# for the whole measurement window.
Add-Type -AssemblyName Microsoft.VisualBasic
try { [Microsoft.VisualBasic.Interaction]::AppActivate($pid2) | Out-Null; Log 'focused game window via AppActivate' }
catch { Log "AppActivate failed: $($_.Exception.Message)" }
Start-Sleep -Seconds 3

# settle, then sample steady state
Start-Sleep -Seconds 30
$proc = Get-Process -Id $pid2 -EA SilentlyContinue
if (-not $proc) { Log 'process died before steady sampling'; exit 1 }
$cores = [Environment]::ProcessorCount
$prevCpu = $proc.TotalProcessorTime
$prevT = Get-Date
$samples = @()
$t = 0
while ($t -lt $SteadySec) {
  Start-Sleep -Seconds 10
  $t += 10
  $pr = Get-Process -Id $pid2 -EA SilentlyContinue
  if (-not $pr) { Log "t=${t}s process gone"; break }
  $now = Get-Date
  $dCpu = ($pr.TotalProcessorTime - $prevCpu).TotalSeconds
  $dWall = ($now - $prevT).TotalSeconds
  $cpuPct = if ($dWall -gt 0) { [math]::Round(100.0 * $dCpu / $dWall / $cores, 1) } else { 0 }
  $ws = [math]::Round($pr.WorkingSet64 / 1MB, 0)
  $line = "t=${t}s cpu=${cpuPct}% (all cores) ws=${ws}MB threads=$($pr.Threads.Count)"
  Log $line
  $samples += [pscustomobject]@{ cpu = $cpuPct; ws = $ws }
  $prevCpu = $pr.TotalProcessorTime; $prevT = $now
}

if ($samples.Count -gt 0) {
  $avgCpu = [math]::Round(($samples | Measure-Object cpu -Average).Average, 1)
  $maxCpu = [math]::Round(($samples | Measure-Object cpu -Maximum).Maximum, 1)
  $avgWs  = [math]::Round(($samples | Measure-Object ws  -Average).Average, 0)
  Log "STEADY: n=$($samples.Count) avgCpu=${avgCpu}% maxCpu=${maxCpu}% avgWS=${avgWs}MB"
}

if (Test-Path $FRAMELOG) {
  Log 'FRAME LINES:'
  Get-Content $FRAMELOG | ForEach-Object { Log "  $_" }
} else {
  Log 'NO FRAME LOG (frameprobe not installed in this scenario)'
}

Log "done $(Get-Date -Format 'HH:mm:ss')"
