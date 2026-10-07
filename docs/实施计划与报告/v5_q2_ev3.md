# E-V3：`<bank>.mp4.segments.json` 是每次播放重读，还是进程级缓存？

结论日期：静态分析完成（未运行游戏进程）
分析对象：`<G>\GameAssembly.dll`（140.55 MB，全镜像扫描 rva 0x1000–0x8F61000）
产物目录：`<工作目录>`（本文与 testplan 均在此）

---

## 1. 结论

**既不是"每次播放都重读"，也不是"启动时解析一次缓存到进程结束"，而是第三种——「按 Bank 加载粒度解析一次，缓存到该 Bank 被卸载为止」；并且 Bundle 加载时会重新扫描磁盘目录。**

准确的表述：

1. **没有任何静态（进程级）缓存。** 全镜像内不存在 `static VideoBank` / `static Dictionary<string, VideoBank>` 之类的字段。解析结果只存在两个地方：
   - `VideoBundlePlayer._videoBankPlayers : Dictionary<string, VideoBankPlayer>`（实例字段 0x38）
   - `VideoBankPlayer._bank : VideoBank`（实例字段 0x50）+ `_segmentNameToIndex : Dictionary<string,int>`（0x58）
2. **每次进入 `LoadMergedVideoAsync` 都是无条件 `File.ReadAllTextAsync` + JSON 反序列化**，没有任何 mtime / 长度 / hash / 缓存命中判断。
3. **但"进入 `LoadMergedVideoAsync`"这个动作本身被上一层的 `_bankLoads` 字典挡住**：`VideoBundlePlayer.LoadVideoBankAsync` 先用规范化后的 bank 路径查 `_bankLoads`，命中就直接返回已有的 load（不碰磁盘）。所以**同一个 Bank 在一次"已加载"生命周期内只解析一次**。
4. **缓存的生命周期 = Bundle/Bank 被加载的那段时间**，不是进程生命周期。以下动作会把缓存清掉，从而让下次播放重新读盘：
   - `VideoBundlePlayer.UnloadVideoBank`（0x995D20，内部 `DestroyBankPlayer` 0x991250）
   - `VideoBundlePlayer.UnloadBundle` / `UnloadBundleImmediate`（0x995BE0 / 0x9957D0）
   - `VideoBundlePlayer.UnloadAllBundlesImmediate`（0x9954C0）
   - `SetBundles` 的差集逻辑：目标集合里没有的 bundle 会被 `UnloadBundle` 卸载（0x98D72A）
5. **Bundle 加载时是"扫盘"而不是"查清单"**：`CompleteBundleLoadAsync` 用 `Directory.GetFiles(bundleDir, "*.segments.json")` 现场枚举 sidecar 文件，再把文件名去掉 `.segments.json` 后缀当作 bank 路径逐个加载。所以**磁盘上新增/改动的 sidecar 会在下一次 bundle 加载时被发现**（不是在进程启动时一次性定死）。

### 对「追加帧 + 扩展 segments.json」路线的影响

- ❌ **不能在同一个已加载的 bundle 上"热生效"**：只要 bundle 还处于加载态，`_bankLoads` 短路，改 json 不会生效，播放中的同一 clip 更不会重读。
- ✅ **但也不需要重启进程**：游戏本身就有现成的"卸载→重扫→重读"通路。最容易触发的两条：
  - 空间视频面板 `SpaceVideoPanel.ReleaseVideoResources()`（0x1A654D0）直接调 `UnloadAllBundlesImmediate()`；面板再次打开时 `EnableBehaviorAfterBundlesLoaded`（d__32，0x1A6EE90）再 `SetBundles` → 重扫目录 → 重新读 json。
  - `SpaceVideoPanel.OnUgcAvatarChanged` → `ReloadCurrentNpcVideo`（d__41，0x1A6FDC0）内部也调 `UnloadAllBundlesImmediate()`，是游戏自带的"重载视频资源"动作。
  - 对 cine 域：切换/重开 `CinematicPlayer` 会话时 `ApplyDesiredBundlesAsync`（d__112，0x13038F0）会重算目标 bundle 集合并 `SetBundles`；与旧集合的差集里的 bundle 被卸载，下次进入重新读盘。
- ⚠️ **一个必须注意的相邻事实（会削弱该路线）**：cine 的 VPath 不是运行时从 segments.json 反推的，而是**烘焙在 AssetBundle 里的 Timeline Clip 资产字段上**。见 §4。

---

## 2. 证据链

### 2.1 从文件名字符串出发：全镜像 xref

两个字符串字面量槽（`stringliteral.json`）：

| 字面量 | RVA（元数据槽） |
|---|---|
| `.segments.json` | `0x8149A00` |
| `*.segments.json` | `0x81700B0` |

**扫描方法（含一次纠错，如实记录）**：
第一次用 capstone 逐指令解码 + 重同步的方式跑（`<工作目录>\tools\xrefscan.py`），40 分钟只覆盖到 rva ≈ 0x1CB1A00（约全镜像 20%）就超时，**因此当时的 xref 表对 rva > 0x1CB1A00 的区域未经验证**。
随后改用**无解码的向量化扫描**（`<工作目录>\tools\ripxref_fast.py`，numpy）：任何 RIP 相对引用的位移字段位置 `o` 满足 `target_rva = rva(o) + 4 + disp`，于是对整文件所有字节逐段求 `disp == target_rva - rva(o) - 4`。该法覆盖**全部 6 个节、全部文件字节**（镜像 147,376,904 字节；节表：`.text` 0x1000 / `il2cpp` 0x658000 / `.rdata` 0x66B1000 / `.data` 0x7F57000 / `.pdata` 0x8825000 / `.reloc` 0x8D15000），两次结果在其重叠区间完全吻合（位移字段偏移 = 指令起始 + 3，故 rva 差 3）。

**完整结果：全镜像内引用这两个槽的代码位置共 52 处，分布在 22 个函数里；最高的代码引用是 rva 0x1CB19CE，其后再无任何引用。** 另有 3 处落在 `.data`（元数据槽自身所在节）内的巧合字节：0x81479FA、0x81499FC、0x81700AC —— 不是指令。

> 下表列的是**位移字段**的 rva；§2.2–§2.4 的机器码清单里用的是**指令起始** rva，同一处引用两者相差 3 字节（`48 8D 0D <disp32>` / `48 8B 15 <disp32>` 之类）。

**运行时播放路径（CompositePlayer.Runtime）**

| 函数 | RVA | 引用（位移字段 rva） |
|---|---|---|
| `VideoBundlePlayer.<CompleteBundleLoadAsync>d__69.MoveNext` | 0x989ED0 | **两个都引**：`*.segments.json`（0x989FFB、0x98A13F）+ 后缀常量 `.segments.json`（0x989FEF、0x98A187） |
| `VideoBankPlayer.<LoadMergedVideoAsync>d__53.MoveNext` | 0x98C270 | `.segments.json`（0x98C385、0x98C4D5） |
| `VideoBundlePlayerDemo.<LoadBundle>d__12.MoveNext` | 0x98B8C0 | `*.segments.json`（0x98B9EE、0x98BD01）—— Demo 脚本，随包但不参与正式流程 |

**游戏侧（Game 命名空间）**

| 函数 | RVA | 引用 | 说明 |
|---|---|---|---|
| `SpaceVideoResourceIndex.IsVideoBundleDirectory` | 0x1606BB0 | `*.segments.json`（0x1606BC5、0x1606BD8） | 判定目录是不是视频 bundle 目录（只做存在性判断，不解析） |
| `UPFSectOverviewPanel.TryReadBundleSegmentVPaths` | 0x1CB1810 | `*.segments.json`（0x1CB18CD、0x1CB19CE） | `Directory.GetFiles(dir,"*.segments.json")` + `File.ReadAllText` + 反序列化（同步，见 §4.2） |
| `CinematicManifestRegistry.ValidateBundles` | 0x12D3C00 | `*.segments.json`（0x12D3CAC、0x12D3FC8） | 诊断用：逐 bundle 检查有没有 `*.segments.json`，只做存在性检查 |

**烘焙/打包/上传工具（不参与运行时播放）**

| 函数 | RVA | 引用 |
|---|---|---|
| `VideoMerger.CheckIfBankMatchesSources` | 0x996FD0 | `.segments.json`（0x997055、0x9970D1） |
| `VideoMerger.CheckIfDirectoryMatchesSources` | 0x997420 | 两个都引（0x997634、0x997846 / 0x99764C、0x9977AF） |
| `VideoMerger.CleanupOldBankFiles` | 0x9984F0 | `*.segments.json`（0x998532、0x998593） |
| `VideoMerger.LoadVideoBundleFromDirectory` | 0x99C200 | `*.segments.json`（0x99C24C、0x99C289） |
| `VideoMerger.MergeSingleParameterGroupToFile` | 0x99CC70 | `.segments.json`（0x99CF40、0x99DFE4、0x99E17D） |
| `UgcVideoBundleBaker.BakeInPlace` | 0xD13580 | `*.segments.json`（0xD13656、0xD13A5B） |
| `UgcVideoBundleBaker.ClearBakedArtifacts` | 0xD14020 | 两个都引（0xD140AB、0xD141BB / 0xD140B7、0xD14200） |
| `UgcVideoBundleBaker.CollectVPathsFromBundleDir` | 0xD143C0 | `*.segments.json`（0xD14468、0xD1455B） |
| `UgcVideoBundleBaker.EnsureBakedBundleForBundlePath` | 0xD146F0 | `*.segments.json`（0xD147E6、0xD14819） |
| `UgcVideoBundleBaker.IsBankFile` | 0xD14FE0 | `.segments.json`（0xD15001、0xD15071） |
| `UgcVideoBundleBaker.IsBundleConsistent` | 0xD150A0 | 两个都引（0xD151A1、0xD1536C、0xD1537F / 0xD151AD、0xD1524B） |
| `UgcVideoBundleBaker.IsBundleReady` | 0xD15990 | `*.segments.json`（0xD159A5、0xD159D4） |
| `UgcWorkshopLayout.BuildSubtree` | 0xD167D0 | `*.segments.json`（0xD16868、0xD16A19） |
| `UgcPublishPackageBuilder.IsAllowedUploadFile` | 0xD47670 | `.segments.json`（0xD476CC、0xD47938） |
| `ModAssetApplier.Walk` | 0xD6EFA0 | `*.segments.json`（0xD6F005、0xD6F04A） |

→ **播放用的 json 只有 `LoadMergedVideoAsync` 一个真正的解析点**（Demo 除外）。不存在"启动时把所有 segments.json 预扫一遍建全局表"的代码。

### 2.2 `CompleteBundleLoadAsync`：bundle 加载 = 扫盘 + 逐 bank 加载

RVA 0x989ED0（`VideoBundlePlayer.<CompleteBundleLoadAsync>d__69.MoveNext`），字符串初始化块在 0x989FF0–0x98A03F 就把两条错误串准备好了：
- `"No VideoBank segment metadata files found in Bundle: "`（槽 rva 0x823C200）
- `"Failed to load any VideoBank in Bundle: "`（槽 rva 0x8216148）

关键指令：

```
0x18098A126  call Directory.Exists
0x18098A13C  mov rdx, qword ptr [rip + 0x77e5f6d]   ; "*.segments.json"
0x18098A146  call Directory.GetFiles               ; ← 现场枚举磁盘
0x18098A1A9  call String.Substring                 ; name.Substring(0, name.Length - 9)
                                                   ;   suffix 槽 = 0x8149A00 ".segments.json"
0x18098A1C0  call VideoBundlePlayer.NormalizeBankPath   ; 0x993990
0x18098A1F6  call 0x180004250                      ; HashSet<string>.Add
...
0x18098A4C6  call VideoBundlePlayer.LoadVideoBankAsync  ; 逐个 bank 加载（0x9934C0）
```

含义：**bank 清单来自磁盘目录**，文件名去掉 9 字符后缀就是 bank mp4 路径。

### 2.3 `LoadVideoBankAsync`：唯一的"缓存命中"闸门

RVA 0x9934C0：

```
0x18099355E  call VideoBundlePlayer.NormalizeBankPath     ; 规范化绝对路径（'/' 分隔）
0x180993596  call 0x1822D2E00      ; rcx = [rdi+0x48] = _bankLoads (Dictionary<string,BankLoad>)
                                   ; TryGetValue(normalizedPath, out load)
0x18099359D  jne  0x1809937A7      ; ← 命中：直接返回已有 load 的 Task，不碰磁盘
0x1809935A8  call File.Exists      ; 未命中：检查 bank mp4 是否存在
0x180993643  call 0x1822C4960      ; _bankLoads.Add(normalizedPath, new BankLoad)
```

`_bankLoads` 是 **实例字段**（`VideoBundlePlayer` 偏移 0x48，类型 `readonly Dictionary<string, VideoBundlePlayer.BankLoad>`）。字段布局见 dump：

```
private Dictionary<string, VideoBankPlayer> _videoBankPlayers;        // 0x38
private Dictionary<string, List<string>> _bundleToBanks;              // 0x40
private readonly Dictionary<string, VideoBundlePlayer.BankLoad> _bankLoads;   // 0x48
private readonly Dictionary<string, VideoBundlePlayer.BundleLoad> _bundleLoads;// 0x50
```

`UnloadVideoBank`（0x995D20）→ `NormalizeBankPath` → 从 `_bankLoads` 移除 → `DestroyBankPlayer`（0x991250）。移除后再次 `LoadVideoBankAsync` 就会重新走到磁盘读。

### 2.4 `LoadMergedVideoAsync`：无条件读盘 + 反序列化，无任何缓存判断

RVA 0x98C270（`VideoBankPlayer.<LoadMergedVideoAsync>d__53.MoveNext`）。状态机布局：
`+0x20 videoFilePath`、`+0x28 <>4__this`、`+0x30 segmentJsonPath`、`+0x38 <normalizedPath>5__2`、`+0x40 <version>5__3`。

```
0x18098C456  call String.IsNullOrEmpty(videoFilePath)          ; 只看空，不看缓存
0x18098C480  call Path.GetFullPath
0x18098C49F  call String.Replace('\\','/')                     ; → <normalizedPath>
0x18098C4BB  call File.Exists(normalizedPath)                  ; 否则 LogError + return false
0x18098C4C8  cmp  qword ptr [rsi+0x30], 0                      ; segmentJsonPath == null ?
0x18098C4DD  call String.Concat(normalizedPath, ".segments.json")   ; 槽 0x8149A00
0x18098C4F8  call File.Exists(segmentJsonPath)                 ; 否则 "…metadata…" LogError + return false
0x18098C50D  inc  dword ptr [rdi+0x74]                         ; _loadVersion++（并发防串号）
0x18098C53B  call String.Concat(…, " Invalid VideoBank path '") ; 错误串 0x82007E0
0x18098C65B  call File.ReadAllTextAsync(path, Encoding.UTF8)   ; ★ 无条件读盘
0x18098C6C9  call 0x1820FA970                                  ; JSON 反序列化 → VideoBank (r13)
0x18098C6E9  cmp  dword ptr [rcx+0x18], 0                      ; 校验 Segments 非空
0x18098C729  cmp  dword ptr [rsi+0x40], eax                    ; version 校验：
0x18098C72C  jne  0x18098CA75                                  ;   期间又发起过新加载 → 丢弃本次结果
0x18098C74F  call VideoBankPlayer.CloseHandle(2)
0x18098C758  mov  qword ptr [rdi+0x40], rdx                    ; _sourcePath = normalizedPath
0x18098C765  mov  qword ptr [rdi+0x50], r13                    ; _bank = 反序列化结果 ★缓存位置
0x18098C775  mov  dword ptr [rdi+0x60], 0xFFFFFFFF             ; _currentSegmentIndex = -1
0x18098C80A  call 0x184F86150                                  ; new Dictionary<string,int>(Segments.Count)
0x18098C817  mov  qword ptr [rdi+0x58], r14                    ; _segmentNameToIndex
0x18098C886  call 0x184F89370                                  ; 逐段 VPath → index 填表
```

`VideoBank` 的字段布局（`TypeDefIndex 27889`）：`Version 0x10 / EncodingProfile 0x18 / Timebase 0x20 / Parameters 0x28 / OutputPath 0x30 / Segments 0x38 / TotalDurationPTS 0x40`；`VideoSegment`（27884）：`VPath 0x10 / FilePath 0x18 / SourceIdentifier 0x20 / FrameCount 0x28 / Timebase 0x30 / StartPTS 0x38 / EndPTS 0x40`。

**这一段里没有任何"先查缓存"的分支**——只要函数被调用，文件就被读。

### 2.5 `LoadMergedVideoAsync` 的全部调用点（全镜像 E8/E9 扫描，工具 `<工作目录>\tools\callscan.py`）

| 调用点 RVA | 所在函数 | 说明 |
|---|---|---|
| 0x9930FF | `VideoBundlePlayer.LoadBankPlayerAsync` (0x9930E0) | **唯一正式入口**，是一条尾跳 `jmp 0x18098ED40`，参数 `(bankPath, null)` ——json 路径由 callee 用 `bankPath + ".segments.json"` 推出来 |
| 0x98D019 | `VideoBankPlayer.<RestoreSourceAsync>d__52.MoveNext` (0x98CF20) | 编辑器 domain reload / 重新 Awake 的恢复路径（`Awake` 0x98E4B0 → `EnsureSourceOpen` 0x98E880 → `RestoreSourceAsync`），用序列化的 `_sourcePath` 再读一次 |

全局只有这两个调用点；`LoadBankPlayerAsync` 在游戏侧**没有被 override**（`VideoResourcePlayer` 只 override 了 `ResolveResourcePath`）。也就是说：**播放 clip 时不会再走一遍 json 解析**——`PlaySegment` / `SeekSegment` / `HasSegment` 全部走内存里的 `_segmentNameToIndex` / `_videoBankPlayers`：

- `VideoBundlePlayer.HasSegment`（0x992D30）：`String.IsNullOrEmpty` → 遍历 `_videoBankPlayers` → `VideoBankPlayer.GetSegmentIndexByName`（0x98EBB0），无文件 IO。
- `VideoBundlePlayer.FindPlayerBySegment`（0x991FA0）：同样只查字典。

### 2.6 谁在什么时候触发加载/卸载（决定缓存的生死）

`SetBundles`（0x994BD0，`d__74` MoveNext 0x98D200）的调用点：

| 调用点 | 所在函数 |
|---|---|
| 0x1303B5E | `CinematicPlayer.<ApplyDesiredBundlesAsync>d__112.MoveNext`（0x13038F0） |
| 0x19AF163 | `SectOverviewPanel.<TryPlaySectIntroVideoDirectly>d__185.MoveNext` |
| 0x19DF0AC / 0x19DF297 | `SpaceMaskVideoPanel.<SetData>d__30.MoveNext` |
| 0x1A6F0F5 | `SpaceVideoPanel.<EnableBehaviorAfterBundlesLoaded>d__32.MoveNext` |
| 0x1AB5738 | `VideoResourcePlayer.<UnloadAllResources>d__9.MoveNext`（0x1AB5690） |
| 0x1C9CE8B | `UPFSectOverviewPanel.<TryPlaySectIntroVideoDirectly>d__86.MoveNext` |

`SetBundles` 内部做差集：`UnloadBundle`（call @0x18098D72A）+ `LoadBundle`（call @0x18098D879）。
`LoadBundle`（0x993110）→ 内联的 `CompleteBundleLoadAsync` → §2.2 的扫盘。

`UnloadAllBundlesImmediate`（0x9954C0）的调用点：`SpaceVideoPanel.ReleaseVideoResources`（0x1A65518）、`SpaceVideoPanel.CleanupBundlesLoadedAfterRelease`（0x1A635E3）、`SpaceVideoPanel.<ReloadCurrentNpcVideo>d__41`（0x1A6FF78）。

→ **缓存存活范围 = 面板/会话存活范围**。空间视频面板关闭、重开、切 NPC 都会经过"卸载 → 重扫目录 → 重读 json"。

---

## 3. 静态分析未能确定的部分 + 运行时验证方案

静态上已经能确定"不是每次播放重读、也不是进程级缓存、而是按 bundle 加载缓存"。静态**不能**确定的只有一件事：

> 客户端在自己机器上"追加帧 + 改 segments.json"之后，**具体哪一个游戏内动作**能让 Unity 真的把 bundle 卸载掉并重扫目录（我依据的调用点全在静态反编译里，无法确认运行时的调用顺序、面板缓存、`_destroyed`/`_loadGeneration` 守卫等是否会绕过卸载）。

因此准备了 `v5_q2_ev3_testplan.md`：**不需要玩到视频，只要观察文件是否被重新打开/重读**——用一个"改坏一字节让解析必然失败"的手法，让"是否重读"变成一个在日志里可观测的二值信号，全程可 byte-exact 还原。请客户端按该文件跑一遍（预计 1–2 分钟）。

---

## 4. 两个相邻发现（与本题直接相关，供后续决策）

### 4.1 cine 域：VPath 烘焙在 Timeline Clip 资产里，不从 segments.json 反推

`CommonVideoClip`（TypeDefIndex 10097，`Game.CinematicRuntime`）：

```
[Tooltip("片段虚拟路径（VPath），例如: Dialogue/1001:2001")]
public string segmentVPath;            // 0x18   ← 序列化在 AssetBundle 里的 Clip 资产上
[HideInInspector] public CinematicGroupId groupId;   // 0x20
[HideInInspector] public CinematicClipId  clipId;    // 0x28
[HideInInspector] public string segmentBankPath;     // 0x38
[HideInInspector] public double segmentStartTime;    // 0x40
[HideInInspector] public double segmentEndTime;      // 0x48
```

`CommonVideoClipBehaviour` 同样带 `public string segmentVPath; // 0x18`，并有 `public string ResolvedSegmentVPath { get; }` 只是该字段的取数口。

→ **在 segments.json 里新增一个 segment 条目，并不会让任何 Timeline 去播放它**：Timeline 上得先存在一个引用该 VPath 的 Clip。所以"追加帧 + 扩展 json"这条路线，对于 cine 走 Timeline 的场景，还需要同时改 bundle 里的 Clip 资产（或由 Mod 自己直接驱动 `VideoResourcePlayer.PlayVideo(vpath)`）。
（判定依据：上述序列化字段布局 + `OnClipActivated`/`GetVideoPath` 都基于该字段；**未**逐条跟踪 `get_ResolvedSegmentVPath` 的机器码，因为该 RVA 落在共享方法跳板上。标为"强静态证据，未运行时验证"。）

对空间视频域（`SpaceVideoPanel`）则不同：那边的 VPath 由 `SpaceVideoPresentation.BuildVPath`（0x16057D0）按 `<path>:<name>` 运行时拼出来，再交给 `HasSegment` 校验，**更容易受到 segments.json 内容的影响**。

### 4.2 游戏内已存在"同步重读 json"的调用点

`UPFSectOverviewPanel.TryReadBundleSegmentVPaths`（0x1CB1810，宗门总览面板）：

```
0x181CB1969  call DlcContentManager.ResolveVideoBundle
0x181CB197E  call VideoResourceBuildPaths.GetBuildOutputDirectory
0x181CB19A4  call Path.Combine
0x181CB19B8  call Directory.Exists
0x181CB19D5  call Directory.GetFiles          ; "*.segments.json"
0x181CB1A6D  call File.ReadAllText            ; ★ 同步、每次调用都读
0x181CB1A91  call 0x1820FDC10                 ; 反序列化（VideoBankIndexFile，只取 VPath）
0x181CB1B13  call String.StartsWith           ; 按前缀过滤 5 字符比较
0x181CB1B56  call 0x180004250                 ; HashSet<string>.Add
```

它反序列化用的轻量类型 `UPFSectOverviewPanel.VideoBankIndexFile { Segment[] Segments }` / `VideoBankIndexSegment { string VPath }` 和 `*.mp4.segments.json` 的 `Segments[].VPath` 结构兼容，所以读的是同一批 sidecar 文件。**这条路径完全没有缓存**，每次进宗门总览都是重读+重解析。

→ 这（连同 §2）说明游戏对 segments.json **没有任何全局缓存机制**；是否重读完全取决于调用它的上层代码是否重走加载流程。

---

## 5. 是否改动了游戏目录文件

**没有。** 本次分析为纯静态读取：

- 只读取了 `GameAssembly.dll`、符号转储 `dump.cs` / `stringliteral.json`（均在本工作目录下，见文首「分析对象」）、以及 `WorldApart_Data\StreamingAssets\MergedVideoResources~\cine\9907001\Video_cine_9907001_bank_1.mp4.segments.json`（**只读，为确认 sidecar 的 JSON schema**）。
- **未修改任何 `.segments.json`**，未触碰 `BepInEx\`、`GameAssembly.dll`、`UnityPlayer.dll`、`gpShell.dll`、`WorldApart.exe`、Mods 目录。
- 未启动/停止/交互任何游戏进程。
- 新增文件只在 `<工作目录>` 下：
  - `tools\xrefscan.py`（capstone 版 rip-xref 扫描器，本次因过慢只跑完 rva ≤ 0x1CB1A00，已弃用）
  - `tools\ripxref_fast.py`（numpy 向量化全镜像 rip-xref 扫描器，§2.1 的完整结果由它产出）
  - `tools\callscan.py`（全镜像 E8/E9 调用点扫描器，§2.5/§2.6 的调用点表由它产出）
  - `tools\lock_hold.py`（运行时测试方法 B 用的独占句柄工具，已自测：占锁期间其他进程读取报 PermissionError）
  - 本文、`v5_q2_ev3_testplan.md`
  - 附注：为跑 `ripxref_fast.py`，向**本工作目录内的专用 venv**（`tools\venv`）`pip install numpy`（2.5.3）。该 venv 是我们自己的隔离环境，与游戏目录、BepInEx、系统 Python 均无关系。

本次分析唯一被读取的游戏目录内 sidecar 文件（**只用 Python `json.load` 只读打开，未写回**）：

```
文件：WorldApart_Data\StreamingAssets\MergedVideoResources~\cine\9907001\Video_cine_9907001_bank_1.mp4.segments.json
大小：6391 字节，mtime 2025-10-05 14:08（分析前既有的时间戳，未被我改动）
SHA256：3fadf7825daf88032052872f55db7b11847b2f2967b4128cf1bb81e2cbbc3e47
```

因为本次**没有对任何 `.segments.json` 做写入**，所以不存在 before/after 哈希对——上面这个哈希是分析结束后重新计算的值，与文件大小/mtime 一起可用来核对文件未被改动。整个过程中**没有任何"改一个字节再还原"的操作**（那条路线的备份/还原协议保留给 `v5_q2_ev3_testplan.md` 里的运行时测试，由客户端执行）。
