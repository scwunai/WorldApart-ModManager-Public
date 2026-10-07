$dir = '<G>'
# temporarily move doorstop winhttp.dll away
if (Test-Path "$dir\winhttp.dll") {
    Move-Item "$dir\winhttp.dll" "$dir\KIMI\winhttp.dll.doorstop" -Force
    Write-Output 'doorstop winhttp.dll moved away'
}
$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
Set-ItemProperty -Path $ifeo -Name 'VerifierDlls' -Value 'winhttp.dll' -Type String
Start-Process "$dir\WorldApart.exe" -WorkingDirectory $dir
Start-Sleep 10
$p = Get-Process WorldApart -ErrorAction SilentlyContinue
if ($p) { Write-Output ('RUNNING pid=' + $p.Id) } else { Write-Output 'NOT_RUNNING' }
if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
# restore doorstop dll
Move-Item "$dir\KIMI\winhttp.dll.doorstop" "$dir\winhttp.dll" -Force
Write-Output 'doorstop winhttp.dll restored'
