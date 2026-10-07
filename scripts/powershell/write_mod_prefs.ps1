$k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Nuverse\WorldApart', $true)
$val = "com.kimi.storytest|persona_patch|0`ncom.kimi.storytest|basecfg_patch|0`ncom.kimi.modmanager|menu_patch|0" + [char]0
$bytes = [System.Text.Encoding]::UTF8.GetBytes($val)
$k.SetValue('MOD_ENABLED_ENTRIES_V2_h2913630511', $bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
$order = "com.kimi.modmanager`ncom.kimi.storytest" + [char]0
$k.SetValue('MOD_ORDER_V1_h2735767082', [System.Text.Encoding]::UTF8.GetBytes($order), [Microsoft.Win32.RegistryValueKind]::Binary)
$k.Close()
Write-Output 'written'
$k2 = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Nuverse\WorldApart')
Write-Output ('entries: ' + [System.Text.Encoding]::UTF8.GetString($k2.GetValue('MOD_ENABLED_ENTRIES_V2_h2913630511')))
Write-Output ('order:   ' + [System.Text.Encoding]::UTF8.GetString($k2.GetValue('MOD_ORDER_V1_h2735767082')))
