# 《不问凡尘》(World Apart) BepInEx MOD 开发文档

> 适用版本：游戏 IL2CPP 构建（Unity 2022.3.43）+ 本包附带的 BepInEx be.788 (CoreCLR) 环境。
> 所有结论均在本作上实测验证，欢迎补充修正。

---

## 1. 运行环境原理

### 1.1 为什么需要专用启动器

游戏主程序 `WorldApart.exe` 的导入表被剥离为 `gpShell.dll!gShell` 存根（反作弊/保护壳），
标准 doorstop 依赖的 `iat_hook(UnityPlayer, "kernel32.dll", GetProcAddress, …)` 永远匹配不到，
静默失败。因此注入链为：

```
启动MOD版.bat → modlauncher.exe（原 wastart）
  → 派生 helper 进程，把命令行中第一个 .exe 改写为 WorldApart_mod.exe 后 CreateProcess
  → gpShell 正常拉起 Unity 子进程
  → 向 Unity 子进程远程线程 LoadLibrary(modprobe.dll)（inject.ini 配置）
  → modprobe 抢先 LoadLibrary(GameAssembly.dll)，热改 il2cpp_init 前 16 字节为绝对跳转
  → 钩子里还原原始字节、调用 il2cpp_init、重新装钩
  → 复刻 doorstop 的 CoreCLR bootstrap：按 doorstop_config.ini 加载 dotnet\coreclr.dll，
    执行 BepInEx\core\BepInEx.Unity.IL2CPP.dll 入口
  → BepInEx 扫描 plugins\*.dll，LocalModManager 等插件加载
```

要点：
- `WorldApart_mod.exe` 是指向原 exe 的**硬链接**（名变内容不变，gpShell 照常工作）；
  `WorldApart_mod_Data` 是指向 `WorldApart_Data` 的**目录联接**（Unity 按 exe 名找 Data 目录）。
- 与调用者零竞态：钩子在 GameAssembly 映射瞬间装上，早于 UnityPlayer 的 GetProcAddress+call。
- 已实测的死路（勿再尝试）：改 gpShell 的 IAT（游戏弹 Failed to load il2cpp）；
  改 GameAssembly 的 EAT（游戏接受但从不使用）；`LdrRegisterDllDllNotification`（回调零触发）。

### 1.2 目录约定（安装后）

| 位置 | 内容 |
|---|---|
| `<游戏目录>\BepInEx\plugins\` | BepInEx 插件 DLL |
| `<游戏目录>\BepInEx\interop\` | 首次启动由 Il2CppInterop 从 GameAssembly 生成（154 个程序集），开发必引 |
| `<游戏目录>\BepInEx\config\BepInEx.cfg` | BepInEx 配置 |
| `<游戏目录>\dotnet\` | CoreCLR 运行时（doorstop 加载） |
| `%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods\<modId>\` | 本地 MOD 包 |
| `<游戏目录>\BepInEx\LogOutput.log` | 运行日志 |

---

## 2. MOD 包格式

### 2.1 目录布局

```
%LocalLow%\Nuverse\WorldApart\Mods\<modId>\
  mod.json                 # 清单（必需）
  <unit root>\tables\*.patch.json   # table_patch 类型的补丁文件
```

### 2.2 mod.json

```json
{
  "schemaVersion": 5,
  "modId": "example.tutorial",
  "title": "教程示例：人物口吻与档案",
  "description": "示例 MOD：演示表补丁。默认关闭。",
  "author": "local",
  "version": "0.2.0",
  "priority": 0,
  "publishedFileId": 0,
  "defaultEnabled": false,
  "units": [
    { "unitId": "persona_patch", "type": "table_patch", "root": "persona" },
    { "unitId": "basecfg_patch", "type": "table_patch", "root": "basecfg" }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `schemaVersion` | 固定 5 |
| `modId` | 唯一 ID，建议 `作者名.包名`；本地包加 `local.` 前缀避免与工坊冲突 |
| `units[].root` | unit 根目录。**该目录下必须再有 `tables\`**：游戏扫描 `<root>\tables\*.patch.json`。root 指错目录时**静默返回空列表，无任何报错**——最常见的坑 |
| `description` | MOD 管理器包行显示的简介（扩展字段，游戏忽略未知键） |
| `defaultEnabled` | `false` 时该包**首次**被看到时初始为关闭（扩展字段；之后由用户开关决定，MOD 管理器用包内 `.lm_init` 标记保证只初始化一次） |
| `priority` | 加载优先级 |

约束：**一个 unit 一个独立目录**。两个 unit 指向同一目录会互相重复读到对方的补丁。

### 2.3 表补丁（table_patch）

补丁文件：`<root>\tables\<表名小写>.patch.json`

```json
{
  "keyFields": ["npcId"],
  "upsert": [
    { "npcId": 100000, "speakStyle": { "zh-Hans": "…", "zh-Hant": "…", "en-US": "…" } }
  ],
  "remove": []
}
```

- `keyFields`：主键字段（该表行唯一标识）
- `upsert`：按键存在则改、不存在则增
- `remove`：按键删除（填主键值即可）

表名即游戏配置表文件名（如 `TbNpcAiPersona` → `tbnpcaipersona.patch.json`）。
全表共 259 张（12 张仅 JSON 无 .bytes）。常用：人物档案 `tbnpcaipersona`、
基础配置 `tbnpcbasecfg`、主界面按钮 `tbmainmenubutton`。
**人物、位置、剧情节点等常见 ID 见包内 `常见ID速查表.md`**；任意表原始数据在
`m1_out（数据解包目录）\assets_luban_data_<表名>.json`。

### 2.4 表加载机制（改补丁前必读）

- **热重载**：仅"Hot"表可在会话内即时重建；存在非热表变更时原生流程要求"重进"
  （`RebuildTablesWithMods` 被跳过）。MOD 管理器插件的 `ModSync` 已代为补做：
  清缓存 → `ModBootstrap.OnGameLoaded()` → `ConfigManager.ForceReloadTables(...)`，
  因此开关 MOD 后**同进程即时生效，无需重启**。
- 换包/改补丁文件后无需重启进程：游戏的 Bank/Bundle 缓存键含内容变更检测；
  视频等资源关掉对应面板重进即重扫。

### 2.5 视频 / 资源覆盖

- 游戏原生提供 `Game.Mod.AssetOverlay`：
  `RegisterVideoOverride(bundlePath, dir)` / `TryResolveVideoOverride` / `HasAnyOverride`。
  已实测**注册即生效**，是视频覆盖首选路线（不碰原文件、可逆）。
- Bundle 加载时游戏会现场扫 `*.segments.json` sidecar（`VideoBundlePlayer`）；
  Bank 加载时**无条件**重读 sidecar，缓存按 bundle 实例、卸载即失效。
- 注意：cine 演出的 VPath 序列化在 AssetBundle 的 `CommonVideoClip.segmentVPath` 上，
  **只往 segments.json 加条目不会被 Timeline 播到**，必须同时改 prefab。
- `SetExternalPackageResolvers` 是 DLC 通道，**不要**当 MOD 资源根用。
- 兜底路线：原地覆盖 mp4（已实测不校验哈希，不需要注入）。

---

## 3. BepInEx 插件开发

### 3.1 工程

- 目标框架 net6.0（随 CoreCLR），C#。
- 引用：`BepInEx\core\` 下全部 DLL + `BepInEx\interop\` 下需要的程序集
  （interop 首次启动生成；换游戏版本需删除 `BepInEx\interop` 与 `cache` 重建）。
- 入口标注：

```csharp
[BepInPlugin("com.author.modname", "显示名", "1.0.0")]
public class Plugin : BasePlugin
{
    public override void Load()
    {
        // Harmony 钩子等
    }
}
```

### 3.2 常用游戏内 API（经 interop）

| API | 用途 |
|---|---|
| `ModRegistry.Instance` / `.Packages` | 枚举已注册 MOD 包 |
| `ModRegistry.UnitEntryNpcId(unit)` | unit 关联的 NPC id |
| `reg.IsEntryEnabled(modId, unitId, npc)` / `SetEntryEnabled(..., bool)` | 查询/设置启用状态（持久） |
| `reg.HasEnabledPatch(pkg)` | 是否有启用的补丁 |
| `ModConfigOverlay.PatchedTableNames` / `.HasAny` | 已补丁表清单 |
| `ConfigManager.Tables` / `ForceReloadTables(...)` | 配置表实例/强制重载 |
| `Game.Mod.AssetOverlay.RegisterVideoOverride` | 视频覆盖 |
| `Game.Mod.ModBootstrap.OnGameLoaded()` | 重建 MOD 表 |

### 3.3 IL2CPP interop 踩坑实录（重要）

1. **`GetChild()` 返回基类 Transform 包装器，`as RectTransform` 恒为 null**。
   用 `go.transform.TryCast<RectTransform>()` 或 `AsRt()` 这类显式 TryCast。
2. **禁止 `GetComponents(Type)` 全量枚举**——会撞 coreclr 崩溃。用泛型 `GetComponents<T>()` 或具体类型。
3. **节点查找用逐子节点 try/catch**：个别 il2cpp 节点访问 `.name` 都会抛异常。
4. il2cpp 字符串与托管 string 互转注意 null；调用游戏方法一律包 try/catch + 日志。
5. 日志统一 `BepInEx.Logging.Logger` / `ManualLogSource`，写 `LogOutput.log`；
   玩家现场排障只看这个文件。

### 3.4 UI 注入（设置页）

设置页是 `SettingsPanel` + `SettingsPanelViewModel`，四个页面是预烘焙 RectTransform，
**没有可追加的设置项集合**（钩集合 getter 不可行）。可行做法：
- 侧栏标签克隆 `navSystem` 行，注入时机用 `Start` postfix（`OnShow` 时目标还是 null）；
- MOD 页挂在 **ScrollRect 的 Viewport 下面**并复制 Viewport 矩形（画布 y 900~3000 区域，
  挂错层级必黑屏）；
- **绝不对原生页 `SetActive(false)`**——会触发 `VM.ActivePage` 弹回默认页（自动关设置+黑屏）。
  用不透明 `Image`（`raycastTarget=true`）覆盖原生页内容来"顶替"显示；
- 本作用 UPF 布局引擎：面板打开后数秒才把真实坐标写回 RectTransform，
  克隆来的几何要在页面存活期间周期重抄（0.25 s 级），但**旋钮位置只能由状态绘制函数独占**
  （两处同时写会跳动）；
- 行模板（系统页）：`Frame#confirmMapTravelTimeCard` 行 / `Frame#swConfirmMapTravelTime` 开关 /
  `Frame#swConfirmMapTravelTimeKnob` 旋钮。

参考实现：`BepInEx\plugins\LocalModManager.dll`（设置页 MOD 管理器 + F10 窗口）。

---

## 4. 调试与验证

- 快速自检：插件里读 `ConfigManager.Tables` 打日志（如读 `tbnpcbasecfg` 里 npc 100000 的
  档案字段是否含你的标记文本），比截图反馈快。
- 双向可逆验证：关 MOD → 标记消失 → 再开 → 标记恢复，同一进程内完成。
- 性能基线方法：独立测量插件（Update 记 dt，统计 p50/p95/长帧/掉帧），
  仪器与被测分离；注意**窗口失焦会造成假阳性冻结**，数据必须带 `focused` 标志。
- 发布前跑一遍无残留扫描：包内所有二进制/文本不得含开发环境标识（本包已做等长字符串补丁，
  脚本见 `build_pack.ps1`）。

## 5. 发布规范

- MOD 相关命名（modId、标题、DLL 名、日志、包内文本）**不得**出现开发环境痕迹。
- 示例包请带 `description` 与 `defaultEnabled`；实验性功能默认关闭。
- 压缩包结构参照本包：`runtime\`（注入组件）+ `BepInEx\` + `dotnet\` + `示例MOD\` + 说明文档。
