# E-V3 运行时验证方案（`v5_q2_ev3_testplan.md`）

目标是回答静态分析无法覆盖的那一小块：**在客户端机器上，具体哪个游戏动作会让 Unity 把 bundle 卸载并重新读盘**（静态结论见 `v5_q2_ev3.md`：不是每次播放重读，也不是进程级缓存，而是"按 bundle 加载缓存，卸载后重扫目录重读"）。

预计耗时：方法 A 约 2 分钟（需改 Mod 插件重新编译），方法 B 约 1 分钟（**零游戏文件改动**），方法 C 约 2 分钟（需临时改 1 个 json，有完整还原协议）。

---

## 0. 安全约束（必须遵守）

- **只有方法 C 会改游戏目录里的文件**，而且只改这一个：一个 `.segments.json`。改前必须备份到 `<工作目录>` 并记录 SHA256，测完**立即还原**并重新计算 SHA256 比对一致。
- 方法 A 只改 `BepInEx\plugins` 下自己的 Mod 插件，不动 `BepInEx` 本体、不动 `GameAssembly.dll` / `UnityPlayer.dll` / `gpShell.dll` / `WorldApart.exe` / Mods 资源。
- 方法 B **完全不改任何文件内容**。
- 不要用"改坏文件来判断是否重读"然后又忘了还原；每次做完立刻核对哈希。

---

## 1. 被测对象

```
<G>\WorldApart_Data\StreamingAssets\MergedVideoResources~\cine\9907001\Video_cine_9907001_bank_1.mp4.segments.json
```

| 项 | 值 |
|---|---|
| 大小 | 6391 字节 |
| SHA256（还原基准） | `3fadf7825daf88032052872f55db7b11847b2f2967b4128cf1bb81e2cbbc3e47` |
| Segments 条数 | 22 |
| Timebase | 15360 |
| TotalDurationPTS | 2703360（= 176 秒） |
| 首条 VPath | `cine/9907001:1`，StartPTS 15360，EndPTS 109568 |
| 编码 | UTF-8 **带 BOM**（编辑时必须保留 BOM） |
| 配套 mp4 | 同目录 `Video_cine_9907001_bank_1.mp4` |

> 若客户端对 9907001 这个 cine 不好稳定复现，**换一个能稳定进入的 bank 即可**，下面所有方法把路径替换成那个 bank 的 `.mp4.segments.json` / `.mp4`，流程完全一致。

---

## 2. 方法 A（首选）：Harmony 计数器，直接测量调用次数

在现有 Mod 插件里加三个 patch 的 prefix，只打日志计数：

| 目标 | 程序集 | 方法 | RVA（参考） |
|---|---|---|---|
| 加载闸门 | CompositePlayer.Runtime | `VideoBundlePlayer.LoadVideoBankAsync(string bankPath)` | 0x9934C0 |
| **真正的读盘点** | CompositePlayer.Runtime | `VideoBankPlayer.LoadMergedVideoAsync(string videoFilePath, string segmentJsonPath)` | 0x98ED40 |
| 卸载点 | CompositePlayer.Runtime | `VideoBundlePlayer.UnloadAllBundlesImmediate()` | 0x9954C0 |

判定逻辑：

1. **冷启动后第一次播放该视频**：`LoadMergedVideoAsync` 计数应为 **1**（json 被读 1 次）。
2. **退出该面板/切场景，再进一次播放同一视频**：
   - 计数**不变**（仍为 1，且 `UnloadAllBundlesImmediate` 未被调用）→ 命中缓存，**没有重读**。
   - 计数 **+1** → 这个动作触发了重读。
   - `UnloadAllBundlesImmediate` 被调用但计数**没变** → 卸载生效了但这次没重新加载（例如目标 bundle 被差集剔除）。
3. **同一视频连续播放两次（不退面板）**：`LoadMergedVideoAsync` 不应增加——这是「不是每次播放都重读」的直接证据。

这个方法同时把「哪个动作能让它重读」测出来，是最省事的。日志写到自己的文件即可，不要依赖游戏日志。

---

## 3. 方法 B（零内容改动）：独占句柄挡住读盘

原理：`File.ReadAllTextAsync` 内部以 `FileShare.Read` 打开文件。我们在外部用 `dwShareMode = 0` 的句柄占住它，**游戏一旦尝试重读就会抛共享冲突**，于是"是否重读"变成日志/画面上的可见差异；如果游戏根本没去读（命中缓存），一切照常。

脚本已就绪（自测通过：占用期间其他进程读取会 `PermissionError`）：

```
<工作目录>\tools\lock_hold.py <json 绝对路径> 180
```

步骤：

1. 先做**对照跑**：正常进游戏，进到能播放该视频的场景，确认视频正常出画面，记下当时的 `BepInEx\LogOutput.log` 行数。
2. 退出到能触发"卸载该 bundle"的界面（对该 bank 通常是关掉/重开对应的空间视频面板；cine 则是结束该 cine 会话）。**然后**执行：
   ```
   <工作目录>\tools\venv\Scripts\python.exe "<工作目录>\tools\lock_hold.py" "<G>\WorldApart_Data\StreamingAssets\MergedVideoResources~\cine\9907001\Video_cine_9907001_bank_1.mp4.segments.json" 180
   ```
3. 在 180 秒窗口内**再次进入该视频**。
4. 看 `BepInEx\LogOutput.log` 的新增行是否出现下列之一（原文都是英文，可安全 grep）：
   - `Failed to load VideoBank`
   - `Failed to load any VideoBank in Bundle:`
   - `No VideoBank segment metadata files found in Bundle:`
   - `Invalid VideoBank path '`
   同时看游戏内该视频是否不出画面/黑屏。
5. 判定：
   - **出现上述任一 / 视频不出** → 该动作确实重新读了磁盘（缓存被卸载了）。
   - **无任何异常且视频照常** → 命中缓存，**没有重读**。
   - 既不报错也不播（异常被吞）→ 用方法 C。
6. 脚本自己会在超时后释放句柄，无需手工清理；确认没有残留进程。

---

## 4. 方法 C（验证"追加帧"路线本身，唯一需要改文件的步骤）

目的：验证"在既有 bank 里新加一个 segment 条目，能否在不重启进程的情况下被看到"。

### 4.1 备份

```bash
cp "<G>/WorldApart_Data/StreamingAssets/MergedVideoResources~/cine/9907001/Video_cine_9907001_bank_1.mp4.segments.json" \
   "<工作目录>/ev3_backup_bank1.segments.json"
sha256sum "<工作目录>/ev3_backup_bank1.segments.json"
# 期望 3fadf7825daf88032052872f55db7b11847b2f2967b4128cf1bb81e2cbbc3e47
sha256sum "<G>/WorldApart_Data/StreamingAssets/MergedVideoResources~/cine/9907001/Video_cine_9907001_bank_1.mp4.segments.json"
# 期望同上
```

### 4.2 最小字节改动

在 `Segments` 数组的**最后一个 `}` 之后、`]` 之前**插入一个逗号和一个新对象。不做任何重排/重格式化，保留 UTF-8 BOM 与原有换行风格：

```json
,{"VPath": "cine/9907001:9001", "SourceIdentifier": "ev3-probe", "FrameCount": 450, "Timebase": 15360, "StartPTS": 0, "EndPTS": 460800}
```

说明：`EndPTS - StartPTS = 460800`，除以 Timebase 15360 = **30 秒**，落在该 bank 的 `0 – 2703360` PTS 区间内，必然可播——**先不追加 mp4 数据**，只用现有画面验证"新条目能不能被看见"，把两件事解耦。

用脚本做（推荐，避免手改破坏格式/编码）：

```
<工作目录>\tools\venv\Scripts\python.exe - <<'PY'
p = r"<G>\WorldApart_Data\StreamingAssets\MergedVideoResources~\cine\9907001\Video_cine_9907001_bank_1.mp4.segments.json"
raw = open(p, "rb").read()
add = b',{"VPath": "cine/9907001:9001", "SourceIdentifier": "ev3-probe", "FrameCount": 450, "Timebase": 15360, "StartPTS": 0, "EndPTS": 460800}'
i = raw.rfind(b"]")
assert raw[i-1:i] == b"}" or raw[i-1:i] == b"\n", raw[i-20:i]
open(p, "wb").write(raw[:i] + add + raw[i:])
print("patched")
PY
```

### 4.3 触发与观察

1. 进到 9907001 的 cine 场景（**不重启进程**，但需要先经历一次"该 bundle 被卸载"的动作：关掉再打开对应面板 / 结束并重开该 cine 会话）。
2. 用 Mod 侧直接调用验证，不要依赖剧情走到那个 clip：
   - `VideoBundlePlayer.HasSegment("cine/9907001:9001")` → true 表示新条目已被看见（= 重读生效）；
   - 或 `VideoResourcePlayer.PlayVideo("cine/9907001:9001")` 能否播出画面。
   - `VideoBundlePlayer.IterVPathsWithPrefixInBundle(...)` 里能否列到 `9907001:9001`。
3. 判定：
   - **能看到** → 「追加 segments.json 条目」在卸载/重载后即可生效，**不需要重启进程**。
   - **看不到**（仍是旧 22 条）→ 该路径没被卸载，必须先关面板/重进场景，或确实需要重启。
4. 如果 4.3 生效，再做一次**真·追加帧**：把新画面帧 append 到 `Video_cine_9907001_bank_1.mp4` 尾部，把新 segment 的 `StartPTS/EndPTS` 指向追加区间（`StartPTS = 原 TotalDurationPTS`），`TotalDurationPTS` 同步加大，重复 4.3 的观察。**这一步会改 mp4，务必同时备份 mp4 并记录 SHA256。**

### 4.4 还原（必做）

```bash
cp "<工作目录>/ev3_backup_bank1.segments.json" \
   "<G>/WorldApart_Data/StreamingAssets/MergedVideoResources~/cine/9907001/Video_cine_9907001_bank_1.mp4.segments.json"
sha256sum "<G>/WorldApart_Data/StreamingAssets/MergedVideoResources~/cine/9907001/Video_cine_9907001_bank_1.mp4.segments.json"
# 必须等于 3fadf7825daf88032052872f55db7b11847b2f2967b4128cf1bb81e2cbbc3e47
```

若做了 4.4 的 mp4 追加，同样把 mp4 还原并比对 SHA256。

---

## 5. 结果记录模板

```
方法：A / B / C
bank：cine/9907001/Video_cine_9907001_bank_1
冷启动首次播放：LoadMergedVideoAsync 调用次数 = ____
连续第二次播放（未退面板）：次数 = ____
退出重进后再播放：次数 = ____
UnloadAllBundlesImmediate 是否被调用：是 / 否
B 方法锁文件时新增日志行：____
C 方法 HasSegment("cine/9907001:9001")：true / false
还原后 SHA256 = 3fadf7825daf88032052872f55db7b11847b2f2967b4128cf1bb81e2cbbc3e47（一致 / 不一致）
```

---

## 6. 我这边**没有**做的验证

- 本次（E-V3 子任务）**未运行游戏进程、未改任何游戏文件**（详见 `v5_q2_ev3.md` §5）。
- 上面三条方法都**未执行**，全部留给客户端。任何"已验证"的说法都不适用于本文件。
