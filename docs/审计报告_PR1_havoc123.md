# Pull Request 安全与质量审计报告

**审计对象**：`scwunai/WorldApart-ModManager-Public` PR #1 —「Add generic BepInEx feature plugin API」
**提交者**：`havoc123` <zhenyu368@gmail.com>（`FIRST_TIME_CONTRIBUTOR`）
**来源分支**：`havoc123/WorldApart-ModManager-Public : codex/bepinex-feature-plugin-api`
**PR head SHA**：`cd57e55945bc1f38382680e0c2f800d80497171e`
**PR base SHA**：`a7d317fef2e09c686b33fbc293c9be787f7f9dd8`（= 当时 public 仓库 main）
**审计时间**：2026-10-07
**审计结论**：**通过 — 已合并**（merge commit `a760ada5960b72ff1c65f3bf2cfb60ff6b7e6259`）

---

## 1. 结论摘要

| 维度 | 结论 | 依据 |
|---|---|---|
| 恶意代码 | ✅ 未发现 | 逐 hunk 人工通读全部 240 行新增代码 |
| 网络行为 | ✅ 无 | 无 `HttpClient` / `WebClient` / `Socket` / `WebRequest` / URL 常量 |
| 进程与文件系统 | ✅ 无 | 无 `Process.Start`、无 `File.*` / `Directory.*` / 注册表写入 |
| 动态加载程序集 | ✅ 无 | 只枚举 `AppDomain.CurrentDomain.GetAssemblies()`，不调用 `Assembly.Load*` |
| 混淆 / 后门载荷 | ✅ 无 | 无可疑 base64、无加密字符串、无反射调用外部载荷 |
| 新增第三方依赖 | ✅ 无 | 未新增任何 NuGet `PackageReference`；仅新增一个本仓库内的 net6.0 类库 |
| 敏感信息泄漏 | ✅ 无 | 新增文件不含凭据、不含个人路径 |
| 编译 | ✅ 通过 | .NET SDK 8.0.425 + 游戏 interop 实测：**0 warning / 0 error** |
| 既有功能回归 | ✅ 未发现 | 改动面小且自洽；`CurrentSnapshot` 重构已完整覆盖所有调用点 |
| 信任边界 | ✅ 未扩大 | 见 §4 |
| **行尾规范** | ❌ **不符合仓库约定** | `Plugin.cs` 被写成混合行尾（11 行 LF）；4 个新增文件用 LF 而仓库既有为 CRLF。见 §6.0 |

**风险等级：低。** 无安全或编译阻断项。已按委托方要求执行合并。

> ⚠️ 唯一发现的**实际缺陷**是行尾规范问题（§6.0），它不影响编译与运行，
> 但违反本仓库既有的行尾约定，建议单独处置。

---

## 2. 审计范围与方法

### 2.1 覆盖范围

PR 共 **1 个 commit、7 个文件、+240 / −10 行**，全部 240 行新增代码均已人工通读：

| # | 文件 | 状态 | +/− |
|---|---|---|---|
| 1 | `README.md` | modified | +4 / −0 |
| 2 | `docs/BepInEx-Feature-Plugin-API.md` | added | +22 / −0 |
| 3 | `src/bepinex_plugin/FeaturePluginRegistry.cs` | added | +99 / −0 |
| 4 | `src/bepinex_plugin/LocalModManager.csproj` | modified | +6 / −2 |
| 5 | `src/bepinex_plugin/Plugin.cs` | modified | +83 / −8 |
| 6 | `src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs` | added | +15 / −0 |
| 7 | `src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj` | added | +11 / −0 |

### 2.2 方法：先证明「审的就是要合的东西」

本机到 github.com 的 git 传输不稳定（多次 `Connection timed out` / `Connection was reset`），
无法直接 fetch PR 分支。因此改用**可验证的重构法**：

1. 确认 PR base SHA `a7d317f` 与本机 `origin/main` **完全相同**，即「main + PR 的 7 个文件内容 = PR head」；
2. 从 `origin/main` 导出干净工作树，再用 GitHub Contents API 逐个取回 PR head 版本的 7 个文件；
3. 对写回的每个文件计算 **git blob SHA-1**，与 GitHub 对 PR 报出的 blob SHA 逐一比对。

**结果：7/7 全部一致，重建树与 PR head 逐字节相同。**

```
README.md                                   OK  b90493409342
docs/BepInEx-Feature-Plugin-API.md          OK  e0d5fd5508b3
src/bepinex_plugin/FeaturePluginRegistry.cs OK  92cde1af9fdb
src/bepinex_plugin/LocalModManager.csproj   OK  6432fb8c45aa
src/bepinex_plugin/Plugin.cs                OK  b028daa16df8
src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs           OK  5ce39105e187
src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj OK  f1c2918b71c4
→ EXACT MATCH to PR head
```

这一步是本次审计的关键：**审计对象不是 PR 页面上渲染的 diff，而是被证明等同的字节**。
后续的编译验证也在同一棵树上进行。

---

## 3. 变更内容说明（它到底做了什么）

### 3.1 新增一个纯接口契约

`abstractions/IManagedFeaturePlugin.cs`（15 行，无任何实现逻辑）：

```csharp
namespace LocalModManager.Abstractions
{
    public enum FeaturePluginState { Stopped, Starting, Running, Stopping, Failed }
    public interface IManagedFeaturePlugin
    {
        string FeatureId { get; }
        string DisplayName { get; }
        string Description { get; }
        string FeatureVersion { get; }
        bool DesiredEnabled { get; }
        FeaturePluginState State { get; }
        string StatusMessage { get; }
        void SetEnabled(bool enabled);
    }
}
```

配套 `LocalModManager.Abstractions.csproj` 把它编成独立的 `net6.0` 类库，
**同样带 `<DebugType>none</DebugType>`**（遵守本仓库「DLL 内不得残留构建机路径」的硬约束）。

### 3.2 新增插件发现器

`FeaturePluginRegistry.cs`（99 行）：通过反射读 BepInEx 已加载的插件实例，
挑出实现了 `IManagedFeaturePlugin` 的插件，列成可管理的条目。

- 先找 `BepInEx.Unity.IL2CPP.IL2CPPChainloader`，读其 `Instance`（先属性后字段），再读 `Plugins`；
- 找不到时回落 `BepInEx.Bootstrap.Chainloader.PluginInfos`（Mono 侧）；
- 对每个条目读 `Value` / `Instance` / `Metadata.GUID`；
- **全部读取操作包在 try/catch 里，失败只记 warning**，不抛出、不中断主流程。

### 3.3 MOD 管理页新增「BepInEx 功能插件」分区

`Plugin.cs` 在原「MOD 管理」页底部追加一个独立分区（行 `Kind = 3` 作小标题，`Kind = 4` 作功能行），
每个功能行显示 `名称 v版本 [运行状态]` + 状态/描述文字，并复用页面原有的开关组件。
点击走 `FeaturePluginRegistry.SetEnabled(id, !On)`。
分区与原生 `mod.json` MOD 包管理**互相独立**，不改动 MOD 包的任何逻辑。

### 3.4 工程与文档

- 主工程 csproj 增加 `DefaultItemExcludes` 排除 `abstractions\**\*.cs`，并加 `ProjectReference`（`Private=true`，使契约 DLL 随插件一起输出）；
- 新增 `docs/BepInEx-Feature-Plugin-API.md` 说明契约与使用方式；
- README 增加一节说明两个分区相互独立、仍需放在 `BepInEx/plugins/`。

---

## 4. 安全分析（重点）

### 4.1 攻击面：**未扩大**

这是本次审计最重要的判断。要点是：

> **BepInEx 插件本身就是完全受信任的代码。** 任何能被 BepInEx 加载的 DLL 都已经在游戏进程内拥有
> 完整的 .NET 权限——能读写文件、能发包、能起进程。管理器的插件发现机制**只对「已经被加载的插件」**
> 调用接口方法，因此**不会引入任何新的信任主体**。

具体来说，本 PR 没有做以下任何一件危险的事：

- ❌ 没有从磁盘按路径加载 DLL（没有 `Assembly.LoadFrom` / `LoadFile`）；
  发现范围严格限定在 `AppDomain.CurrentDomain.GetAssemblies()`——即 BepInEx 按自己的规则已经加载的集合。
- ❌ 没有新增「插件目录扫描」之类会引入新信任来源的机制。
- ❌ 没有把配置文件内容当代码执行。

### 4.2 网络行为

全文检索新增代码：无 `System.Net.*`、无 `HttpClient`、无 `WebClient`、无 `Socket`、
无任何 `http://` / `https://` 常量。**该改动完全不联网。**

### 4.3 进程 / 文件 / 注册表

无 `System.Diagnostics.Process`、无 `File.*` / `Directory.*` / `StreamWriter`、
无 `Microsoft.Win32.Registry`。**该改动不落任何盘、不改注册表、不起任何进程。**

### 4.4 反射的使用是否越界

`FeaturePluginRegistry.Read()` 使用 `BindingFlags.NonPublic`，能读私有成员。这是**必要且可接受**的：

- 它只**读**，不写、不调用非接口方法；
- 目标对象是 BepInEx 自身的 Chainloader 与插件实例，属于同一进程内的受信任组件；
- 读取失败一律静默降级（返回 null 并跳过）。

### 4.5 依赖与供应链

**未新增任何 NuGet 包或第三方二进制。** 新增的 `LocalModManager.Abstractions` 是仓库内的
自有源码类库。这一点尤其值得肯定：PR 描述中明确声明「不要把 TTS 相关依赖带进管理器」，
且实际 diff 中确实没有引入旁路依赖。

### 4.6 发布合规约束

仓库硬约束要求插件 csproj 必须含 `<DebugType>none</DebugType>`（否则 DLL 内会残留
构建机绝对路径，破坏「去内部工程代号化」扫描）。本次改动的**两个** csproj（主工程 + 新增的
abstractions）**都保留了该设置**。✅

### 4.7 文档中提到的外部项目

新增文档提到另一仓库的 IndexTTS 参考实现。这仅是**说明性文字**，不含代码引用、
不含依赖、不含网络地址。其中一个信息点值得记录：文档声明接口源码在两个仓库间
「逐字节保持对齐」——这意味着未来修改该接口会产生**跨仓库的同步义务**（见 §6.3）。

---

## 5. 正确性与构建验证

### 5.1 静态检查

| 检查项 | 结果 |
|---|---|
| `ManagerBehaviour.CurrentSnapshot` 由实例方法改为静态方法 | ✅ **改动完整**。全仓库检索 `CurrentSnapshot` 仅 4 处命中，3 处调用 + 1 处定义，**全部在 `Plugin.cs` 内且已被本 PR 一并更新**；`DebugActions.cs` / `WriteTest.cs` / `ReadOnlyLoad.cs` 均不引用该成员，**不会编译失败** |
| 新代码引用的符号是否存在 | ✅ 全部存在：`_rowLabelStyle`、`_rows`、`Label`、`LabelText`、`Relayout()`、`_rowHeight`、`ManagerBehaviour.Instance` |
| `NewRowRoot` 是否保证有 `Image` 组件 | ✅ 是。`NewRowRoot` 内部无条件 `go.AddComponent<UnityEngine.UI.Image>()`，因此新代码里 `header.GetComponent<Image>()` 不会空引用 |
| `MakeLabel` 第 5 个参数语义 | ✅ 名为 `rightInset`（默认 16f）。新代码传 `24f`（小标题）与 `140f`（功能行，为右侧开关留位），语义正确 |
| `FeatureLabel` 字符串截断 | ✅ 安全。先判 `Length > 100` 再 `Substring(0, 97)`，无越界 |
| 新增 `Kind` 取值冲突 | ✅ 无冲突。原用 1（包标题）/ 2（单位行），新增 3（分区标题）/ 4（功能行）。分区标题行未挂点击组件且 `raycastTarget=false`，不会被误点 |

### 5.2 编译验证（实测）

使用本机 `%USERPROFILE%\.dotnet\dotnet.exe`（**SDK 8.0.425**）配合游戏实际 interop
（`<G>\BepInEx\interop\` 154 个程序集）：

| 构建对象 | 结果 | 产物 |
|---|---|---|
| `main`（基线，合并前） | **0 warning / 0 error** | `LocalModManager.dll` 139,264 B |
| **PR head**（合入内容） | **0 warning / 0 error** | `LocalModManager.dll` 143,360 B + `LocalModManager.Abstractions.dll` 4,096 B |
| **合并后 main** | **0 warning / 0 error** | 与 PR head 产物**完全一致**（143,360 B + 4,096 B） |

提交者在 PR 中声称「0 warnings, 0 errors」——**该声明经独立复现属实**。

### 5.3 `DefaultItemExcludes` 是否真的生效

这是本 PR 最容易出错的一处：主工程开启了 `EnableDefaultCompileItems=true`，
若不排除，`abstractions/` 下的源码会被**同时**编进两个程序集（契约类型被定义两次），
跨程序集共享契约的设计就失效了。

用 `msbuild -getItem:Compile` 直接导出实际参与编译的文件清单验证：

```
DebugActions.cs / Plugin.cs / ReadOnlyLoad.cs / WriteTest.cs   ← 仅这 4 个
abstractions/**                                                ← 未出现 ✅
```

**排除生效，契约类型只存在于 `LocalModManager.Abstractions.dll`。**
产物里也确实同时生成了主 DLL 与 4,096 字节的契约 DLL（`<Private>true</Private>` 生效）。

### 5.4 关于基线 MD5 对不上的说明

本机从 `main` 编出的 `LocalModManager.dll` 是 139,264 B —— **与文档记载的 v9 构建
（`75a7c8514fe546886a185576072e375b`，139,264 B）字节数完全相同**，但 MD5 为
`44fbf38b6ad50f69a482c82bd0661355`，**对不上**。

已排查但**未完全定因**：公开仓库的源码经过隐私脱敏，`Plugin.cs` 与原始树仅差 **1 行注释**
（一处用户名绝对路径 → `%USERPROFILE%`，第 2552 行，共 2 字节），而注释不参与 IL 生成，
理论上不应改变产物。因此差异更可能来自工具链/构建输入的细微不同（文档记载的 v9 产物
所对应的源码快照未必与仓库内容逐字节一致）。

**此现象与 PR 无关**：基线与 PR 均在同一工具链、同一源码基线上构建，两者之间的比较是有效的。
仅作为观察项记录——**当前无法从仓库源码逐字节复现历史发布产物**。

---

## 6. 发现的问题与建议（均非阻断项）

按严重程度排序。**没有任何一条足以阻止合并。**

### 6.0 【缺陷 · 已在合并后确认】行尾（EOL）不符合仓库约定

> 本项是在把 diff 落到**字节层面**复核时才暴露的，晚于初版审查，故编号补在 6.1 之前。

**问题一：`Plugin.cs` 被写成混合行尾。**

| | CRLF 行 | 纯 LF 行 | 判定 |
|---|---|---|---|
| base `Plugin.cs` | 4612 | 0 | 纯 CRLF |
| **PR head `Plugin.cs`** | **4676** | **11** | ❌ **混合** |

新增的 91 行里有 **11 行以 LF 结尾**，其余为 CRLF。集中在新方法
`AddMissingFeatureRows()` 与 `FeatureLabel()` 的几条长语句上，例如：

```csharp
float rowH = row.Kind == 1 ? _pkgRowH : row.Kind == 4 ? FeatureRowHeight : _rowHeight;  // LF
var rt = NewRowRoot("FeaturePluginRow", FeatureRowHeight);                               // LF
string detail = status.Length > 0 ? status : description;                                // LF
```

**问题二：4 个新增文件统一用 LF，与仓库既有约定不符。**

| 文件 | 行尾 | 同类既有文件 |
|---|---|---|
| `FeaturePluginRegistry.cs`（新） | LF | `Plugin.cs` 等 —— **CRLF** |
| `abstractions/IManagedFeaturePlugin.cs`（新） | LF | 同上 |
| `abstractions/LocalModManager.Abstractions.csproj`（新） | LF | `LocalModManager.csproj` —— **CRLF** |
| `docs/BepInEx-Feature-Plugin-API.md`（新） | LF | 仓库内 md 多为 CRLF |

**验证方式（排除了假象）**：用 `git -c core.autocrlf=false apply` 应用该 diff 后，
7 个文件的 blob SHA 与 PR head 报出的值**完全一致**，
证明上述行尾数据是 PR 的**真实字节**，而非 diff 渲染或本机转换的产物。

> 附带发现：本机系统级 gitconfig 设了 `core.autocrlf=true`，
> **不加 `-c core.autocrlf=false` 直接用 `git apply`，新建文件会被静默写成 CRLF**，
> 导致 4 个文件哈希对不上。复现本 PR 时必须注意这一点。

**后果**：C# 编译器不在乎，实测 0 warning / 0 error，**不影响功能**。
但本工程对行尾有明确要求，且此前**已经因行尾问题吃过亏**——
批次 25 修复过 9 个文件被错误规范化为 LF 的问题。
一个「既非 CRLF 也非 LF」的源文件会让后续编辑器/工具行为不可预测，并使 diff 产生噪声。

**建议处置**（需委托方决策，本次未擅自改动）：

| 选项 | 说明 |
|---|---|
| A. 保持现状 | 无功能影响，但留下混合行尾文件 |
| B. 统一为 CRLF | 把 11 行 LF 与 4 个新文件规范为 CRLF，与仓库一致 |
| C. 统一为 LF | 与 B 相反，需连带规范既有文件，改动面大 |
| **D.（推荐）A + `.gitattributes`** | 不动贡献者提交，另加 `.gitattributes` 固定后续行为，并提示贡献者遵守 |

### 6.1 【重要 · 影响本次合并的实际价值】仓库快照滞后于现场工程两个代际

审计过程中发现现场工程已推进到 **v13**，而仓库（公开与私有）停留在 **v9 代**：

| | 构建标记 | `Plugin.cs` | 其他 |
|---|---|---|---|
| **仓库快照** | `20261007-0530 v9-tabgate` | 220,066 B | 含 `DebugActions.cs` / `WriteTest.cs` / `ReadOnlyLoad.cs` |
| **现场工程** | `20261007-1930 v13-split` | 187,773 B | 新增 `VideoBridge.cs`（10,151 B）；原三个文件已不在该目录 |

现场已部署的插件也与仓库产物不同：

- `BepInEx\plugins\LocalModManager.dll` = **89,600 B**（现场 v13 构建）
- `BepInEx\plugins\LocalStoryDebug.dll` = **80,896 B**（新增的独立插件——与 v13 构建标记里的
  `split` 相呼应，调试工具被拆成了另一个插件）

**影响**：本次 PR 的 `Plugin.cs` 改动（+83 行，含一个 40 行的新方法、一个方法签名重构）
是针对 v9 代代码写的。合并进仓库**完全正确且能编译**，但**不能直接套用到现场 v13 的
`Plugin.cs` 上**——若要吃到这个功能，需要在 v13 上手工移植。

**建议**：先把仓库快照与现场工程同步（这本身也是「让公开仓库对大众有参考价值」的前提），
再决定如何把功能插件 API 落到 v13。

### 6.2 【中 · 可维护性】`CurrentSnapshot` 的签名重构是多余的

`ManagerBehaviour.CurrentSnapshot()` 由实例方法改为
`internal static List<ModPackage> CurrentSnapshot(ManagerBehaviour instance)`，
并把 3 处调用改成 `ManagerBehaviour.CurrentSnapshot(mb)`。

但从 diff 看，**这个重构对本功能并非必需**——原有的 `mb.CurrentSnapshot()` 在
`Build()` / `OnRowClicked()` / `PackageAllOn()` 里本来就能正常工作。
这类「顺手改动」会显著抬高未来向 v13 移植时的冲突概率。

**建议**：向提交者反馈，后续 PR 避免夹带与功能无关的签名改动。

### 6.3 【中 · 治理】这是一个对外发布的 API 契约承诺

合并之后，`LocalModManager.Abstractions.IManagedFeaturePlugin` 就是**已公开的稳定契约**，
第三方插件会按它编译。同时新增文档声明该接口源码需与另一仓库（IndexTTS）
**逐字节保持对齐**——即未来任何接口变更都产生跨仓库同步义务。

**建议**：明确接口的版本化与废弃策略（文档目前只有一句「changes to the API should be
versioned」），否则将来加字段就会连带打断所有已发布的功能插件。

### 6.4 【中 · 文档缺口】打包发布手册未同步

新增了随插件一起发布的 `LocalModManager.Abstractions.dll`，README 已说明，
但 **`docs/打包发布版本操作手册.md` §2「同步最新构建产物的规则」未提及该文件**。

**影响**：下次打包若只往 `BepInEx\plugins\` 放 `LocalModManager.dll`，
功能插件将因找不到契约程序集而无法加载，且**不会有明显报错**。

**建议**：在打包手册 §1/§2 的清单里补上该 DLL，并把「插件目录须含 2 个 DLL」
写进 §7 发布前 checklist。

### 6.5 【低 · 性能】每秒一次的跨程序集类型查找

`FeaturePluginRegistry.Refresh()` 被挂在页面每秒一次的刷新节流里
（`_refreshAt` 分支），而它每次都会遍历 `AppDomain.CurrentDomain.GetAssemblies()`
并对每个程序集调用一次 `Assembly.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader")`。
本工程加载的 interop 程序集有 **154 个**，即约 **150+ 次类型查找/秒**，
外加每次重建 `ManagedFeature` 列表。

单次开销很小，但本项目的验收门槛是「帧时间 p95 偏差 ≤5%，当前实测 0.00%」。

**建议**：把 Chainloader 类型解析结果缓存起来（只需解析一次），
或降低发现频率 / 仅在页面可见且插件集合变化时重扫。

### 6.6 【低 · 健壮性】功能行只增不减

`AddMissingFeatureRows()` 只处理「新增」，没有对应移除逻辑。
若某功能插件消失（或 `FeatureId` 变化），页面上会残留一行指向已不存在插件的开关，
点击时只会记一条 `no feature accepted request` 警告。

实际发生概率低（BepInEx 插件一旦加载不会卸载），记录备查。

### 6.7 【低 · 可移植性】`DefaultItemExcludes` 使用了反斜杠

```xml
<DefaultItemExcludes>$(DefaultItemExcludes);abstractions\**\*.cs</DefaultItemExcludes>
```

Windows 下工作正常（已实测）。MSBuild 会把 item 规格里的 `\` 规范化为路径分隔符，
因此在 Linux/macOS 上通常也能工作，但惯用写法是 `/`。

### 6.8 【提示】PR 由 AI 编码代理产出

来源分支名为 `codex/...`，PR 描述中亦有「If you use a coding agent, it can inspect this branch」
之类措辞，判断该 PR 出自 AI 编码代理、由 `havoc123` 提交。
这**不影响本次审计结论**——审计针对的是字节与构建结果，而非产出方式。
不过它解释了 §6.2 那类「顺手重构」为何会出现，也提示未来对该贡献者的 PR 应默认做完整审计。

---

## 7. 合并执行记录

| 项 | 值 |
|---|---|
| 合并方式 | GitHub merge commit（非 squash / 非 rebase，保留 PR 归因） |
| 合并前 main | `a7d317fef2e09c686b33fbc293c9be787f7f9dd8` |
| PR head | `cd57e55945bc1f38382680e0c2f800d80497171e` |
| **合并结果** | **`a760ada5960b72ff1c65f3bf2cfb60ff6b7e6259`** |
| 合并时间 | 2026-10-07T12:50:05Z |
| 合并后文件数 | 347（合并前 343，新增 4 个） |
| 合并后编译 | ✅ 0 warning / 0 error |
| 合并后产物 | `LocalModManager.dll` 143,360 B + `LocalModManager.Abstractions.dll` 4,096 B |

**合并后核查**：

- `origin/main` = `a760ada…`，与合并返回的 SHA 一致；
- `FeaturePluginRegistry.cs`、`abstractions/IManagedFeaturePlugin.cs`、
  `abstractions/LocalModManager.Abstractions.csproj`、`docs/BepInEx-Feature-Plugin-API.md`
  四个新文件均已存在于 `main`；
- 从合并后的 `main` 重新导出并编译，产物与 PR head 构建**完全一致**。

---

## 8. 遗留事项清单

| # | 事项 | 优先级 | 归属 |
|---|---|---|---|
| 1 | 仓库快照（v9）与现场工程（v13）同步 | 高 | 委托方 |
| 2 | **处置行尾缺陷（§6.0）：`Plugin.cs` 混合行尾 + 4 个新文件用 LF** | **中** | 委托方 |
| 3 | 向提交者反馈 §6.2 的无关重构问题 | 中 | 委托方 |
| 4 | 明确 `IManagedFeaturePlugin` 的接口版本化策略 | 中 | 委托方 |
| 5 | 更新打包发布手册，纳入 `LocalModManager.Abstractions.dll` | 中 | 委托方 |
| 6 | 评估 `Refresh()` 每秒跨程序集扫描的性能影响 | 低 | 委托方 |
| 7 | 为 `abstractions` 增加跨平台 `DefaultItemExcludes` 写法 | 低 | 委托方 |
| 8 | 无法从仓库源码逐字节复现历史发布产物（§5.4） | 低 | 委托方 |

---

## 9. 交付物清单

| 文件 | 内容 |
|---|---|
| `审计报告_PR1_havoc123.md` | 本文件 |
| `PR1_havoc123.diff` | **逐字节精确**的统一 diff（21,227 B / 415 行），可直接 `git apply`；已验证可重现 PR head |
| `PR1_havoc123.patch` | `git format-patch` 格式（22,192 B / 434 行），含 commit 元数据与作者信息 |
| `PR1_havoc123_diff详解.md` | 同一 diff 的可读版全文 + 逐处注解 + 行尾分析（§9） |
| `项目报告_WorldApart-ModManager.md` | 整体项目报告 |

---

*本报告的每一条结论均可回溯到具体命令与输出：diff 逐 hunk 阅读、blob SHA 逐文件比对、
两次独立编译（PR head 与合并后 main）、`msbuild -getItem:Compile` 的编译项导出、
diff 应用级重现验证、以及逐文件行尾字节统计。*
