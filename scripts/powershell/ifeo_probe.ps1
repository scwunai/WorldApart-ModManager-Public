$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
Set-ItemProperty -Path $ifeo -Name 'VerifierDlls' -Value 'kimi_missing_probe.dll' -Type String
Write-Output 'VerifierDlls set to kimi_missing_probe.dll'
