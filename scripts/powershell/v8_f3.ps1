param(
  [string]$Tag = 'F3',
  [int]$WheelTicks = 14
)
# F3 / A5 evidence run on the deployed binary.
# Order matters: the first row of the list is always a PACKAGE header (unit rows have
# no click target), so the collapse is clicked while the list is still at the top.
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
Remove-Item $Log -EA SilentlyContinue
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W3 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public const uint WHEEL = 0x0800, LDOWN = 0x0002, LUP = 0x0004;
}
"@
function Count-Log($pat) {
  if (-not (Test-Path $Log)) { return 0 }
  return (Select-String -Path $Log -Pattern $pat -SimpleMatch -EA SilentlyContinue | Measure-Object).Count
}
function Snap($name) {
  try {
    $r = New-Object W3+RECT; [W3]::GetClientRect($script:h, [ref]$r) | Out-Null
    $p2 = New-Object W3+POINT; [W3]::ClientToScreen($script:h, [ref]$p2) | Out-Null
    $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $hh
    $gr = [System.Drawing.Graphics]::FromImage($bmp)
    $gr.CopyFromScreen($p2.X, $p2.Y, 0, 0, (New-Object System.Drawing.Size $w, $hh))
    $bmp.Save("$ShotDir\$name.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $gr.Dispose(); $bmp.Dispose()
    Write-Output "[$Tag] screenshot $ShotDir\$name.png"
  } catch { Write-Output "[$Tag] screenshot $name failed: $_" }
}

$deadline = (Get-Date).AddSeconds(200)
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  if (Count-Log 'MODPAGE] shown (native pages hidden') { break }
}
$proc = $null
for ($i = 0; $i -lt 30; $i++) {
  $proc = Get-Process WorldApart* -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($proc) { break }
  Start-Sleep -Seconds 2
}
if (-not $proc) { Write-Output "[$Tag] no game window"; exit 1 }
$script:h = $proc.MainWindowHandle
[W3]::ShowWindow($script:h, 5) | Out-Null
[W3]::SetForegroundWindow($script:h) | Out-Null
Start-Sleep -Seconds 8

$cr = New-Object W3+RECT; [W3]::GetClientRect($script:h, [ref]$cr) | Out-Null
$pt = New-Object W3+POINT; [W3]::ClientToScreen($script:h, [ref]$pt) | Out-Null
$winW = $cr.Right - $cr.Left; $winH = $cr.Bottom - $cr.Top
function ToWin($ux, $uy) { return @(($pt.X + [int]([double]$ux * $winW / 3840)), ($pt.Y + [int]([double](2160 - $uy) * $winH / 2160))) }
function ClickAt($ux, $uy) {
  $wp = ToWin $ux $uy
  [W3]::SetCursorPos($wp[0], $wp[1]) | Out-Null
  Start-Sleep -Milliseconds 400
  [W3]::mouse_event([W3]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 100
  [W3]::mouse_event([W3]::LUP, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Seconds 3
}
function WheelDown($n) {
  $cx = $pt.X + [int]($winW / 2); $cy = $pt.Y + [int]($winH * 0.45)
  [W3]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 300
  for ($i = 0; $i -lt $n; $i++) { [W3]::mouse_event([W3]::WHEEL, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300 }
  Start-Sleep -Seconds 2
}
function WheelUp($n) {
  $cx = $pt.X + [int]($winW / 2); $cy = $pt.Y + [int]($winH * 0.45)
  [W3]::SetCursorPos($cx, $cy) | Out-Null
  Start-Sleep -Milliseconds 300
  for ($i = 0; $i -lt $n; $i++) { [W3]::mouse_event([W3]::WHEEL, 0, 0, 120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 300 }
  Start-Sleep -Seconds 2
}

Snap '01_top_expanded'
Write-Output "[$Tag] step 1: click the first package row (collapse), at unity 1900,1240"
ClickAt 1900 1240
Snap '02_collapsed'
Write-Output "[$Tag] step 2: wheel to the bottom (clamp must follow the smaller content)"
WheelDown $WheelTicks
Snap '03_bottom_after_collapse'
Write-Output "[$Tag] step 3: wheel back to the top, then expand again"
WheelUp $WheelTicks
ClickAt 1900 1240
Snap '04_expanded_again'
WheelDown 3
Snap '05_after_expand_scroll'

$arch = "$G\KIMI\acceptance"; New-Item -ItemType Directory -Force -Path $arch | Out-Null
if (Test-Path $Log) { Copy-Item $Log "$arch\LogOutput_$Tag.log" -Force }
Write-Output "[$Tag] done"
