param(
  [string]$Tag = 'VERIFY',
  [int]$WheelTicks = 10,
  [switch]$ClickTab
)
$G = '<G>'
$Log = "$G\BepInEx\LogOutput.log"
$ShotDir = "$G\KIMI\shots_$Tag"
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 4

$env:MOD_FRAME_LOG = "$G\KIMI\v8_frames_$Tag.log"
$env:MOD_FRAME_TAG = $Tag
$env:MOD_SCROLLRECON = '1'
$env:MOD_P2_SELFTEST = '1'
$env:MOD_DEBUG = '0'
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
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
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

function Get-GameWindow {
  for ($i = 0; $i -lt 40; $i++) {
    $p = Get-Process WorldApart* -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { return $p }
    Start-Sleep -Seconds 3
  }
  return $null
}

function Snap($name) {
  try {
    $r = New-Object W+RECT
    [W]::GetWindowRect($script:h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $path = "$ShotDir\$name.png"
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "[$Tag] screenshot $path"
  } catch { Write-Output "[$Tag] screenshot $name failed: $_" }
}

# ---- wait for the MOD page ---------------------------------------------------
$deadline = (Get-Date).AddSeconds(200)
$ready = $false
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  if (Test-Path $Log) {
    if (Select-String -Path $Log -Pattern 'MOD page visible=True' -SimpleMatch -EA SilentlyContinue) { $ready = $true; break }
  }
}
if (-not $ready) { Write-Output "[$Tag] TIMEOUT waiting for MOD page"; exit 1 }

$proc = Get-GameWindow
if (-not $proc) { Write-Output "[$Tag] no game window"; exit 1 }
$script:h = $proc.MainWindowHandle
[W]::ShowWindow($script:h, 5) | Out-Null
[W]::SetForegroundWindow($script:h) | Out-Null
Start-Sleep -Seconds 6

$cr = New-Object W+RECT
[W]::GetClientRect($script:h, [ref]$cr) | Out-Null
$pt = New-Object W+POINT
[W]::ClientToScreen($script:h, [ref]$pt) | Out-Null
$winW = $cr.Right - $cr.Left; $winH = $cr.Bottom - $cr.Top
Write-Output "[$Tag] client at $($pt.X),$($pt.Y) size ${winW}x${winH}"

# unity screen size from the recon log
$usw = 1920; $ush = 1080
$ml = Select-String -Path $Log -Pattern 'screen=(\d+)x(\d+)' -EA SilentlyContinue | Select-Object -Last 1
if ($ml -and $ml.Matches.Count -gt 0) {
  $usw = [int]$ml.Matches[0].Groups[1].Value
  $ush = [int]$ml.Matches[0].Groups[2].Value
}
Write-Output "[$Tag] unity screen=${usw}x${ush}"
function ToWin($ux, $uy) {
  $x = $pt.X + [int]([double]$ux * $winW / $usw)
  $y = $pt.Y + [int]([double]($ush - $uy) * $winH / $ush)
  return @($x, $y)
}

$before = (Select-String -Path $Log -Pattern 'native page switch detected' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
$scrollBefore = (Select-String -Path $Log -Pattern '[MODPAGE] scroll y=' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
Write-Output "[$Tag] baseline: nativePageSwitch=$before scrollLines=$scrollBefore"

$cx = $pt.X + [int]($winW / 2)
$cy = $pt.Y + [int]($winH * 0.45)
[W]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 400
Snap '01_top'

Write-Output "[$Tag] --- real wheel DOWN x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [W]::mouse_event([W]::WHEEL, 0, 0, -120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 260
}
Start-Sleep -Seconds 3
Snap '02_bottom'

$afterDown = (Select-String -Path $Log -Pattern 'native page switch detected' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
$scrollAfterDown = (Select-String -Path $Log -Pattern '[MODPAGE] scroll y=' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
Write-Output "[$Tag] after wheel-down: nativePageSwitch=$afterDown scrollLines=$scrollAfterDown"

Write-Output "[$Tag] --- real wheel UP x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [W]::mouse_event([W]::WHEEL, 0, 0, 120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 260
}
Start-Sleep -Seconds 3
Snap '03_back_to_top'

$afterUp = (Select-String -Path $Log -Pattern 'native page switch detected' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
Write-Output "[$Tag] after wheel-up: nativePageSwitch=$afterUp"

if ($ClickTab) {
  $ml2 = Select-String -Path $Log -Pattern 'Frame#navSystem=\(([\-\d\.]+), ([\-\d\.]+)\)' -EA SilentlyContinue | Select-Object -Last 1
  if ($ml2 -and $ml2.Matches.Count -gt 0) {
    $ux = [double]$ml2.Matches[0].Groups[1].Value
    $uy = [double]$ml2.Matches[0].Groups[2].Value
    $wp = ToWin $ux $uy
    Write-Output "[$Tag] clicking native navSystem at unity=$ux,$uy -> window $($wp[0]),$($wp[1])"
    [W]::SetCursorPos($wp[0], $wp[1]) | Out-Null
    Start-Sleep -Milliseconds 400
    [W]::mouse_event([W]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 90
    [W]::mouse_event([W]::LUP, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 4
    Snap '04_after_tab_click'
    $afterClick = (Select-String -Path $Log -Pattern 'native page switch detected' -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
    Write-Output "[$Tag] after native tab click: nativePageSwitch=$afterClick"
  } else { Write-Output "[$Tag] nav tab position not found in log" }
}
Write-Output "[$Tag] done"
