# 不问凡尘（WorldApart）MOD 管理器

给 Steam 游戏《不问凡尘》（WorldApart，Unity 2022.3 IL2CPP + gpShell 壳）建立的第三方 MOD 环境：注入 BepInEx → 插件提供游戏内 MOD 管理页 + F9 调试窗 → MOD 以补丁包形式改表/换资源。

## 架构速览

```
启动MOD版.bat
  └─ wastart.exe (IFEO 调试器, src/injector/)
       └─ CreateProcess 挂起 WorldApart_mod.exe
       └─ 注入 modprobe.dll
            └─ 钩 il2cpp_init（probe6 方案）→ 复刻 doorstop CoreCLR bootstrap
                 └─ BepInEx 6.0.0-be.788
                      └─ 加载 LocalModManager.dll 插件（src/bepinex_plugin/）
                           ├─ 设置页注入：MOD 管理第 5 页（克隆原生标签/行模板）
                           ├─ ModSync：ApplyChanges → ForceReloadTables
                           ├─ F9 IMGUI 调试窗（DebugActions）
                           └─ 写入自测（WriteTest）
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
| `src/bepinex_plugin/` | BepInEx 插件源码（Plugin.cs / DebugActions.cs / WriteTest.cs / ReadOnlyLoad.cs + csproj） |
| `src/injector/` | 注入器源码：wastart.c、probe1–6.c 全迭代、ctest.c、build_*.bat、inject.ini |
| `src/frameprobe/` | 帧探针插件（性能测量） |
| `docs/实施计划与报告/` | v1→v10 各轮计划、报告、模型指令 + 项目交接文档、验收报告、注入实施文档（排查问题的考古线索） |
| `docs/MOD开发文档/` | MOD 包格式、字段/ID 对照表、安装说明、示例教程 |
| `docs/` 其余 | 逆向、注入、接口截获、验收报告、发布手册、排查手册、未闭环项汇总 |
| `scripts/python/` | 验收与侦察脚本（accept_* / bundle 解析 / AI 接口服务端等） |
| `scripts/powershell/` | 部署与诊断脚本（IFEO、启动观察、写存档偏好等） |
| `tools/` | 解压好的测试运行环境，按游戏根目录的布局平铺：`BepInEx/`（BepInEx 6.0.0-be.788 本体）、`dotnet/`（.NET 6 运行时，`doorstop_config.ini` 用 `coreclr_path` 指过来）、`doorstop_config.ini`、`.doorstop_version`、`winhttp.dll`、`changelog.txt` |
| `tools/BepInEx/plugins/` | 插件投放目录（空，带 `.gitkeep`）；`tools/BepInEx/patchers/` 同理 |

## 关键约束

- 发布包内任何用户可见字符串/文件名/日志零 kimi 字样；日志前缀 `[Mod Manager]`。
- 插件 csproj 必须含 `<DebugType>none</DebugType>`。
- bat：GBK + CRLF + 引号；中文路径下的 .ps1 必须 UTF-8 带 BOM。
- bash 处理中文路径不可靠：文件操作用 Python，zip 用 Python zipfile。
- 本仓库以源码与文档为主；二进制只有 `tools/` 下解压好的测试运行环境（BepInEx 本体 + dotnet 运行时 + doorstop 文件）入库，其余二进制（DLL/EXE/zip）、日志、备份、反编译工具链等运行产物不入库。

## 状态

- 已完成：BepInEx 注入稳定、游戏内 MOD 管理页（原生样式、包/分组两级、滚动修复）、性能无损（p95 偏差 0.00%）、F9 写入四件套实测通过。
- 进行中：空间传送到达未确认；剧情跳跃未闭环（TryStartQuestProc 返回 False，备选 FlowRuntimeManager.StartInWorld）。
- 规划：视频替换 AssetOverlay 优先路线、MOD 页自开关 bug 单独立项。
