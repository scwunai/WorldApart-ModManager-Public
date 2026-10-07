Start-Process '<G>\WorldApart.exe' -WorkingDirectory '<G>'
Start-Sleep 6
$p = Get-Process WorldApart -ErrorAction SilentlyContinue
if ($p) {
    Write-Output ('RUNNING pid=' + $p.Id)
} else {
    Write-Output 'NOT_RUNNING (verifier blocked start => IFEO active)'
}
