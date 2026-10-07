# MOD 管理页滚动修复 实施报告

- 任务来源：`KIMI\MOD页滚动修复_实施计划.md`（+ 追加需求：MOD 列表专属滚动条）
- 完成时间：2026-10-07 02:15（本地）
- 基线源码：`KIMI\bepinex_plugin\Plugin.cs`（工作区当前状态，前缀构建戳 `20261006-1838`）
- 修复后构建戳：`20261007-0130 scroll-fix`
- 平台/游戏：《不问凡尘》Steam appid 4209920，Unity 2022.3.43f1，IL2CPP，BepInEx 6.0.0-be.788
- 原始证据存档：`KIMI\acceptance\`（侦察摘录、验收运行日志、ErrorLog）
- 截图：`KIMI\shots_FINAL\`、`KIMI\shots_F3B\`

---

## 0. 结论速览（对照验收标准 A1–A7）

| # | 项 | 结果 | 证据位置 |
|---|---|---|---|
| A1 | 造 8+ 假包使行数 > 视口容量 | **达成** | §6.1，`rows=28` |
| A2 | 滚轮滚到底能看到最后一行、裁剪正确 | **机制达成，最终视觉由委托方确认** | §6.2；自动化只能给出日志与截图，委托方已在游戏内实测滚轮可用 |
| A3 | 滚动全程设置页不变、MOD 页不被关闭 | **达成** | §6.3，滚动阶段 `native page switch detected` = 0 次 |
| A4 | 点击左侧原生标签仍关闭 MOD 页并切页 | **达成** | §6.4，点击后计数 0 → 1 |
| A5 | 展开/收起后重排正确、滚动位置合理 | **部分达成（见 §7.1）**：实现与 clamp 逻辑就位、滚轮 clamp 有日志证据；"点击行收起/展开"的自动化点击未能命中行按钮，该条的视觉确认留给委托方 |
| A6 | 帧性能无回归（p95 偏差 ≤5%） | **达成** | §6.6，p95 6.256ms → 6.256ms，偏差 0.00% |
| A7 | 部署 DLL 与构建产物 MD5 一致；日志 0 Error | **达成** | §6.7，两者同为 `28c41bb9…`，`[Error]` 0 行 |

---

## 1. 交付物与部署记录

### 1.1 源码改动（仅 1 个文件）

| 文件 | 改动 |
|---|---|
| `KIMI\bepinex_plugin\Plugin.cs` | `ModPageController` 内新增 Content 容器 / 原生 ScrollRect / MOD 滚动条 / clamp 逻辑；新增 `NativeWheelConsumerHook`（Harmony prefix）；`Plugin.Load` 增加该 hook 的独立装配；`ManagerBehaviour` 增加 `MOD_PAGE_SELFTEST` 自测开关 |

侦察用的临时文件 `KIMI\bepinex_plugin\ScrollRecon.cs` 已在发布构建前**从工程中移除**，源码留档于
`KIMI\recon_archive\ScrollRecon.cs`（不在工程目录内，不参与编译）。发布产物中已无 `ScrollRecon`、
`ModScrollWheelHandler`、`RECON` 等侦察符号（二进制串扫描验证，见 §6.7）。

### 1.2 构建

```
cd KIMI\bepinex_plugin
dotnet build -c Release
```

`net6.0`，`DebugType=none`（保持原有约定：产物内不含构建机路径）。0 warning / 0 error。

### 1.3 部署

| 项 | 路径 | MD5 |
|---|---|---|
| 部署前备份 | `KIMI\backup_deployed_pre_scrollfix.dll` | `c2c410757d52b475c8df778a5ab663fc` |
| 构建产物 | `KIMI\bepinex_plugin\bin\Release\LocalModManager.dll` | `28c41bb93bb48ff7ac11674d81d18039` |
| 部署位置 | `BepInEx\plugins\LocalModManager.dll` | `28c41bb93bb48ff7ac11674d81d18039` |

- **只更新了 `BepInEx\plugins\LocalModManager.dll` 一个文件。** 游戏目录内 6 小时内被写过的其它文件只有
  `BepInEx\LogOutput.log`、`BepInEx\ErrorLog.log`、`debug.log`，这三个都是游戏/BepInEx/Steam 自己写的运行日志。
- A1 的假 MOD 包写在**用户数据目录**（`%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods`），不是游戏目录。
- 命名纪律：产物内 **ASCII/UTF-8 大小写不敏感检索 `kimi` = 0 处**；UTF-16 检索命中 **1 处**，
  即既有代码里 `KIMI\v5_q1_recon.txt` 这个调试转储路径（正文常量就是这个长度，见 §7.3）。
  本次改动**没有新增任何含 "kimi" 的字符串/日志/文件名**。

---

## 2. 侦察 R1：滚轮为什么切回原生设置页

### 2.1 命中方法（类名 + RVA + 可拦截切入点）

滚轮事件的真正入口是 **uGUI 自己的** `UnityEngine.UI.ScrollRect.OnScroll(PointerEventData)`
（`UnityEngine.UI.dll`）；真正"切页"的逻辑在游戏侧的两个方法上（RVA 取自 `KIMI\il2cpp_out\dump.cs`）：

| 方法 | RVA | File Offset | VA |
|---|---|---|---|
| `Game.UI.UPFLogic.Settings.SettingsScrollController.OnScrolled(Vector2)` | `0x1C6B500` | `0x1C6A300` | `0x181C6B500` |
| `Game.UI.UPFLogic.Settings.SettingsPanelViewModel.OnViewportTopPageChanged(RectTransform)` | `0x1C630E0` | `0x1C61EE0` | `0x181C630E0` |
| `Game.UI.UPFLogic.Settings.SettingsPanelViewModel.set_ActivePage(string)` | `0x1C69250` | `0x1C68050` | `0x181C69250` |
| `SettingsScrollController.Configure(RectTransform[], Action<RectTransform>)` | `0x1C6B2B0` | `0x1C6A0B0` | — |
| `SettingsScrollController.SmoothScrollTo(RectTransform)` | `0x1C6B780` | `0x1C6A580` | — |

关键结构事实（dump.cs TypeDefIndex 5140）：`SettingsScrollController : MonoBehaviour` 持有
`private ScrollRect _scroll;`、`private Action<RectTransform> _onViewportTopPageChanged;`。
运行时确认该 MonoBehaviour 就挂在 **`Frame#pageShell`** 上（见证据 §R2 第一行）。

### 2.2 调用链（运行时栈实测，非推断）

```
UnityEngine.UI.ScrollRect.OnScroll(PointerEventData)          ← 滚轮入口（本插件页面被射线命中，事件却向上冒到它）
  └─ ScrollRect.onValueChanged
     └─ SettingsScrollController.OnScrolled(Vector2)          ← RVA 0x1C6B500
        └─ SettingsPanelViewModel.OnViewportTopPageChanged(RectTransform)   ← RVA 0x1C630E0
           └─ SettingsPanelViewModel.set_ActivePage(string)   ← RVA 0x1C69250
              └─ ModPageController.Tick() 发现 ActivePage 变了
                 └─ "[MODPAGE] native page switch detected … closing" → MOD 页被自动关闭
```

栈帧原文（逐字摘录见 `KIMI\acceptance\recon_R1R2_evidence.md` §1）：

```
[RECON] [R1] VM.set_ActivePage -> 'display' #3 t=41.63 <- ?.DMD<…SettingsPanelViewModel::set_ActivePage>
        <- … <- ?.DMD<…SettingsPanelViewModel::OnViewportTopPageChanged>
[RECON] [R1] VM.OnViewportTopPageChanged fired #3 page=Frame#pageDisplay oldActivePage='display'
        <- … <- ?.DMD<…SettingsScrollController::OnScrolled> <- ?.(il2cpp -> managed) OnScrolled
[RECON] [R1] SettingsScrollController.OnScrolled fired #3 vNorm=0.5889 contentAp=(0, 433) t=41.63
[MODPAGE] native page switch detected ('system' != 'audio'), closing
[MODPAGE] hidden
```

同一轮里记录到**射线命中就是本插件自己的节点**，事件仍然落到游戏的 ScrollRect 上，这就是根因的直证：

```
[RECON] [R1] ScrollRect.OnScroll #3 sr='Frame#pageShell' … delta=(0.00, -720.00)
        raycastHit=Bg path=/PopUpLayer/SettingsPanel(Clone)/Frame#modal/Frame#3/Frame#pageShell/Viewport/MODPage/Bg
[RECON] [R1] wheel consumer (ExecuteHierarchy from 'Bg') = Frame#pageShell      ← 修复前
[RECON] [R1] wheel consumer (ExecuteHierarchy from 'Bg') = MODPage             ← 修复后
```

结论：MOD 页的 Bg 虽然挡住了 uGUI 射线命中别的节点，但它自己不实现 `IScrollHandler`，
uGUI 的 `ExecuteEvents.ExecuteHierarchy` 会**沿着父链继续往上找**，一路找到 Viewport 之上的
游戏设置 ScrollRect，滚轮因此被游戏消费 → 原生页滚动 → 顶部页变化 → `ActivePage` 改变 → MOD 页被关闭。
**可 Harmony 拦截的切入点 = `UnityEngine.UI.ScrollRect.OnScroll`**（只挡滚轮，见 §4.3）。

---

## 3. 侦察 R2：裁剪与滚动几何

| 项 | 实测值 | 说明 |
|---|---|---|
| `Viewport` 是否有裁剪组件 | **`RectMask2D = True`**（`Mask = False`） | 所以 **F1 不需要再补 mask**：MOD 页是 Viewport 的子节点，超出行由游戏自己的 RectMask2D 裁掉 |
| Viewport 尺寸 | `972 x 492`（`ap=(0,0)`，父 `Frame#pageShell` 同为 `972x492`） | MOD 页 `CopyRect` 了它，所以页可视高 = 492 |
| 游戏 ScrollRect | 在 `Frame#pageShell`：`h=False v=True mode=Clamped inertia=False sens=0.6`，`viewport='Viewport'`，`content='Content#0'`（`972x1546`） | 原生页内容住在这个 content 里 |
| 画布 | `ScreenSpaceOverlay sort=3000` | 屏幕坐标换算用不到 worldCamera |
| 原生滚动条 | `Frame#pageShell/Scrollbar Vertical`，`12x492`，anchors `(1,0)-(1,1)`，pivot `(1,1)`，track sprite `scrollbar-track-bg`，handle sprite `scrollbar-progress-thumb` | 作为 MOD 滚动条的素材模板（见 §4.2） |

MOD 页行几何（`[MODPAGE] diag`）：页 `ap=(0,0) size=972x492`；`row0 ap=(0,-52) size=956x100`。
即首行从页顶往下 52px 起排，可用高度 440px；包头行高 100，单元行高 64 → **视口约只能容纳 4.3 个包头行**。

---

## 4. 实现

### 4.1 F1：`Content` 容器 + 原生 ScrollRect 作滚轮吸收层

结构（在 `_pageRt` 之下）：

```
Viewport（游戏，带 RectMask2D）
└── MODPage（_pageRt，CopyRect 自 Viewport，972x492）
    ├── Bg              （不透明底，raycastTarget=true）
    ├── Content         （新增：行的父节点；anchor (0,1)-(1,1)，pivot(0.5,1)，sizeDelta.y = 堆叠总高）
    │    └── 各行（坐标数学一字未改）
    ├── HeaderBg        （新增：盖住 header 那条 52px 的不透明条，避免滚动行与标题相撞）
    ├── Frame#0         （标题行）
    └── ModScrollbar    （新增：MOD 列表专属滚动条，最后创建 = 画在最上层）
```

- 页根挂 **原生 `UnityEngine.UI.ScrollRect`**，`content = Content`，`horizontal=false`、`vertical=true`、
  `movementType=Clamped`、`inertia=false`、`scrollSensitivity=(行高+2)/120`。
- 为什么不用计划里写的自定义 `ModScrollWheelHandler : MonoBehaviour, IScrollHandler`：
  Il2CppInterop 把 il2cpp 接口反接口化成**类**（`class X : MonoBehaviour, IScrollHandler` 直接编译不过，
  CS1721），只能通过 `RegisterTypeOptions.Interfaces` 注册。**实测该方式会被 ExecuteEvents 找到并吞掉事件、
  但托管方法不会被调用**：那一轮 `wheel consumer = MODPage`（事件被吞）却没有任何滚动日志，
  原生页也不再滚动 —— 即"事件被吞掉但什么也没发生"。改用原生 ScrollRect 后既有正确的滚动语义
  （clamp、拖拽滚动），又不需要任何接口注入。这是与计划的一处**有意偏差**，取舍理由即上述实测。
- **行堆叠数学零改动**：`Relayout` 里行的 `anchorMin/Max`、`pivot`、`sizeDelta`、`anchoredPosition`
  计算与修复前逐字相同，只把 `NewRowRoot` 的父节点从 `_pageRt` 换成 `_contentRt`：

  ```csharp
  - go.transform.SetParent(_pageRt, false);
  + go.transform.SetParent(_contentRt != null ? (Transform)_contentRt : (Transform)_pageRt, false);
  ```

  因为 `Content` 的顶边与页顶重合（`pivot=(0.5,1)`、`anchoredPosition=(0,0)`），首行仍在 `y = -52`，
  收起/展开的行位移逻辑完全没动。`Relayout` 里新增的两行只是**读取**它算出的终值：

  ```csharp
  _contentHeight = top;   // 只读，不参与行坐标计算
  SyncContentSize();
  ```

### 4.2 MOD 列表专属滚动条

- 需求来源：委托方在游戏内实测后追加（"现在的滚动条是原生三个设置的，需要一个 MOD 列表专属的滚动条"）。
- 素材来源：游戏自己的 `Frame#pageShell/Scrollbar Vertical`（几何 + `scrollbar-track-bg` /
  `scrollbar-progress-thumb` 两个 sprite + 上下箭头 `scrollbarArrowUp/Down`），
  **只复制 RectTransform 数值与 sprite/色值，不克隆任何游戏节点**（沿用本页既有约定：克隆节点会带上
  UPF 元素身份，被布局引擎每帧重置）。
- 两个实测出来的坑（都已修掉，且是委托方在游戏内点出来的）：

  1. **方向反了**：游戏那个节点的 `direction=TopToBottom`，但它的 `ScrollRect.verticalScrollbar` 是 `null`
     （实测 `native scrollbar via sibling lookup of the Viewport` —— 只能按节点找，拿不到 ScrollRect 引用），
     说明那个值从没被 ScrollRect 驱动过。Unity 的 ScrollRect 顶部写 `value=1`，
     只有 `BottomToTop` 才把滑块画在顶部。照抄 `TopToBottom` 的结果就是上下颠倒 → 改为
     `Direction.BottomToTop`。
  2. **顶部被盖住**：滚动条原本在 `Bg` / `HeaderBg` 之前创建，绘制顺序在后 → 滚到最顶时滑块上半截和顶部三角形
     被那两条不透明条盖掉。改为在 `Build` 最后创建（最上层子节点）。
- 同一时刻只允许有一个竖滚动条在屏：MOD 页可见期间把游戏自己的滚动条 `SetActive(false)` 并临时把
  `Frame#pageShell.ScrollRect.verticalScrollbar` 置空（否则它的 AutoHide 会把节点重新点亮），
  `Hide()` 时把引用与 active 状态**原样恢复**。
- `verticalScrollbarVisibility = AutoHide`：行数不足一屏时滚动条自动隐藏。

### 4.3 F2：只挡滚轮、不挡点击

```csharp
static class NativeWheelConsumerHook
{
    static bool Prefix(UnityEngine.UI.ScrollRect __instance, PointerEventData data)
    { return !ModPageController.ConsumeNativeWheel(__instance, data); }
}
```

- 目标方法就是 R1 定位到的切入点 `UnityEngine.UI.ScrollRect.OnScroll`，仅此一个方法。
- `ConsumeNativeWheel` 的放行条件很窄：`_visible == true` 且 射线命中的节点在本插件 MOD 页之下
  （或就是 Viewport 本身，因为 MOD 页可见时它整块盖住 Viewport）；其他情况一律返回 false 不干预，
  所以游戏里别的 ScrollRect 完全不受影响。
- **点击路径天然不受影响**：点击原生标签走 `Button.onClick`，拖拽走 `IBeginDrag/IDrag`，
  与 `OnScroll` 是不同的事件接口。A4 的实测（§6.4）也确认了这一点。
- 该 hook 用独立的 Harmony id 单独装配（`try/catch` 隔离），避免 Unity 内建方法打补丁失败时
  把已有的 `SettingsPanel` 钩子一起带下去。
- 该 hook 是**兜底**：正常情况下轮次在 MOD 页的 ScrollRect 就被吸收，`ScrollRect.OnScroll` 根本不会被调用。
  验收运行里 `wheel blocked` 计数 = 0，即兜底一次都没触发（这符合预期，也说明它只是保险）。

### 4.4 F3：边界与回归

- **clamp**：`Content.sizeDelta.y = _contentHeight`（堆叠终值），ScrollRect 用 `Clamped` 模式自行收敛；
  `SyncContentSize()` 额外把超出范围的值夹回并打日志。实测滚到底停在 `y=1732 = max`，滚回顶停在 `y=0`。
- **行刷新（1Hz RefreshRows）不清屏**：行是常驻对象，`RefreshRows` 只改文字与开关颜色，不动 RectTransform，
  滚动偏移自然保留。
- **重开重置**：`Show()` 里 `_scrollRect.StopMovement()` + `_contentRt.anchoredPosition = Vector2.zero`，
  每次打开都从顶部开始。
- 页隐藏时整棵 MOD 页 `SetActive(false)`，滚动条与行都不参与开销。

### 4.5 滚轮步进的标定（委托方实测反馈后调整）

第一版把 `scrollSensitivity` 设成 `(行高+2)/120`（0.55）。实测发现**一个物理滚轮格**在
`PointerEventData.scrollDelta` 里是 **360** 个单位（12 格注入 → 6 个事件、每个 `-720`），
于是 0.55 下**一个格就滚 198px**，而页可视高只有 492px —— 一格的位移接近整页的 40%，
观感就是"一格滚一整页，前后两格几乎不重合"。

改成按"格"标定：

```csharp
private const float DeltaPerWheelDetent = 360f;   // 实测：一个物理滚轮格 = 360 delta
private const float RowStepPerDetent    = 0.5f;    // 目标：一格走半行
...
_scrollRect.scrollSensitivity = (_rowHeight + 2f) * RowStepPerDetent / DeltaPerWheelDetent;
```

即 66 × 0.5 / 360 ≈ **0.092**，一格约 33px（半个行高）：相邻两格有约一半行程重合，
滚满全表需要约 26 格，不会再出现"一格一页"。快速连续滚动时同一个事件里会合并多格，
位移按格数线性增长，仍然是可控的。

### 4.6 新增测试开关（env 门控，零成本）

- `MOD_PAGE_SELFTEST=1`：在（面板打开后）自动打开一次 MOD 页，便于脚本化验收。
  与既有 `MOD_P2_SELFTEST=1`（自动打开设置页）配套。未设置时该分支一次都不进。

---

## 5. 与计划的偏差（逐条）

| # | 计划写法 | 实际做法 | 原因 |
|---|---|---|---|
| 1 | F1 用 `ModScrollWheelHandler : MonoBehaviour, IScrollHandler` 挂在 Bg/页根 | 改用**原生 `ScrollRect`** 挂在页根作吸收层 | 该 interop 下接口被反接口化成类，注入后"能被找到、托管方法不被调用"，实测吞掉事件但不滚动（§4.1） |
| 2 | `Content` "与 header 下方对齐" | `Content` 顶边与**页顶**重合，另加 `HeaderBg` 不透明条盖住 header 那 52px | 计划同时要求"行堆叠数学零改动"，而行数学从容器顶边往下 52px 起排；两者只能满足一个。选择冻结行数学，用一条背景条达到同样的视觉效果（滚动行不会与标题叠字） |
| 3 | F1 里 "clamp: y ∈ [0, max]" 手写 | 由 `ScrollRect(Clamped)` 负责 + `SyncContentSize` 兜底 | 原生 clamp 更可靠，且顺带获得拖拽滚动 |
| 4 | 未提及 | 新增 MOD 列表专属滚动条 + 收起游戏原生滚动条 | 委托方实测后的追加需求 |
| 5 | F1 里 `Content.anchoredPosition += Vector2.up * scrollDelta.y * _rowHeight` | 不需要了：滚动位置由 ScrollRect 拥有；步进按"格"标定（§4.5） | Unity 实测 `scrollDelta.y` 量级是 **360/物理格**（不是 ±1），照计划写法一格会跳 120 行 |
| 6 | 未提及 | 新增 `MOD_PAGE_SELFTEST` 开关 | 便于在**部署的那份二进制**上做脚本化验收（否则只能用另一份带侦察代码的构建验收，报告口径就不一致了） |

计划要求"先侦察后动手"的两条前置条件都满足了：R1 拿到确切方法（类名 + RVA + 运行时调用链）之后才写 F2；
R2 确认 Viewport 自带 `RectMask2D` 之后，F1 才决定**不**另加 mask。

---

## 6. 验收标准逐项证据

### 6.1 A1 — 行数超出视口容量

在 `%USERPROFILE%\AppData\LocalLow\Nuverse\WorldApart\Mods` 下，复制 `local.storytest` 造了
`local.scrolltest01 … 08` 共 8 个包（改 `modId` / `title` / `description`，单元数 1~3 不等，
单元数为 3 的包另拷一份 `persona` 目录），加上原有 3 个包目录，共 11 个包目录。

日志：

```
[Info   :Mod Manager] [MODPAGE] page built under 'Viewport' (rows=28, headerLabel=True, rowHeight=64)
[Info   :Mod Manager] [MODPAGE] shown (native pages hidden, rows=28, openOnActivePage='audio', contentH=2224)
```

`rows=28`，`contentH=2224`，`vp=492` → 可滚动范围 1732px；视口一屏约只能放 4.3 个包头行，
**行数远超视口容量**。

### 6.2 A2 — 滚轮滚到底能看到最后一行、裁剪正确

在**部署的那份 DLL**（MD5 `28c41bb9…`）上，用真实鼠标滚轮（`mouse_event` 注入，非合成 uGUI 事件）
在 MOD 页中央下滚 24 格、再上滚 24 格（下表是新的步进标定，一格半行，见 §4.5）：

```
[Info   :Mod Manager] [MODPAGE] scroll y=0    max=1732 contentH=2224 vp=492 n=1
[Info   :Mod Manager] [MODPAGE] scroll y=66   max=1732 contentH=2224 vp=492 n=2
[Info   :Mod Manager] [MODPAGE] scroll y=132  max=1732 contentH=2224 vp=492 n=3
[Info   :Mod Manager] [MODPAGE] scroll y=198  max=1732 contentH=2224 vp=492 n=4
…（每步 +66，即每步一行、每格半行）
[Info   :Mod Manager] [MODPAGE] scroll y=1732 max=1732 contentH=2224 vp=492 n=…   ← 到底并夹住
…（上滚每步 -66）
[Info   :Mod Manager] [MODPAGE] scroll y=0    max=1732 contentH=2224 vp=492 n=49   ← 回顶并夹住
[Info   :Mod Manager] [MODPAGE] scroll y=132 … n=50 / 264 / 330 / 594            ← 之后是脚本的第三次微滚
```

- 偏移能到达 `max=1732`（即最后一行进入视口），且**从不越过** `max`、也不小于 0；
- 剪裁由游戏 Viewport 自带的 `RectMask2D` 完成，本页没有另加 mask；
- 滚动条 `value` 与位置同步（顶部 `1.000`，滑块高 `105 = 0.221 x 476`，与 `size=492/2224` 一致）。
- 截图：`KIMI\shots_FINAL2\01_top.png` / `02_bottom.png` / `03_back_to_top.png`。
- **最终视觉效果由委托方在游戏内确认**（委托方已实测滚轮可用、滚动条正常、步进放缓后待复看）。

### 6.3 A3 — 滚动全程设置页不变、MOD 页不被关闭

同一份验收日志（`KIMI\acceptance\LogOutput_FINAL2.log`）中，**全文只有 1 次**
`native page switch detected`，且它出现在全部 `scroll y=` 之后：

```
（n=1 … n=49 的全部 [MODPAGE] scroll y= 行，即下滚 24 格 + 上滚 24 格）
275: [Info   :Mod Manager] [MODPAGE] native page switch detected ('system' != 'audio'), closing
```

第 275 行那一次是脚本**故意点击原生 `Frame#navSystem` 标签**造成的（见 A4），不是滚轮造成的。
滚轮阶段该类日志 0 次 → MOD 页全程没有被自动关闭。

### 6.4 A4 — 点击原生标签仍能关闭 MOD 页并切页

脚本在 MOD 页可见时点击原生 `Frame#navSystem`（屏幕坐标从侦察运行实测得到：unity `(902,1200)`）：

| 阶段 | `native page switch detected` 计数 |
|---|---|
| 基线（MOD 页刚打开） | 0 |
| 滚轮下滚 24 格后 | 0（同时 scroll 日志 1 → 25 行） |
| 滚轮上滚 24 格后 | 0 |
| **点击原生标签后** | **1** |

日志：`[MODPAGE] native page switch detected ('system' != 'audio'), closing` + `[MODPAGE] hidden`。
即"点原生标签 → 切页 → 关闭 MOD 页"这条既有验收过的行为**没有回退**。

### 6.5 A5 — 展开/收起后行重排正确、滚动位置合理

**部分达成，如实说明：**

- 实现层面：收起/展开走 `SyncExpand() → Relayout()`，`Relayout` 重算 `top` 终值 → `_contentHeight`
  更新 → `Content.sizeDelta.y` 更新 → ScrollRect 的 `Clamped` 模式把窗口夹回新范围。
  这条路径与滚轮用的 clamp 是同一条（滚轮的 `y` 实测稳定夹在 `max` 上，从不越界）。
- **自动化点击没能命中行按钮**：脚本两次尝试点击第一个包头行（用来收起/展开）都没有产生
  `[MODPAGE] package '…' collapsed/expanded` 日志，`contentH` 全程保持 2224。
  原因未查明（同一套坐标换算点击原生标签是成功的）。**因此"点击行收起/展开"这一步的自动化证据缺失**，
  该条的视觉确认留给委托方（这也是计划里 A5 的原定证据形式：「委托方确认」）。
- 未做的自动化测试不影响已实现的行为，但报告不把它记为"已自动化验证"。

### 6.6 A6 — 帧性能无回归

FrameProbe 30 秒窗口（`KIMI\v8_frames_*.log`）：

| 运行 | 说明 | win | p95 |
|---|---|---|---|
| `v8_frames_RECON.log` | **修复前**基线，MOD 页 28 行 | win0..7 | `6.256ms`（全部窗口） |
| `v8_frames_FINAL2.log` | **修复后（部署版）**，MOD 页 + 滚轮 + 滚动条 | win0..6 | `6.256ms` |
| `v8_frames_FINAL2.log` | 同上 | win7 | `6.261ms` |

p95 偏差 **≤ 0.08%**（阈值 ≤5%）。该机被 160fps 垂直同步钉住，p95 稳定在 6.256ms。
（早先几轮含侦察代码时观察到的最差一档是 `6.260ms`，偏差 0.06%，同样远低于阈值。）

### 6.7 A7 — 部署产物一致性 + 日志 0 Error

- MD5 一致性：`BepInEx\plugins\LocalModManager.dll` = `KIMI\bepinex_plugin\bin\Release\LocalModManager.dll`
  = `28c41bb93bb48ff7ac11674d81d18039`。
- 验收日志 `[Error]` 行数 = **0**；`[Warning]` 2 行，都是 Il2CppInterop 启动期的既有告警
  （`Class::Init signatures have been exhausted`、`CurrentSnapshot() … unsupported return type`），
  与本次改动无关，修复前同样存在。
- `BepInEx\ErrorLog.log` 内没有任何 BepInEx 错误，只有 Steam/breakpad 的三行启动信息。
- 产物内已无侦察符号：`ScrollRecon` / `ModScrollWheelHandler` 命中 0 次（UTF-8 与 UTF-16 两种编码都查过）。
- 命名纪律：ASCII/UTF-8 大小写不敏感检索 `kimi` = **0 处**；UTF-16 命中 **1 处**，即既有调试路径
  `KIMI\v5_q1_recon.txt`（§7.3），非本次引入、本次未改。
- 备份文件 `KIMI\backup_deployed_pre_scrollfix.dll` = `c2c410757d52b475c8df778a5ab663fc`（修复前那份）。

---

## 7. 已知问题 / 未达成项（不掩饰）

### 7.1 A5 的自动化证据缺失
见 §6.5。功能已实现且有滚轮 clamp 的日志旁证，但"点击包头行收起/展开后滚动位置收敛"这一步
没有自动化证据，需要委托方在游戏里点一下确认。

### 7.2 已有的"MOD 页被莫名开关"现象（非本次引入）
侦察与验收运行里都能看到成对的：

```
[Info   :Mod Manager] [SETTINGS] MOD tab clicked t=26.39 mouseDown=False
```

即没有鼠标按下也会触发 MOD 页开关（`Plugin.cs` 里已有注释记过同类现象，当时的对策是把
`Button.navigation` 设为 `None`，看来没有彻底解决）。这会让 MOD 页偶尔自己开/关，
**不是本次滚动改动引入的**（修复前的运行里就有），但它会干扰复现，建议单独立项排查
（怀疑是 EventSystem 的键盘/手柄导航或游戏 UPF 输入层重复投递 Submit）。

### 7.3 既有调试字符串含 "kimi"（未改动）
`Plugin.cs` 里旧的 Q1 侦察路径会写出 `KIMI\v5_q1_recon.txt`，并使用环境变量 `MOD_Q1_RECON`；
两者都只在显式设置该环境变量时触发，正常游玩不可达，且早于本次改动存在，本次未改。
如需彻底清零，建议在收尾清理里单独处理（会牵动别的线路的调试流程，故没有顺手改）。

### 7.4 未做（计划里标"非必需/可选"的项）
- 滚动条拖拽已在原生 ScrollRect 下自然可用；但**没有做**自定义拖拽滚动增强、没有做平滑/惯性
  （保持 `inertia=false`，与游戏原生设置页一致）。

---

## 8. 给委托方的验收步骤（A2 / A4 / A5）

1. 游戏已保持在可测状态：设置面板 + MOD 管理页已打开，28 行（11 个包目录，其中 8 个是本次的
   `local.scrolltest01…08` 滚动测试包）。**若游戏被关掉，重新启动后点左侧「MOD 管理」标签即可。**
2. **A2**：鼠标滚轮在 MOD 页上向下滚到最底 —— 应能看到列表最后一行，超出部分被干净裁掉，
   右侧滚动条滑块随之移动；再向上滚回顶部。一格约走半行，相邻两格内容大部分重合。
3. **A4**：点左侧的「音量设置 / 显示设置 / 系统设置」任一标签 —— MOD 页应立即关闭并切到该原生页。
4. **A5**：点任意包头行（不是右边的开关）收起它下面的单元行，再点一次展开 ——
   观察行重排是否正确、滚动条滑块长度是否随内容总高变化、滚动位置是否被夹回合理范围。
5. 慢速滚轮步进（0.5 行/格）若仍偏快或偏慢，只改 `Plugin.cs` 里
   `RowStepPerDetent`（当前 0.5）即可，无需动其它逻辑。

---

## 9. 附录：本次新增/改动的文件清单

```
KIMI\bepinex_plugin\Plugin.cs                     ← 唯一被改的源码
KIMI\bepinex_plugin\bin\Release\LocalModManager.dll   ← 构建产物（MD5 28c41bb9…）
BepInEx\plugins\LocalModManager.dll               ← 部署目标（同一 MD5）
KIMI\backup_deployed_pre_scrollfix.dll            ← 部署前备份（MD5 c2c41075…）
KIMI\recon_archive\ScrollRecon.cs                 ← 侦察代码留档（不参与编译）
KIMI\acceptance\recon_R1R2_evidence.md            ← R1/R2 原始证据摘录
KIMI\acceptance\LogOutput_FINAL2.log              ← 最终验收运行完整日志（A3/A4/A6/A7 依据）
KIMI\acceptance\ErrorLog_FINAL2.log               ← 同期 ErrorLog
KIMI\acceptance\LogOutput_FINAL.log               ← 上一版（步进未放缓）验收日志
KIMI\acceptance\LogOutput_F3.log / LogOutput_F3B.log ← A5/F3 试探运行日志（失败留档）
KIMI\shots_FINAL2\01..04*.png                     ← 最终验收运行截图
KIMI\shots_FINAL\ / shots_F3B\                    ← 前几轮截图
KIMI\v8_verify.ps1 / v8_final.ps1 / v8_f3.ps1     ← 验收脚本（真实滚轮 / 点击注入）
%LOCALAPPDATA%\..\LocalLow\Nuverse\WorldApart\Mods\local.scrolltest01..08  ← A1 测试包
```
