$ev = Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-30)} -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'WorldApart|winhttp|Doorstop|BepInEx|Application Error' }
if ($ev) {
    $ev | ForEach-Object { Write-Output ("[{0}] {1} ({2})" -f $_.TimeCreated, $_.ProviderName, $_.Id); Write-Output ($_.Message -split "`n" | Select-Object -First 8 | Out-String) }
} else {
    Write-Output 'NO_MATCHING_EVENTS'
}
Write-Output '---PROCS---'
Get-Process | Where-Object { $_.ProcessName -match 'WorldApart|steam' } | Select-Object ProcessName, Id, StartTime | Format-Table | Out-String
