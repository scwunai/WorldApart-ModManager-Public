$dir = '<G>'
Copy-Item "$dir\KIMI\shim\kimi_shim.dll" "$dir\kimi_shim.dll" -Force
$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
Set-ItemProperty -Path $ifeo -Name 'VerifierDlls' -Value 'kimi_shim.dll' -Type String
Get-ItemProperty $ifeo | Format-List VerifierDlls, GlobalFlag
Remove-Item "$env:TEMP\kimi_shim_marker.txt" -ErrorAction SilentlyContinue
Remove-Item "$dir\BepInEx\LogOutput.log" -ErrorAction SilentlyContinue
Start-Process "$dir\WorldApart.exe" -WorkingDirectory $dir
Start-Sleep 12
$p = Get-Process WorldApart -ErrorAction SilentlyContinue
if ($p) { Write-Output ('RUNNING pid=' + $p.Id) } else { Write-Output 'NOT_RUNNING' }
if (Test-Path "$env:TEMP\kimi_shim_marker.txt") {
    Write-Output '--- marker ---'
    Get-Content "$env:TEMP\kimi_shim_marker.txt"
} else { Write-Output 'NO_MARKER' }
$log = "$dir\BepInEx\LogOutput.log"
if (Test-Path $log) { Write-Output '--- LogOutput ---'; Get-Content $log -TotalCount 40 } else { Write-Output 'NO_LOGOUTPUT' }
