param(
  [switch]$NoSelfTests
)
$G = '<G>'

Get-Process WorldApart*,wastart -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 3
Get-Process WorldApart* -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 2

$env:MOD_FRAME_LOG = "$G\KIMI\v4_frames_FINAL.log"
$env:MOD_FRAME_TAG = 'FINAL'
if ($NoSelfTests) {
  Remove-Item Env:\MOD_REV_TEST -EA SilentlyContinue
  Remove-Item Env:\MOD_P2_SELFTEST -EA SilentlyContinue
} else {
  $env:MOD_REV_TEST = '1'
  $env:MOD_P2_SELFTEST = '1'
}

Write-Output "launching WorldApart.exe  REV=$($env:MOD_REV_TEST) P2SELFTEST=$($env:MOD_P2_SELFTEST)"
Start-Process "$G\WorldApart.exe" -WorkingDirectory $G
