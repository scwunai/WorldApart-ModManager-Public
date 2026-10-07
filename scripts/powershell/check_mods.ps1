$p = Get-Process WorldApart -ErrorAction SilentlyContinue
if (-not $p) { Write-Output 'GAME_NOT_RUNNING'; exit }
Write-Output ('pid=' + $p.Id)
$mods = $p.Modules | Select-Object -ExpandProperty ModuleName
Write-Output '--- modules of interest ---'
$mods | Where-Object { $_ -match 'winhttp|kimi_shim|doorstop|BepInEx|UnityPlayer|GameAssembly|gpShell' }
Write-Output ('total_modules=' + $mods.Count)
