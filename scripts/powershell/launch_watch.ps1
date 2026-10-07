Start-Process '<G>\WorldApart.exe' -WorkingDirectory '<G>'
Start-Sleep 10
$p = Get-Process WorldApart -ErrorAction SilentlyContinue
if ($p) { Write-Output ('RUNNING pid=' + $p.Id) } else { Write-Output 'NOT_RUNNING' }
$log = '<G>\BepInEx\LogOutput.log'
if (Test-Path $log) { Write-Output '--- LogOutput.log ---'; Get-Content $log -TotalCount 60 } else { Write-Output 'NO_LOGOUTPUT' }
$il = '<G>\BepInEx\interop'
if (Test-Path $il) { Write-Output ('INTEROP_DIR_EXISTS files=' + (Get-ChildItem $il).Count) } else { Write-Output 'NO_INTEROP_DIR' }
