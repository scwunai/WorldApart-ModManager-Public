$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\notepad.exe'
New-Item -Path $ifeo -Force -ErrorAction SilentlyContinue | Out-Null
New-ItemProperty -Path $ifeo -Name 'VerifierDlls' -Value 'kimi_shim.dll' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $ifeo -Name 'GlobalFlag' -Value 0x100 -PropertyType DWord -Force | Out-Null
Remove-Item 'C:\Windows\System32\kimi_shim_marker.txt' -ErrorAction SilentlyContinue
Start-Process notepad.exe
Start-Sleep 5
$p = Get-Process notepad -ErrorAction SilentlyContinue
if ($p) {
    Write-Output ('NOTEPAD RUNNING pid=' + ($p.Id -join ','))
    $p | Stop-Process -Force
} else { Write-Output 'NO_NOTEPAD' }
if (Test-Path 'C:\Windows\System32\kimi_shim_marker.txt') {
    Write-Output '--- marker ---'
    Get-Content 'C:\Windows\System32\kimi_shim_marker.txt'
} else { Write-Output 'NO_MARKER' }
Remove-Item $ifeo -Recurse -Force
Remove-Item 'C:\Windows\System32\kimi_shim.dll' -Force
Remove-Item 'C:\Windows\System32\notepad_kimi.exe' -Force
Write-Output 'cleaned up'
