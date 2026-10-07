$procs = Get-Process WorldApart* -ErrorAction SilentlyContinue
if (-not $procs) { Write-Output 'NO_GAME_PROCESS'; exit }
foreach ($p in $procs) {
    Write-Output ('=== ' + $p.ProcessName + ' pid=' + $p.Id + ' ===')
    try {
        $mods = $p.Modules | Select-Object -ExpandProperty ModuleName
        Write-Output ('modules=' + $mods.Count)
        $mods | Where-Object { $_ -match 'Unity|GameAssembly|gpShell|winhttp|kimi|mono|il2cpp|doorstop' }
    } catch {
        Write-Output ('modules enum failed: ' + $_.Exception.Message)
    }
}
