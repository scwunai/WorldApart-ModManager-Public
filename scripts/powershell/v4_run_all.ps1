$ErrorActionPreference = 'Continue'
$G = '<G>'
$K = "$G\KIMI"
$P = "$G\BepInEx\plugins"
$HOLD = "$K\v4_hold"

New-Item -ItemType Directory -Force -Path $HOLD | Out-Null

function Hold-Plugin($name) {
  if (Test-Path "$P\$name") { Move-Item -Force "$P\$name" "$HOLD\$name"; return $true }
  return $false
}
function Release-Plugin($name) {
  if (Test-Path "$HOLD\$name") { Move-Item -Force "$HOLD\$name" "$P\$name" }
}

# ---- A: no injection at all (hardlink exe, IFEO not triggered) ----
Hold-Plugin 'LocalModManager.dll' | Out-Null
& "$K\v4_measure.ps1" -Scenario A -SteadySec 180 -NoInjector

# ---- B: injection + BepInEx + frame instrument only (mod manager absent) ----
& "$K\v4_measure.ps1" -Scenario B -SteadySec 180

# ---- D1: fixed build, but the old 10 s re-apply loop forced back on ----
Release-Plugin 'LocalModManager.dll'
$env:MOD_PERF_LEGACY_PERIODIC = '1'
& "$K\v4_measure.ps1" -Scenario D1 -SteadySec 240

# ---- D2: fixed build, default behaviour ----
Remove-Item Env:\MOD_PERF_LEGACY_PERIODIC -EA SilentlyContinue
& "$K\v4_measure.ps1" -Scenario D2 -SteadySec 240

Write-Output 'ALL_SCENARIOS_DONE'
