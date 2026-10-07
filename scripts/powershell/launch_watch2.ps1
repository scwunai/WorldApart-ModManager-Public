Start-Process '<G>\WorldApart.exe' -WorkingDirectory '<G>'
Start-Sleep 20
$p = Get-Process WorldApart* -ErrorAction SilentlyContinue
if ($p) { $p | Select-Object ProcessName, Id, StartTime | Format-Table | Out-String } else { Write-Output 'NOT_RUNNING' }
$log = '<G>\KIMI\injector\kimi_inject.log'
if (Test-Path $log) {
    Write-Output '--- inject log ---'
    Get-Content $log
} else { Write-Output 'NO_INJECT_LOG' }
$blog = '<G>\BepInEx\LogOutput.log'
if (Test-Path $blog) {
    Write-Output '--- BepInEx log (first 40 lines) ---'
    Get-Content $blog -TotalCount 40
} else { Write-Output 'NO_BEPINEX_LOG_YET' }
$il = '<G>\BepInEx\interop'
if (Test-Path $il) { Write-Output ('INTEROP files=' + (Get-ChildItem $il).Count) } else { Write-Output 'NO_INTEROP_DIR_YET' }
