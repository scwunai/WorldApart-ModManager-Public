# 实施计划：MOD 管理页滚动修复（独立任务）

> 本文档是独立任务，不依附任何其他实施线路。基线即当前工作区源码。

## 背景

《不问凡尘》MOD 环境的管理页（游戏内 设置 → MOD管理）当前两个滚动相关缺陷：

1. **鼠标滚轮会切回游戏原生的设置项**：在 MOD 页上滚动时，游戏把滚轮事件解释为切换设置标签页（audio/display/...），插件检测到设置页变化后自动关闭 MOD 页（日志 `[MODPAGE] native page switch detected`），用户看到"跳回原本的设置项目"。
2. **超过一页的 MOD 无法显示**：行数是动态的（每个包一行 + 下属 unit 各一行），行数超出视口高度后，超出部分永远不可达——MOD 页没有任何滚动机制（行直接以绝对 anchoredPosition 挂在页根下）。

已确认的结构事实（源码 `KIMI\bepinex_plugin\Plugin.cs`，build stamp 20261006-1838）：

- 页根 `_pageRt` 直接挂在设置面板 ScrollViewer 的 **Viewport** 下（Build 内 `while (vp.name.StartsWith("Viewport"))` 上溯），并复制 Viewport 几何。
- 页根下有 opaque `Bg` Image（`raycastTarget=true`，全覆盖）——uGUI 射线被挡住，但**游戏自研 UPF 输入层不依赖 uGUI 射线**，滚轮仍被 UPF 组件消费并切换标签页。
- 原生设置页内容本身住在 scroll-space（y 900..3000），说明设置面板**存在原生滚动机制**，MOD 页可复用其裁剪。
- 行布局：Relayout 每 0.25s 把所有可见行以 `anchorMin/Max=(0,1)`、`anchoredPosition=(indent*0.5, -top)` 绝对堆叠；包头行高 100、unit 行高 `_rowHeight`；收起包会把下方行上拉。
- 插件的 `Tick()` 依赖设置面板当前页变化来检测"用户点了原生标签 → 关闭 MOD 页"，这条路径必须保留。

## 目标

- MOD 页可见时，滚轮**只滚动 MOD 页内容**，绝不触发原生标签切换。
- MOD 行数超出视口时，滚轮能上/下滚到全部行，裁剪正确。
- 展开/收起、启用开关、Relayout 重排后滚动位置合理（不跳变、不出空白）。
- 点击原生标签页仍能正常关闭 MOD 页（既有行为不回退）。

## 任务

### R1（侦察，必须先做）：定位滚轮转标签的 UPF 处理者

需要回答：滚轮事件经哪个类型/方法把设置面板当前页切走的。

候选路径（按可能性排序，逐一验证）：
1. 设置面板左侧标签栏的 UPF 组件（TabControl/NavBar 之类）注册了滚轮输入委托。
2. ScrollViewer 的横向滚轮转页。
3. 设置面板 ViewModel 的切页方法被输入层直接调用。

方法：复用现有 Harmony 环境给设置面板 ViewModel 的当前页 setter（或切页方法）打临时 postfix 日志（调用栈 `new StackTrace()`），在设置面板开着时滚一下轮子，抓调用栈定位到 UPF 组件；再从该组件反查其输入绑定。

产出：确切的方法（类名+方法名+RVA）、调用链简述、**可 Harmony 拦截的切入点**。

### R2（侦察）：确认裁剪与滚动几何

- Viewport 上是否有 `RectMask2D`/`Mask` 组件（dump 组件列表）。
- 原生 ScrollViewer 的滚动方向与 content 尺寸来源（供参考，不必复用）。
- Viewport 实际高度（世界 rect）→ MOD 页可视高度基准。

### F1：MOD 页内容容器 + 滚轮滚动

- 在 `_pageRt` 下、Header 之下新建 `Content` 容器（RectTransform，anchor 拉伸、pivot 顶部、与 header 下方对齐）。
- 所有行根节点改挂到 `Content` 下；Relayout 的行堆叠数学**不变**（只是坐标系从容器的顶部算起）。
- 新增组件 `ModScrollWheelHandler : MonoBehaviour, IScrollHandler` 挂在 Bg（或页根）上：
  - `OnScroll(PointerEventData d)`：`Content.anchoredPosition += Vector2.up * d.scrollDelta.y * _rowHeight`（步进 = 一行高；`scrollDelta.y` 在 Windows 通常为 ±1）。
  - clamp：`y ∈ [0, max(0, contentTotalH - viewportH)]`，`contentTotalH` 取 Relayout 算出的堆叠终值。
  - 事件**不再向下传递**（原生行在 Bg 之下，uGUI 层面本来就到不了；UPF 层面靠 F2）。
- 裁剪：若 R2 确认 Viewport 有 mask，溢出行自动被裁；否则给 `Content` 加 `RectMask2D`。
- 滚动条 UI 非必需（轮子+拖拽够用）；拖拽滚动（IBeginDragHandler/IDragHandler）列为可选项，时间够就做。

### F2：阻断原生滚轮转标签

- 基于 R1 的切入点打 Harmony prefix：MOD 页可见时 `return false` 跳过原生处理；不可见时不干预。
- 注意：**只挡滚轮路径**，点击标签的路径必须原样放行（关闭 MOD 页的逻辑依赖它）。
- 若 R1 发现挡输入层影响面太大（如全局输入管理器），退而求其次：在设置面板 ViewModel 当前页 setter 打 prefix——当 MOD 页可见且新值≠当前值且调用栈来自滚轮处理者（R1 确认的帧上类型名匹配）时跳过。这是兜底方案，报告里说明取舍。

### F3：边界与回归

- 收起/展开包后重算 clamp；若当前滚动位置超出新范围则收敛到新 max。
- 行刷新（RefreshRows 1Hz）重建行时保留滚动偏移（相对锚点用容器顶部，重建后 offset 语义不变即天然保留，验证之）。
- MOD 页隐藏再打开：滚动位置重置为 0（合理默认）。

## 验收标准（实施报告必须逐项给证据）

| # | 项 | 证据 |
|---|---|---|
| A1 | 制造 8+ 个假 MOD 包（复制 example.tutorial 改 modId，或程序化造包），使行数 > 视口容量 | 日志：rows= 数量 |
| A2 | MOD 页上滚轮滚动到底可看到最后一行，裁剪正确 | 委托方截图/视频 + diag 日志 |
| A3 | 滚动全程设置面板当前页不变、MOD 页不被关闭（无 `native page switch detected` 日志） | LogOutput 摘录 |
| A4 | 点击左侧原生标签仍能关闭 MOD 页并切页（既有行为） | 委托方确认或日志 |
| A5 | 展开/收起后行重排正确，滚动位置合理 | 委托方确认 |
| A6 | 帧性能无回归（p95 帧时偏差 ≤5%） | FrameProbe 数据 |
| A7 | 部署 DLL 与构建产物 MD5 一致；日志 0 Error | 报告附 MD5 |

## 约束

- 基线源码：`KIMI\bepinex_plugin\Plugin.cs`（当前工作区状态）。
- 禁止改游戏目录任何文件；部署只更新 `BepInEx\plugins\LocalModManager.dll`（部署前备份现 DLL 到 `KIMI\backup_deployed_pre_scrollfix.dll`）。
- 游戏可能正在运行，部署后由委托方重启验证。
- 命名纪律：任何用户可见字符串/日志/文件名不得出现 kimi。
