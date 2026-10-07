# KIMI MOD Manager — WorldApart 本地 Mod 管理器（外部工具）
# 功能：列出 Mods 目录的 Mod、读取/写入启用状态（注册表）、调整加载顺序
# 注意：游戏运行期间不要修改（游戏退出时会覆盖偏好）

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:ModsRoot = Join-Path $env:LOCALAPPDATA '..\LocalLow\Nuverse\WorldApart\Mods'
$script:ModsRoot = [System.IO.Path]::GetFullPath($script:ModsRoot)
$script:RegPath  = 'Software\Nuverse\WorldApart'
$script:EntriesValueName = 'MOD_ENABLED_ENTRIES_V2_h2913630511'
$script:OrderValueName   = 'MOD_ORDER_V1_h2735767082'

function Read-RegBytes($name) {
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RegPath)
    if ($null -eq $k) { return $null }
    $v = $k.GetValue($name)
    $k.Close()
    if ($v -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($v).TrimEnd("`0") }
    return $null
}

function Write-RegBytes($name, [string]$text) {
    $k = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($script:RegPath, $true)
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text + [char]0)
    $k.SetValue($name, $bytes, [Microsoft.Win32.RegistryValueKind]::Binary)
    $k.Close()
}

function Get-GameRunning {
    return [bool](Get-Process -Name 'WorldApart' -ErrorAction SilentlyContinue)
}

function Get-ModPackages {
    $pkgs = @()
    if (Test-Path $script:ModsRoot) {
        Get-ChildItem $script:ModsRoot -Directory | ForEach-Object {
            $mj = Join-Path $_.FullName 'mod.json'
            $pkg = [pscustomobject]@{ ModId=$_.Name; Title=$_.Name; Author=''; Version=''; Units=@(); Dir=$_.FullName; LoadError=$null }
            if (Test-Path $mj) {
                try {
                    $j = Get-Content $mj -Raw -Encoding UTF8 | ConvertFrom-Json
                    $pkg.Title = if ($j.title) { $j.title } else { $_.Name }
                    $pkg.Author = $j.author; $pkg.Version = $j.version
                    $pkg.Units = @($j.units | ForEach-Object { "{0} [{1}]" -f $_.unitId, $_.type })
                } catch { $pkg.LoadError = 'mod.json 解析失败: ' + $_.Exception.Message }
            } else { $pkg.LoadError = '缺少 mod.json' }
            $pkgs += $pkg
        }
    }
    return $pkgs
}

# ---------- UI ----------
$form = New-Object System.Windows.Forms.Form
$form.Text = 'KIMI MOD Manager — WorldApart'
$form.Size = New-Object System.Drawing.Size(720, 520)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 9)

$lblInfo = New-Object System.Windows.Forms.Label
$lblInfo.Location = New-Object System.Drawing.Point(12, 10)
$lblInfo.Size = New-Object System.Drawing.Size(680, 36)
$lblInfo.Text = "Mods 目录: $script:ModsRoot`n启用状态写入注册表（游戏关闭时修改才有效）"
$form.Controls.Add($lblInfo)

$list = New-Object System.Windows.Forms.ListView
$list.Location = New-Object System.Drawing.Point(12, 52)
$list.Size = New-Object System.Drawing.Size(680, 330)
$list.View = 'Details'
$list.CheckBoxes = $true
$list.FullRowSelect = $true
$list.GridLines = $true
[void]$list.Columns.Add('启用', 50)
[void]$list.Columns.Add('Mod 名称', 170)
[void]$list.Columns.Add('版本', 60)
[void]$list.Columns.Add('作者', 90)
[void]$list.Columns.Add('单元', 200)
[void]$list.Columns.Add('ModId', 100)
$form.Controls.Add($list)

$btnSave = New-Object System.Windows.Forms.Button
$btnSave.Location = New-Object System.Drawing.Point(12, 396)
$btnSave.Size = New-Object System.Drawing.Size(120, 32)
$btnSave.Text = '保存启用状态'
$form.Controls.Add($btnSave)

$btnOpenDir = New-Object System.Windows.Forms.Button
$btnOpenDir.Location = New-Object System.Drawing.Point(142, 396)
$btnOpenDir.Size = New-Object System.Drawing.Size(120, 32)
$btnOpenDir.Text = '打开 Mods 目录'
$form.Controls.Add($btnOpenDir)

$btnRefresh = New-Object System.Windows.Forms.Button
$btnRefresh.Location = New-Object System.Drawing.Point(272, 396)
$btnRefresh.Size = New-Object System.Drawing.Size(120, 32)
$btnRefresh.Text = '刷新'
$form.Controls.Add($btnRefresh)

$lblWarn = New-Object System.Windows.Forms.Label
$lblWarn.Location = New-Object System.Drawing.Point(12, 436)
$lblWarn.Size = New-Object System.Drawing.Size(680, 40)
$lblWarn.ForeColor = [System.Drawing.Color]::DarkRed
$form.Controls.Add($lblWarn)

function Refresh-List {
    $list.Items.Clear()
    $enabledSet = @{}
    $raw = Read-RegBytes $script:EntriesValueName
    if ($raw) { $raw -split "`n" | ForEach-Object { $enabledSet[$_] = $true } }
    foreach ($pkg in Get-ModPackages) {
        foreach ($unit in $pkg.Units) {
            if ($unit -match '^([^\[]+)\s*\[') { $unitId = $matches[1].Trim() } else { $unitId = $unit }
            $entryKey = "{0}|{1}|0" -f $pkg.ModId, $unitId
            $item = New-Object System.Windows.Forms.ListViewItem('')
            $item.Checked = $enabledSet.ContainsKey($entryKey)
            $item.SubItems.Add($pkg.Title) | Out-Null
            $item.SubItems.Add($pkg.Version) | Out-Null
            $item.SubItems.Add($pkg.Author) | Out-Null
            $item.SubItems.Add($unit) | Out-Null
            $item.SubItems.Add($pkg.ModId) | Out-Null
            $item.Tag = $entryKey
            if ($pkg.LoadError) { $item.ForeColor = [System.Drawing.Color]::Red; $item.SubItems.Add($pkg.LoadError) | Out-Null }
            [void]$list.Items.Add($item)
        }
        if ($pkg.Units.Count -eq 0) {
            $item = New-Object System.Windows.Forms.ListViewItem('')
            $item.SubItems.Add($pkg.Title) | Out-Null
            $item.SubItems.Add($pkg.Version) | Out-Null
            $item.SubItems.Add($pkg.Author) | Out-Null
            $item.SubItems.Add($pkg.LoadError) | Out-Null
            $item.SubItems.Add($pkg.ModId) | Out-Null
            $item.Tag = $null
            $item.ForeColor = [System.Drawing.Color]::Red
            [void]$list.Items.Add($item)
        }
    }
    if (Get-GameRunning) {
        $lblWarn.Text = '警告：检测到游戏正在运行！游戏退出时会覆盖注册表，请先关闭游戏再保存。'
    } else { $lblWarn.Text = '游戏未运行，可以安全保存。' }
}

$btnSave.Add_Click({
    if (Get-GameRunning) {
        [System.Windows.Forms.MessageBox]::Show('请先关闭游戏再保存！', '游戏运行中', 'OK', 'Warning') | Out-Null
        return
    }
    $lines = @()
    foreach ($item in $list.Items) { if ($item.Checked -and $item.Tag) { $lines += $item.Tag } }
    Write-RegBytes $script:EntriesValueName ($lines -join "`n")
    # MOD_ORDER：按列表中已启用 Mod 的顺序
    $order = @()
    foreach ($item in $list.Items) {
        if ($item.Checked -and $item.Tag) {
            $modId = $item.Tag.Split('|')[0]
            if ($order -notcontains $modId) { $order += $modId }
        }
    }
    Write-RegBytes $script:OrderValueName ($order -join "`n")
    $lblWarn.ForeColor = [System.Drawing.Color]::DarkGreen
    $lblWarn.Text = '已保存 ' + $lines.Count + ' 个启用单元。'
})

$btnOpenDir.Add_Click({ if (Test-Path $script:ModsRoot) { Start-Process explorer.exe $script:ModsRoot } })
$btnRefresh.Add_Click({ Refresh-List })

Refresh-List
[void]$form.ShowDialog()
