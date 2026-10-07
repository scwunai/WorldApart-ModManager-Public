$out = '<G>\KIMI\crash_check.txt'
"=== Application event log (last 15 min) ===" | Out-File $out
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-15)} -ErrorAction SilentlyContinue |
  Where-Object { $_.LevelDisplayName -match 'Error|Warning' -or $_.Message -match 'WorldApart|wastart|modprobe' } |
  Select-Object -First 15 TimeCreated, Id, ProviderName, @{n='Msg';e={$_.Message.Substring(0,[Math]::Min(300,$_.Message.Length))}} |
  Format-List | Out-File $out -Append
"=== processes ===" | Out-File $out -Append
Get-Process WorldApart*,wastart,modlauncher,UnityCrashHandler* -ErrorAction SilentlyContinue | Select-Object Id, ProcessName, StartTime | Format-Table | Out-File $out -Append
"=== done ===" | Out-File $out -Append
