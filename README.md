# 不问凡尘（WorldApart）MOD 管理器

给 Steam 游戏《不问凡尘》（WorldApart，Unity 2022.3 IL2CPP + gpShell 壳）建立的第三方 MOD 环境：注入 BepInEx → 插件提供游戏内 MOD 管理页（F10）+ 剧情调试窗（F9）→ MOD 以补丁包形式改表/换资源。

## 插件组成（两个插件 + 一个契约 DLL）

现场 `BepInEx\plugins\` 下的 MOD 环境由两个零交叉引用的 BepInEx 插件与一个契约程序集构成：

| DLL | 插件名 / GUID | 职责 | 窗口 |
|---|---|---|---|
| `LocalModManager.dll` | Mod Manager / `local.modmanager` | MOD 管线刷新与开关、Mod 页滚动修复、视频桥接、`IManagedFeaturePlugin` 的发现方 | F10 |
| `LocalStoryDebug.dll` | Story Debug Tool / `local.storydebug` | 剧情调试：F9 三标签页（剧情跳跃 / 玩家参数 / NPC 社交）、空间传送、只读载档 harness、存档写入测试 | F9 |
| `LocalModManager.Abstractions.dll` | 契约程序集（`IManagedFeaturePlugin`，net6.0，v1.0.0） | 第三方「功能插件」契约；必须与上面两个 DLL 一起放在 `BepInEx\plugins\`，缺了它功能插件的分区会静默为空 | — |
| `FrameProbe.dll` | Frame Probe | 帧时间采样（性能验收用），独立第三方件 | — |

- 两个插件零交叉引用：各自的 DLL、GUID、日志源独立（`[Info   :Mod Manager]` / `[Info   :Story Debug Tool]`）。
- 功能插件分区：MOD 管理页（F10）新增「BepInEx 功能插件」分区，凡是实现了 `LocalModManager.Abstractions.IManagedFeaturePlugin` 的 BepInEx 插件都会自动出现一行可切换的开关；`LocalStoryDebug` 是第一个实现方，它的开关**只控 F9 窗口显隐**（不卸载插件、不停调试驱动）。
- 接口与版本化策略见 [BepInEx-Feature-Plugin-API.md](docs/BepInEx-Feature-Plugin-API.md)。

## 架构速览

```
启动MOD版.bat  （或 Steam 点「开始游戏」直启，见「Steam 直接启动 MOD 版」）
  └─ wastart.exe (IFEO 调试器, src/injector/)
       └─ CreateProcess 挂起 WorldApart_mod.exe
       └─ 注入 modprobe.dll
            └─ 钩 il2cpp_init（probe6 方案）→ 复刻 doorstop CoreCLR bootstrap
                 └─ BepInEx 6.0.0-be.788
                      ├─ 加载 LocalModManager.dll（src/bepinex_plugin/）
                      │    ├─ 设置页注入：MOD 管理第 5 页（克隆原生标签/行模板）
                      │    ├─ ModSync：ApplyChanges → ForceReloadTables
                      │    ├─ 视频桥接（官方 AssetOverlay API）
                      │    └─ 功能插件发现（FeaturePluginRegistry）
                      └─ 加载 LocalStoryDebug.dll（src/storydebug_plugin/）
                           ├─ F9 IMGUI 调试窗（剧情跳跃 / 玩家参数 / NPC 社交三标签页）
                           ├─ 空间传送 / 剧情跳跃（DebugActions）
                           └─ 只读载档 harness + 写入自测（ReadOnlyLoad / WriteTest）
```

游戏侧 MOD 管线（原生）：`<Mods>\<包>\mod.json` + units → `EnumerateIn(<unitRoot>\tables\*.patch.json)` → 热表热重建 / Reenter 表需重进。

## 关键架构事实（反汇编实证）

- 游戏 exe / UnityPlayer.dll / GameAssembly.dll 的 IAT 被 gpShell 整体剥离成 gpShell.dll 存根——doorstop 4.3 的 iat_hook 永远匹配不到。改 gpShell IAT 会弹 Failed to load il2cpp；改 GameAssembly EAT 游戏从不调用；LdrRegisterDllNotification 零触发。**唯一可行路线是 probe6 的 il2cpp_init 抢占钩**（见 src/injector/probe6.c）。
- EnumerateIn 粒度是**目录**不是 unit：补丁必须放 `<unitRoot>\tables\`，目录不存在时静默返回空 List。
- ApplyChanges 返回值是"需重进的表集合"而非应用条数；只要 tbmainmenubutton（Reenter 表）启用，热重建就被整体跳过。
- IL2CPP 共享折叠桩陷阱：get_CurrentSpaceId/get_CurrentSpaceUuid 走折叠桩，interop 直调抛异常。**所有钩子和直调按"类型全名+方法名"解析，不得用 dump RVA 当唯一标识。**

更多踩坑记录见 docs/ 下各逆向与接口报告。

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/bepinex_plugin/` | MOD 管理器（Mod Manager）插件源码（Plugin.cs / FeaturePluginRegistry.cs / Mod 页与视频桥接 + csproj） |
| `src/storydebug_plugin/` | 剧情调试（Story Debug Tool）插件源码（Plugin.cs + DebugActions.cs / WriteTest.cs / ReadOnlyLoad.cs 等 + csproj） |
| `src/bepinex_plugin/abstractions/` | 功能插件契约程序集（IManagedFeaturePlugin.cs + LocalModManager.Abstractions.csproj，net6.0） |
| `src/injector/` | 注入器源码：wastart.c、probe1–6.c 全迭代、ctest.c、build_*.bat、inject.ini |
| `src/frameprobe/` | 帧探针插件（性能测量） |
| `docs/实施计划与报告/` | v1→v10 各轮计划、报告、模型指令 + 项目交接文档、验收报告、注入实施文档（排查问题的考古线索） |
| `docs/MOD开发文档/` | MOD 包格式、字段/ID 对照表、安装说明、示例教程 |
| `docs/` 其余 | 逆向、注入、接口截获、验收报告、发布手册、排查手册、未闭环项汇总 |
| `scripts/python/` | 验收与侦察脚本（accept_* / bundle 解析 / AI 接口服务端等） |
| `scripts/powershell/` | 部署与诊断脚本（IFEO、启动观察、写存档偏好等） |
| `tools/` | 解压好的测试运行环境，按游戏根目录的布局平铺：`BepInEx/`（BepInEx 6.0.0-be.788 本体）、`dotnet/`（.NET 6 运行时，`doorstop_config.ini` 用 `coreclr_path` 指过来）、`doorstop_config.ini`、`.doorstop_version`、`winhttp.dll`、`changelog.txt` |
| `tools/BepInEx/plugins/` | 插件投放目录（空，带 `.gitkeep`）；`tools/BepInEx/patchers/` 同理 |

## 安装 / 部署

把以下文件放进 `<游戏安装目录>\BepInEx\plugins\`：

- `LocalModManager.dll`（Mod Manager，F10）
- `LocalStoryDebug.dll`（Story Debug Tool，F9）
- `LocalModManager.Abstractions.dll`（功能插件契约程序集，**必须与上面两个 DLL 放在一起**）

三个 DLL 缺一不可；`FrameProbe.dll` 是独立的第三方帧时间采样件，只在性能验收时需要，可以不部署。

**缺 `LocalModManager.Abstractions.dll` 的后果**：MOD 管理页的「BepInEx 功能插件」分区会**静默为空**——不报错、不写错误日志、不显示任何功能插件行（契约类型解析不到，实现方不会被发现）。两个插件本身仍能加载。

两个插件的 GUID 独立、可单独升级或移除；`LocalStoryDebug` 缺席时 MOD 管理页照常工作，只是功能插件分区里不再有它那一行。

## 构建

```
dotnet build -c Release -p:G=<游戏安装目录>
```

- `G` 指向游戏根（该目录下需有 `BepInEx\core` 与 `BepInEx\interop`）。csproj 里的默认值只是常见的 Windows Steam 路径，现场构建必须显式传入。
- 两个插件 csproj 都保持 `<DebugType>none</DebugType>`，产物里不带构建机路径。
- 契约程序集由 `src/bepinex_plugin/abstractions/`（net6.0）构建为 `LocalModManager.Abstractions.dll`，随插件一起产出/部署。

## Steam 直接启动 MOD 版

v13 起，在**游戏目录**新增两个文件即可直接在 Steam 点「开始游戏」进 MOD 版：

- `USERENV.dll`：自建代理，转发 59 个导出到同目录的 `userenv_orig.dll`，并把 `modprobe.dll` 装进 Unity 子进程。
- `userenv_orig.dll`：原 system32 userenv 的同名替身，供上面的代理转发。

要点：

- **不改 Steam 启动项、不改游戏任何既有文件**，只新增这两个文件。
- **卸载就是删掉这两个文件**，即回原版。
- `启动MOD版.bat` 仍可用；两条启动路径共存幂等（代理检测到 `modprobe.dll` 已加载会跳过）。
- Steam 启动仍使用单一 Doorstop/BepInEx 链；契约 DLL 不替代或新增注入器。

## 关键约束

- 发布包内任何用户可见字符串/文件名/日志零内部代号字样；日志前缀 `[Mod Manager]`。
- 插件 csproj 必须含 `<DebugType>none</DebugType>`。
- bat：GBK + CRLF + 引号；中文路径下的 .ps1 必须 UTF-8 带 BOM。
- bash 处理中文路径不可靠：文件操作用 Python，zip 用 Python zipfile。
- 本仓库以源码与文档为主；二进制只有 `tools/` 下解压好的测试运行环境（BepInEx 本体 + dotnet 运行时 + doorstop 文件）入库，其余二进制（DLL/EXE/zip）、日志、备份、反编译工具链等运行产物不入库。

## 状态

- 已完成：BepInEx 注入稳定；游戏内 MOD 管理页（原生样式、包/分组两级、滚动修复）；性能无损（p95 偏差 0.00%）；F9 写入四件套实测通过；F9 窗口白屏已修复（根因是预设按钮行的数组索引越界，每帧异常逃出 `OnGUI` 使整帧 GUI 中止）；MOD 页自开关 bug 已闭环（MOD 标签改为从零建节点，不再克隆原生标签）；v13 完成插件拆分（两个插件零交叉引用）；Steam 点击直启实测可用。
- 进行中：剧情跳跃可经官方 `QuestManager.DebugForceAcceptQuest` 起流程（任务进入 InProgress），objective 推进仍需玩家实际移动；视频替换桥接（官方 AssetOverlay API）已可逆落地，画面级确认待委托方。
- 规划：剧情编辑器（需先解决 AssetOverlay 目录约定与 cine 的 `CommonVideoClip.segmentVPath` 序列化——只改 segments.json 不会被播到，新增 Timeline 片段需重烘焙 bundle）；传送的"完整旅行体验"补全（旅行耗时结算/旅行引导提示/离场确认）待产品裁定。

## BepInEx 功能插件接口

`src/bepinex_plugin/abstractions/` 定义与原生 `mod.json` 包相互独立的功能插件控制接口。管理器只发现实现了 `LocalModManager.Abstractions.IManagedFeaturePlugin` 的插件，不为任意 DLL 推断开关，也不写插件配置。基础包需将 `LocalModManager.dll`、`LocalStoryDebug.dll` 与 `LocalModManager.Abstractions.dll` 一起放入 `BepInEx/plugins/`。接口和安装方式见 [BepInEx-Feature-Plugin-API.md](docs/BepInEx-Feature-Plugin-API.md)。Steam 启动仍使用单一 Doorstop/BepInEx 链；此 DLL 不替代或新增注入器。
