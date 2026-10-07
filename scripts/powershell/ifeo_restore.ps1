$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
Set-ItemProperty -Path $ifeo -Name 'VerifierDlls' -Value 'winhttp.dll' -Type String
Get-ItemProperty $ifeo | Format-List VerifierDlls, GlobalFlag
