$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
try {
    New-Item -Path $ifeo -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $ifeo -Name 'VerifierDlls' -PropertyType String -Value 'winhttp.dll' -Force -ErrorAction Stop | Out-Null
    New-ItemProperty -Path $ifeo -Name 'GlobalFlag' -PropertyType DWord -Value 0x100 -Force -ErrorAction Stop | Out-Null
    Write-Output 'IFEO written'
    Get-ItemProperty $ifeo | Format-List VerifierDlls, GlobalFlag
} catch {
    Write-Output ('FAILED: ' + $_.Exception.Message)
}
