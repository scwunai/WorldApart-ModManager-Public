Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

$found = @()
$cb = [Win+EnumProc]{
  param($h, $l)
  $sb = New-Object System.Text.StringBuilder 512
  [Win]::GetWindowTextW($h, $sb, 512) | Out-Null
  $title = $sb.ToString()
  if ($title.Length -gt 0) {
    $pid2 = 0
    [Win]::GetWindowThreadProcessId($h, [ref]$pid2) | Out-Null
    $pname = ''
    try { $pname = (Get-Process -Id $pid2 -ErrorAction Stop).ProcessName } catch {}
    $script:found += [pscustomobject]@{ PID = $pid2; Proc = $pname; Title = $title; Visible = [Win]::IsWindowVisible($h) }
    # child window texts (message box body text lives in a child static)
    $childTexts = @()
    $cb2 = [Win+EnumProc]{
      param($ch, $cl)
      $sb2 = New-Object System.Text.StringBuilder 1024
      [Win]::GetWindowTextW($ch, $sb2, 1024) | Out-Null
      if ($sb2.Length -gt 0) { $script:childTexts += $sb2.ToString() }
      return $true
    }
    [Win]::EnumChildWindows($h, $cb2, [IntPtr]::Zero) | Out-Null
    foreach ($c in $script:childTexts) {
      $script:found += [pscustomobject]@{ PID = $pid2; Proc = $pname; Title = "  > $c"; Visible = $true }
    }
    $script:childTexts = @()
  }
  return $true
}
[Win]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
$found | Where-Object { $_.Proc -match 'WorldApart|wastart|dotnet' -or $_.Title -match 'error|Error|Fatal|BepInEx|Failed|KIMI' } |
  Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Output
Write-Output "--- all top-level windows of game procs ---"
$found | Where-Object { $_.Proc -match 'WorldApart' -and $_.Title -notmatch '^  >' } |
  Format-Table -AutoSize -Wrap | Out-String -Width 200 | Write-Output
