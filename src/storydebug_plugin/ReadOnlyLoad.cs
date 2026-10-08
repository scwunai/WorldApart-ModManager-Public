using System;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace LocalModManager
{
    /// <summary>
    /// v9 P3/P4 driver: load a save slot **read-only** so the F9 剧情跳跃 / 传送 self
    /// test (MOD_DEBUG_JUMPSELFTEST=1) has a stable world to start from without
    /// anyone clicking through the menus, and without touching WriteTest's state
    /// machine (P5 only re-runs it).
    ///
    ///   MOD_DEBUG_LOADONLY=1             enable this driver
    ///   MOD_DEBUG_LOADSLOT=auto_02       slot to load (required)
    ///   MOD_DEBUG_TESTSTORAGE=&lt;uuid&gt;    storage to operate in (optional; a
    ///                                    read-only scan picks the newest otherwise)
    ///
    /// Save-safety protocol (same as v7 §3/P0): this class never writes a slot or a
    /// file. It calls SuppressAutoSave() for the whole session and only ever selects
    /// the named slot in the named storage.
    /// </summary>
    static class ReadOnlyLoad
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_DEBUG_LOADONLY") == "1";

        private static readonly string StorageOverride = Env("MOD_DEBUG_TESTSTORAGE");
        private static readonly string SlotOverride = Env("MOD_DEBUG_LOADSLOT");

        /// <summary>v11 P2：载档等待改成事件式。
        /// `MOD_DEBUG_LOADAT` 未设或 `auto`（默认）→ 等 `BootWatch` 的"开机流程收尾"事件，
        /// 超时 `MOD_DEBUG_LOADAT_TIMEOUT`（默认 60 s）兜底；
        /// `MOD_DEBUG_LOADAT=&lt;秒&gt;` → 保留为手工覆盖（至少等到该 unscaledTime）。</summary>
        private static readonly string LoadAtRaw = Env("MOD_DEBUG_LOADAT");
        private static readonly bool LoadWaitAuto =
            string.IsNullOrEmpty(LoadAtRaw) || LoadAtRaw.Equals("auto", StringComparison.OrdinalIgnoreCase);
        private static readonly float LoadAt = EnvF("MOD_DEBUG_LOADAT", 0f);
        private static readonly float AutoLoadTimeout = EnvF("MOD_DEBUG_LOADAT_TIMEOUT", 60f);

        private static float EnvF(string k, float d)
        {
            float v;
            var s = Environment.GetEnvironmentVariable(k);
            return (s != null && float.TryParse(s, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out v)) ? v : d;
        }

        internal static string Status = "(未启用)";

        private static int _step;
        private static bool _done;
        private static float _deadline;
        private static float _nextLog;
        private static Il2CppSystem.IDisposable _noAutoSave;
        private static bool _waitLogged;
        private static string _storage;
        private static string _slot;

        private static string Env(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private static void Log(string s)
        {
            Status = s;
            DebugTool.Observe(s);
            StoryDebugPlugin.Logger.LogInfo("[DBG] " + s);
        }

        private static float Now { get { return Time.unscaledTime; } }
        private static void Next() { _step++; _deadline = 0f; _nextLog = 0f; }
        private static Game.Model.PlayerModel Player() { return Game.Model.GameStoreManager.CurrentPlayer; }
        private static string Safe(Func<string> f)
        {
            try { return f() ?? "null"; } catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        /// <summary>Read-only scan of the save root for the newest gameworld.msgpack.</summary>
        private static void Scan(out string storage, out string slot)
        {
            storage = null; slot = null;
            try
            {
                var mgr = Game.Model.GameStoreManager.Instance;
                if (mgr == null) { Log("LOADONLY scan: GameStoreManager.Instance == null"); return; }
                string root = mgr.RootSavePath;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
                DateTime best = DateTime.MinValue;
                var storages = Directory.GetDirectories(root);
                for (int i = 0; i < storages.Length; i++)
                {
                    string sid = Path.GetFileName(storages[i]);
                    string slotsRoot = Path.Combine(storages[i], "slots");
                    if (!Directory.Exists(slotsRoot)) continue;
                    var slotDirs = Directory.GetDirectories(slotsRoot);
                    for (int j = 0; j < slotDirs.Length; j++)
                    {
                        string gw = Path.Combine(slotDirs[j], "gameworld.msgpack");
                        if (!File.Exists(gw)) continue;
                        var t = File.GetLastWriteTime(gw);
                        if (t > best) { best = t; storage = sid; slot = Path.GetFileName(slotDirs[j]); }
                    }
                }
                if (storage != null)
                    Log("LOADONLY scan: newest storage=" + storage + " slot=" + slot + " @" + best.ToString("MM-dd HH:mm"));
            }
            catch (Exception e) { Log("LOADONLY scan failed: " + e.Message); }
        }

        internal static void Tick()
        {
            if (!Enabled || _done) return;
            try
            {
                switch (_step)
                {
                    case 0:
                        {
                            string s, l;
                            Scan(out s, out l);
                            _storage = StorageOverride ?? s;
                            _slot = SlotOverride ?? l;
                            Log("LOADONLY plan storage=" + (_storage ?? "<null>") + " slot=" + (_slot ?? "<null>")
                                + " (只读载入，全程 SuppressAutoSave)");
                            if (_slot == null) { Log("LOADONLY aborted: no slot (set MOD_DEBUG_LOADSLOT)"); _done = true; return; }
                            Next();
                            break;
                        }
                    case 1:
                        {
                            // v11 P2：默认事件式等待 —— 等开机流程真正收尾，而不是猜 45 s。
                            if (LoadWaitAuto)
                            {
                                bool evtOk = !BootWatch.Installed || BootWatch.UnloadSeen;
                                if (!evtOk && Now < AutoLoadTimeout)
                                {
                                    if (Now > _nextLog)
                                    {
                                        _nextLog = Now + 5f;
                                        Log("LOADONLY 0: 等开机流程收尾事件（UnloadStore）t=" + Now.ToString("F1")
                                            + "s，超时=" + AutoLoadTimeout + "s");
                                    }
                                    return;
                                }
                                if (!_waitLogged)
                                {
                                    _waitLogged = true;
                                    Log("LOADONLY 0: 开机流程收尾（"
                                        + (BootWatch.UnloadSeen ? "事件 UnloadStore 已见" : "超时兜底")
                                        + "，t=" + Now.ToString("F1") + "s）→ 开始载档");
                                }
                            }
                            if (Now < LoadAt)
                            {
                                if (Now > _nextLog)
                                {
                                    _nextLog = Now + 5f;
                                    Log("LOADONLY 0: 手工 LOADAT=" + LoadAt + "s 未到（t=" + Now.ToString("F1") + "s）");
                                }
                                return;
                            }
                            bool tables = false, flows = false;
                            try
                            {
                                var c = Game.ConfigManager.Instance;
                                tables = c != null && c.Tables != null && c.Tables.TbQuest != null && c.Tables.TbNpcBaseCfg != null;
                            }
                            catch { }
                            try { var f = Game.FlowRuntimeManager.Instance; flows = f != null && f.FlowsLoadedAndTranslated; } catch { }
                            if (tables)
                            {
                                Log("LOADONLY 1a: 配置表就绪 t=" + Now.ToString("F1") + "s flowsLoaded=" + flows);
                                Next();
                                return;
                            }
                            if (_deadline == 0f) _deadline = Now + 240f;
                            if (Now > _nextLog) { _nextLog = Now + 10f; Log("LOADONLY 1a: 等待配置表 t=" + Now.ToString("F1") + "s"); }
                            if (Now > _deadline) { Log("LOADONLY 1a: 等待就绪超时，仍继续"); Next(); }
                            break;
                        }
                    case 2:
                        {
                            var mgr = Game.Model.GameStoreManager.Instance;
                            if (mgr == null) { Log("LOADONLY: GameStoreManager.Instance == null"); _done = true; return; }
                            try { _noAutoSave = mgr.SuppressAutoSave(); Log("LOADONLY 2: 已抑制自动存档"); }
                            catch (Exception e) { Log("LOADONLY 2: SuppressAutoSave 失败 " + e.Message); }

                            if (_storage != null)
                            {
                                try { mgr.SelectStorage(_storage, _slot); Log("LOADONLY 2: SelectStorage → storage=" + Safe(() => mgr.SelectedStorageId) + " slot=" + Safe(() => mgr.SelectedSlotId)); }
                                catch (Exception e) { Log("LOADONLY 2: SelectStorage 异常 " + e.GetType().Name + ": " + e.Message); }
                                try { mgr.LoadStorage(_storage); Log("LOADONLY 2: LoadStorage 返回 SelectedStorageId=" + Safe(() => mgr.SelectedStorageId)); }
                                catch (Exception e) { Log("LOADONLY 2: LoadStorage 异常 " + e.GetType().Name + ": " + e.Message); }
                            }
                            try { bool rc = mgr.LoadCurrentSlot(_slot); Log("LOADONLY 2: LoadCurrentSlot(" + _slot + ") rc=" + rc); }
                            catch (Exception e) { Log("LOADONLY 2: LoadCurrentSlot 异常 " + e.GetType().Name + ": " + e.Message); }

                            _deadline = Now + 180f;
                            Log("LOADONLY 2: 等待世界载入…");
                            Next();
                            break;
                        }
                    case 3:
                        {
                            if (Player() != null)
                            {
                                var w = Game.Model.GameStoreManager.CurrentGameWorld;
                                Log("LOADONLY READY t=" + Now.ToString("F1") + "s storage=" + Safe(() => Game.Model.GameStoreManager.Instance.SelectedStorageId)
                                    + " slot=" + Safe(() => Game.Model.GameStoreManager.Instance.SelectedSlotId)
                                    + " savedSpaceUuid=" + (w == null ? "null" : w.CurrentSavedSpaceUuid.ToString()));

                                // P3: loading the slot through the manager API skips the
                                // step the normal "continue" path runs - translating the
                                // quest table's flow references. Without it
                                // FlowRuntimeManager.FlowsLoadedAndTranslated stays False
                                // and every objective's activeFlowId reads 0 at runtime
                                // (the config table says 11118 for quest 1001), so
                                // QuestManager.TryStartQuestProc has nothing to start and
                                // returns False. Do it here so the harness reaches the same
                                // state a normal load leaves behind.
                                try
                                {
                                    var f = Game.FlowRuntimeManager.Instance;
                                    if (f == null) Log("LOADONLY flows: FlowRuntimeManager.Instance == null");
                                    else
                                    {
                                        Log("LOADONLY flows before=" + f.FlowsLoadedAndTranslated);
                                        if (!f.FlowsLoadedAndTranslated)
                                        {
                                            // The flow build refuses to run before the Luban
                                            // expression namespaces are bound - its own error says
                                            // "LoadFlowsAndTranslateQuestTable 必须在
                                            // LubanExprManager.OnGameLoaded 之后调用". The game runs
                                            // that step when it enters the world through its own
                                            // UI; loading the slot through the manager API skips it.
                                            try
                                            {
                                                var lem = Game.LubanExprManager.Instance;
                                                if (lem == null) Log("LOADONLY expr: LubanExprManager.Instance == null");
                                                else { lem.OnGameLoaded(); Log("LOADONLY expr: LubanExprManager.OnGameLoaded() 已调用"); }
                                            }
                                            catch (Exception e2)
                                            {
                                                var i2 = e2.InnerException ?? e2;
                                                Log("LOADONLY expr 失败 " + i2.GetType().Name + ": " + i2.Message);
                                            }
                                            f.LoadFlowsAndTranslateQuestTable();
                                            Log("LOADONLY flows after LoadFlowsAndTranslateQuestTable=" + f.FlowsLoadedAndTranslated);
                                        }
                                        Log("LOADONLY flows final=" + f.FlowsLoadedAndTranslated
                                            + " | quest " + Safe(() => DebugActions.QuestFlowIds(1001)));
                                    }
                                }
                                catch (Exception e)
                                {
                                    var inner = e.InnerException ?? e;
                                    Log("LOADONLY flows 失败 " + inner.GetType().Name + ": " + inner.Message);
                                }

                                // P3: loading a slot through the manager API also skips the
                                // world-session registration the normal entry performs. Without
                                // it every business proc refuses with "StartInWorld：当前无世界会话
                                // （标题界面/世界未 Alloc）". Publishing a ProcHostResource is what
                                // the world entry does; FlowRuntimeManager.PublishWorldProcHost is
                                // internal, so it is invoked through reflection.
                                try
                                {
                                    var f = Game.FlowRuntimeManager.Instance;
                                    var m = AccessTools.Method(typeof(Game.FlowRuntimeManager), "PublishWorldProcHost");
                                    if (m == null) Log("LOADONLY worldhost: PublishWorldProcHost not found");
                                    else
                                    {
                                        var pt = m.GetParameters()[0].ParameterType;
                                        var schedProp = AccessTools.Property(typeof(Game.FlowRuntimeManager), "Scheduler");
                                        object sched = schedProp == null ? null : schedProp.GetValue(f);
                                        var ctor = pt.GetConstructors()[0];
                                        var cps = ctor.GetParameters();
                                        var args = new object[cps.Length];
                                        args[0] = sched;
                                        for (int i = 1; i < cps.Length; i++)
                                        {
                                            var t = cps[i].ParameterType;
                                            if (t.IsEnum) args[i] = Enum.ToObject(t, 5);
                                            else if (cps[i].HasDefaultValue) args[i] = cps[i].DefaultValue;
                                            else args[i] = Activator.CreateInstance(t);
                                        }
                                        object host = ctor.Invoke(args);
                                        m.Invoke(f, new object[] { host });
                                        Log("LOADONLY worldhost: PublishWorldProcHost(" + pt.Name + ") 已调用");
                                    }
                                }
                                catch (Exception e)
                                {
                                    var inner = e.InnerException ?? e;
                                    Log("LOADONLY worldhost 失败 " + inner.GetType().Name + ": " + inner.Message);
                                }

                                Log("LOADONLY 起点 " + DebugActions.SpaceIdText() + " | " + DebugActions.VisitedText(10001));
                                _done = true;
                                return;
                            }
                            if (_deadline > 0f && Now > _deadline)
                            {
                                _deadline = Now + 300f;
                                Log("LOADONLY 3: 仍在等待玩家（再等 300s）CurrentPlayer=null");
                            }
                            if (Now > _nextLog) { _nextLog = Now + 15f; Log("LOADONLY 3: 等待中 t=" + Now.ToString("F1") + "s"); }
                            break;
                        }
                }
            }
            catch (Exception e)
            {
                _done = true;
                Status = "异常终止";
                Log("LOADONLY FATAL " + e.GetType().Name + ": " + e.Message);
            }
        }
    }
    /// <summary>
    /// v11 P2：开机流程"收尾"事件观察。`BootLoadStorage`（开机命令）结束时执行
    /// `GameStoreManager.UnloadStore()`（RVA 0xE2D9C0，v10 报告 §4.1 已定位），
    /// 这是"现在可以安全载档"的唯一可靠事件 —— 单看状态量（`CurrentStore == null`）
    /// 在整段开机过程中都成立，区分不出来（v10 用 45 s 硬编码就是为此）。
    /// 只挂一个**无参 prefix** 置位，不读任何参数、不改返回值；
    /// 若本机对 il2cpp 钩子敏感，可用 `MOD_DEBUG_LOADAT_EVT=0` 关闭并退回超时等待。
    /// </summary>
    static class BootWatch
    {
        internal static bool UnloadSeen;
        internal static bool Installed;

        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_DEBUG_LOADAT_EVT") != "0";

        internal static void Install()
        {
            if (!Enabled) { StoryDebugPlugin.Logger.LogInfo("[BOOT] 事件观察已由 MOD_DEBUG_LOADAT_EVT=0 关闭（退回超时等待）"); return; }
            try
            {
                var m = HarmonyLib.AccessTools.Method(typeof(Game.Model.GameStoreManager), "UnloadStore");
                if (m == null) { StoryDebugPlugin.Logger.LogWarning("[BOOT] UnloadStore 未找到，退回超时等待"); return; }
                new HarmonyLib.Harmony("local.modmanager.bootwatch").Patch(
                    m, prefix: new HarmonyLib.HarmonyMethod(
                        HarmonyLib.AccessTools.Method(typeof(BootWatch), nameof(Pre))));
                Installed = true;
                StoryDebugPlugin.Logger.LogInfo("[BOOT] 已观察 GameStoreManager.UnloadStore（开机流程收尾事件）");
            }
            catch (Exception e)
            {
                StoryDebugPlugin.Logger.LogWarning("[BOOT] 装钩失败，退回超时等待: " + e.Message);
            }
        }

        private static void Pre()
        {
            try
            {
                if (UnloadSeen) return;
                UnloadSeen = true;
                StoryDebugPlugin.Logger.LogInfo("[BOOT] UnloadStore 被调用 → 判定开机流程收尾 t="
                                      + Time.unscaledTime.ToString("F1") + "s");
            }
            catch { }
        }
    }
}
