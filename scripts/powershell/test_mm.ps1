$errs = $null
[System.Management.Automation.PSParser]::Tokenize((Get-Content '<G>\KIMI\ModManager.ps1' -Raw), [ref]$errs) | Out-Null
Write-Output ('语法错误数: ' + $errs.Count)
foreach ($e in $errs) { Write-Output $e.Message }
$k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Nuverse\WorldApart')
$v = $k.GetValue('MOD_ENABLED_ENTRIES_V2_h2913630511')
$s = [System.Text.Encoding]::UTF8.GetString($v).TrimEnd([char]0)
Write-Output ('当前启用项: ' + ($s -replace "`n", ' ; '))
$modsRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA '..\LocalLow\Nuverse\WorldApart\Mods'))
Write-Output ('ModsRoot: ' + $modsRoot + '  存在: ' + (Test-Path $modsRoot))
Get-ChildItem $modsRoot -Directory | ForEach-Object { Write-Output ('  mod: ' + $_.Name) }
