$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
# 迁移后注册表项可能整体缺失，先确保键存在（原脚本假设键已存在，新机上会重试 10 次后失败）
New-Item -Path $ifeo -Force -ErrorAction SilentlyContinue | Out-Null
$ok = $false
for ($i = 0; $i -lt 10 -and -not $ok; $i++) {
    try {
        Remove-ItemProperty -Path $ifeo -Name 'VerifierDlls' -ErrorAction Stop
    } catch {}
    try {
        Remove-ItemProperty -Path $ifeo -Name 'GlobalFlag' -ErrorAction Stop
    } catch {}
    try {
        New-ItemProperty -Path $ifeo -Name 'Debugger' -Value '<G>\KIMI\injector\wastart.exe' -PropertyType String -Force -ErrorAction Stop | Out-Null
        $ok = $true
    } catch {
        Start-Sleep 1
    }
}
if ($ok) {
    Write-Output 'IFEO Debugger set:'
    Get-ItemProperty $ifeo | Format-List Debugger
} else {
    Write-Output 'FAILED after retries'
}
