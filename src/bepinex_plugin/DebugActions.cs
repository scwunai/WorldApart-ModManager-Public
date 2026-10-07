using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LocalModManager
{
    /// <summary>
    /// v7 的"会改动游戏状态"的三件动作，全部经反射调用游戏自己的 API（不改表内存、不写文件）：
    ///   TryAddItem      —— P0 货币写入（BagModel.AddItem 的 Nullable/Action 参数用反射绕开类型名未知）
    ///   Teleport        —— P1 传送（Game.SpaceManager.GotoSpace，13 参）
    ///   StartQuestProc  —— P2 剧情跳跃（QuestManager.TryStartQuestProc 让游戏自己按 questId 起 flow）
    ///
    /// 另提供 MOD_DEBUG_JUMPSELFTEST=1 的自动联测（必须已载入存档才会触发）。
    /// </summary>
    static class DebugActions
    {
        internal static readonly bool SelfTest =
            Environment.GetEnvironmentVariable("MOD_DEBUG_JUMPSELFTEST") == "1";

        /// <summary>MOD_DEBUG_QUESTSCAN=1: walk the quest table, report the quests whose
        /// objective binds a non-zero activeFlowId, and run the jump on the first one.
        /// Needed because quest 11124 (v7) and quest 1001 both read activeFlowId=0.</summary>
        internal static readonly bool QuestScan =
            Environment.GetEnvironmentVariable("MOD_DEBUG_QUESTSCAN") == "1";

        private static readonly int TestSpace = EnvInt("MOD_DEBUG_TESTSPACE", 10001);
        private static readonly int TestQuest = EnvInt("MOD_DEBUG_TESTQUEST2", 11124);
        private static readonly float StoryAt = EnvF("MOD_DEBUG_JMP_STORYAT", 2f);
        private static readonly float TeleportAt = EnvF("MOD_DEBUG_JMP_TELEPORTAT", 50f);
        private static readonly float DoneAt = EnvF("MOD_DEBUG_JMP_DONEAT", 140f);

        internal static string Status = "(待机)";

        private static bool _sigLogged;
        private static float _t0;
        private static bool _tp, _story, _done;

        private static int EnvInt(string k, int d)
        {
            int v;
            var s = Environment.GetEnvironmentVariable(k);
            return (s != null && int.TryParse(s, out v)) ? v : d;
        }

        private static float EnvF(string k, float d)
        {
            float v;
            var s = Environment.GetEnvironmentVariable(k);
            return (s != null && float.TryParse(s, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out v)) ? v : d;
        }

        /// <summary>
        /// Walk TbQuest and report the quests whose objective #0 binds a non-zero
        /// activeFlowId; returns the first one found (or -1). Both quest ids tried so
        /// far (11124, 1001) read activeFlowId=0, i.e. no flow is bound at all, which
        /// is why TryStartQuestProc has nothing to start.
        /// </summary>
        internal static int ScanQuestWithFlow(int maxQuests)
        {
            try
            {
                var rows = Game.ConfigManager.Instance.Tables.TbQuest.DataList;
                if (rows == null) { Log("QUESTSCAN: TbQuest.DataList == null"); return -1; }
                int n = Mathf.Min(rows.Count, maxQuests);
                Log("QUESTSCAN: TbQuest.DataList=" + rows.Count + "，扫描前 " + n + " 条");
                int found = -1, logged = 0;
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (r == null) continue;
                    int qid;
                    try { qid = r.id.Value; } catch { continue; }
                    try
                    {
                        if (r.objectives == null || r.objectives.Count == 0) continue;
                        var o = r.objectives[0];
                        if (o == null) continue;
                        var af = o.activeFlowId;
                        if (af == null || !af.HasValue) continue;
                        int fid = af.Value.Value;
                        if (fid == 0) continue;
                        if (logged < 10) { logged++; Log("QUESTSCAN: quest " + qid + " objective#0 activeFlowId=" + fid); }
                        if (found < 0) found = qid;
                    }
                    catch { }
                }
                Log("QUESTSCAN: 命中 " + logged + " 条（最多打印 10 条），选中 quest=" + found);
                return found;
            }
            catch (Exception e)
            {
                Log("QUESTSCAN 失败 " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
        }

        private static void Log(string s)
        {
            Status = s;
            DebugTool.Observe("JMP " + s);
            Plugin.Logger.LogInfo("[DBG] JMP " + s);
        }

        private static float Now { get { return Time.unscaledTime; } }
        private static Game.Model.PlayerModel Player() { return Game.Model.GameStoreManager.CurrentPlayer; }

        // ------------------------------------------------------------ observation
        /// <summary>
        /// 空间观测。注意：实测 SpaceManager.get_CurrentSpaceId 走的是 IL2CPP 折叠共享桩，
        /// interop 调用会抛 TargetInvocationException（侦察报告 §0 的 RVA 不可信问题已复现），
        /// 因此以 CurrentSpaceUuid + 玩家 visited/left 标记为准。
        /// </summary>
        internal static string SpaceIdText()
        {
            var parts = new List<string>();
            try
            {
                var sm = Game.SpaceManager.Instance;
                if (sm == null) return "SpaceManager.Instance=null";
                try
                {
                    var c = sm.CurrentSpaceId;
                    parts.Add("spaceId=" + (c == null ? "null" : (c.HasValue ? c.Value.Value.ToString() : "none")));
                }
                catch (Exception e) { parts.Add("spaceId<err:" + e.GetType().Name + ">"); }
                parts.Add(UuidText());
                try { parts.Add("changing=" + sm.IsChangingSpace); } catch (Exception e) { parts.Add("changing<err:" + e.GetType().Name + ">"); }
            }
            catch (Exception e) { parts.Add("sm<err:" + e.GetType().Name + ">"); }
            try
            {
                var w = Game.Model.GameStoreManager.CurrentGameWorld;
                parts.Add("savedUuid=" + (w == null ? "null" : w.CurrentSavedSpaceUuid.ToString()));
            }
            catch (Exception e) { parts.Add("world<err:" + e.GetType().Name + ">"); }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>玩家侧的"到过/离开过该空间"标记——传送是否真的生效的持久化观测点。</summary>
        internal static string VisitedText(int spaceId)
        {
            try
            {
                var p = Game.Model.GameStoreManager.CurrentPlayer;
                if (p == null) return "visited(" + spaceId + ")=<no-player>";
                var id = new LubanDatas.TbSpaceId(spaceId);
                return "visited(" + spaceId + ")=" + p.HasVisitedSpace(id) + " left=" + p.HasLeftSpace(id);
            }
            catch (Exception e) { return "visited(" + spaceId + ")=<err:" + e.Message + ">"; }
        }

        internal static string QuestStateText(int questId)
        {
            try
            {
                var qm = Game.QuestManager.Instance;
                var id = new LubanDatas.TbQuestId(questId);
                var cfg = qm.GetQuestConfig(id);
                int n = (cfg == null || cfg.objectives == null) ? 0 : cfg.objectives.Count;
                int settled = 0;
                for (int i = 0; i < n; i++) if (qm.IsObjectiveSettled(id, i)) settled++;
                return "quest " + questId + " state=" + qm.GetQuestState(id) + " settled=" + settled + "/" + n
                    + " flows=" + QuestFlowIds(questId);
            }
            catch (Exception e) { return "quest " + questId + " <err:" + e.Message + ">"; }
        }

        /// <summary>objectives[].activeFlowId / submitFlowId —— quest → flow 映射（P2 用）。</summary>
        internal static string QuestFlowIds(int questId)
        {
            var parts = new List<string>();
            try
            {
                var cfg = Game.QuestManager.Instance.GetQuestConfig(new LubanDatas.TbQuestId(questId));
                if (cfg == null) return "cfg=null";
                try { parts.Add("acq=" + Nz(cfg.questAcqFlowId)); } catch (Exception e) { parts.Add("acq<err:" + e.Message + ">"); }
                try
                {
                    if (cfg.objectives != null)
                        for (int i = 0; i < cfg.objectives.Count; i++)
                        {
                            var o = cfg.objectives[i];
                            if (o == null) { parts.Add("#" + i + "=null"); continue; }
                            string a, s;
                            try { a = Nz(o.activeFlowId); } catch (Exception e) { a = "<err:" + e.Message + ">"; }
                            try { s = Nz(o.submitFlowId); } catch (Exception e) { s = "<err:" + e.Message + ">"; }
                            parts.Add("#" + i + " active=" + a + " submit=" + s);
                        }
                }
                catch (Exception e) { parts.Add("objectives<err:" + e.Message + ">"); }
                try { parts.Add("submit=" + Nz(cfg.questSubmitFlowId)); } catch (Exception e) { parts.Add("submit<err:" + e.Message + ">"); }
            }
            catch (Exception e) { parts.Add("<err:" + e.Message + ">"); }
            return parts.Count == 0 ? "-" : string.Join(" ", parts.ToArray());
        }

        private static string Nz(Il2CppSystem.Nullable<LubanDatas.TbGameActionFlowId> n)
        {
            if (n == null) return "null";
            return n.HasValue ? n.Value.Value.ToString() : "none";
        }

        /// <summary>SpaceUuid 是 struct 且没有可读 Value，"到过/离开过"标记才是可用观测点。</summary>
        internal static string UuidText()
        {
            try
            {
                var sm = Game.SpaceManager.Instance;
                if (sm == null) return "uuid=<no-sm>";
                var u = sm.CurrentSpaceUuid;
                return "uuid=" + (u == null ? "null" : u.ToString());
            }
            catch (Exception e) { return "uuid<err:" + e.Message + ">"; }
        }

        // -------------------------------------------------------------- P0 currency
        /// <summary>
        /// BagModel.AddItem(TbItemId, int, ItemSourceType, Nullable&lt;AddItemContext&gt;, Action)。
        /// 后两个参数在 interop 里的确切类型不方便硬写，这里用反射构造默认 Nullable 再调用；
        /// 第一次调用会把真实签名打进日志作为证据。
        /// </summary>
        internal static long AddItemReflect(string label, int itemId, int count)
        {
            var bag = Game.Model.GameStoreManager.CurrentPlayer == null ? null : Game.Model.GameStoreManager.CurrentPlayer.bag;
            if (bag == null) { Log(label + " 跳过：无背包"); return -1; }
            var tid = new LubanDatas.TbItemId(itemId);
            long before = bag.GetItemCount(tid);
            try
            {
                var ms = typeof(Game.Model.Components.BagModel).GetMethods();
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "AddItem") continue;
                    var ps = m.GetParameters();
                    if (!_sigLogged)
                    {
                        _sigLogged = true;
                        Log("AddItem interop 签名 " + m);
                    }
                    if (ps.Length != 5) continue;

                    object ctx = null;
                    try { ctx = Activator.CreateInstance(ps[3].ParameterType); }
                    catch (Exception e1) { Log("构造 ctx(" + ps[3].ParameterType.Name + ") 失败 " + e1.Message); }

                    // 回调参数：先试 null，再试给一个 no-op 委托
                    object[] attempts = new object[] { null, MakeNoop(ps[4].ParameterType) };
                    for (int a = 0; a < attempts.Length; a++)
                    {
                        try
                        {
                            m.Invoke(bag, new object[] { tid, count, Game.Model.ItemSourceType.GMCommand, ctx, attempts[a] });
                            long after = bag.GetItemCount(tid);
                            Log(label + " 货币写入成功 item=" + itemId + " 回调=" + (a == 0 ? "null" : "noop")
                                + " before=" + before + " after=" + after + " delta=" + (after - before));
                            return after;
                        }
                        catch (Exception ex)
                        {
                            var inner = ex.InnerException ?? ex;
                            Log(label + " 货币写入尝试" + a + " 失败 " + inner.GetType().Name + ": " + inner.Message);
                        }
                    }
                    break;
                }
            }
            catch (Exception e) { Log(label + " 货币写入异常 " + e.GetType().Name + ": " + e.Message); }
            long b = bag.GetItemCount(tid);
            Log(label + " 货币写入未生效 item=" + itemId + " count=" + b);
            return b;
        }

        private static object MakeNoop(Type delegateType)
        {
            try
            {
                if (!typeof(Delegate).IsAssignableFrom(delegateType)) return null;
                var mi = delegateType.GetMethod("Invoke");
                if (mi == null || mi.ReturnType != typeof(void)) return null;
                var ps = mi.GetParameters();
                if (ps.Length != 1) return null;
                // 只处理 Action<T>：用 DelegateSupport 造一个空实现
                var t = ps[0].ParameterType;
                var helper = typeof(DebugActions).GetMethod("NoopOne", BindingFlags.NonPublic | BindingFlags.Static);
                var generic = helper.MakeGenericMethod(t);
                return Delegate.CreateDelegate(delegateType, generic);
            }
            catch { return null; }
        }

        private static void NoopOne<T>(T _) { }

        // --------------------------------------------------------------- P1 teleport
        /// <summary>
        /// Game.SpaceManager.Instance.GotoSpace(Nullable&lt;TbSpaceId&gt; spaceId, 12 个可选参数)。
        /// 非 async、不需要 ProcContext：调用即入队，迁移由 SpaceManager.OnUpdate 异步跑完。
        /// 两个 Nullable 参数（loadingStyleId / battleExitResult）用 Activator 造默认值。
        /// </summary>
        internal static bool Teleport(int spaceId, bool force, bool ignoreWorldStatus)
        {
            Log("传送请求 spaceId=" + spaceId + " 之前 " + SpaceIdText());
            try
            {
                var sm = Game.SpaceManager.Instance;
                if (sm == null) { Log("传送失败：SpaceManager.Instance == null"); return false; }
                var ms = typeof(Game.SpaceManager).GetMethods();
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "GotoSpace") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 13) continue;
                    object boxed = null;
                    try { boxed = Activator.CreateInstance(ps[0].ParameterType, new object[] { new LubanDatas.TbSpaceId(spaceId) }); }
                    catch (Exception e1) { Log("构造 spaceId Nullable(" + ps[0].ParameterType.Name + ") 失败 " + e1.Message); }
                    object loading = SafeNew(ps[6].ParameterType);
                    object battle = SafeNew(ps[10].ParameterType);
                    var args = new object[]
                    {
                        boxed, null, null, null, force, false, loading, null, 0, ignoreWorldStatus, battle, null, null
                    };
                    m.Invoke(sm, args);
                    Log("传送已提交 spaceId=" + spaceId + " force=" + force + " ignoreWorldStatus=" + ignoreWorldStatus);
                    return true;
                }
                Log("传送失败：没找到 13 参的 GotoSpace");
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Log("传送异常 " + inner.GetType().Name + ": " + inner.Message);
            }
            return false;
        }

        private static object SafeNew(Type t)
        {
            try { return Activator.CreateInstance(t); } catch { return null; }
        }

        // ------------------------------------------------------------ P2 story jump
        /// <summary>
        /// QuestManager.TryStartQuestProc(TbQuestId, Quest, Action&lt;FlowResult&gt;) —— 游戏自己按 questId 起 flow。
        /// </summary>
        internal static bool StartQuestProc(int questId)
        {
            Log("剧情跳跃请求 quest=" + questId + " 之前 " + QuestStateText(questId));
            try
            {
                var qm = Game.QuestManager.Instance;
                var tid = new LubanDatas.TbQuestId(questId);
                var cfg = qm.GetQuestConfig(tid);
                if (cfg == null) { Log("剧情跳跃失败：quest " + questId + " 配置为空"); return false; }

                // 顺序修正（v9）：先让游戏按 questId 起流程；只有在流程入口拒绝时才退回
                // 官方 DebugForceAcceptQuest。反过来（v7 的实现）会先把任务置为 InProgress，
                // 而 TryStartQuestProc / TryAcceptQuest 都是"接取时"入口，对已在跑的任务必然
                // 返回 False —— v9 实测日志见实施报告 §2。
                var ms = typeof(Game.QuestManager).GetMethods();
                bool started = false;
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "TryStartQuestProc") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 3) continue;
                    object cb = null;
                    try { cb = MakeNoop(ps[2].ParameterType); } catch { }
                    object ret = m.Invoke(qm, new object[] { tid, cfg, cb });
                    started = ret is bool && (bool)ret;
                    Log("剧情跳跃 TryStartQuestProc(先) 返回 " + (ret == null ? "null" : ret.ToString())
                        + " 回调=" + (cb == null ? "null" : "noop")
                        + " → " + QuestBrief(questId) + " | " + FlowsText());
                    break;
                }

                if (!started)
                {
                    try
                    {
                        string st0 = qm.GetQuestState(tid).ToString();
                        if (st0 == "Available")
                        {
                            bool acc = qm.DebugForceAcceptQuest(tid);
                            Log("剧情跳跃兜底：任务未接取，DebugForceAcceptQuest rc=" + acc + " → " + qm.GetQuestState(tid)
                                + " | " + FlowsText());
                        }
                        else Log("剧情跳跃兜底跳过：state=" + st0);
                    }
                    catch (Exception e) { Log("剧情跳跃兜底异常 " + e.GetType().Name + ": " + e.Message); }
                }

                try
                {
                    Log("剧情跳跃诊断 HasAnyObjectiveFlow(" + questId + ")= "
                        + Game.QuestManager.HasAnyObjectiveFlow(cfg)
                        + " objectives=" + (cfg.objectives == null ? -1 : cfg.objectives.Count)
                        + " state=" + qm.GetQuestState(tid) + " accepted=" + qm.IsQuestAccepted(tid)
                        + " inProgress=" + qm.IsQuestInProgress(tid));
                }
                catch (Exception e) { Log("剧情跳跃诊断异常 " + e.GetType().Name + ": " + e.Message); }

                // 备选路线（v9）：官方"接取并跑流程"入口 TryAcceptQuest(TbQuestId, Action<FlowResult>)。
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "TryAcceptQuest") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 2) continue;
                    object cb2 = null;
                    try { cb2 = MakeNoop(ps[1].ParameterType); } catch { }
                    object ret2 = null;
                    try { ret2 = m.Invoke(qm, new object[] { tid, cb2 }); }
                    catch (Exception e2)
                    {
                        var i2 = e2.InnerException ?? e2;
                        Log("剧情跳跃备选 TryAcceptQuest 异常 " + i2.GetType().Name + ": " + i2.Message);
                        continue;
                    }
                    Log("剧情跳跃备选 TryAcceptQuest 返回 " + (ret2 == null ? "null" : ret2.ToString())
                        + " → " + QuestBrief(questId) + " | " + FlowsText());
                    break;
                }
                return true;

                /* v7 的顺序（保留备查，勿改回）：
                // TryStartQuestProc 只在任务已接取时有效；未接取先走官方 DebugForceAcceptQuest
                try
                {
                    string st = qm.GetQuestState(tid).ToString();
                    if (st == "Available")
                    {
                        bool acc = qm.DebugForceAcceptQuest(tid);
                        Log("剧情跳跃前置：任务未接取，DebugForceAcceptQuest rc=" + acc + " → " + qm.GetQuestState(tid));
                    }
                }
                catch (Exception e) { Log("剧情跳跃前置异常 " + e.GetType().Name + ": " + e.Message); }
                */

#pragma warning disable CS0162
#pragma warning restore CS0162
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Log("剧情跳跃异常 " + inner.GetType().Name + ": " + inner.Message);
            }
            return false;
        }

        // --------------------------------------------------- MOD_DEBUG_JUMPSELFTEST
        /// <summary>
        /// 自动联测（v9：观察窗扩到 140 s，且每次采样同时打任务与流程读数）。
        /// 顺序：先剧情跳跃（世界必须稳定），再传送（传送会卸载/重载世界）。
        /// MOD_DEBUG_TESTSPACE&lt;=0 时跳过传送，用于 P3 的单剧情跳跃干净复测。
        /// </summary>
        internal static void Tick()
        {
            if (!SelfTest || _done) return;
            if (_t0 == 0f)
            {
                if (Player() == null) return;           // 等存档载入
                _t0 = Now;
                Log("JUMPSELFTEST begin space=" + TestSpace + " quest=" + TestQuest
                    + " storyAt=" + StoryAt + "s teleportAt=" + TeleportAt + "s doneAt=" + DoneAt + "s"
                    + " questScan=" + QuestScan);
                Log("起点 " + SpaceIdText() + " | " + VisitedText(TestSpace));
                Log("起点 " + QuestBrief(TestQuest) + " | " + FlowsText());
                return;
            }
            float e = Now - _t0;

            if (!_story && e > StoryAt)
            {
                _story = true;
                int q = TestQuest;
                if (QuestScan)
                {
                    int found = ScanQuestWithFlow(2000);
                    if (found > 0) q = found;
                    _scannedQuest = q;
                }
                else Log("剧情跳跃前 " + QuestBrief(TestQuest) + " | " + FlowsText());
                StartQuestProc(q);
                return;
            }
            for (int i = 0; i < StoryTimes.Length; i++)
            {
                if (!_storyLog[i] && e > StoryTimes[i])
                {
                    _storyLog[i] = true;
                    int q = _scannedQuest > 0 ? _scannedQuest : TestQuest;
                    Log("剧情+" + StoryTimes[i] + "s " + QuestBrief(q) + " | " + FlowsText());
                    return;
                }
            }
            if (!_tp && e > TeleportAt)
            {
                _tp = true;
                if (TestSpace > 0)
                {
                    Log("传送前 " + VisitedText(TestSpace) + " | player=" + (Player() == null ? "null" : "ok"));
                    Teleport(TestSpace, true, true);
                }
                else Log("跳过传送（MOD_DEBUG_TESTSPACE<=0）");
                return;
            }
            for (int i = 0; i < LogTimes.Length; i++)
            {
                if (!_tpLog[i] && e > LogTimes[i])
                {
                    _tpLog[i] = true;
                    Log("+" + LogTimes[i] + "s " + SpaceIdText() + " | " + VisitedText(TestSpace)
                        + " | player=" + (Player() == null ? "null" : "ok"));
                    return;
                }
            }
            if (e > DoneAt) { _done = true; Log("JUMPSELFTEST done"); }
        }

        private static int _scannedQuest = -1;

        /// <summary>紧凑任务读数（P3 用；QuestStateText 还带 flow id 映射，太长不适合作时间序列）。</summary>
        internal static string QuestBrief(int questId)
        {
            try
            {
                var qm = Game.QuestManager.Instance;
                var id = new LubanDatas.TbQuestId(questId);
                var cfg = qm.GetQuestConfig(id);
                int n = (cfg == null || cfg.objectives == null) ? 0 : cfg.objectives.Count;
                int settled = 0;
                for (int i = 0; i < n; i++) if (qm.IsObjectiveSettled(id, i)) settled++;
                return "quest " + questId + " state=" + qm.GetQuestState(id) + " settled=" + settled + "/" + n;
            }
            catch (Exception e) { return "quest " + questId + " <err:" + e.Message + ">"; }
        }

        /// <summary>"flow 真的在跑"的读数：调度器不静止 = 有流程在推进。</summary>
        internal static string FlowsText()
        {
            var sb = new List<string>();
            try
            {
                var f = Game.FlowRuntimeManager.Instance;
                if (f == null) return "flows=<no-manager>";
                sb.Add("loaded=" + f.FlowsLoadedAndTranslated);
                try
                {
                    var s = f.Scheduler;
                    sb.Add(s == null ? "sched=null"
                                     : "sched[quiescent=" + s.IsQuiescent + " draining=" + s.IsDraining
                                       + " shuttingDown=" + s.IsShuttingDown + "]");
                }
                catch (Exception e) { sb.Add("sched<err:" + e.GetType().Name + ">"); }
                try
                {
                    var chains = f.FindInFlightChangedQuestChains();
                    sb.Add("inFlightChains=" + (chains == null ? -1 : chains.Count));
                }
                catch (Exception e) { sb.Add("inFlight<err:" + e.GetType().Name + ">"); }
            }
            catch (Exception e) { sb.Add("flows<err:" + e.GetType().Name + ">"); }
            return string.Join(" ", sb.ToArray());
        }

        private static readonly float[] StoryTimes = new float[] { 4f, 8f, 12f, 20f, 30f, 42f };
        private static readonly bool[] _storyLog = new bool[6];
        private static readonly float[] LogTimes = new float[] { 56f, 62f, 70f, 82f, 92f, 105f, 118f, 130f };
        private static readonly bool[] _tpLog = new bool[8];

        /// <summary>把自动联测的进度接进 F9 观察区（由 DebugTool.OnGui 调用）。</summary>
        internal static string SelfTestLine()
        {
            return SelfTest ? ("联测: t0=" + _t0.ToString("F1") + " tp=" + _tp + " story=" + _story + " done=" + _done) : null;
        }
    }
}
