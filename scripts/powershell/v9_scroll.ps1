param(
  [string]$Tag = 'SCROLL',
  [int]$WheelTicks = 24,
  [switch]$KeepMods,             # leave the 8 fake packages in place afterwards
  [int]$MaxAttempts = 4,
  [int]$HoldSec = 60
)

# v9 R3: re-run the v8 scroll acceptance (A1/A3/A4/A6) on the v9 binary.
#
#   A1  8 fake packages installed -> the MOD page has more rows than fit  (rows= log)
#   A3  wheel down/up leaves the settings page alone ("native page switch detected" == 0)
#   A4  a real click on the native navSystem tab still closes the MOD page
#   A6  FrameProbe p95, read out of KIMI\v9_frames_<Tag>.log
#
# Differences from v8_final.ps1: launches through 启动MOD版.bat's modlauncher (IFEO is
# blocked on this machine), makes this process DPI aware, and takes the click target
# from the plugin's own [DBG] NAVRECT line instead of a hardcoded 3840x2160 constant.

$G = '<G>'
$K = Join-Path $G 'KIMI'
$acc = Join-Path $K 'acceptance'
$Mods = '%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods'
$stash = Join-Path $K 'backup\scrolltest_mods_removed_20261007'
$dest = Join-Path $G 'BepInEx\LogOutput.log'
$errl = Join-Path $G 'BepInEx\ErrorLog.log'
if (-not (Test-Path $acc)) { New-Item -ItemType Directory -Path $acc | Out-Null }

function Log([string]$s) {
  $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' [' + $Tag + '] ' + $s
  Write-Output $line
  Add-Content -Path (Join-Path $K 'v9_runs.log') -Value $line -Encoding UTF8
}
function KillGame() {
  Get-Process WorldApart*, wastart, modlauncher, UnityCrashHandler* -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 4
}
function Count-Log([string]$pat) {
  if (-not (Test-Path $dest)) { return 0 }
  return (Select-String -Path $dest -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue | Measure-Object).Count
}
function Wait-LogMatch([string]$pat, [int]$timeoutSec) {
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt $timeoutSec) {
    Start-Sleep -Seconds 2
    if (Test-Path $dest) {
      $m = Select-String -Path $dest -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -Last 1
      if ($m) { return $m.Line }
    }
  }
  return $null
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class V9S {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public const uint WHEEL = 0x0800, LDOWN = 0x0002, LUP = 0x0004;
}
"@
try { [V9S]::SetProcessDpiAwareness(2) | Out-Null } catch { try { [V9S]::SetProcessDPIAware() | Out-Null } catch { } }

# ---- A1: install the fake packages -----------------------------------------
if (-not (Test-Path $stash)) { Log "stash not found: $stash"; return }
$installed = @()
Get-ChildItem $stash -Directory | ForEach-Object {
  $t = Join-Path $Mods $_.Name
  if (Test-Path $t) { Remove-Item $t -Recurse -Force }
  Copy-Item $_.FullName $t -Recurse -Force
  $installed += $_.Name
}
Log ("A1 installed " + $installed.Count + " fake packages: " + ($installed -join ','))

function Remove-FakeMods() {
  foreach ($n in $installed) {
    $t = Join-Path $Mods $n
    if (Test-Path $t) {
      $b = Join-Path $stash $n
      if (Test-Path $b) { Remove-Item $b -Recurse -Force }
      Move-Item $t $b -Force
    }
  }
  Log ("removed " + $installed.Count + " fake packages again")
}

# ---- launch ----------------------------------------------------------------
$env:MOD_FRAME_LOG = Join-Path $K ("v9_frames_" + $Tag + ".log")
$env:MOD_FRAME_TAG = $Tag
$env:MOD_P2_SELFTEST = '1'
$env:MOD_PAGE_SELFTEST = '1'
$env:MOD_TAB_DIAG = '1'
Get-ChildItem Env: | Where-Object { $_.Name -like 'MOD_DEBUG*' -or $_.Name -like 'MOD_Q1*' -or $_.Name -like 'MOD_SCROLL*' } |
  ForEach-Object { Remove-Item ("Env:\" + $_.Name) -ErrorAction SilentlyContinue }

$crashes = 0; $attempt = 0; $ok = $false
while ($attempt -lt $MaxAttempts -and -not $ok) {
  $attempt++
  KillGame
  Remove-Item $dest -ErrorAction SilentlyContinue
  Remove-Item $errl -ErrorAction SilentlyContinue
  Log ("attempt " + $attempt + ": launching modlauncher.exe WorldApart.exe")
  Start-Process (Join-Path $G 'modlauncher.exe') -ArgumentList (Join-Path $G 'WorldApart.exe') -WorkingDirectory $G | Out-Null

  $proc = $null; $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt 90) {
    Start-Sleep -Seconds 2
    $proc = Get-Process WorldApart_mod -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { break }
  }
  if (-not $proc) { $crashes++; Log ("attempt " + $attempt + ": no game process -> crash"); continue }

  $line = Wait-LogMatch 'MODPAGE] shown (native pages hidden' 200
  if (-not $line) { $crashes++; Log ("attempt " + $attempt + ": MOD page never shown -> retry"); continue }

  if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $crashes++; Log ("attempt " + $attempt + ": died before the checks"); continue }
  $ok = $true
}
if (-not $ok) { Log ("FAILED after " + $MaxAttempts + " attempts (crashes=" + $crashes + ")"); Remove-FakeMods; return }
Log ("attempt " + $attempt + ": MOD page shown; crashes=" + $crashes)

# ---- window + mapping ------------------------------------------------------
$gw = $null
for ($i = 0; $i -lt 30; $i++) {
  $gw = Get-Process WorldApart_mod -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($gw) { break }
  Start-Sleep -Seconds 2
}
if (-not $gw) { Log 'no game window'; Remove-FakeMods; return }
$h = $gw.MainWindowHandle
[V9S]::ShowWindow($h, 9) | Out-Null
[V9S]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
[V9S]::SetForegroundWindow($h) | Out-Null
[V9S]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
Start-Sleep -Seconds 2
[V9S]::SetForegroundWindow($h) | Out-Null
[V9S]::BringWindowToTop($h) | Out-Null
Start-Sleep -Seconds 5
Log ("foreground match=" + ([V9S]::GetForegroundWindow() -eq $h))

$cr = New-Object V9S+RECT
[V9S]::GetClientRect($h, [ref]$cr) | Out-Null
$pt = New-Object V9S+POINT
[V9S]::ClientToScreen($h, [ref]$pt) | Out-Null
$winW = $cr.Right - $cr.Left; $winH = $cr.Bottom - $cr.Top
$navLine = Select-String -Path $dest -Pattern 'NAVRECT unity' -ErrorAction SilentlyContinue | Select-Object -Last 1
if (-not $navLine) { $navLine = Select-String -Path $dest -Pattern 'TABRECT unity' -ErrorAction SilentlyContinue | Select-Object -Last 1 }
$m = [regex]::Match($navLine.Line, 'unity=\((-?\d+),(-?\d+)\)')
$sm = [regex]::Match($navLine.Line, 'screen=(\d+)x(\d+)')
$ux = [int]$m.Groups[1].Value; $uy = [int]$m.Groups[2].Value
$sw = [int]$sm.Groups[1].Value; $sh = [int]$sm.Groups[2].Value
$tx = $pt.X + [int]([double]$ux * $winW / $sw)
$ty = $pt.Y + [int]([double]($sh - $uy) * $winH / $sh)
Log ("NAVRECT unity=($ux,$uy) -> window ($tx,$ty) client ${winW}x${winH} unityScreen ${sw}x${sh}")

Log ("A1 rows: " + ((Select-String -Path $dest -Pattern 'rows=' -ErrorAction SilentlyContinue | Select-Object -Last 1).Line))

# ---- A3: wheel down / up ---------------------------------------------------
$cx = $pt.X + [int]($winW / 2); $cy = $pt.Y + [int]($winH * 0.5)
[V9S]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 400
$sw0 = Count-Log 'native page switch detected'
$sc0 = Count-Log 'MODPAGE] scroll y='
Log "A3 wheel DOWN x$WheelTicks"
for ($i = 0; $i -lt $WheelTicks; $i++) { [V9S]::mouse_event([V9S]::WHEEL, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 3
$sw1 = Count-Log 'native page switch detected'; $sc1 = Count-Log 'MODPAGE] scroll y='
Log "A3 after wheel-down: nativePageSwitch=$sw1 scrollLines=$sc1"
Log "A3 wheel UP x$WheelTicks"
for ($i = 0; $i -lt $WheelTicks; $i++) { [V9S]::mouse_event([V9S]::WHEEL, 0, 0, 120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 250 }
Start-Sleep -Seconds 3
$sw2 = Count-Log 'native page switch detected'; $sc2 = Count-Log 'MODPAGE] scroll y='
Log "A3 after wheel-up: nativePageSwitch=$sw2 scrollLines=$sc2"

# ---- A4: real click on the native tab --------------------------------------
[V9S]::SetCursorPos($tx, $ty) | Out-Null
Start-Sleep -Milliseconds 500
[V9S]::mouse_event([V9S]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 100
[V9S]::mouse_event([V9S]::LUP, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 4
$sw3 = Count-Log 'native page switch detected'
Log "A4 after native tab click: nativePageSwitch=$sw3 (baseline $sw0)"

# ---- hold + archive --------------------------------------------------------
$t0 = Get-Date
while (((Get-Date) - $t0).TotalSeconds -lt $HoldSec) {
  Start-Sleep -Seconds 2
  if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { Log 'game exited during the hold'; break }
}
if (Test-Path $dest) { Copy-Item $dest (Join-Path $acc ("LogOutput_" + $Tag + ".log")) -Force }
if (Test-Path $errl) { Copy-Item $errl (Join-Path $acc ("ErrorLog_" + $Tag + ".log")) -Force }
Log ("archived to " + $acc)
Log ("A6 frame p95: " + ((Get-Content (Join-Path $K ("v9_frames_" + $Tag + ".log")) -ErrorAction SilentlyContinue | Select-String 'p95' | Select-Object -Last 1).Line))

KillGame
if (-not $KeepMods) { Remove-FakeMods }
Log ("done; attempts=" + $attempt + " crashes=" + $crashes)
