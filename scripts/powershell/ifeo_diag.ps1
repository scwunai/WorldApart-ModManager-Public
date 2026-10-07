$ifeo = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\WorldApart.exe'
$out = '<G>\KIMI\ifeo_diag.txt'
"=== diag start ===" | Out-File $out
try {
    New-Item -Path $ifeo -Force -ErrorAction Stop | Out-Null
    "New-Item OK" | Out-File $out -Append
} catch {
    ("New-Item FAIL: " + $_.Exception.Message) | Out-File $out -Append
}
try {
    New-ItemProperty -Path $ifeo -Name 'Debugger' -Value '<G>\KIMI\injector\wastart.exe' -PropertyType String -Force -ErrorAction Stop | Out-Null
    "Debugger write OK" | Out-File $out -Append
} catch {
    ("Debugger write FAIL: " + $_.Exception.GetType().FullName + " | " + $_.Exception.Message) | Out-File $out -Append
}
try {
    $v = Get-ItemProperty $ifeo
    ("Read back: Debugger=" + $v.Debugger) | Out-File $out -Append
} catch {
    ("Read back FAIL: " + $_.Exception.Message) | Out-File $out -Append
}
"=== diag end ===" | Out-File $out -Append
