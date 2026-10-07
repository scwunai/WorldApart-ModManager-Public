param(
  [string]$Tag = 'S3',
  [switch]$NoRecon,
  [switch]$NoSelfTestOpen
)
$G = '<G>'

Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
Get-Process WorldApart* -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 2

$env:MOD_FRAME_LOG = "$G\KIMI\v6_frames_$Tag.log"
$env:MOD_FRAME_TAG = $Tag
if ($NoRecon) { Remove-Item Env:\MOD_Q1_RECON -EA SilentlyContinue } else { $env:MOD_Q1_RECON = '1' }
if ($NoSelfTestOpen) { Remove-Item Env:\MOD_P2_SELFTEST -EA SilentlyContinue } else { $env:MOD_P2_SELFTEST = '1' }
Remove-Item Env:\MOD_REV_TEST -EA SilentlyContinue

Write-Output "launching WorldApart.exe RECON=$($env:MOD_Q1_RECON) OPENSETTINGS=$($env:MOD_P2_SELFTEST) DEBUG=$($env:MOD_DEBUG) DBGSELFTEST=$($env:MOD_DEBUG_SELFTEST)"
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G
