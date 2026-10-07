param(
  [string]$Tag = 'RECON',
  [switch]$NoWheel,
  [int]$WheelTicks = 8
)
$G = '<G>'
$Log = "$G\BepInEx\LogOutput.log"

Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3

$env:MOD_FRAME_LOG = "$G\KIMI\v8_frames_$Tag.log"
$env:MOD_FRAME_TAG = $Tag
$env:MOD_SCROLLRECON = '1'
$env:MOD_P2_SELFTEST = '1'
$env:MOD_DEBUG = '0'
Remove-Item Env:\MOD_Q1_RECON -EA SilentlyContinue
Remove-Item Env:\MOD_DEBUG_WRITETEST -EA SilentlyContinue
Remove-Item Env:\MOD_DEBUG_JUMPSELFTEST -EA SilentlyContinue

Write-Output "[$Tag] launching WorldApart.exe RECON=$($env:MOD_SCROLLRECON) OPENSETTINGS=$($env:MOD_P2_SELFTEST)"
Remove-Item $Log -EA SilentlyContinue
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G

if ($NoWheel) { Write-Output "[$Tag] wheel injection skipped"; exit 0 }

# ---- wait until the recon driver has the MOD page visible ------------------
$deadline = (Get-Date).AddSeconds(180)
$ready = $false
while ((Get-Date) -lt $deadline) {
  Start-Sleep -Seconds 3
  if (Test-Path $Log) {
    $m = Select-String -Path $Log -Pattern 'MOD page visible=True' -SimpleMatch -EA SilentlyContinue
    if ($m) { $ready = $true; break }
  }
}
if (-not $ready) { Write-Output "[$Tag] TIMEOUT waiting for MOD page"; Get-Content $Log -Tail 40; exit 1 }
Write-Output "[$Tag] MOD page visible, waiting 6s for layout"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Wheel {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public const uint WHEEL = 0x0800;
}
"@

Start-Sleep -Seconds 6
$p = Get-Process WorldApart -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output "[$Tag] no WorldApart main window"; exit 1 }
$h = $p.MainWindowHandle
[Wheel]::ShowWindow($h, 5) | Out-Null
[Wheel]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Seconds 2
$r = New-Object Wheel+RECT
[Wheel]::GetWindowRect($h, [ref]$r) | Out-Null
$cx = [int](($r.Left + $r.Right) / 2)
$cy = [int](($r.Top + $r.Bottom) / 2)
Write-Output "[$Tag] window rect L=$($r.Left) T=$($r.Top) R=$($r.Right) B=$($r.Bottom) center=$cx,$cy"
[Wheel]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 500

Write-Output "[$Tag] --- wheel DOWN x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [Wheel]::mouse_event([Wheel]::WHEEL, 0, 0, -120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 220
}
Start-Sleep -Seconds 3
Write-Output "[$Tag] --- wheel UP x$WheelTicks ---"
for ($i = 0; $i -lt $WheelTicks; $i++) {
  [Wheel]::mouse_event([Wheel]::WHEEL, 0, 0, 120, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 220
}
Start-Sleep -Seconds 4
Write-Output "[$Tag] wheel injection done"
