param(
  [string]$Tag = 'FINAL',
  [int]$WheelTicks = 12,
  [switch]$NoClick
)
# Acceptance run for the deployed binary: no recon instrumentation involved.
# Opens settings + MOD page via the env-gated self tests, drives real wheel input
# and a real click on a native tab, captures screenshots and log counters.
$G = '<G>'
$Log = "$G\BepInEx\LogOutput.log"
$ShotDir = "$G\KIMI\shots_$Tag"
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 4

$env:MOD_FRAME_LOG = "$G\KIMI\v8_frames_$Tag.log"
$env:MOD_FRAME_TAG = $Tag
$env:MOD_P2_SELFTEST = '1'
$env:MOD_PAGE_SELFTEST = '1'
$env:MOD_DEBUG = '0'
Remove-Item Env:\MOD_SCROLLRECON -EA SilentlyContinue
Remove-Item Env:\MOD_Q1_RECON -EA SilentlyContinue
Remove-Item Env:\MOD_DEBUG_WRITETEST -EA SilentlyContinue
Remove-Item Env:\MOD_DEBUG_JUMPSELFTEST -EA SilentlyContinue

Remove-Item $Log -EA SilentlyContinue
Write-Output "[$Tag] launching WorldApart.exe"
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public const uint WHEEL = 0x0800;
  public const uint LDOWN = 0x0002, LUP = 0x0004;
}
"@

function Count-Log($pat) {
  if (-not (Test-Path $Log)) { return 0 }
  return (Select-String -Path $Log -Pattern $pat -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
}
function Snap($name) {
  try {
    $r = New-Object W+RECT
    [W]::GetClientRect($script:h, [ref]$r) | Out-Null
    $pt2 = New-Object W+POINT
    [W]::ClientToScreen($script:h, [ref]$pt2) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.CopyFromScreen($pt2.X, $pt2.Y, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $bmp.Save("$ShotDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $gr.Dispose(); $bmp.Dispose()
    Write-Output "[$Tag] screenshot $ShotDir\$name.png"
  } catch { Write-Output "[$Tag] screenshot $name failed: $_" }
}

# ---- wait for the MOD page ---------------------------------------------------
$deadline = (Get-Date).AddSeconds(200)
$ready = $false
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  if (Count-Log 'MODPAGE] shown (native pages hidden') { $ready = $true; break }
}
if (-not $ready) { Write-Output "[$Tag] TIMEOUT waiting for MOD page"; exit 1 }

$proc = $null
for ($i = 0; $i -lt 30; $i++) {
  $proc = Get-Process WorldApart* -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($proc) { break }
  Start-Sleep -Seconds 2
}
if (-not $proc) { Write-Output "[$Tag] no game window"; exit 1 }
$script:h = $proc.MainWindowHandle
[W]::ShowWindow($script:h, 5) | Out-Null
[W]::SetForegroundWindow($script:h) | Out-Null
Start-Sleep -Seconds 8

$cr = New-Object W+RECT
[W]::GetClientRect($script:h, [ref]$cr) | Out-Null
$pt = New-Object W+POINT
[W]::ClientToScreen($script:h, [ref]$pt) | Out-Null
$winW = $cr.Right - $cr.Left; $winH = $cr.Bottom - $cr.Top
$usw = 3840; $ush = 2160
Write-Output "[$Tag] client at $($pt.X),$($pt.Y) size ${winW}x${winH}; unity ${usw}x${ush}"
function ToWin($ux, $uy) {
  return @(($pt.X + [int]([double]$ux * $winW / $usw)), ($pt.Y + [int]([double]($ush - $uy) * $winH / $ush)))
}

$sw0 = Count-Log 'native page switch detected'
$sc0 = Count-Log 'MODPAGE] scroll y='
Write-Output "[$Tag] baseline nativePageSwitch=$sw0 scrollLines=$sc0"

$cx = $pt.X + [int]($winW / 2)
$cy = $pt.Y + [int]($winH * 0.45)
[W]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 500
Snap '01_top'

Write-Output "[$Tag] --- real wheel DOWN x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [W]::mouse_event([W]::WHEEL, 0, 0, -120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 300
}
Start-Sleep -Seconds 3
Snap '02_bottom'
$sw1 = Count-Log 'native page switch detected'
$sc1 = Count-Log 'MODPAGE] scroll y='
Write-Output "[$Tag] after wheel-down nativePageSwitch=$sw1 scrollLines=$sc1"

Write-Output "[$Tag] --- real wheel UP x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [W]::mouse_event([W]::WHEEL, 0, 0, 120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 300
}
Start-Sleep -Seconds 3
Snap '03_back_to_top'
$sw2 = Count-Log 'native page switch detected'
Write-Output "[$Tag] after wheel-up nativePageSwitch=$sw2"

if (-not $NoClick) {
  # Frame#navSystem measured at unity (902, 1200) in the recon run (screen 3840x2160)
  $wp = ToWin 902 1200
  Write-Output "[$Tag] clicking native navSystem (unity 902,1200) -> window $($wp[0]),$($wp[1])"
  [W]::SetCursorPos($wp[0], $wp[1]) | Out-Null
  Start-Sleep -Milliseconds 500
  [W]::mouse_event([W]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [W]::mouse_event([W]::LUP, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Seconds 4
  Snap '04_after_tab_click'
  $sw3 = Count-Log 'native page switch detected'
  Write-Output "[$Tag] after native tab click nativePageSwitch=$sw3"
}
# archive the run log for the acceptance report
$arch = "$G\KIMI\acceptance"
New-Item -ItemType Directory -Force -Path $arch | Out-Null
if (Test-Path $Log) { Copy-Item $Log "$arch\LogOutput_$Tag.log" -Force }
if (Test-Path "$G\BepInEx\ErrorLog.log") { Copy-Item "$G\BepInEx\ErrorLog.log" "$arch\ErrorLog_$Tag.log" -Force }
Write-Output "[$Tag] archived log to $arch\LogOutput_$Tag.log"
Write-Output "[$Tag] done"
