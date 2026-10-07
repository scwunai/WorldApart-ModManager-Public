$procs = Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'WorldApart' }
foreach ($p in $procs) {
    $parent = (Get-Process -Id $p.ParentProcessId -ErrorAction SilentlyContinue).ProcessName
    Write-Output ($p.Name + ' pid=' + $p.ProcessId + ' parent=' + $parent + '(' + $p.ParentProcessId + ') cmd=' + $p.CommandLine)
}
Write-Output '--- module paths (winhttp/unity) ---'
foreach ($p in (Get-Process WorldApart*)) {
    Write-Output ('== pid ' + $p.Id)
    try {
        $p.Modules | Where-Object { $_.ModuleName -match 'WINHTTP|Unity|gpShell' } | ForEach-Object {
            Write-Output ('   ' + $_.ModuleName + ' @ ' + $_.FileName)
        }
    } catch { Write-Output '   enum failed' }
}
