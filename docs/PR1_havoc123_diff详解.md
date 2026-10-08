# PR #1 Diff 全文与逐处详解

**对象**：`scwunai/WorldApart-ModManager-Public` PR #1 —「Add generic BepInEx feature plugin API」
**提交者**：`havoc123` <zhenyu368@gmail.com>
**base**：`a7d317fef2e09c686b33fbc293c9be787f7f9dd8`
**head**：`cd57e55945bc1f38382680e0c2f800d80497171e`
**规模**：7 文件，**+240 / −10**，1 个 commit
**合并**：`a760ada5960b72ff1c65f3bf2cfb60ff6b7e6259`

## 配套文件

| 文件 | 用途 |
|---|---|
| `PR1_havoc123.diff` | **逐字节精确**的统一 diff（21,227 B / 415 行），可直接 `git apply` |
| `PR1_havoc123.patch` | `git format-patch` 格式（22,192 B / 434 行），含 commit 元数据与作者信息 |
| 本文件 | 同一 diff 的**可读版全文 + 逐处注解** |

## 本 diff 的可信度

本文件中的 diff 来自 GitHub 的 diff API（`Accept: application/vnd.github.v3.diff`），
并已做**应用级验证**：

```bash
# 导出 base 树 → 应用 diff → 比对每个文件的 git blob SHA
git archive a7d317f | tar -x -C <workdir>
cd <workdir> && git -c core.autocrlf=false apply PR1_havoc123.diff
```

结果：**7 个文件的 blob SHA 与 GitHub 为 PR head 报出的值逐一对上**，
即该 diff 应用到 base 后能**逐字节重现 PR head**。

> ⚠️ **必须加 `-c core.autocrlf=false`**。本机系统级 gitconfig（`C:\Program Files\Git\etc\gitconfig`）
> 设了 `core.autocrlf=true`，直接 `git apply` 会把**新建**文件写成 CRLF，
> 而 PR 里的新文件其实是 LF，结果会静默产生 4 个文件的哈希偏差。
> 这一点是本次验证时实际踩到并定位的。

> 📌 **本节显示的 diff 内容把行尾统一显示为 LF**，以便阅读。真实字节下行尾是混合的
> —— 详见文末「§8 行尾（EOL）分析」，那是本次审计发现的一个实际缺陷。
> 需要精确字节时请以 `PR1_havoc123.diff` 为准。

---

## §1 总览

```
README.md                                                  |   4 +
docs/BepInEx-Feature-Plugin-API.md                         |  22 +++++      (新增)
src/bepinex_plugin/FeaturePluginRegistry.cs                |  99 ++++++++++   (新增)
src/bepinex_plugin/LocalModManager.csproj                  |   8 +-
src/bepinex_plugin/Plugin.cs                               |  91 +++++++--
src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs   |  15 +++        (新增)
src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj | 11 +  (新增)
```

改动可以拆成三块，互相独立：

| 块 | 文件 | 作用 |
|---|---|---|
| **A. 契约** | `abstractions/*`（2 个新文件） | 定义纯接口 + 独立类库 |
| **B. 发现** | `FeaturePluginRegistry.cs`（新）+ csproj（改） | 反射找出实现该接口的插件 |
| **C. 界面** | `Plugin.cs`（改）+ README + docs | 在 MOD 页加一个功能插件分区 |

---

## §2 `README.md`　+4 / −0

```diff
@@ -57,3 +57,7 @@
 - 已完成：BepInEx 注入稳定、游戏内 MOD 管理页（原生样式、包/分组两级、滚动修复）、性能无损（p95 偏差 0.00%）、F9 写入四件套实测通过。
 - 进行中：空间传送到达未确认；剧情跳跃未闭环（TryStartQuestProc 返回 False，备选 FlowRuntimeManager.StartInWorld）。
 - 规划：视频替换 AssetOverlay 优先路线、MOD 页自开关 bug 单独立项。
+
+## BepInEx 功能插件接口
+
+`src/bepinex_plugin/abstractions/` 定义与原生 `mod.json` 包相互独立的功能插件控制接口。基础包需将 `LocalModManager.Abstractions.dll` 与 `LocalModManager.dll` 放入 `BepInEx/plugins/`。接口和安装方式见 [BepInEx-Feature-Plugin-API.md](docs/BepInEx-Feature-Plugin-API.md)。Steam 启动仍使用单一 Doorstop/BepInEx 链；此 DLL 不替代或新增注入器。
```

**注解**：纯文档追加，在 README 末尾新增一节。提到两个关键约束——① 契约 DLL 需随主插件一起放进 `BepInEx/plugins/`；
② 该 DLL 不改变注入链路。**行尾与文件原有风格一致（CRLF）**。

---

## §3 `docs/BepInEx-Feature-Plugin-API.md`（新增 22 行）

```diff
@@ -0,0 +1,22 @@
+# BepInEx feature plugin API
+
+The settings page has two independent sections: native `mod.json` packages and BepInEx feature plugins. The manager discovers only plugins that implement `LocalModManager.Abstractions.IManagedFeaturePlugin`; it does not infer a switch for arbitrary DLLs and does not write plugin configuration.
+
+## Contract
+
+`src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj` builds `LocalModManager.Abstractions.dll` for .NET 6. Install that DLL once in `BepInEx/plugins/`, beside the manager plugin. Feature plugins reference the assembly but do not embed its types. The IndexTTS standalone package also ships the same dependency, so IndexTTS loads without the manager.
+
+Implement the interface on the BepInEx plugin instance:
+
+- `FeatureId`: stable unique ID, preferably the plugin GUID.
+- `DisplayName`, `Description`, `FeatureVersion`: UI metadata.
+- `DesiredEnabled`: persisted user intent.
+- `State`: `Stopped`, `Starting`, `Running`, `Stopping`, or `Failed`.
+- `StatusMessage`: current readiness or failure detail.
+- `SetEnabled(bool)`: accept quickly and perform slow work asynchronously. Calls should be idempotent. The implementation owns persistence and cleanup.
+
+Assemblies remain loaded. Disabling a feature must stop its feature activity and owned processes without attempting to unload the BepInEx plugin assembly. The manager refreshes metadata/state while its settings page is visible and tolerates the feature plugin loading before or after the manager.
+
+## IndexTTS reference implementation
+
+The first implementation is in the A1 IndexTTS repository under `src/managed-feature-api/` and `src/Plugin.cs`. Its `Stage3Mvp.Enabled` config remains the source of desired state, so standalone operation keeps working. The interface source is intentionally kept byte-for-byte aligned in both repositories; changes to the API should be versioned and delivered to the IndexTTS author at the same time.
```

**注解**：说明性文档，无代码。三个值得注意的点：

1. 明确声明「**不会为任意 DLL 推断开关**，也不写插件配置」——这是设计上的安全声明；
2. 明确「**不得尝试卸载 BepInEx 插件程序集**」——与 .NET 无法真正卸载程序集的现实一致；
3. 最后一段声明接口源码需**跨仓库逐字节对齐**（与 IndexTTS 仓库），
   这构成一项长期维护义务（见审计报告 §6.3）。

**行尾：LF**（该文件为新增，仓库内既有 md 多为 CRLF，见 §8）。

---

## §4 `src/bepinex_plugin/FeaturePluginRegistry.cs`（新增 99 行）

### 4.1 数据结构与入口

```diff
@@ -0,0 +1,99 @@
+using System;
+using System.Collections;
+using System.Collections.Generic;
+using System.Reflection;
+using BepInEx;
+using LocalModManager.Abstractions;
+
+namespace LocalModManager
+{
+    internal sealed class ManagedFeature
+    {
+        internal string OwnerGuid;
+        internal IManagedFeaturePlugin Feature;
+    }
+
+    internal static class FeaturePluginRegistry
+    {
+        private static readonly List<ManagedFeature> Items = new List<ManagedFeature>();
+        internal static IReadOnlyList<ManagedFeature> Current { get { return Items; } }
+
+        internal static void Refresh()
+        {
+            Items.Clear();
```

**注解**：`Items` 是静态单例列表，`Refresh()` 每次先清空再重建。

### 4.2 主路径：从 IL2CPP Chainloader 取插件表

```diff
+            try
+            {
+                Type chainloader = null;
+                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
+                {
+                    chainloader = assembly.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader");
+                    if (chainloader != null) break;
+                }
+                object infos = null;
+                if (chainloader != null)
+                {
+                    object instance = null;
+                    var singleton = chainloader.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
+                    if (singleton != null) instance = singleton.GetValue(null, null);
+                    if (instance == null)
+                    {
+                        var instanceField = chainloader.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
+                        if (instanceField != null) instance = instanceField.GetValue(null);
+                    }
+                    infos = Read(instance, "Plugins");
+                }
```

**注解**：这是**安全上最关键的一段**——它遍历的是
`AppDomain.CurrentDomain.GetAssemblies()`，即**已经被加载的程序集**，
**没有任何从磁盘按路径加载 DLL 的代码**。因此不会引入新的信任主体。

「先找属性、再找字段」的写法是为了兼容 BepInEx 不同版本把 `Instance` 实现为属性或字段的情况。

> ⚠️ **性能提示**：这段在 `Refresh()` 里，而 `Refresh()` 挂在页面**每秒一次**的刷新节流上。
> 本工程加载 154 个 interop 程序集，即约 150+ 次 `GetType` 调用/秒。建议缓存解析结果。

### 4.3 回落路径：Mono 侧 Chainloader

```diff
+                if (infos == null)
+                {
+                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
+                    {
+                        var fallback = assembly.GetType("BepInEx.Bootstrap.Chainloader");
+                        if (fallback == null) continue;
+                        var prop = fallback.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
+                        if (prop != null) infos = prop.GetValue(null, null);
+                        if (infos == null)
+                        {
+                            var field = fallback.GetField("PluginInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
+                            if (field != null) infos = field.GetValue(null);
+                        }
+                        break;
+                    }
+                }
```

**注解**：Mono 运行时（非 IL2CPP）的兼容路径。本作是 IL2CPP，实际不会走到，属防御性代码。

### 4.4 筛选实现了契约的插件

```diff
+                var entries = infos as IEnumerable;
+                if (entries == null) return;
+                foreach (var entry in entries)
+                {
+                    object info = entry;
+                    object value = Read(entry, "Value");
+                    if (value != null) info = value;
+                    if (info == null) continue;
+                    var feature = Read(info, "Instance") as IManagedFeaturePlugin;
+                    if (feature == null || String.IsNullOrWhiteSpace(feature.FeatureId)) continue;
+                    object metadata = Read(info, "Metadata");
+                    Items.Add(new ManagedFeature { OwnerGuid = Convert.ToString(Read(metadata, "GUID")) ?? "", Feature = feature });
+                }
+            }
+            catch (Exception e) { Plugin.Logger.LogWarning("[FEATURES] discovery failed: " + e.GetType().Name + ": " + e.Message); }
+        }
```

**注解**：核心过滤就是 `Read(info, "Instance") as IManagedFeaturePlugin`——
**只有实现了接口的插件才会被纳入**，其余插件被忽略。这正是文档里「不为任意 DLL 推断开关」的落地方式。

整个 `Refresh()` 用一个大 `try/catch` 兜住，**任何反射失败只记一条 warning，不影响主流程**。

### 4.5 通用反射读取辅助

```diff
+        private static object Read(object instance, string name)
+        {
+            if (instance == null) return null;
+            var type = instance.GetType();
+            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
+            if (prop != null) return prop.GetValue(instance, null);
+            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
+            return field == null ? null : field.GetValue(instance);
+        }
```

**注解**：带 `NonPublic` 的反射读取。**只读、不写、不调用**，且空值安全（`instance == null` 直接返回）。
这是访问 BepInEx 内部 `Plugins` / `Metadata` 所必需的——这些成员未必是 public。

### 4.6 开关下发

```diff
+        internal static bool SetEnabled(string id, bool enabled)
+        {
+            foreach (var item in Items)
+            {
+                if (item.Feature.FeatureId != id) continue;
+                try { item.Feature.SetEnabled(enabled); return true; }
+                catch (Exception e) { Plugin.Logger.LogWarning("[FEATURES] " + id + " request failed: " + e.GetType().Name + ": " + e.Message); return false; }
+            }
+            return false;
+        }
+    }
+}
```

**注解**：按 `FeatureId` 找到插件并调用 `SetEnabled`。
**关键安全属性**：它只调用契约里定义的 `SetEnabled`，不会调用任意方法。
插件抛异常时被捕获并返回 `false`，由调用方（UI）记 warning。

**行尾：LF（99 行全部为 LF）。**

---

## §5 `src/bepinex_plugin/LocalModManager.csproj`　+6 / −2

```diff
@@ -8,16 +8,15 @@
     <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
     <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
     <EnableDefaultCompileItems>true</EnableDefaultCompileItems>
+    <DefaultItemExcludes>$(DefaultItemExcludes);abstractions\**\*.cs</DefaultItemExcludes>
     <!-- P0: no debug metadata, so the assembly carries no build-machine path that
          would make `grep -ri <codename> BepInEx\plugins\` match. Flip to `portable` when
          you need line numbers in stack traces during development. -->
     <DebugType>none</DebugType>
   </PropertyGroup>
-
   <PropertyGroup>
     <G Condition="'$(G)' == ''">C:\Program Files (x86)\Steam\steamapps\common\WorldApart</G>
   </PropertyGroup>
-
   <ItemGroup>
     <Reference Include="BepInEx.Core">
       <HintPath>$(G)\BepInEx\core\BepInEx.Core.dll</HintPath>
@@ -119,4 +118,9 @@
       <Private>false</Private>
     </Reference>
   </ItemGroup>
+  <ItemGroup>
+    <ProjectReference Include="abstractions\LocalModManager.Abstractions.csproj">
+      <Private>true</Private>
+    </ProjectReference>
+  </ItemGroup>
 </Project>
```

**注解**：这段是**本次改动里最容易出错的地方**，三处要点：

1. **`DefaultItemExcludes` 是必需的**。主工程开着 `EnableDefaultCompileItems=true`，若不排除，
   `abstractions/` 下的源码会被**同时编进两个程序集**——契约类型被定义两次，
   「插件引用独立契约程序集」的设计就静默失效了。
   本次审计已用 `msbuild -getItem:Compile` 实测确认排除生效（编译项只剩
   `Plugin.cs` / `DebugActions.cs` / `ReadOnlyTest.cs` / `WriteTest.cs`）。

   ⚠️ 该写法用了反斜杠 `\`。Windows 正常，MSBuild 在其他平台通常也会规范化，
   但惯用写法是 `/`。

2. **`<Private>true</Private>`** 让契约 DLL 复制到主工程输出目录。
   实测产物中确实同时出现 `LocalModManager.dll` 与 `LocalModManager.Abstractions.dll`。

3. **删掉了两处空行**（`-` 两行）——纯格式改动，无功能影响。

**行尾：CRLF（与文件原有风格一致）。**

---

## §6 `src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs`（新增 15 行）

```diff
@@ -0,0 +1,15 @@
+namespace LocalModManager.Abstractions
+{
+    public enum FeaturePluginState { Stopped, Starting, Running, Stopping, Failed }
+    public interface IManagedFeaturePlugin
+    {
+        string FeatureId { get; }
+        string DisplayName { get; }
+        string Description { get; }
+        string FeatureVersion { get; }
+        bool DesiredEnabled { get; }
+        FeaturePluginState State { get; }
+        string StatusMessage { get; }
+        void SetEnabled(bool enabled);
+    }
+}
```

**注解**：整个 PR 的契约本体，**纯声明、零实现、零逻辑**。
只有 7 个只读属性 + 1 个方法。`SetEnabled` 是唯一的「下行」通道。

注意设计取向：**「用户意图」（`DesiredEnabled`）与「运行状态」（`State`）分开表达**，
因此可以表示「用户想开，但启动失败了（`Failed`）」这种中间态。

**行尾：LF。**

---

## §7 `src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj`（新增 11 行）

```diff
@@ -0,0 +1,11 @@
+<Project Sdk="Microsoft.NET.Sdk">
+  <PropertyGroup>
+    <TargetFramework>net6.0</TargetFramework>
+    <AssemblyName>LocalModManager.Abstractions</AssemblyName>
+    <RootNamespace>LocalModManager.Abstractions</RootNamespace>
+    <Version>1.0.0</Version>
+    <Nullable>disable</Nullable>
+    <ImplicitUsings>disable</ImplicitUsings>
+    <DebugType>none</DebugType>
+  </PropertyGroup>
+</Project>
```

**注解**：`net6.0` 与主工程一致；`DebugType=none` **保留**，
符合本仓库「DLL 内不得残留构建机路径」的硬约束 ✅。
`ImplicitUsings=disable` 是正确的选择（避免依赖 SDK 默认 using，保持契约可移植）。

**行尾：LF。**

---

## §8 `src/bepinex_plugin/Plugin.cs`　+83 / −8

这是唯一有逻辑改动的既有文件，17 个 hunk。按功能分组如下。

### 8.1 引入命名空间与给行结构加字段

```diff
@@ -1,5 +1,6 @@
 using System;
 using System.Collections.Generic;
+using LocalModManager.Abstractions;
 using System.IO;
 using System.Text;
 using BepInEx;
@@ -970,6 +971,7 @@ private class Row
             internal int Npc;
             internal bool On;
             internal bool IsPackage;   // master switch for the whole package
+            internal string FeatureId;
             internal int Kind;         // 1 = package header, 2 = unit row
             internal string PkgOf;     // unit row -> owning package ModId
             internal RectTransform RootRt;
```

**注解**：`Row` 新增 `FeatureId`，用于把 UI 行关联到具体功能插件。

### 8.2 挂入每秒刷新节流

```diff
@@ -1471,6 +1473,8 @@ internal static void Tick()
                     if (Time.unscaledTime >= _refreshAt)
                     {
                         _refreshAt = Time.unscaledTime + 1f;
+                        FeaturePluginRegistry.Refresh();
+                        AddMissingFeatureRows();
                         RefreshRows();
                     }
                 }
```

**注解**：`FeaturePluginRegistry.Refresh()` 就在这里被每秒调用一次
（性能提示见 §4.2）。

### 8.3 布局高度按行类型区分

```diff
@@ -1509,7 +1513,7 @@ private static void Relayout()
                     var row = _rows[i];
                     if (row == null || row.RootRt == null) continue;
                     if (!row.RootRt.gameObject.activeSelf) continue;
-                    float rowH = row.Kind == 1 ? _pkgRowH : _rowHeight;
+                    float rowH = row.Kind == 1 ? _pkgRowH : row.Kind == 4 ? FeatureRowHeight : _rowHeight;
                     float indent = row.Kind == 2 ? 40f : 0f;
                     var rt = row.RootRt;
                     rt.anchorMin = new Vector2(0f, 1f);
```

**注解**：嵌套三元。功能行（`Kind == 4`）用更高的行高（`max(_rowHeight*1.6, 96)`），
因为要显示两行文字。

### 8.4 构建页面时初始化

```diff
@@ -1942,6 +1946,8 @@ private static bool Build(Game.UI.UPFLogic.Settings.SettingsPanel panel)
             // ---- rows: stacked vertically from the template row's slot ----
             _rowHeight = rowTemplate.rect.height > 10f ? rowTemplate.rect.height : 60f;
             _nextRowY = rowTemplate.anchoredPosition.y;
+            FeaturePluginRegistry.Refresh();
+            _featureHeaderAdded = false;
 
             var mb = ManagerBehaviour.Instance;
             if (mb == null) Plugin.Logger.LogWarning("[MODPAGE] ManagerBehaviour not up yet");
@@ -1965,6 +1971,8 @@ private static bool Build(Game.UI.UPFLogic.Settings.SettingsPanel panel)
                 }
             }
 
+            AddMissingFeatureRows();
+
             // The scrollbar is built LAST on purpose: it must be the topmost child of
             // the page, otherwise the opaque Bg / HeaderBg (both created earlier) draw
             // over its top band - which cut the handle in half and hid the up arrow.
```

**注解**：重建页面时先刷新插件表、重置分区标题标记，
并在原生 MOD 行建完之后调用 `AddMissingFeatureRows()`。

注意 `AddMissingFeatureRows()` 插在**滚动条之前**——上部注释说明滚动条必须最后建
（否则会被不透明背景盖住）。新代码遵守了这个顺序 ✅。

### 8.5 新增功能行构建逻辑（核心）

```diff
@@ -2143,6 +2151,46 @@ private static TMPro.TextMeshProUGUI MakeLabelAt(Transform parent, string name,
             return label;
         }
 
+        private static bool _featureHeaderAdded;
+        private static float FeatureRowHeight { get { return Mathf.Max(_rowHeight * 1.6f, 96f); } }
+
+        private static void AddMissingFeatureRows()
+        {
+            bool added = false;
+            foreach (var item in FeaturePluginRegistry.Current)
+            {
+                bool exists = false;
+                foreach (var existingRow in _rows) if (existingRow != null && existingRow.Kind == 4 && existingRow.FeatureId == item.Feature.FeatureId) { exists = true; break; }
+                if (exists) continue;
+                if (!_featureHeaderAdded)
+                {
+                    var header = NewRowRoot("FeaturePluginSection", _rowHeight);
+                    MakeLabel(header, "Text", "BepInEx 功能插件", _rowLabelStyle, 24f);
+                    header.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
+                    _rows.Add(new Row { Kind = 3, RootRt = header });
+                    _featureHeaderAdded = true;
+                }
+                var rt = NewRowRoot("FeaturePluginRow", FeatureRowHeight);
+                string text = FeatureLabel(item.Feature);
+                var label = MakeLabel(rt, "Text", text, _rowLabelStyle, 140f);
+                if (label != null) label.enableWordWrapping = true;
+                var row = new Row { Kind = 4, FeatureId = item.Feature.FeatureId, Label = label, LabelText = text, On = item.Feature.DesiredEnabled, RootRt = rt };
+                AddSwitch(rt, row);
+                added = true;
+            }
+            if (added) Relayout();
+        }
+
+        private static string FeatureLabel(IManagedFeaturePlugin feature)
+        {
+            string description = feature.Description ?? "";
+            string status = feature.StatusMessage ?? "";
+            string detail = status.Length > 0 ? status : description;
+            if (detail.Length > 100) detail = detail.Substring(0, 97) + "...";
+            return (feature.DisplayName ?? feature.FeatureId) + "  v" + (feature.FeatureVersion ?? "?")
+                + "  [" + feature.State + "]\n" + detail;
+        }
+
         private static void AddUnitRow(string modId, string unitId, int npc, string typeName)
         {
             var rowRt = NewRowRoot("ModUnitRow", _rowHeight);
```

**注解**（逐点核查结论）：

| 核查点 | 结论 |
|---|---|
| `header.GetComponent<UnityEngine.UI.Image>()` 会不会空引用 | ✅ 安全。`NewRowRoot()` 内部**无条件** `AddComponent<Image>()` |
| `MakeLabel` 第 5 参数 | ✅ 名为 `rightInset`。传 `24f`（标题）与 `140f`（功能行，给右侧开关留位），语义正确 |
| `FeatureLabel` 截断是否越界 | ✅ 安全。先判 `Length > 100` 再 `Substring(0, 97)` |
| 分区标题是否会被误点 | ✅ 不会。`MakeLabel` 内部已设 `raycastTarget = false`，这里再显式设一次背景图的 |
| 重复添加保护 | ✅ 有。按 `FeatureId` 查重后才新增 |
| 行只增不减 | ⚠️ 确认存在。插件消失后不会移除残留行（实际概率低，仅记录） |

标题文字 `"BepInEx 功能插件"` 是硬编码中文——与页面其余部分一致 ✅。

### 8.6 点击分发

```diff
@@ -2237,6 +2285,12 @@ private static void OnRowClicked(Row row)
         {
             try
             {
+                if (row.Kind == 4)
+                {
+                    if (!FeaturePluginRegistry.SetEnabled(row.FeatureId, !row.On))
+                        Plugin.Logger.LogWarning("[FEATURES] no feature accepted request: " + row.FeatureId);
+                    return;
+                }
                 row.On = !row.On;
                 var mb = ManagerBehaviour.Instance;
                 if (mb != null)
```

**注解**：功能行的点击**提前返回**，完全不走原生 MOD 包的开关逻辑——两个子系统互不干扰 ✅。

### 8.7 状态刷新（三处）

```diff
@@ -2296,7 +2350,18 @@ private static void RefreshRows()
                 {
                     if (row.Label != null && row.Label.text != row.LabelText)
                         row.Label.text = row.LabelText;
-                    if (mb != null)
+                    if (row.Kind == 4)
+                    {
+                        foreach (var item in FeaturePluginRegistry.Current)
+                        {
+                            if (item.Feature.FeatureId != row.FeatureId) continue;
+                            row.On = item.Feature.DesiredEnabled;
+                            string label = FeatureLabel(item.Feature);
+                            if (row.Label != null && row.Label.text != label) row.Label.text = label;
+                            break;
+                        }
+                    }
+                    else if (mb != null)
                         row.On = row.IsPackage
                             ? PackageAllOn(mb, row.ModId)
                             : mb.EntryEnabled(row.ModId, row.UnitId, row.Npc);
```

**注解**：刷新时**功能行的开关状态取自插件自己上报的 `DesiredEnabled`**，
而不是本地缓存的 `row.On`。设计正确——状态的唯一真相在插件侧。

### 8.8 启动 5 秒后的一次性扫描与日志

```diff
@@ -3957,6 +4022,7 @@ public class ManagerBehaviour : MonoBehaviour
             Environment.GetEnvironmentVariable("MOD_PAGE_SELFTEST") == "1";
 
         private bool _scanTried;
+        private bool _featureScanTried;
         private float _selfCheckAt = -1f;
         private int _checkRounds;
         private bool _revDone;
@@ -4043,6 +4109,14 @@ private void Update()
                 if (Input.GetKeyDown(KeyCode.F10)) _show = !_show;
                 if (Input.GetMouseButtonDown(0)) SettingsEntryHook.NoteMouseDown();   // P1 gate source
                 SettingsEntryHook.Maintain();
+                if (!_featureScanTried && Time.time > 5f)
+                {
+                    _featureScanTried = true;
+                    FeaturePluginRegistry.Refresh();
+                    var features = new List<string>();
+                    foreach (var item in FeaturePluginRegistry.Current) features.Add(item.Feature.FeatureId);
+                    Plugin.Logger.LogInfo("[FEATURES] discovered " + features.Count + " managed feature plugin(s): " + string.Join(", ", features));
+                }
                 if (TabDiag.Enabled) TabDiag.Tick();                                  // MOD_TAB_DIAG input probe
 
                 if (!_scanTried && Time.time > 5f)
```

**注解**：与既有的 `_scanTried` 模式保持一致，只跑一次，打一条汇总日志。

### 8.9 三处调用点跟随签名重构

```diff
@@ -1950,7 +1956,7 @@ private static bool Build(...)
-                foreach (var p in mb.CurrentSnapshot())
+                foreach (var p in ManagerBehaviour.CurrentSnapshot(mb))
@@ -2244,7 +2298,7 @@ private static void OnRowClicked(Row row)
-                        foreach (var p in mb.CurrentSnapshot())
+                        foreach (var p in ManagerBehaviour.CurrentSnapshot(mb))
@@ -2269,7 +2323,7 @@ private static bool PackageAllOn(ManagerBehaviour mb, string modId)
-                foreach (var p in mb.CurrentSnapshot())
+                foreach (var p in ManagerBehaviour.CurrentSnapshot(mb))
@@ -4474,10 +4548,11 @@ private void Apply(string modId, string unitId, int npcId, bool enabled)
         // ---- internal API for the settings-page MOD manager (S2) ----
 
         /// <summary>Throttled package snapshot, shared with the IMGUI window.</summary>
-        internal List<ModPackage> CurrentSnapshot()
+        internal static List<ModPackage> CurrentSnapshot(ManagerBehaviour instance)
         {
-            RefreshSnapshotIfDue();
-            return _snap;
+            if (instance == null) return new List<ModPackage>();
+            instance.RefreshSnapshotIfDue();
+            return instance._snap;
         }
```

**注解**：`CurrentSnapshot` 由实例方法改为**静态**，并新增空值保护。

**这处重构对本功能并非必需**（原来的 `mb.CurrentSnapshot()` 本来就能工作），
却是 diff 里风险最高的改动——**因为它改的是既有 API 签名**。
已验证安全性：全仓库检索 `CurrentSnapshot` 仅 4 处命中（3 处调用 + 1 处定义），
**全部在 `Plugin.cs` 内且已被本 PR 一并更新**；`DebugActions.cs` / `WriteTest.cs` /
`ReadOnlyLoad.cs` 均不引用该成员，**不会编译失败**。

---

## §9 行尾（EOL）分析 —— 本次审计发现的**实际缺陷**

这是把 diff 落到字节层面复核时才暴露出来的问题。

### 9.1 实测数据

| 文件 | base 行尾 | PR head 行尾 | 判定 |
|---|---|---|---|
| `README.md` | CRLF | CRLF（63 CRLF / 0 LF-only） | ✅ 一致 |
| `src/bepinex_plugin/LocalModManager.csproj` | CRLF | CRLF（126 / 0） | ✅ 一致 |
| **`src/bepinex_plugin/Plugin.cs`** | **CRLF（4612 / 0）** | **CRLF 4676 + 11 行纯 LF** | ❌ **混合** |
| `src/bepinex_plugin/FeaturePluginRegistry.cs`（新） | — | **LF（0 / 99）** | ⚠️ 与既有 `.cs` 不一致 |
| `src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs`（新） | — | **LF（0 / 15）** | ⚠️ 同上 |
| `src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj`（新） | — | **LF（0 / 11）** | ⚠️ 与既有 `.csproj` 不一致 |
| `docs/BepInEx-Feature-Plugin-API.md`（新） | — | **LF（0 / 22）** | ⚠️ 与多数既有 md 不一致 |

### 9.2 问题一：`Plugin.cs` 被写成混合行尾

base 是**纯 CRLF**（4612 行 CRLF、0 行纯 LF）。PR head 变成
**4676 CRLF + 11 行纯 LF** —— 新增的 91 行里有 **11 行是 LF 结尾**，其余是 CRLF。

这 11 行集中在 `AddMissingFeatureRows()` 与 `FeatureLabel()` 附近的几条长语句上，
例如：

```csharp
float rowH = row.Kind == 1 ? _pkgRowH : row.Kind == 4 ? FeatureRowHeight : _rowHeight;   // ← LF
var rt = NewRowRoot("FeaturePluginRow", FeatureRowHeight);                                // ← LF
var label = MakeLabel(rt, "Text", text, _rowLabelStyle, 140f);                            // ← LF
string detail = status.Length > 0 ? status : description;                                 // ← LF
```

**后果**：C# 编译器不在乎，能正常编译（已实测 0 warning / 0 error）。
但本工程对行尾有明确要求，且**此前正因为行尾问题吃过亏**——
批次 25 曾修复 9 个文件被错误规范化成 LF 的问题。
一个「既不是 CRLF 也不是 LF」的源文件，会让后续任何编辑器/工具的处理结果不可预测，
也会让 `git diff` 产生噪声。

### 9.3 问题二：新增文件统一用 LF，与仓库既有约定不符

仓库里既有的 `.cs`（`Plugin.cs`、`DebugActions.cs`、`WriteTest.cs`、`ReadOnlyLoad.cs`）
与 `.csproj`（`LocalModManager.csproj`、`FrameProbe.csproj`）**都是 CRLF**，
本次新增的 4 个文件**全部是 LF**。同一目录下两种风格并存。

### 9.4 该发现的验证方式

`git apply` 用 `-c core.autocrlf=false` 应用本 diff 后，
7 个文件的 blob SHA 与 PR head 报出的值**完全一致**——
说明上面的行尾数据是 PR 的**真实字节**，不是 diff 渲染或本机转换产生的假象。

### 9.5 建议处置

| 选项 | 说明 |
|---|---|
| A. 保持现状 | 能编译、无功能影响，但留下一个混合行尾的源文件与 4 个风格不一致的新文件 |
| B. 统一为 CRLF | 把 `Plugin.cs` 的 11 行 LF 与 4 个新文件全部规范为 CRLF，与仓库既有风格一致 |
| C. 统一为 LF | 与 B 相反，需要连带规范既有文件，改动面大 |
| D. 落 `.gitattributes` | 用 `* text=auto` / 显式声明把行尾交给 git 管理，一劳永逸 |

**推荐 A + D**：不动贡献者的既有提交，另加一个 `.gitattributes` 固定后续行为
（并提示贡献者后续 PR 遵守）。**该处置需委托方决策，本次未擅自改动。**

---

## §10 复现与验证命令

```bash
# 1) 取回 diff（公开仓库，无需认证）
curl -sL -H "Accept: application/vnd.github.v3.diff" \
  https://api.github.com/repos/scwunai/WorldApart-ModManager-Public/pulls/1 \
  -o PR1_havoc123.diff

# 2) 导出 base 树（注意：base 是合并前的 a7d317f）
git archive a7d317fef2e09c686b33fbc293c9be787f7f9dd8 | tar -x -C /tmp/pr1base

# 3) 应用（必须关掉 autocrlf，否则新建文件会被写为 CRLF）
cd /tmp/pr1base
git -c core.autocrlf=false apply --verbose /path/to/PR1_havoc123.diff

# 4) 校验：7 个文件的 git blob SHA 应与下表一致
git hash-object README.md
#   b90493409342...
```

**head 端 blob SHA 对照表**（用于校验）：

| 文件 | blob SHA |
|---|---|
| `README.md` | `b90493409342…` |
| `docs/BepInEx-Feature-Plugin-API.md` | `e0d5fd5508b3…` |
| `src/bepinex_plugin/FeaturePluginRegistry.cs` | `92cde1af9fdb…` |
| `src/bepinex_plugin/LocalModManager.csproj` | `6432fb8c45aa…` |
| `src/bepinex_plugin/Plugin.cs` | `b028daa16df8…` |
| `src/bepinex_plugin/abstractions/IManagedFeaturePlugin.cs` | `5ce39105e187…` |
| `src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj` | `f1c2918b71c4…` |

**编译验证**（在应用后的树上）：

```bash
"%USERPROFILE%/.dotnet/dotnet.exe" build src/bepinex_plugin/LocalModManager.csproj \
  -c Release -p:G=<G>
# 预期：0 警告 0 错误
# 产物：LocalModManager.dll 143,360 B + LocalModManager.Abstractions.dll 4,096 B
```

---

## §11 小结

| 维度 | 结论 |
|---|---|
| 改动规模 | 7 文件 / +240 −10，其中新增文件 4 个、既有文件改动 3 个 |
| 逻辑复杂度 | 低。契约是纯声明；发现器是防御性反射；UI 改动模式与同类既有代码一致 |
| 安全 | ✅ 无网络、无进程、无文件写入、无动态加载程序集、无新增依赖 |
| 编译 | ✅ 0 warning / 0 error（独立复现） |
| 既有功能 | ✅ 未发现回归；`CurrentSnapshot` 签名重构的所有调用点已完整覆盖 |
| **行尾** | ❌ **`Plugin.cs` 混合行尾（11 行 LF）+ 4 个新文件用 LF 而仓库既有为 CRLF** |
| 其它 | 功能行只增不减；`Refresh()` 每秒跨 154 个程序集做类型查找；`DefaultItemExcludes` 用反斜杠 |

**结论：功能与安全无阻断问题，已合并（`a760ada`）。行尾问题建议单独处置（§9.5）。**

---

*本文件中的 diff 为 GitHub diff API 原文的可读化呈现（行尾统一显示为 LF）；
精确字节以同目录的 `PR1_havoc123.diff` 为准，该文件已验证可逐字节重现 PR head。*
