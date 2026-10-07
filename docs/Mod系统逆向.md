# WorldApart 原生 Mod 系统逆向笔记

逆向时间：2026-10-05
工具：Il2CppDumper v6.7.46（输出在 `KIMI\il2cpp_out\`，含 dump.cs 与 DummyDll）
涉及程序集：`Game.dll`（命名空间 `Game.Mod`）

---

## 一、目录结构

```
%LocalAppData%\..\LocalLow\Nuverse\WorldApart\Mods\<modId>\
├── mod.json          # 清单（必需）
├── tables\           # table_patch 单元的数据目录
│   └── <表名>.patch.json
├── assets\           # asset_override 单元的数据目录（可选）
└── cover.png / preview.jpg   # 展示图（可选）
```

- `LocalModSource.ROOT_FOLDER_NAME = "Mods"`，每个子目录 = 一个 Mod（`ModRootFor(modId)`）
- Mod 也可不带 mod.json（`LoadConvention` 按目录约定自动合成 units），但显式清单更可靠
- 订阅的 Mod 走 `SubscribedModSource`（Steam 工坊），本地 Mod 走 `LocalModSource`

## 二、mod.json 清单格式（已验证字段，来自 ModManifest 类）

```json
{
  "schemaVersion": 5,              // CURRENT_SCHEMA_VERSION = 5，高了会报 "高于本版本支持的"
  "modId": "com.kimi.storytest",   // 全局唯一，校验见 IsValidModId
  "title": "KIMI 剧情修改测试",
  "author": "KIMI",
  "version": "0.1.0",
  "priority": 0,                   // 多个 Mod 改同一张表时的应用顺序
  "publishedFileId": 0,            // 本地 Mod 填 0
  "units": [
    { "unitId": "persona_patch", "type": "table_patch", "root": "tables" }
  ]
}
```

单元类型（`ModUnitTypeNames`）：`npc_avatar` / `table_patch` / `asset_override`

## 三、表补丁格式（`<表名>.patch.json`）

```json
{
  "keyFields": ["npcId"],          // 必填，非空字符串数组，主键字段
  "refFields": {"字段名": "表名"},  // 可选，外键字段 -> 目标表名，用于引用校验
  "upsert": [ { "npcId": "com.kimi.storytest:narrator", "speakStyle": "……" } ],
  "remove": [ "主键值" ]
}
```

- 表名 = 文件名去掉 `.patch.json`（小写，如 `tbnpcaipersona.patch.json`）
- **命名空间 ID**：Mod 新增行的主键写成 `"<modId>:<本地名>"`，加载时由 `ModIdRewriter`
  重写为 `900000000` 起分配的整数 ID（`ModIdAllocator.SegmentStart = 900000000`，按表隔离）
- upsert 按 keyFields 合并进基础表（JsonMergeSettings 合并）；remove 删行
- 校验失败会有中文日志，如 `xxx 的 upsert 必须是数组`、`xxx 的 keyFields 不能为空`、
  `xxx 的 upsert 里有一项缺主键字段(...)`、`mod.json 缺失或无法解析`
- 引用完整性由 `ModRefValidator` 检查（字段名后缀 `_Ref` 视为外键），坏引用记 BadRef

## 四、加载流程

1. `ModRegistry`（单例）扫描 Mods 目录 → `ModPackage.Load` 解析 mod.json + units
2. 启用状态：`ModRegistry.IsEntryEnabled(modId, unitId, npcId)`，存在 prefs 键
   `MOD_ENABLED_ENTRIES_V2`（Unity PlayerPrefs，注册表），顺序存 `MOD_ORDER_V1`
3. **管理入口是游戏内 `UgcWorkshopPanel`（UGC 工坊界面）**：
   该类持有 `s_RescanModsMarker / s_SyncOverlayMarker / s_PresentLocalPageMarker`，
   工坊里有"本地"分页列出 Mods 目录的本地 Mod，可逐单元启用/停用
4. 启用后 `ModBootstrap.ApplyChanges()` → `ModConfigOverlay.Rebuild()` →
   `ModTablePatcher.ApplyAll(...)` 把补丁合入 Luban 配置表
   - `ModTableTiers` 区分热更表（Hot，立即生效）与需重进场景的表（Reenter）
5. 存档（每个存档位）记录 `mods`（ModStamp 列表）与 `modIdMap`（ID 映射），
   缺 Mod 时 `ModSaveCheck` 提示

## 五、与剧情修改直接相关的表（ModConfigOverlay 中出现的）

| 表名 | 用途 | 已知字段 |
|------|------|----------|
| `tbnpcbasecfg` | NPC 基础配置 | npcName / npcTitle / gender / propPersonality / propPlot |
| `tbnpcaipersona` | NPC AI 人设 | npcId / speakStyle |
| `tbaiprompttemplate` | AI 提示词模板（TbAiPromptTemplate） | — |
| `tbquest` | 任务 | 见 dump.cs `TbQuest`（questId、acqOpText、objectiveMod 等） |

## 六、无工坊界面时直接启用本地 Mod（注册表法）

游戏没有暴露本地 Mod 管理页时，可直接写 Unity PlayerPrefs（注册表）：

- 位置：`HKCU\Software\Nuverse\WorldApart`
- 值名 = `<键名>_h<DJB2a哈希>`；哈希算法：h=5381，逐字节 `h = h*33 ^ c`（UTF-8，uint32）
  - **哈希对象是键名本身**（Unity PlayerPrefs 在 Windows 的标准键名哈希），
    不是 entry 内容：`djb2a("MOD_ENABLED_ENTRIES_V2") = 2913630511`、`djb2a("MOD_ORDER_V1") = 2735767082`，
    用注册表全部 8 组既有 `_h` 键（access_token / GuestID / Screenmanager* 等）验证通过
  - 即 `MOD_ENABLED_ENTRIES_V2` 是**单个 prefs 键**，值为多行文本（`SplitLines` 逐行解析），
    每行一条 EntryKey：`modId|unitId|npcId`，table_patch 的 npcId=0
- 字符串值编码：REG_BINARY = UTF-8 字节 + 0x00 结尾；整型：REG_DWORD
- 需要的两个键（已写入）：
  - `MOD_ENABLED_ENTRIES_V2_h2913630511` = `com.kimi.storytest|persona_patch|0` 换行 `com.kimi.storytest|basecfg_patch|0` + NUL
  - `MOD_ORDER_V1_h2735767082` = `com.kimi.storytest` + NUL
- 写入方式（PowerShell，键需可写句柄打开）：
  ```powershell
  $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Nuverse\WorldApart', $true)
  $enc = [System.Text.Encoding]::UTF8
  $key.SetValue('MOD_ENABLED_ENTRIES_V2_h2913630511', $enc.GetBytes('com.kimi.storytest|persona_patch|0' + [char]0), [Microsoft.Win32.RegistryValueKind]::Binary)
  ```
- 注意：必须在游戏关闭时写入（游戏退出时会把内存中的偏好覆盖回注册表）

## 七、当前状态与下一步

- [x] 逆向完成格式
- [x] 启用键已写入注册表（双单元：persona_patch + basecfg_patch）
- [x] **已解出配置表 bundle**：`StreamingAssets\yoo\DefaultPackage\*.bundle` 带 24 字节
      自定义头（magic `A1BNDLHP`），剥离后就是标准 UnityFS；每张 Luban 表都有
      `.bytes` + `.json` 两份 TextAsset，导出工具 `KIMI\parse_bundle.py`，
      导出结果在 `KIMI\bundle_out\`（tbnpcbasecfg 1592 行 / tbnpcaipersona 1156 行 /
      tbaiprompttemplate / tbquest）
- [x] **可见效果补丁已就绪**：目标 NPC = 苏倾盏（npcId **100000**，客栈老板娘，
      与 20:08 截获的 AI 请求"身份：女，人"一致）。`com.kimi.storytest` v0.2.0：
  - `tables\tbnpcaipersona.patch.json`：upsert 100000 的 speakStyle → 带
    【KIMI·MOD生效】标记的豪爽江湖口吻（三语）
  - `tables\tbnpcbasecfg.patch.json`：upsert 100000 的 propPlot → 标记文案
  - keyFields 分别 `["npcId"]` / `["id"]`，JsonMergeSettings 按主键合并进基础表
- [x] **EntryKey 格式机器码级确认**：stringliteral.json 存在 `{0}|{1}|{2}` 字面量，
      与 `ModPackage.EntryKey(modId, unitId, npcId)` 签名吻合 = 注册表值格式正确
- [x] **主界面入口补丁** `com.kimi.modmanager`：upsert tbmainmenubutton 强制 Workshop 行

## 七点六、最终结论（2026-10-06 凌晨）：原生路线在此构建中被停用

人工验证结果（23:59 会话）：苏倾盏档案/说话风格均无变化，Player.log 零 mod 痕迹，
主界面**没有工坊按钮**（尽管本地表 tbmainmenubutton 里 Workshop 行 enabled=true）。
由此确认：

1. **运行时表数据 ≠ 本地 bundle 不可能**（无热更缓存），按钮隐藏是**代码层门控**
   （BuildMainMenuButtons/ViewModel 内，IL2CPP 无方法体，反汇编未能直接读出条件；
   疑似渠道/区域开关或远端功能 flag）
2. **Mod 管线无任何常规调用方**：Game.Mod 命名空间全是静态类/单例，
   唯一确定的触发点是 UgcWorkshopPanel.Awake（RescanModSources）——按钮隐藏 →
   面板永远打不开 → 管线永不运行 → 补丁永不应用。死锁闭环。
3. 破环只能：a) 满足未知门控条件（不可控）；b) 补丁 GameAssembly.dll —— **不做**，
   内核反作弊（gpldriver.sys）在场，改游戏二进制风险不可接受。
4. 所有 Mod 文件、注册表格式、键格式均已验证正确——**将来官方开启或门控条件满足时，
   现有三个 Mod 应直接生效**。

## 七点七、交付的外部工具

- `KIMI\ModManager.bat` / `ModManager.ps1`：Windows 原生 GUI（需游戏关闭时保存）
  - 列出 Mods 目录全部 Mod（解析 mod.json：标题/版本/作者/单元）
  - 勾选 = 启用，取消勾选 = 停用，保存写注册表（格式已验证）
  - 自动检测游戏是否运行并警告；一键打开 Mods 目录
  - 注意：.ps1 必须带 UTF-8 BOM（PowerShell 5.1 否则按 GBK 解析中文乱码）
- 剧情修改的可用路线：**路线 B 本地剧情服务器**（mitmproxy 拦截 AI 接口，
  `ainpc_story_server.py` + `story_config.json`，20:08 已验证可截获/改写）

## 七点八、终局验证（2026-10-06 00:17）：原生路线确认死亡

新档案新开局测试：苏倾盏档案故事仍为原文（截图确认）；
新存档位（profile 6b372e77…）的 gameworld.msgpack 中 **无任何 ModStamp / modIdMap /
com.kimi 痕迹**。五个层面（UI/日志/效果/旧档/新档指纹）全部阴性，结论确定：
**该构建中 Mod 加载管线从不运行，原生 Mod（table_patch / asset_override）不可用。**
Mods 目录中的三个 Mod 保留备用（官方开启后可再生效）。

## 九、残余可行路线（改剧情/视频/其他内容）

| 路线 | 覆盖内容 | 风险 |
|------|----------|------|
| ① 安装目录资源替换（重打包 bundle） | 视频/CG/图片/表数据全量 | **高**：内核反作弊 gpldriver.sys + 在线账号（存档上传 A1.EscapeSaveUpload），可能封号 |
| ② 存档编辑（gameworld.msgpack 可解析） | 任务状态/好感/剧情 flag | 中：存档退出时上传服务器，篡改可能被发现；单机游玩影响小 |
| ③ 网络层代理（mitmproxy） | AI 对话/服务端下发的配置 | 低：不改任何本地文件 |

①的技术准备已就绪：bundle 格式（24 字节 A1BNDLHP 头 + UnityFS）已解、UnityPy 可读写、
Luban 表可导出改回。是否执行需用户明确接受封号风险；如测试建议用游客账号（注册表有 GuestID）。

## 七点五、主界面 MOD 管理入口（tbmainmenubutton 表驱动）

- 主界面按钮是 **Luban 表驱动**：`tbmainmenubutton`（bundle 里有 .json），
  行 = {id: MainMenuButtonType 枚举, sort, text_key, enabled, open_time_start/end}
- `MainMenuButtonType`：1=继续 2=新游戏 3=读档 4=设置 5=退出 6=抢先体验声明 7=**Workshop**
- 表内 Workshop(id=7) **本来就 enabled=true**（text_key=MAIN_ENTRY_BTN_WORKSHOP）→
  理论上主菜单已有"工坊"按钮，点击 `OpenCreativeWorkshop()` 打开 `UgcWorkshopPanel`（PanelId 1315）
- **UgcWorkshopPanel 就是原生 Mod 管理器**：
  - `UgcLocalEntry` = {Id, ModId, UnitId, UnitType, NpcId, RootDir, ...} —— 覆盖全部单元类型
    （不只头像），`RefreshLocalLibraryAsync` 列本地 Mods 目录条目
  - `OnCardClick`→应用(启用) / `CancelEntryApply`→停用 / `ConfirmDelete`→删除 / `OpenEntryDir`
  - `Awake` 里 `RescanModSources` → 打开面板即触发 Mod 重扫
  - Mod 来源：`LocalModSource`（扫 Mods 目录，ROOT_FOLDER_NAME="Mods"）+ `SubscribedModSource`（工坊订阅）
- 按钮动作是枚举 switch（`OnMainMenuButtonClicked`），**表补丁只能改既有按钮的
  显示/排序，不能新增动作**；但 Workshop 动作已存在，无需新增
- **已部署 `com.kimi.modmanager`**：upsert tbmainmenubutton 强制 Workshop 行
  enabled=true + 最宽时间窗（防热更 flag 关闭），priority=100 排最前；
  注册表启用列表已加 `com.kimi.modmanager|menu_patch|0`，MOD_ORDER 置顶
- 未知项：该表是否在主菜单构建前就应用（ModTableTiers 热更表应无问题，待人工验证）

## 八、注意事项

- 游戏带内核反作弊（gpldriver.sys）：Mod 只写在官方 Mods 目录、走官方加载器，风险可控；
  不要改游戏安装目录文件
- 多个 Mod 同表冲突按 priority + MOD_ORDER 决定
- 存档带 Mod 指纹，换机器/分享存档需带上 Mod
