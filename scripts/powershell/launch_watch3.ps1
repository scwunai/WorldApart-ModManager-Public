Start-Process '<G>\WorldApart.exe' -WorkingDirectory '<G>'
Start-Sleep 15
$p = Get-Process WorldApart* -ErrorAction SilentlyContinue
if ($p) { $p | Select-Object ProcessName, Id, StartTime | Format-Table | Out-String } else { Write-Output 'NOT_RUNNING_YET' }
$log = '<G>\KIMI\injector\kimi_inject.log'
if (Test-Path $log) { Write-Output '--- inject log ---'; Get-Content $log }
$probe = '<G>\kimi_probe.txt'
if (Test-Path $probe) { Write-Output '--- probe ---'; Get-Content $probe }
$blog = '<G>\BepInEx\LogOutput.log'
if (Test-Path $blog) { Write-Output '--- BepInEx log ---'; Get-Content $blog -TotalCount 30 } else { Write-Output 'NO_BEPINEX_LOG_YET' }
