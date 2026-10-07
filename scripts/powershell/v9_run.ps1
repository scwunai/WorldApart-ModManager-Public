param(
  [string]$Tag = 'V9',
  [string]$Vars = '',            # extra env vars, "NAME=VALUE;NAME2=VALUE2"
  [int]$HoldSec = 150,           # how long each attempt is allowed to run
  [int]$MaxAttempts = 3,         # relaunch on a crash (injection is flaky)
  [int]$GapSec = 6,              # wait between attempts
  [switch]$NoSelfTestOpen,       # do not set MOD_P2_SELFTEST
  [switch]$NoFrameLog,           # do not set MOD_FRAME_LOG / MOD_FRAME_TAG
  [switch]$KeepRunning,          # do not kill the game when the hold expires
  [switch]$ArchiveOnly,          # no launch; just archive the current LogOutput
  [switch]$ClickMODTab,          # drive real mouse clicks on the MOD tab
  [int]$Clicks = 3,              # clicks per direction when -ClickMODTab
  [int]$ClickPrepSec = 120,      # how long to wait for the MOD tab rect to appear
  [string]$LogTag = ''           # name for archived logs (default: <Tag>)
)

# v9 runner. The only supported launch path on this machine is
#   启动MOD版.bat -> modlauncher.exe (=wastart) -> WorldApart_mod.exe
# IFEO is blocked by local security policy here, so nothing in this script
# touches the registry.
#
# Usage example (P0 tab-click reconnaissance):
#   powershell -NoProfile -ExecutionPolicy Bypass -File KIMI\v9_run.ps1 `
#       -Tag TABDIAG -Vars "MOD_TAB_DIAG=1;MOD_P2_SELFTEST=1" -HoldSec 120
#
# Every attempt is recorded in KIMI\v9_runs.log (start, pid, crash or clean),
# which is the injection-crash-rate evidence the v9 report needs.

$G = '<G>'
$K = Join-Path $G 'KIMI'
$acc = Join-Path $K 'acceptance'
$dest = Join-Path $G 'BepInEx\LogOutput.log'
$errl = Join-Path $G 'BepInEx\ErrorLog.log'
if (-not (Test-Path $acc)) { New-Item -ItemType Directory -Path $acc | Out-Null }
if ($LogTag -eq '') { $LogTag = $Tag }

function Log([string]$s) {
  $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' [' + $Tag + '] ' + $s
  Write-Output $line
  Add-Content -Path (Join-Path $K 'v9_runs.log') -Value $line -Encoding UTF8
}

function KillGame() {
  Get-Process WorldApart*, wastart, modlauncher, UnityCrashHandler* -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 3
  Get-Process WorldApart* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Seconds 2
}

function Archive([string]$suffix) {
  if (Test-Path $dest) {
    Copy-Item $dest (Join-Path $acc ("LogOutput_" + $LogTag + $suffix + ".log")) -Force
  }
  if (Test-Path $errl) {
    Copy-Item $errl (Join-Path $acc ("ErrorLog_" + $LogTag + $suffix + ".log")) -Force
  }
}

if ($ArchiveOnly) { Archive ''; Log 'archive only'; return }

# ---- real-mouse helpers (used by -ClickMODTab) ------------------------------
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class V9W {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int v);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("user32.dll")] public static extern IntPtr SetActiveWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  [DllImport("user32.dll", SetLastError=true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  public static string Title(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetWindowTextW(h, sb, 256); return sb.ToString(); }
  public static uint ClickAt(int x, int y) {
    SetCursorPos(x, y);
    INPUT[] a = new INPUT[2];
    a[0].type = 0; a[0].u.mi.dwFlags = 0x0002;
    a[1].type = 0; a[1].u.mi.dwFlags = 0x0004;
    return SendInput(2, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public struct RECT { public int Left, Top, Right, Bottom; }
  public struct POINT { public int X, Y; }
  public const uint LDOWN = 0x0002, LUP = 0x0004;
}
"@

# The desktop is scaled (2560x1600 display, 2048x1280 client rect = 125%). Without
# DPI awareness the window APIs return virtualised coordinates that do not match
# Unity's Screen.width/height, and SetCursorPos lands in the wrong place.
try { [V9W]::SetProcessDpiAwareness(2) | Out-Null } catch { try { [V9W]::SetProcessDPIAware() | Out-Null } catch { } }

function Count-Log([string]$pat) {
  if (-not (Test-Path $dest)) { return 0 }
  return (Select-String -Path $dest -Pattern $pat -SimpleMatch -ErrorAction SilentlyContinue | Measure-Object).Count
}

function Wait-LogMatch([string]$pat, [int]$timeoutSec) {
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt $timeoutSec) {
    Start-Sleep -Seconds 2
    if (Test-Path $dest) {
      $m = Select-String -Path $dest -Pattern $pat -ErrorAction SilentlyContinue | Select-Object -Last 1
      if ($m) { return $m.Line }
    }
  }
  return $null
}

function Click-MODTab([int]$n) {
  $line = Wait-LogMatch 'TABRECT unity=' $ClickPrepSec
  if (-not $line) { Log 'no TABRECT line; click phase skipped'; return }
  $m = [regex]::Match($line, 'unity=\((-?\d+),(-?\d+)\)')
  if (-not $m.Success) { Log ('TABRECT unparsed: ' + $line); return }
  $ux = [int]$m.Groups[1].Value; $uy = [int]$m.Groups[2].Value

  $proc = $null
  for ($i = 0; $i -lt 30; $i++) {
    $proc = Get-Process WorldApart* -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($proc) { break }
    Start-Sleep -Seconds 2
  }
  if (-not $proc) { Log 'no game window; click phase skipped'; return }
  $h = $proc.MainWindowHandle
  $fgw = [V9W]::GetForegroundWindow()
  $fgTid = [V9W]::GetWindowThreadProcessId($fgw, [IntPtr]::Zero)
  $myTid = [V9W]::GetCurrentThreadId()
  Log ("foreground before: hwnd=$fgw tid=$fgTid title='" + [V9W]::Title($fgw) + "'")
  [V9W]::AttachThreadInput($myTid, $fgTid, $true) | Out-Null
  [V9W]::ShowWindow($fgw, 6) | Out-Null          # minimise whatever holds the foreground
  Start-Sleep -Milliseconds 500
  [V9W]::ShowWindow($h, 9) | Out-Null
  [V9W]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
  [V9W]::SetForegroundWindow($h) | Out-Null
  [V9W]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
  [V9W]::BringWindowToTop($h) | Out-Null
  [V9W]::SetActiveWindow($h) | Out-Null
  [V9W]::SwitchToThisWindow($h, $true) | Out-Null
  [V9W]::AttachThreadInput($myTid, $fgTid, $false) | Out-Null
  Start-Sleep -Seconds 6
  $fg = [V9W]::GetForegroundWindow()
  Log ("window handle=$h foreground=$fg match=" + ($fg -eq $h) + " title='" + [V9W]::Title($h) + "'")
  Get-Process WorldApart_mod -ErrorAction SilentlyContinue |
    ForEach-Object { Log ("  proc pid=" + $_.Id + " hwnd=" + $_.MainWindowHandle + " title='" + $_.MainWindowTitle + "'") }

  $cr = New-Object V9W+RECT
  [V9W]::GetClientRect($h, [ref]$cr) | Out-Null
  $pt = New-Object V9W+POINT
  [V9W]::ClientToScreen($h, [ref]$pt) | Out-Null
  $winW = $cr.Right - $cr.Left; $winH = $cr.Bottom - $cr.Top
  $sw = 0; $sh = 0
  $sl = Select-String -Path $dest -Pattern 'TABRECT unity' -ErrorAction SilentlyContinue | Select-Object -Last 1
  if ($sl) {
    $sm = [regex]::Match($sl.Line, 'screen=(\d+)x(\d+)')
    if ($sm.Success) { $sw = [int]$sm.Groups[1].Value; $sh = [int]$sm.Groups[2].Value }
  }
  if (-not $sw -or -not $sh) { $sw = 3840; $sh = 2160 }
  $sx = $pt.X + [int]([double]$ux * $winW / $sw)
  $sy = $pt.Y + [int]([double]($sh - $uy) * $winH / $sh)
  Log ("TABRECT unity=($ux,$uy) -> window ($sx,$sy) client ${winW}x${winH} unityScreen ${sw}x${sh}")

  $cp = New-Object V9W+POINT
  [V9W]::SetCursorPos($sx, $sy) | Out-Null
  Start-Sleep -Milliseconds 300
  [V9W]::GetCursorPos([ref]$cp) | Out-Null
  Log ("cursor readback after SetCursorPos: ($($cp.X),$($cp.Y)) wanted ($sx,$sy)")

  $c0 = Count-Log 'MOD tab clicked'
  $r0 = Count-Log 'MOD tab click ignored'
  $o0 = Count-Log 'MODPAGE] shown (native pages hidden'
  $h0 = Count-Log 'MODPAGE] hidden'
  Log ("before clicks: clicked=$c0 ignored=$r0 shown=$o0 hidden=$h0")

  for ($i = 1; $i -le $n; $i++) {
    [V9W]::SetCursorPos($sx, $sy) | Out-Null
    Start-Sleep -Milliseconds 400
    $sent = [V9W]::ClickAt($sx, $sy)
    if ($sent -ne 2) {
      Log ("click $i/$n : SendInput returned " + $sent + ", falling back to mouse_event")
      [V9W]::mouse_event([V9W]::LDOWN, 0, 0, 0, [UIntPtr]::Zero)
      Start-Sleep -Milliseconds 90
      [V9W]::mouse_event([V9W]::LUP, 0, 0, 0, [UIntPtr]::Zero)
    }
    Start-Sleep -Seconds 3
    Log ("click $i/$n : clicked=" + (Count-Log 'MOD tab clicked') +
         " ignored=" + (Count-Log 'MOD tab click ignored') +
         " shown=" + (Count-Log 'MODPAGE] shown (native pages hidden') +
         " hidden=" + (Count-Log 'MODPAGE] hidden'))
  }
  Log ("after clicks: clicked=" + (Count-Log 'MOD tab clicked') +
       " ignored=" + (Count-Log 'MOD tab click ignored') +
       " shown=" + (Count-Log 'MODPAGE] shown (native pages hidden') +
       " hidden=" + (Count-Log 'MODPAGE] hidden'))
}

# fresh environment: drop every MOD_* left over from a previous call
Get-ChildItem Env: | Where-Object { $_.Name -like 'MOD_*' } | ForEach-Object {
  Remove-Item ("Env:\" + $_.Name) -ErrorAction SilentlyContinue
}

if (-not $NoFrameLog) {
  $env:MOD_FRAME_LOG = Join-Path $K ("v9_frames_" + $Tag + ".log")
  $env:MOD_FRAME_TAG = $Tag
}
if (-not $NoSelfTestOpen) { $env:MOD_P2_SELFTEST = '1' }

foreach ($kv in ($Vars -split ';')) {
  $i = $kv.IndexOf('=')
  if ($i -gt 0) {
    $name = $kv.Substring(0, $i).Trim()
    $val = $kv.Substring($i + 1).Trim()
    Set-Item ("Env:\" + $name) $val
  }
}

$shown = (Get-ChildItem Env: | Where-Object { $_.Name -like 'MOD_*' } |
          ForEach-Object { $_.Name + '=' + $_.Value }) -join ' | '
Log ("launch tag=" + $Tag + " hold=" + $HoldSec + "s env: " + $shown)

$crashes = 0
$attempt = 0
$launched = $false

while ($attempt -lt $MaxAttempts -and -not $launched) {
  $attempt++
  KillGame
  Remove-Item $dest -ErrorAction SilentlyContinue
  Remove-Item $errl -ErrorAction SilentlyContinue
  Log ("attempt " + $attempt + ": launching modlauncher.exe WorldApart.exe")
  Start-Process (Join-Path $G 'modlauncher.exe') -ArgumentList (Join-Path $G 'WorldApart.exe') `
    -WorkingDirectory $G | Out-Null

  # wait for the injected game process to appear
  $proc = $null
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt 90) {
    Start-Sleep -Seconds 2
    $proc = Get-Process WorldApart_mod, WorldApart -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -ne 'modlauncher' } | Select-Object -First 1
    if ($proc) { break }
  }
  if (-not $proc) {
    $crashes++
    Log ("attempt " + $attempt + ": NO GAME PROCESS within 90s -> counted as crash")
    Archive ('_attempt' + $attempt + '_noproc')
    continue
  }
  Log ("attempt " + $attempt + ": pid=" + $proc.Id + " name=" + $proc.ProcessName + " started=" + $proc.StartTime)

  if ($ClickMODTab) { try { Click-MODTab $Clicks } catch { Log ('click phase failed: ' + $_) } }

  # hold; note an early exit as a crash
  $t0 = Get-Date
  $alive = $true
  while (((Get-Date) - $t0).TotalSeconds -lt $HoldSec) {
    Start-Sleep -Seconds 2
    if (-not (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue)) { $alive = $false; break }
  }
  Archive ('_attempt' + $attempt)
  if (-not $alive) {
    $crashes++
    $el = [int]((Get-Date) - $t0).TotalSeconds
    Log ("attempt " + $attempt + ": process died after " + $el + "s in hold -> crash (total crashes=" + $crashes + ")")
    Start-Sleep -Seconds $GapSec
    continue
  }
  $launched = $true
  Log ("attempt " + $attempt + ": held " + $HoldSec + "s OK (crashes so far=" + $crashes + ")")
}

if (-not $launched) {
  Log ("FAILED: no successful run in " + $MaxAttempts + " attempts (crashes=" + $crashes + ")")
  return
}

if (-not $KeepRunning) {
  KillGame
  Log ("killed the game after the hold; attempts=" + $attempt + " crashes=" + $crashes)
} else {
  Log ("leaving the game running; attempts=" + $attempt + " crashes=" + $crashes)
}
