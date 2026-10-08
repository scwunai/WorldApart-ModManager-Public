# 《不问凡尘》MOD 环境 —— 整体项目报告

**报告日期**：2026-10-07
**覆盖范围**：源码仓库（公开 / 私有）、发布包 v1.6、社区 PR 合并、工程质量与风险
**报告性质**：阶段性整体评估

---

## 1. 摘要

《不问凡尘》（WorldApart）的第三方 MOD 环境已经从「一次性实验」推进到
**可公开发布、可被第三方扩展、有社区贡献流入**的工程状态。当前：

- 一个**免安装分发包**（v1.6，72.1 MiB，466 条目）已在公网发布，任何人可下载；
- 一套**源码与文档已公开**（347 文件，73.65 MB），含开箱即用的运行环境；
- **首个外部贡献 PR 已完成审计并合并**，项目出现社区协作迹象；
- 但存在一个**结构性隐患**：仓库快照停留在 v9 代，而现场工程已推进到 v13 代，
  两者相差两个代际且代码结构已经分裂（见 §6）。

**一句话结论**：项目交付面完整、质量约束严格、外部代码流入经受了完整审计；
**最需要立即处理的是仓库与现场工程的代际同步**。

---

## 2. 项目定位与交付物

### 2.1 做什么

给 Steam 游戏《不问凡尘》（WorldApart，Unity 2022.3 IL2CPP + gpShell 保护壳）
建立一套完整的第三方 MOD 环境：

```
玩家双击 启动MOD版.bat
  └─ modlauncher.exe（启动器兼注入器）
       └─ 挂起启动 WorldApart_mod.exe
       └─ 注入 modprobe.dll
            └─ 钩 il2cpp_init（probe6 方案，抢占式）
                 └─ 复刻 CoreCLR bootstrap
                      └─ BepInEx 6.0.0-be.788
                           └─ 生成 interop\ 154 个程序集
                                └─ LocalModManager.dll
                                     ├─ 游戏内「MOD 管理」页（第 5 页，原生样式）
                                     ├─ ModSync：改表热重建
                                     ├─ F9 调试窗
                                     └─ 写入自测
游戏侧原生管线：
  <Mods>\<包>\mod.json + units → EnumerateIn(<unitRoot>\tables\*.patch.json)
  → 热表热重建 / Reenter 表需重进
```

### 2.2 交付物清单

| 交付物 | 形态 | 状态 |
|---|---|---|
| 创意工坊发布包 v1.6 | `不问凡尘_MOD环境_v1.6.zip`，75,565,534 B，466 条目 | ✅ 已发布（公网可下载） |
| 源码仓库（公开） | `WorldApart-ModManager-Public`，347 文件 | ✅ 已上线 |
| 源码仓库（私有） | `WorldApart-ModManager`，343 文件，38 commits | ✅ 已同步 |
| 完整测试运行环境 | 仓库内 `tools/`（BepInEx 本体 + .NET 6 运行时，平级放置） | ✅ 已入库 |
| MOD 开发文档 | 包格式、字段/ID 对照表、教程（6 份） | ✅ 随包 + 入库 |

---

## 3. 仓库与发布资产现状

### 3.1 双仓库布局

| | 私有 `WorldApart-ModManager` | 公开 `WorldApart-ModManager-Public` |
|---|---|---|
| 可见性 | private | **public** |
| main HEAD | `8ab2b9c` | `a760ada` |
| 提交数 | 38 | 2（初始快照 + PR 合并） |
| 文件数 | 343 | 347 |
| 历史 | 完整（含 27 个批次提交） | 单次快照 + 合并提交（刻意不带历史） |
| Release | v1.6，**3 个资产** | v1.6，**1 个资产** |
| 协作 | 无 | 1 个已合并 PR |

**为什么公开仓库不带历史**：私有仓库的历史中包含早先入库的 66 MB 二进制包
以及**未脱敏**的文档版本。带历史公开等于脱敏白做，因此公开仓库是全新的一次性快照。

### 3.2 Release 资产

| 仓库 | 资产 | 大小 | MD5 |
|---|---|---|---|
| 私有 | `WorldApart_MOD-Environment_v1.6.zip` | 75,565,534 B | `5d2c7812d6f95cf6724615c9c2b9062c` |
| 私有 | `be788.zip` | 34,336,405 B | `08d0ef345bf138b7936f01c96b2327f4` |
| 私有 | `dotnet_runtime.zip` | 32,787,860 B | `717f8f3001c50b2e6ed135a1353e75e4` |
| 公开 | `WorldApart_MOD-Environment_v1.6.zip` | 75,565,534 B | `5d2c7812d6f95cf6724615c9c2b9062c` |

私有仓库资产合计 142,689,799 B（≈136 MiB）。

**已知冗余**：`dotnet_runtime.zip` 与 `be788.zip` 内的 `dotnet/` 目录逐文件 CRC 完全相同，
属重复内容（私有仓库 Release 中保留，公开仓库不含这两个包）。

**命名限制**：GitHub 会在服务端**剥离发布资产名中的非 ASCII 字符**。
原始文件名 `不问凡尘_MOD环境_v1.6.zip` 会被削成 `_MOD._v1.6.zip`，
因此两个仓库的资产名统一改为 ASCII 形式，压缩包内容不变。

### 3.3 仓库内容构成

| 区域 | 文件数 | 大小 | 说明 |
|---|---|---|---|
| `tools/dotnet/` | 187 | 65.85 MB | .NET 6 运行时（doorstop 通过 `coreclr_path` 引用） |
| `tools/BepInEx/` | 39 | 6.28 MB | BepInEx 6.0.0-be.788 本体 |
| `docs/MOD开发文档/` | 6 | 541 KB | 包格式、字段/ID 对照表、教程 |
| `docs/实施计划与报告/` | 30 | 435 KB | 各轮计划、报告、交接与验收文档 |
| `src/bepinex_plugin/` | 8 | 302 KB | 插件源码（`Plugin.cs` 为主体） |
| `src/injector/` | 14 | 95 KB | 注入器（`wastart.c`、`probe1–6.c`、`build_*.bat`） |
| `scripts/powershell/` | 36 | 78.5 KB | 部署、诊断、验收脚本 |
| `scripts/python/` | 12 | 25.3 KB | 验收、侦察、bundle 解析脚本 |
| `src/frameprobe/` | 2 | 7.3 KB | 帧探针（性能测量，不进发布包） |
| `docs/`（散篇） | 9 | ~43 KB | 逆向、接口截获、发布手册、排查手册、未闭环项 |
| `tools/`（散件） | 4 | ~0.03 MB | doorstop 配置、winhttp.dll、changelog |
| **合计** | **347** | **73.65 MB** | |

---

## 4. 技术要点

### 4.1 为什么必须自研注入（核心技术资产）

这是整个项目的技术前提，也是最有价值的逆向成果：

游戏 exe / `UnityPlayer.dll` / `GameAssembly.dll` 的 IAT 被 gpShell **整体剥离成存根**，
导致 doorstop 4.3 的 `iat_hook` 永远匹配不到——这正是早期「厂商 logo 放完黑屏卡死」的根因。
三条常规路线均已证伪并留档：

- 改 gpShell IAT → 弹 `Failed to load il2cpp`；
- 改 GameAssembly EAT → 游戏从不调用；
- `LdrRegisterDllNotification` → 零触发。

**唯一可行路线**是 probe6 的 `il2cpp_init` 抢占钩：自行 `LoadLibraryW(GameAssembly.dll)`，
在返回瞬间把 `il2cpp_init` 开头 16 字节改成绝对跳转，钩内还原字节 → 调原始函数 → 重装钩，
与调用者零竞态。

### 4.2 其它已固化的架构事实

| 事实 | 影响 |
|---|---|
| `EnumerateIn` 粒度是**目录**不是 unit | 补丁必须放 `<unitRoot>\tables\`；目录不存在时静默返回空 List，连 failure 都不写 |
| `ApplyChanges` 返回「需重进的表集合」而非应用条数 | 只要 `tbmainmenubutton`（Reenter 表）启用，热重建被整体跳过 |
| IL2CPP 共享折叠桩陷阱 | `get_CurrentSpaceId` 等走折叠桩，interop 直调抛异常；**所有钩子必须按「类型全名+方法名」解析，不得用 dump RVA 当唯一标识** |
| 视频替换三级缓存 | 换视频不用重启进程；但 cine 走 AssetBundle 内 `CommonVideoClip.segmentVPath`，只改 `segments.json` 不会被播到 |
| `A1ResourceSystem.SetExternalPackageResolvers` | 原生扩展点，优先级高于内置包，是干净的 MOD 资源路线 |

### 4.3 质量约束（项目自定的硬规则）

- 发布包内任何用户可见字符串 / 文件名 / 日志**零内部工程代号字样**；日志前缀 `[Mod Manager]`；
- 插件 csproj **必须**含 `<DebugType>none</DebugType>`（否则 DLL 内嵌 PDB 路径会破坏上述扫描）；
- bat 一律 **GBK + CRLF + 路径引号**；
- 中文路径下文件操作一律用 Python（bash 不可靠）；
- 示例 MOD 默认**关闭**；
- 有性能门槛：帧时间 p95 偏差 ≤5%。

---

## 5. 交付历史

| 阶段 | 内容 | 结果 |
|---|---|---|
| 批次 1–22 | 源码与文档分批入库（.gitignore、README、injector、frameprobe、插件工程、全部 docs 与 scripts） | ✅ |
| 批次 23–25 | 补齐最后 4 个文本文件；**并修正批次 1–22 中 23 个文件的转写/编码差错** | ✅ |
| 批次 26 | 按委托方要求把 BepInEx 与 dotnet 运行包入库 | ✅（后被批次 27 取代） |
| 批次 27 | `tools/` 改为**解压后的运行环境**，`BepInEx/` 与 `dotnet/` 平级 | ✅ |
| 公开发布 | 新建公开仓库，脱敏后发布快照；发布 v1.6 Release | ✅ |
| 社区协作 | 审计并合并 `havoc123` 的 PR #1 | ✅ |

### 5.1 批次 25 修正的差错类型（值得记录）

早期通过 API 推送时引入的系统性差错，已全部按本地源树恢复：

| 差错类型 | 涉及文件数 | 示例 |
|---|---|---|
| UTF-8 BOM 被剥离 | 4 | `ModManager.ps1`、`v6_run.ps1`、`v9_run.ps1`、`v9_scroll.ps1` |
| 内容转写错误 | 4 | `setup_ifeo_debugger.ps1` 的 `$i -lt 10` 变成 `$i - 10` |
| CRLF 被规范化为 LF | 9 | `README.md`、`LocalModManager.csproj`、`inject.ini` 等 |
| 文件末尾换行丢失 | 8 | `实施报告v4/v5/v6r2/v7.md` 等 |

### 5.2 隐私脱敏

公开前扫描并清理（共改动 69 个文本文件）：

| 类型 | 命中 | 处理 |
|---|---|---|
| Windows 用户名 | 用户目录绝对路径（两个用户名）共 32 处 | → `%USERPROFILE%` |
| 机器专属绝对路径 | 110 处 | → `<G>` |
| 散文中的他人机器名 | 2 处 | → 「原机器」 |

**扫描确认无泄漏**：无密钥 / token / 私钥 / 邮箱 / 局域网或公网 IP（仅 `127.0.0.1`）、
无个人域名、无 `scwunai` 字样。`tools/` 内 228 个文件零命中。

清理中处理了两个易错点：csproj 的 `<G>` 属性（直接替换会破坏 XML）与 14 处宏定义行
（直接替换会退化成 `<G>` = `<G>` 的循环表述）。

---

## 6. 【核心问题】仓库快照与现场工程相差两个代际

这是本次整体评估中**最需要立即处理的结构性问题**。

| | 仓库快照（公开 + 私有） | 现场工程 |
|---|---|---|
| 构建标记 | `20261007-0530 v9-tabgate` | **`20261007-1930 v13-split`** |
| `Plugin.cs` | 220,066 B | 187,773 B |
| `DebugActions.cs` / `WriteTest.cs` / `ReadOnlyLoad.cs` | 存在 | **已不在该目录** |
| `VideoBridge.cs` | 不存在 | 10,151 B |
| 已部署插件 | — | `LocalModManager.dll` **89,600 B** + `LocalStoryDebug.dll` **80,896 B** |

**结论**：现场工程不仅版本更靠前，**代码结构已经分裂**——调试工具被拆成独立的
`LocalStoryDebug.dll` 插件（与构建标记里的 `split` 相呼应），插件主体从 220 KB 缩到 188 KB，
并新增了 `VideoBridge.cs`。

**影响面**：

1. 公开仓库当前呈现的是 v9 代代码，**对大众的参考价值随时间衰减**；
2. 刚落地的 PR #1 改动的是 v9 代 `Plugin.cs`，**无法直接套用到 v13**，需要在 v13 上手工移植；
3. 打包发布手册描述的是 v9 代的包结构（例如 plugins 目录只有 1 个 DLL），
   而 v13 现场已经是 2 个插件 DLL。

**建议**：把仓库同步到 v13 作为下一步的**最高优先级**动作；同步后再统一决定
功能插件 API 如何落到 v13。

**附带发现**：从仓库源码编译出的 `main` 产物，与文档记载的 v9 构建产物
**字节数相同（139,264 B）但 MD5 不同**（`44fbf38b…` vs 文档记载的 `75a7c851…`）。
已排查确认与 PR 无关，仅一处注释被脱敏修改（注释不参与 IL 生成），
具体原因未完全定因。**当前无法从仓库源码逐字节复现历史发布产物**，建议记录备查。

---

## 7. 社区贡献：PR #1 审计与合并

| 项 | 值 |
|---|---|
| PR | #1「Add generic BepInEx feature plugin API」 |
| 提交者 | `havoc123`（`FIRST_TIME_CONTRIBUTOR`） |
| 规模 | 7 文件，+240 / −10 |
| 内容 | 新增 `IManagedFeaturePlugin` 契约 + 插件发现器 + 设置页新分区 |
| 审计结论 | **通过（风险低）** |
| 合并结果 | merge commit `a760ada` |
| 合并后编译 | ✅ 0 warning / 0 error |

**审计方法的关键点**：由于 git 传输不稳定，审计方通过
「PR base SHA 与本机 main 完全相同 → 用 API 重建 PR head → 逐文件比对 git blob SHA」
的方式**证明审计对象与合并对象逐字节等同**（7/7 匹配），随后在同一棵树上完成编译验证。

**安全结论**：无网络行为、无进程创建、无文件/注册表写入、无动态加载程序集
（仅枚举 `AppDomain.CurrentDomain.GetAssemblies()`）、无新增第三方依赖、
两个 csproj 均保留 `DebugType=none`。**信任边界未扩大**——BepInEx 插件本就完全受信任，
该 API 只对已加载插件调用接口方法。

**唯一实际缺陷（行尾）**：把 diff 落到字节层面复核时发现，
`Plugin.cs` 被写成**混合行尾**——base 是纯 CRLF（4612 行），PR head 变成
4676 CRLF + **11 行纯 LF**；此外 4 个新增文件统一用 LF，而仓库既有
`.cs` / `.csproj` 都是 CRLF。不影响编译（实测 0 warning / 0 error），
但违反本仓库既有的行尾约定，且本工程此前已因行尾问题吃过亏（批次 25 修复过 9 个此类文件）。
建议处置见审计报告 §6.0。

**交付物**：

| 文件 | 内容 |
|---|---|
| `审计报告_PR1_havoc123.md` | 完整审计报告 |
| `PR1_havoc123.diff` | 逐字节精确的统一 diff（21,227 B / 415 行），可直接 `git apply` |
| `PR1_havoc123.patch` | `git format-patch` 格式（含 commit 元数据与作者） |
| `PR1_havoc123_diff详解.md` | diff 可读版全文 + 逐处注解 + 行尾分析（35.9 KB） |

---

## 8. 质量保障体系

| 手段 | 说明 |
|---|---|
| 内容寻址校验 | 所有入库内容用 git blob SHA-1 逐文件比对（公开仓库 347/347、私有 343/343 全一致） |
| 二进制 MD5 复核 | 发布资产与仓库内二进制均从 GitHub 下载回来做 MD5 比对 |
| 真实构建验证 | 用本机 .NET SDK 8.0.425 + 游戏真实 interop（154 程序集）实测编译，非仅静态检查 |
| 编译项导出 | 用 `msbuild -getItem:Compile` 验证 `DefaultItemExcludes` 确实生效 |
| 隐私扫描 | 密钥 / token / 用户名 / 路径 / 邮箱 / IP / 域名 多模式扫描，脱敏后复扫为 0 |
| 性能门槛 | 帧时间 p95 偏差 ≤5%（当前实测 0.00%） |
| 发布前 checklist | 打包手册规定 8 项，含插件 MD5 与部署版一致、bat 编码、零内部工程代号命中、示例 MOD 默认关闭 |

---

## 9. 已知问题与风险登记

### 9.1 功能侧未闭环项

| 项 | 状态 | 说明 |
|---|---|---|
| 空间传送 | ❌ 不可用 | 传送后世界被拆除且不重建，v9 已证伪原假设，转 v10 路线 B |
| 剧情跳跃 | ◑ 部分可用 | 可起流程，objective 推进受游戏机制限制 |
| MOD 标签偶发自开关 | ◑ 主要通路已移除 | v9 实测零命中，物理点击待人工复看 |
| 视频替换 | ⚠️ 实验性 | 日志级通过（钩在游戏自身启动视频路径上），画面级待确认 |
| 注入偶发崩溃 | ⚠️ 10.9% | 55 次启动 6 次 ntdll `0xc0000005`，退出重试即成功 |
| 其他设备兼容性 | ⚠️ 未实测 | v1.6 在新设备上未验证 |

### 9.2 工程侧风险

| 风险 | 级别 | 说明 |
|---|---|---|
| 仓库与现场代际脱节 | **高** | 见 §6 |
| 无法逐字节复现历史产物 | 中 | 见 §6 附带发现 |
| 新增对外 API 契约的版本化策略缺失 | 中 | `IManagedFeaturePlugin` 已成为公开契约，且需与另一仓库逐字节对齐 |
| 打包手册未纳入 `LocalModManager.Abstractions.dll` | 中 | 漏带会导致功能插件静默加载失败 |
| 公开仓库无 LICENSE | 中 | 默认「保留所有权利」，第三方无法合法复用 |
| `tools/` 使仓库达 73.65 MB | 低 | 217 个二进制文件在网页端浏览价值有限；本机 git 大流量传输不稳定（多次 reset） |
| 绕过游戏保护壳的公开文档 | **需法务判断** | 详见下 |

### 9.3 法律与合规提示

公开仓库的文档与源码**完整记录了绕过游戏保护壳（gpShell）的过程**，
并说明如何注入商业游戏进程。这类内容在部分司法管辖区可能触及反规避条款。
相关材料：`docs/官方接口截获报告.md`、`src/injector/probe*.c`、`docs/Mod系统逆向.md` 等。

发布包本身**不含任何游戏资产**（地图/剧情/美术版权归 Nuverse），
随包分发的 BepInEx / Harmony / MonoMod / Doorstop / .NET 运行时均为各自上游的开源产物。

**建议**：由委托方对「公开逆向与绕壳内容」的边界作出明确决策。

---

## 10. 后续建议（按优先级）

1. **【最高】同步仓库到 v13**：把现场工程（`Plugin.cs` + `VideoBridge.cs` + `LocalStoryDebug` 插件）
   同步入库，消除代际脱节；同步后公开仓库才真正具备参考价值。
2. **把功能插件 API 移植到 v13**（在 1 完成后），并决定 `IManagedFeaturePlugin` 的版本化策略。
3. **更新打包发布手册**：纳入新的插件 DLL 集合与 `LocalModManager.Abstractions.dll`。
4. **为公开仓库补 LICENSE**，明确第三方可复用的范围。
5. **重新打包一个新版本**（当前发布包 v1.6 的插件构建 `20261007-0130 scroll-fix`
   落后于仓库 main 的 `20261007-0530 v9-tabgate`，更远落后于现场 v13）。
6. **注入崩溃率监控**：10.9% 低于 1/3 阈值，继续观察；超阈值取 WER 转储。
7. **性能回归验证**：功能插件发现器每秒做约 150 次跨程序集类型查找，
   建议纳入性能门槛复测（当前门槛为 p95 偏差 ≤5%）。

---

## 11. 附录：关键数据速查

```
私有仓库  scwunai/WorldApart-ModManager          private  main=8ab2b9c  343 文件 38 commits
公开仓库  scwunai/WorldApart-ModManager-Public   public   main=a760ada  347 文件  2 commits

发布包 v1.6  75,565,534 B  466 条目  MD5 5d2c7812d6f95cf6724615c9c2b9062c
  插件 LocalModManager.dll  114,688 B  MD5 28c41bb93bb48ff7ac11674d81d18039
  构建标记 20261007-0130 scroll-fix

仓库源码编译（main，本机 SDK 8.0.425 + 游戏 interop 154 程序集）
  合并前 139,264 B   MD5 44fbf38b6ad50f69a482c82bd0661355
  合并后 143,360 B + LocalModManager.Abstractions.dll 4,096 B   （0 warning / 0 error）

创意工坊条目 3814675406    游戏 appid 4209920
帧基线 p95 = 6.256 ms      验收门槛 偏差 ≤5%（实测 0.00%）
注入崩溃率 55 次启动 / 6 次崩溃 = 10.9%
```

---

*本报告中的每一项数字均来自实际执行结果：git blob SHA 比对、GitHub API 查询、
本机 .NET 编译输出、zip 逐条目 CRC/MD5 计算。*
