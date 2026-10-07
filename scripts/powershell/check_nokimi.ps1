$files = Get-ChildItem '<G>\BepInEx\plugins\*.dll'
$hits = @()
foreach ($f in $files) {
    $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes).ToLower()
    if ($ascii.Contains('kimi')) { $hits += $f.Name }
}
if ($hits.Count -gt 0) { $hits } else { 'no kimi hits in plugins' }
