using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
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

        // v10 P1: 传送路线选择（见 Teleport 的 recon 注释）
        private static readonly string TpMode = EnvStr("MOD_DEBUG_TPMODE", "auto");
        private static readonly bool TpLoadingWait = EnvStr("MOD_DEBUG_TPLWAIT", "1") != "0";

        internal static string Status = "(待机)";

        private static bool _sigLogged;
        private static float _t0;
        private static bool _tp, _story, _done;
        private static int _lastTarget = -1;   // 最近一次传送的目标 spaceId（采样行用它打 visited）

        private static int EnvInt(string k, int d)
        {
            int v;
            var s = Environment.GetEnvironmentVariable(k);
            return (s != null && int.TryParse(s, out v)) ? v : d;
        }

        private static string EnvStr(string k, string d)
        {
            var s = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(s) ? d : s;
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
            StoryDebugPlugin.Logger.LogInfo("[DBG] JMP " + s);
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
        /// v10 P1. Whole-chain disassembly (实施报告v10.md §1.1):
        ///
        ///   GotoSpace(TbSpaceId?, …)                     0x15F1390
        ///     └─ 解出实例 uuid（含地牢/房间特例分支）
        ///        └─ GotoSpaceInstance(SpaceUuid, …)      0x15F1190  ← 所谓"路线 B"
        ///             └─ GotoTargetHandler(handler, …)   0x15F18A0
        ///                  ├─ SpaceTransitionContextFactory.Create
        ///                  └─ GotoSpaceForce(request)    0x15F0D90  （入队 ChangeSpaceRequest）
        ///   SpaceManager.OnUpdate                          0x15F2CE0
        ///     └─ TryChangeSpaceAsync()                   0x15F8380  （真正拆/建世界的状态机）
        ///          └─ ChangeSpaceRequest.UseLoadingWaitPanel 选用 ITransitionScreen
        ///             （LoadingWaitPanel : ITransitionScreen）；v9 传的是 false。
        ///
        /// 即 GotoSpaceInstance 是 GotoSpace 的**内层调用**，不是并列路线；v9 真正传错的
        /// 是 UseLoadingWaitPanel。这里两条路线都实现、都显式记录，失败给明确拒绝原因，
        /// 不做静默换路线。
        ///   MOD_DEBUG_TPMODE  = auto(默认) | gotoinstance | gotospace
        ///   MOD_DEBUG_TPLWAIT = 0/1（默认 1：走加载等待面板）
        ///   MOD_DEBUG_TPDIAG  = 1 安装传送链跟踪钩子（只读日志）
        /// </summary>
        internal static bool Teleport(int spaceId, bool force, bool ignoreWorldStatus)
        {
            // spaceId == 0 → 自动选点（仅测试驱动用）；< 0 → 明确拒绝（不能落进自动选点）
            if (spaceId < 0) { Log("传送拒绝：spaceId=" + spaceId + " 非法（-1 表示输入框解析失败）"); return false; }
            if (spaceId == 0) spaceId = PickSpace(60);
            if (spaceId <= 0) { Log("传送失败：MOD_DEBUG_TESTSPACE<=0 时自动选目标也失败（见 TPSCAN 行）"); return false; }
            _lastTarget = spaceId;
            Log("传送请求 spaceId=" + spaceId + " 之前 " + SpaceIdText());
            Log("传送路线 mode=" + TpMode + " useLoadingWaitPanel=" + TpLoadingWait
                + " force=" + force + " ignoreWorldStatus=" + ignoreWorldStatus);
            bool submitted = false;
            if (TpMode == "auto" || TpMode == "gotoinstance")
                submitted |= TeleportViaInstance(spaceId, force, ignoreWorldStatus);
            if (!submitted && (TpMode == "auto" || TpMode == "gotospace"))
            {
                if (TpMode == "auto")
                    Log("传送路线：route B 未提交请求，显式改用 route A′（GotoSpace + 加载等待面板，同一 chain 的 id 解析入口）");
                submitted |= TeleportViaSpaceId(spaceId, force, ignoreWorldStatus);
            }
            if (!submitted) Log("传送失败：两条路线都没有提交请求（拒绝原因见上面各行）");
            return submitted;
        }

        /// <summary>路线 B：GotoSpaceInstance(uuid)。uuid 必须由游戏自己解析/创建出来——
        /// v10 第一轮实测的拒绝原因就是 "space 10001 has no resident instance"，
        /// 说明直调前必须先像游戏旅行链那样把目标空间实例建出来。顺序：
        ///   ResolveHandlerInCurrentContext（当前上下文，等价游戏旅行链的第一步）
        ///   → GetResidentSpaceHandler → ResolveSpaceHandler
        ///   → 都没有则 CreateGameplaySpaceHandler(id, SpaceInstanceKindResolver.ResolveFromConfig(id), 当前 handler 的 uuid)
        /// </summary>
        private static bool TeleportViaInstance(int spaceId, bool force, bool ignoreWorldStatus)
        {
            try
            {
                var sm = Game.SpaceManager.Instance;
                if (sm == null) { Log("传送[B] 拒绝：SpaceManager.Instance == null"); return false; }
                var id = new LubanDatas.TbSpaceId(spaceId);

                // 先拿"当前空间"的 uuid：ResolveHandlerInCurrentContext(id) 在目标就是当前空间时
                // 会返回当前 handler，把它交给 GotoSpaceInstance 会被 'already in target space' 退回，
                // 所以凡是等于当前 uuid 的候选一律不用（v10 实测 P1D）。
                string curUuid = null;
                Game.SpaceHandlers.BaseSpaceHandler cur = null;
                try
                {
                    cur = sm.CurrentSpaceHandler;
                    if (cur != null) curUuid = cur.Uuid.ToString();
                    Log("传送[B] 当前 handler uuid=" + (curUuid ?? "<null>"));
                }
                catch (Exception e) { Log("传送[B] CurrentSpaceHandler 异常 " + e.GetType().Name + ": " + e.Message); }

                string uuid = null, how = null;

                // ① 当前 handler 上下文里的目标实例（游戏旅行链走的就是这一步）
                if (cur != null)
                {
                    try
                    {
                        var inst = cur.ResolveInstanceInMyContext(id);
                        string s = inst.ToString();
                        if (!string.IsNullOrEmpty(s) && !s.Equals(curUuid, StringComparison.OrdinalIgnoreCase))
                        {
                            uuid = s; how = "CurrentSpaceHandler.ResolveInstanceInMyContext";
                        }
                        else Log("传送[B] ResolveInstanceInMyContext(=" + (string.IsNullOrEmpty(s) ? "<empty>" : s) + ") 与当前空间相同或为空，跳过");
                    }
                    catch (Exception e)
                    {
                        var inner = e.InnerException ?? e;
                        Log("传送[B] ResolveInstanceInMyContext 异常 " + inner.GetType().Name + ": " + inner.Message);
                    }
                }

                // ② 三个 handler 解析入口，只接受"不是当前空间"的结果
                if (uuid == null)
                {
                    var cands = new Func<Game.SpaceHandlers.BaseSpaceHandler>[]
                    {
                        () => sm.ResolveHandlerInCurrentContext(id),
                        () => sm.GetResidentSpaceHandler(id),
                        () => sm.ResolveSpaceHandler(id),
                    };
                    var names = new[] { "ResolveHandlerInCurrentContext", "GetResidentSpaceHandler", "ResolveSpaceHandler" };
                    for (int i = 0; i < cands.Length && uuid == null; i++)
                    {
                        try
                        {
                            var h = cands[i]();
                            if (h == null) { Log("传送[B] " + names[i] + " → null"); continue; }
                            string s = h.Uuid.ToString();
                            if (string.IsNullOrEmpty(s) || s.Equals(curUuid, StringComparison.OrdinalIgnoreCase))
                            {
                                Log("传送[B] " + names[i] + " → " + s + "（与当前空间相同，不能用）");
                                continue;
                            }
                            uuid = s; how = names[i];
                        }
                        catch (Exception e)
                        {
                            var inner = e.InnerException ?? e;
                            Log("传送[B] " + names[i] + " 异常 " + inner.GetType().Name + ": " + inner.Message);
                        }
                    }
                }

                // ③ 都没有 → 按游戏旅行链自行创建目标实例
                if (uuid == null)
                {
                    Log("传送[B] 无可用现成实例，按游戏旅行链创建：CreateGameplaySpaceHandler");
                    try
                    {
                        var kind = Game.SpaceInstanceKindResolver.ResolveFromConfig(id);
                        Game.Model.SpaceUuid owner = Game.Model.SpaceUuid.Empty;
                        if (cur != null) owner = cur.Uuid;
                        var created = sm.CreateGameplaySpaceHandler(id, kind, owner);
                        if (created != null)
                        {
                            uuid = created.Uuid.ToString();
                            how = "CreateGameplaySpaceHandler";
                            Log("传送[B] 已创建实例 kind=" + kind + " owner=" + owner + " uuid=" + uuid);
                        }
                        else Log("传送[B] CreateGameplaySpaceHandler → null");
                    }
                    catch (Exception e)
                    {
                        var inner = e.InnerException ?? e;
                        Log("传送[B] CreateGameplaySpaceHandler 异常 " + inner.GetType().Name + ": " + inner.Message);
                    }
                }

                if (string.IsNullOrEmpty(uuid))
                {
                    Log("传送[B] 拒绝：spaceId=" + spaceId + " 没有可用的目标实例（现成解析全失败、创建也失败）");
                    return false;
                }
                Log("传送[B] 目标 uuid=" + uuid + " via " + how);

                var ms = typeof(Game.SpaceManager).GetMethods();
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "GotoSpaceInstance") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 13) continue;
                    object boxed = null;
                    try { boxed = Activator.CreateInstance(ps[0].ParameterType, new object[] { uuid }); }
                    catch (Exception e1) { Log("传送[B] 构造 SpaceUuid(" + ps[0].ParameterType.Name + ") 失败 " + e1.Message); return false; }
                    object loading = SafeNew(ps[6].ParameterType);
                    object battle = SafeNew(ps[10].ParameterType);
                    var args = new object[]
                    {
                        boxed, null, null, null, force, TpLoadingWait, loading, null, 0, ignoreWorldStatus, battle, null, null
                    };
                    m.Invoke(sm, args);
                    Log("传送[B] 已提交 GotoSpaceInstance(uuid=" + uuid + ") force=" + force
                        + " useLoadingWaitPanel=" + TpLoadingWait + " ignoreWorldStatus=" + ignoreWorldStatus);
                    return true;
                }
                Log("传送[B] 失败：没找到 13 参的 GotoSpaceInstance");
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Log("传送[B] 异常 " + inner.GetType().Name + ": " + inner.Message);
            }
            return false;
        }


        /// <summary>路线 A′：GotoSpace(TbSpaceId?)，由游戏自己解析 uuid；
        /// 与 v9 的唯一差别是 useLoadingWaitPanel 与跟踪钩子，不是静默回退。</summary>
        private static bool TeleportViaSpaceId(int spaceId, bool force, bool ignoreWorldStatus)
        {
            try
            {
                var sm = Game.SpaceManager.Instance;
                if (sm == null) { Log("传送[A′] 拒绝：SpaceManager.Instance == null"); return false; }
                var ms = typeof(Game.SpaceManager).GetMethods();
                for (int i = 0; i < ms.Length; i++)
                {
                    var m = ms[i];
                    if (m.Name != "GotoSpace") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 13) continue;
                    object boxed = null;
                    try { boxed = Activator.CreateInstance(ps[0].ParameterType, new object[] { new LubanDatas.TbSpaceId(spaceId) }); }
                    catch (Exception e1) { Log("传送[A′] 构造 spaceId Nullable(" + ps[0].ParameterType.Name + ") 失败 " + e1.Message); return false; }
                    object loading = SafeNew(ps[6].ParameterType);
                    object battle = SafeNew(ps[10].ParameterType);
                    var args = new object[]
                    {
                        boxed, null, null, null, force, TpLoadingWait, loading, null, 0, ignoreWorldStatus, battle, null, null
                    };
                    m.Invoke(sm, args);
                    Log("传送[A′] 已提交 GotoSpace(spaceId=" + spaceId + ") force=" + force
                        + " useLoadingWaitPanel=" + TpLoadingWait + " ignoreWorldStatus=" + ignoreWorldStatus);
                    return true;
                }
                Log("传送[A′] 失败：没找到 13 参的 GotoSpace");
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Log("传送[A′] 异常 " + inner.GetType().Name + ": " + inner.Message);
            }
            return false;
        }

        private static object SafeNew(Type t)
        {
            try { return Activator.CreateInstance(t); } catch { return null; }
        }

        /// <summary>
        /// MOD_DEBUG_TESTSPACE=0 时的自动选点：扫 TbSpace，列出"玩家到过、且当前 handler
        /// 上下文里能解析出**与当前空间不同**的实例"的空间，取第一个作为目标。
        /// 这同时把空间映射表打进日志（TPSCAN 行）。选不到返回 -1。
        /// </summary>
        private static int PickSpace(int limit)
        {
            try
            {
                var rows = Game.ConfigManager.Instance.Tables.TbSpace.DataList;
                if (rows == null) { Log("TPSCAN: TbSpace.DataList == null"); return -1; }
                var sm = Game.SpaceManager.Instance;
                if (sm == null) { Log("TPSCAN: SpaceManager.Instance == null"); return -1; }
                Game.SpaceHandlers.BaseSpaceHandler cur = null;
                try { cur = sm.CurrentSpaceHandler; } catch { }
                string curUuid = cur == null ? null : cur.Uuid.ToString();
                var player = Game.Model.GameStoreManager.CurrentPlayer;
                Log("TPSCAN: TbSpace=" + rows.Count + " 当前handlerUuid=" + (curUuid ?? "<null>")
                    + " player=" + (player == null ? "null" : "ok") + "，扫描前 " + limit + " 条");
                int n = Mathf.Min(rows.Count, limit);
                int pick = -1;
                for (int i = 0; i < n; i++)
                {
                    var r = rows[i];
                    if (r == null) continue;
                    int sid;
                    try { sid = r.id.Value; } catch { continue; }
                    var id = new LubanDatas.TbSpaceId(sid);
                    string child = "-";
                    try { child = cur == null ? "<no-cur>" : cur.ResolveInstanceInMyContext(id).ToString(); }
                    catch (Exception e) { child = "<err:" + e.GetType().Name + ">"; }
                    bool vis = false;
                    try { vis = player != null && player.HasVisitedSpace(id); } catch { }
                    bool usable = vis && !string.IsNullOrEmpty(child)
                                      && !child.Equals(curUuid, StringComparison.OrdinalIgnoreCase);
                    Log("TPSCAN: space " + sid + " visited=" + vis + " child=" + child
                        + " usable=" + usable + (usable ? "  <= 选中" : ""));
                    if (pick < 0 && usable) pick = sid;
                }
                Log("TPSCAN: 选中 spaceId=" + pick);
                return pick;
            }
            catch (Exception e)
            {
                Log("TPSCAN 失败 " + e.GetType().Name + ": " + e.Message);
                return -1;
            }
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
                    int vis = _lastTarget > 0 ? _lastTarget : TestSpace;
                    Log("剧情+" + StoryTimes[i] + "s " + QuestBrief(q) + " | " + FlowsText()
                        + " | " + SpaceIdText()
                        + " | " + VisitedText(vis)
                        + " | player=" + (Player() == null ? "null" : "ok"));
                    return;
                }
            }
            if (!_tp && e > TeleportAt)
            {
                _tp = true;
                if (TestSpace >= 0)
                {
                    Log("传送前 " + (TestSpace > 0 ? VisitedText(TestSpace) : "space=auto(扫描选点)")
                        + " | player=" + (Player() == null ? "null" : "ok"));
                    Teleport(TestSpace, true, true);
                }
                else Log("跳过传送（MOD_DEBUG_TESTSPACE<0）");
                return;
            }
            for (int i = 0; i < LogTimes.Length; i++)
            {
                if (!_tpLog[i] && e > LogTimes[i])
                {
                    _tpLog[i] = true;
                    Log("+" + LogTimes[i] + "s " + SpaceIdText() + " | " + VisitedText(TestSpace > 0 ? TestSpace : 0)
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

    /// <summary>
    /// v10 P1 传送链跟踪。仅在 MOD_DEBUG_TPDIAG=1 时安装；四个点全部只打日志、
    /// 不改任何返回值，因此即使开着也与出厂行为一致。四个点来自反汇编
    /// （见 实施报告v10.md §1.1）：入队 → 状态机启动 → 两个拒绝出口。
    /// </summary>
    static class TeleportDiag
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_DEBUG_TPDIAG") == "1";

        private static void Log(string s)
        {
            try { StoryDebugPlugin.Logger.LogInfo("[DBG] TPDIAG " + s); } catch { }
        }

        internal static void Install()
        {
            var h = new Harmony("local.modmanager.tpdiag");
            int n = 0;
            // 只保留这一个点：它实测安全，而且是"拒绝原因"的唯一出口。
            // RejectQueuedChangeSpaceRequest / GotoSpaceForce / TryChangeSpaceAsync 三个点
            // 在 v10 第一轮实测中触发过 il2cpp_runtime_invoke 的 AccessViolation
            // （ErrorLog_P1A_attempt4.log），因此撤掉——诊断值不值一次崩溃，不值。
            n += Try(h, "NotifyChangeSpaceRejected", nameof(RejectedPost));
            Log("hooks installed=" + n + "/1");
        }

        private static int Try(Harmony h, string name, string postfix)
        {
            try
            {
                var m = AccessTools.Method(typeof(Game.SpaceManager), name);
                if (m == null) { Log(name + " not found"); return 0; }
                h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(TeleportDiag), postfix)));
                return 1;
            }
            catch (Exception e) { Log(name + " patch failed: " + e.Message); return 0; }
        }

        private static void RejectedPost(string __1)
        {
            Log("NotifyChangeSpaceRejected reason='" + __1 + "'");
        }
    }
}
