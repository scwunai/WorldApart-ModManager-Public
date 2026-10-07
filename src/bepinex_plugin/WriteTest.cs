using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LocalModManager
{
    /// <summary>
    /// P0 —— 真实存档三写入往返（任务 / 货币 / 属性 / 亲密度）。
    ///
    /// 全部写入都走游戏自己的原生 API（禁止直接改表内存）：
    ///   quest    : QuestManager.DebugForceAcceptQuest + SettleObjectiveFromFlowVm
    ///   currency : BagModel.AddItem(..., ItemSourceType.GMCommand, ...)
    ///   attr     : PlayerModel.IncreaseInteractAttributeValue
    ///   intimacy : NpcModel.AddIntimacy(1, CommissionAffinityActionType.GIFT)
    ///
    /// 驱动器是环境变量，默认完全不运行（Phase == 0 时 Tick 首行返回，零开销）：
    ///   MOD_DEBUG_WRITETEST=1  第一阶段：载入存档 → 打 before → 四个写入 → 打 after → 存到测试槽
    ///   MOD_DEBUG_WRITETEST=2  第二阶段：重启后载入测试槽 → 打 reload（与 after 比对）
    ///   MOD_DEBUG_TESTSTORAGE=<storageId>  覆盖要操作的 storage（默认文件系统扫描取最新）
    ///   MOD_DEBUG_LOADSLOT=<slotId>        覆盖第一阶段要载入的槽位
    ///   MOD_DEBUG_TESTSLOT=<n>             专用测试槽位序号（默认 10 → manual_10）
    ///   MOD_DEBUG_TESTQUEST=<id>           任务写入用 questId（默认 1001 初到河望镇）
    ///   MOD_DEBUG_TESTNPC=<cfgId>          亲密度写入用 NPC 配置 id（默认探测在场 NPC）
    ///
    /// 存档目录只用只读方式扫描（RootSavePath 下的 <storage>/slots/<slot>/gameworld.msgpack）；
    /// 唯一落盘动作是显式调用 GameStoreManager.SaveCurrentManualSlot(测试槽)，
    /// 且测试全程用 SuppressAutoSave() 挡住自动存档，避免污染其它槽位。
    /// </summary>
    static class WriteTest
    {
        internal static readonly int Phase = EnvInt("MOD_DEBUG_WRITETEST", 0);

        private static readonly string StorageOverride = Env("MOD_DEBUG_TESTSTORAGE");
        private static readonly string LoadSlotOverride = Env("MOD_DEBUG_LOADSLOT");
        private static readonly int SlotIndex = EnvInt("MOD_DEBUG_TESTSLOT", 10);
        private static readonly int QuestId = EnvInt("MOD_DEBUG_TESTQUEST", 1001);
        private static readonly int NpcOverride = EnvInt("MOD_DEBUG_TESTNPC", -1);

        internal static string Status = "(未启用)";

        private static int _step;
        private static bool _done;
        private static float _deadline;
        private static float _nextLog;
        private static Il2CppSystem.IDisposable _noAutoSave;

        private static string _storageId;
        private static string _loadSlotId;
        private static int _currencyItem = -1;
        private static int _attrId = int.MinValue;
        private static int _npcId = -1;

        // ------------------------------------------------------------ entry point
        /// <summary>Called every frame from DebugTool.OnGui() — the only call site.</summary>
        internal static void Tick()
        {
            if (Phase <= 0 || _done) return;
            try
            {
                if (Phase == 1) Phase1();
                else Phase2();
            }
            catch (Exception e)
            {
                _done = true;
                Status = "异常终止";
                Log("WT FATAL " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------- helpers
        private static string Env(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        private static int EnvInt(string k, int fallback)
        {
            int v;
            var s = Env(k);
            return (s != null && int.TryParse(s, out v)) ? v : fallback;
        }

        private static void Log(string s)
        {
            Status = s;
            DebugTool.Observe(s);
            Plugin.Logger.LogInfo("[DBG] " + s);
        }

        private static float Now { get { return Time.unscaledTime; } }
        private static void Next() { _step++; _deadline = 0f; _nextLog = 0f; }

        private static Game.Model.PlayerModel Player() { return Game.Model.GameStoreManager.CurrentPlayer; }
        private static Game.Model.Components.BagModel Bag() { var p = Player(); return p == null ? null : p.bag; }
        private static Game.Model.GameWorldModel World() { return Game.Model.GameStoreManager.CurrentGameWorld; }
        private static LubanDatas.TbQuestId Q(int id) { return new LubanDatas.TbQuestId(id); }

        private static string ManualSlotId(int idx)
        {
            try { return Game.Model.GameStore.GetManualSlotId(idx); }
            catch (Exception e) { Log("WT GetManualSlotId 失败: " + e.Message); return "manual_" + idx.ToString("D2"); }
        }

        /// <summary>
        /// Read-only scan of the save root. Picks the storage/slot whose gameworld.msgpack
        /// is newest; with onlyTestSlot it restricts to the dedicated test slot (phase 2).
        /// </summary>
        private static bool ScanSaves(bool onlyTestSlot, out string storage, out string slot)
        {
            storage = null; slot = null;
            var mgr = Game.Model.GameStoreManager.Instance;
            if (mgr == null) { Log("WT scan: GameStoreManager.Instance == null"); return false; }
            string root = null;
            try { root = mgr.RootSavePath; } catch (Exception e) { Log("WT scan: RootSavePath 读取失败 " + e.Message); }
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                Log("WT scan: 存档根目录不可用 root=" + root);
                return false;
            }
            Log("WT scan: 存档根目录 " + root);

            string testSlot = ManualSlotId(SlotIndex);
            DateTime best = DateTime.MinValue;
            var storages = Directory.GetDirectories(root);
            Log("WT scan: storage 目录 " + storages.Length + " 个");
            for (int i = 0; i < storages.Length; i++)
            {
                string sid = Path.GetFileName(storages[i]);
                string slotsRoot = Path.Combine(storages[i], "slots");
                if (!Directory.Exists(slotsRoot)) { Log("WT scan:   [" + sid + "] 无 slots 目录，跳过"); continue; }
                string cur = "?";
                try
                {
                    var cm = Path.Combine(storages[i], "current", "meta.msgpack");
                    if (File.Exists(cm)) cur = File.GetLastWriteTime(cm).ToString("MM-dd HH:mm");
                }
                catch { }
                var slotDirs = Directory.GetDirectories(slotsRoot);
                Log("WT scan:   [" + sid + "] current=" + cur + " slot 目录=" + slotDirs.Length);
                for (int j = 0; j < slotDirs.Length; j++)
                {
                    string slotId = Path.GetFileName(slotDirs[j]);
                    string gw = Path.Combine(slotDirs[j], "gameworld.msgpack");
                    if (!File.Exists(gw)) continue;
                    var t = File.GetLastWriteTime(gw);
                    Log("WT scan:      slot " + slotId + "  " + t.ToString("MM-dd HH:mm") + "  " + new FileInfo(gw).Length + " B"
                        + (onlyTestSlot && slotId.Equals(testSlot, StringComparison.OrdinalIgnoreCase) ? "  <= 测试槽" : ""));
                    if (onlyTestSlot && !slotId.Equals(testSlot, StringComparison.OrdinalIgnoreCase)) continue;
                    if (t > best) { best = t; storage = sid; slot = slotId; }
                }
            }
            if (storage == null) { Log("WT scan: 未找到可用槽位 (onlyTestSlot=" + onlyTestSlot + ")"); return false; }
            Log("WT scan: 选中 storage=" + storage + " slot=" + slot + " @" + best.ToString("MM-dd HH:mm"));
            return true;
        }

        // -------------------------------------------------------------- phase one
        private static void Phase1()
        {
            switch (_step)
            {
                case 0: Case0_Report(); break;
                case 1: Case1_WaitReady(); break;
                case 2: Case2_LoadSave(); break;
                case 3: Case2_WaitPlayer(); break;
                case 4: Case3_ResolveTargets(); break;
                case 5: SnapAll("P1-BEFORE"); Next(); break;
                case 6: Case5_WriteQuest(); break;
                case 7: Case6_WriteCurrency(); break;
                case 8: Case7_WriteAttribute(); break;
                case 9: Case8_WriteIntimacy(); break;
                case 10: SnapAll("P1-AFTER"); Next(); break;
                case 11: Case10_SaveToTestSlot(); break;
                case 12:
                    Log("WT P1-DONE 第一阶段完成；请关闭游戏后用 MOD_DEBUG_WRITETEST=2 重启做回读");
                    _done = true;
                    break;
            }
        }

        /// <summary>
        /// 载入存档必须等游戏自己把配置表与流程脚本准备好；实测在 t≈3s 时调
        /// GameStoreManager.LoadCurrentSlot 会抛 MessagePack 反序列化失败（依赖未就绪）。
        /// </summary>
        private static void Case1_WaitReady()
        {
            bool tables = false, flows = false;
            try
            {
                var c = Game.ConfigManager.Instance;
                tables = c != null && c.Tables != null && c.Tables.TbQuest != null && c.Tables.TbNpcBaseCfg != null;
            }
            catch { }
            try
            {
                var f = Game.FlowRuntimeManager.Instance;
                flows = f != null && f.FlowsLoadedAndTranslated;
            }
            catch { }
            if (tables) { Log("WT 1a: 配置表已就绪 (t=" + Now.ToString("F1") + "s, flowsLoaded=" + flows + ")，开始载入存档"); Next(); return; }
            if (_deadline == 0f) _deadline = Now + 240f;
            if (Now > _nextLog)
            {
                _nextLog = Now + 10f;
                Log("WT 1a: 等待游戏初始化 tables=" + tables + " flows=" + flows + " t=" + Now.ToString("F1") + "s");
            }
            if (Now > _deadline)
            {
                Log("WT 1a: 等待就绪超时（tables=" + tables + " flows=" + flows + "），仍继续尝试载入");
                Next();
            }
        }

        private static void Case0_Report()
        {
            var mgr = Game.Model.GameStoreManager.Instance;
            if (mgr == null) { Log("WT 0: GameStoreManager.Instance == null，放弃"); _done = true; return; }

            // 游戏内部缓存接口（未载入 storage 时会 NRE，仅作证据记录）
            try
            {
                var metas = mgr.ListStorageMetas();
                Log("WT 0: ListStorageMetas=" + (metas == null ? -1 : metas.Count));
            }
            catch (Exception e) { Log("WT 0: ListStorageMetas 不可用(未选 storage): " + e.GetType().Name); }
            try
            {
                string a, b;
                bool ok = mgr.TryGetLatestSaveSelection(out a, out b);
                Log("WT 0: TryGetLatestSaveSelection rc=" + ok + " storage=" + a + " slot=" + b);
            }
            catch (Exception e) { Log("WT 0: TryGetLatestSaveSelection 不可用: " + e.GetType().Name); }

            // 主路径：文件系统只读扫描
            string sid, slot;
            if (ScanSaves(false, out sid, out slot)) { _storageId = sid; _loadSlotId = slot; }
            if (StorageOverride != null) { _storageId = StorageOverride; Log("WT 0: MOD_DEBUG_TESTSTORAGE 覆盖 storage=" + _storageId); }
            if (LoadSlotOverride != null) { _loadSlotId = LoadSlotOverride; Log("WT 0: MOD_DEBUG_LOADSLOT 覆盖槽=" + _loadSlotId); }

            Log("WT 0: 计划 storage=" + _storageId + " 载入槽=" + _loadSlotId
                + " 测试写入槽=" + ManualSlotId(SlotIndex) + " 任务=" + QuestId);
            Next();
        }

        private static void Case2_LoadSave()
        {
            var mgr = Game.Model.GameStoreManager.Instance;
            Log("WT 2: 载入前 SelectedStorageId=" + Safe(() => mgr.SelectedStorageId)
                + " SelectedSlotId=" + Safe(() => mgr.SelectedSlotId)
                + " CurrentPlayer=" + (Player() == null ? "null" : "ok"));

            try { _noAutoSave = mgr.SuppressAutoSave(); Log("WT 1: 已抑制自动存档（测试期间不写 auto 槽）"); }
            catch (Exception e) { Log("WT 1: SuppressAutoSave 失败 " + e.Message); }

            if (_storageId != null)
            {
                // 顺序实测：SelectStorage 先设好 SelectedStorageId/SelectedSlotId，再 LoadStorage，
                // 最后 LoadCurrentSlot（LoadStorage 单独先调会 NRE）
                try { mgr.SelectStorage(_storageId, _loadSlotId); Log("WT 1: SelectStorage 返回 storage=" + Safe(() => mgr.SelectedStorageId) + " slot=" + Safe(() => mgr.SelectedSlotId)); }
                catch (Exception e) { Log("WT 1: SelectStorage 异常 " + e.GetType().Name + ": " + e.Message); }
                try { mgr.LoadStorage(_storageId); Log("WT 1: LoadStorage 返回，SelectedStorageId=" + Safe(() => mgr.SelectedStorageId)); }
                catch (Exception e) { Log("WT 1: LoadStorage 异常 " + e.GetType().Name + ": " + e.Message); }
                try { var c = Game.Model.GameStoreManager.CurrentStore; Log("WT 1: CurrentStore=" + (c == null ? "null" : "ok ActiveSlotId=" + Safe(() => c.ActiveSlotId))); }
                catch (Exception e) { Log("WT 1: 读取 CurrentStore 异常 " + e.GetType().Name + ": " + e.Message); }
                try
                {
                    var slots = mgr.ListCurrentSaveSlots();
                    Log("WT 1: ListCurrentSaveSlots=" + (slots == null ? -1 : slots.Count));
                }
                catch (Exception e) { Log("WT 1: ListCurrentSaveSlots 异常 " + e.GetType().Name + ": " + e.Message); }
            }
            else Log("WT 1: 未取到 storageId（可用 MOD_DEBUG_TESTSTORAGE 指定）");

            if (_loadSlotId != null)
            {
                try
                {
                    bool rc = mgr.LoadCurrentSlot(_loadSlotId);
                    Log("WT 2: LoadCurrentSlot(" + _loadSlotId + ") rc=" + rc);
                }
                catch (Exception e) { Log("WT 2: LoadCurrentSlot 异常 " + e.GetType().Name + ": " + e.Message); }

                if (Player() == null)
                {
                    // 兜底：直接命令当前 GameStore 载槽（绕过 manager 的一层包装）
                    try
                    {
                        var store = Game.Model.GameStoreManager.CurrentStore;
                        if (store == null) Log("WT 2: 兜底跳过（CurrentStore=null）");
                        else
                        {
                            bool rc2 = store.LoadSlot(_loadSlotId);
                            Log("WT 2: 兜底 CurrentStore.LoadSlot(" + _loadSlotId + ") rc=" + rc2);
                        }
                    }
                    catch (Exception e) { Log("WT 2: 兜底 LoadSlot 异常 " + e.GetType().Name + ": " + e.Message); }
                }
            }

            _deadline = Now + 180f;
            Log("WT 2: 等待世界载入…（若 180s 内未自动载入，请人工在游戏内载入任意存档，Tick 会继续）");
            Next();
        }

        private static void Case2_WaitPlayer()
        {
            if (Player() != null) { Log("WT 2: 玩家已载入 world=" + (World() == null ? "null" : "ok")); Next(); return; }
            if (_deadline > 0f && Now > _deadline)
            {
                _deadline = Now + 300f;
                Log("WT 2: 仍在等待玩家（已超时一次，再等 300s）；CurrentPlayer=null");
            }
            if (Now > _nextLog)
            {
                _nextLog = Now + 15f;
                Log("WT 2: 等待中 t=" + Now.ToString("F1") + "s CurrentPlayer=null");
            }
        }

        private static void Case3_ResolveTargets()
        {
            // 货币：从玩家背包里挑一个被游戏判定为货币的物品
            var bag = Bag();
            if (bag != null && bag.Items != null)
            {
                var items = bag.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null) continue;
                    if (Game.Model.Components.BagModel.IsCurrencyItem(it.ItemId)) { _currencyItem = it.ItemId.Value; break; }
                }
            }
            Log("WT 3: 货币目标 item=" + _currencyItem + (_currencyItem < 0 ? "（背包内没有货币物品，货币写入将跳过）" : ""));

            // 属性：优先用玩家运行时字典，否则回退配置表
            var p = Player();
            if (p != null)
            {
                var attrs = p.interactAttributes;
                if (attrs != null && attrs.Count > 0)
                {
                    foreach (var kv in attrs) { _attrId = kv.Key.Value; break; }
                }
                if (_attrId == int.MinValue)
                {
                    var rows = Game.ConfigManager.Instance.Tables.TbInteractAttribute.DataList;
                    if (rows != null && rows.Count > 0) _attrId = rows[0].id.Value;
                }
            }
            Log("WT 3: 属性目标 id=" + _attrId + (_attrId == int.MinValue ? "（无可用属性 id，属性写入将跳过）" : ""));

            // 亲密度：指定 id 优先，否则从 NPC 配置表前若干行探测第一个"在场"的 NPC
            // （ConcurrentDictionary 的 interop 枚举器不能 foreach，故用 GetNpc 探测）
            if (NpcOverride >= 0) _npcId = NpcOverride;
            if (_npcId < 0)
            {
                try
                {
                    var rows = Game.ConfigManager.Instance.Tables.TbNpcBaseCfg.DataList;
                    int probe = rows == null ? 0 : Math.Min(rows.Count, 400);
                    for (int i = 0; i < probe; i++)
                    {
                        var r = rows[i];
                        if (r == null) continue;
                        if (FindNpc(r.id.Value) != null) { _npcId = r.id.Value; break; }
                    }
                }
                catch (Exception e) { Log("WT 3: 探测在场 NPC 失败 " + e.Message); }
            }
            Log("WT 3: 亲密度目标 npc=" + _npcId + (_npcId < 0 ? "（当前世界没有可用 NPC，亲密度写入将跳过）" : ""));
            Next();
        }

        private static void Case5_WriteQuest()
        {
            try
            {
                var qm = Game.QuestManager.Instance;
                var id = Q(QuestId);
                Log("WT 5: 任务写入前 " + QuestSnap());
                bool acc = false;
                try { acc = qm.DebugForceAcceptQuest(id); } catch (Exception e) { Log("WT 5: DebugForceAcceptQuest 异常 " + e.GetType().Name + ": " + e.Message); }
                Log("WT 5: DebugForceAcceptQuest rc=" + acc + " → " + QuestSnap());

                var cfg = qm.GetQuestConfig(id);
                int n = (cfg == null || cfg.objectives == null) ? 0 : cfg.objectives.Count;
                int idx = -1;
                for (int i = 0; i < n; i++)
                    if (!qm.IsObjectiveSettled(id, i)) { idx = i; break; }
                if (idx < 0) Log("WT 5: 没有未结算步骤（objectives=" + n + "），跳过 SettleObjectiveFromFlowVm");
                else
                {
                    qm.SettleObjectiveFromFlowVm(id, idx);
                    Log("WT 5: SettleObjectiveFromFlowVm(#" + idx + ") → " + QuestSnap());
                }
                Log("WT 5: 任务写入后 " + QuestSnap());
            }
            catch (Exception e) { Log("WT 5: 任务写入异常 " + e.GetType().Name + ": " + e.Message); }
            Next();
        }

        private static void Case6_WriteCurrency()
        {
            try
            {
                if (_currencyItem < 0) { Log("WT 6: 没有货币物品可写，跳过"); Next(); return; }
                DebugActions.AddItemReflect("WT 6", _currencyItem, 1);
                Log("WT 6: 货币快照 " + CurrencySnap());
            }
            catch (Exception e) { Log("WT 6: 货币写入异常 " + e.GetType().Name + ": " + e.Message); }
            Next();
        }

        private static void Case7_WriteAttribute()
        {
            try
            {
                var p = Player();
                if (p == null) { Log("WT 7: 无玩家，跳过属性写入"); Next(); return; }
                if (_attrId == int.MinValue) { Log("WT 7: 无属性 id，跳过"); Next(); return; }
                var key = new LubanDatas.TbInteractAttributeId(_attrId);
                int before = p.GetInteractAttributeValue(key);
                p.IncreaseInteractAttributeValue(key, 1);
                int after = p.GetInteractAttributeValue(key);
                Log("WT 7: 属性写入 id=" + _attrId + " before=" + before + " after=" + after
                    + " delta=" + (after - before) + " " + AttrSnap());
            }
            catch (Exception e) { Log("WT 7: 属性写入异常 " + e.GetType().Name + ": " + e.Message); }
            Next();
        }

        private static void Case8_WriteIntimacy()
        {
            try
            {
                if (_npcId < 0) { Log("WT 8: 无在场 NPC，跳过亲密度写入"); Next(); return; }
                var npc = FindNpc(_npcId);
                if (npc == null) { Log("WT 8: NPC " + _npcId + " 不在当前世界，跳过"); Next(); return; }
                int before = npc.Intimacy;
                npc.AddIntimacy(1, LubanDatas.CommissionAffinityActionType.GIFT);
                int after = npc.Intimacy;
                Log("WT 8: 亲密度写入 npc=" + _npcId + " before=" + before + " after=" + after
                    + " delta=" + (after - before) + " " + IntimacySnap());
            }
            catch (Exception e) { Log("WT 8: 亲密度写入异常 " + e.GetType().Name + ": " + e.Message); }
            Next();
        }

        private static void Case10_SaveToTestSlot()
        {
            try
            {
                var mgr = Game.Model.GameStoreManager.Instance;
                string slotId = ManualSlotId(SlotIndex);
                Log("WT 10: 存档前 SelectedSlotId=" + Safe(() => mgr.SelectedSlotId) + " 目标槽=" + slotId);
                mgr.SaveCurrentManualSlot(SlotIndex);
                Log("WT 10: SaveCurrentManualSlot(" + SlotIndex + ") 返回; ActiveSlotId="
                    + Safe(() => Game.Model.GameStoreManager.CurrentStore == null ? "?" : Game.Model.GameStoreManager.CurrentStore.ActiveSlotId));

                string path = null;
                try
                {
                    var store = Game.Model.GameStoreManager.CurrentStore;
                    if (store != null) path = store.GetSlotPath(slotId);
                }
                catch (Exception e) { Log("WT 10: GetSlotPath 失败 " + e.Message); }
                if (path != null)
                {
                    var f = Path.Combine(path, "gameworld.msgpack");
                    var fi = new FileInfo(f);
                    Log("WT 10: 落盘检查 " + f + " exists=" + fi.Exists
                        + " size=" + (fi.Exists ? fi.Length.ToString() : "-")
                        + " mtime=" + (fi.Exists ? fi.LastWriteTime.ToString("HH:mm:ss") : "-"));
                }
                else Log("WT 10: 未能取到测试槽路径（CurrentStore 为空?）");
            }
            catch (Exception e) { Log("WT 10: 存档异常 " + e.GetType().Name + ": " + e.Message); }
            Next();
        }

        // -------------------------------------------------------------- phase two
        private static void Phase2()
        {
            switch (_step)
            {
                case 0: Case0_Report2(); break;
                case 1: Case1_WaitReady(); break;
                case 2: Case1_LoadSave2(); break;
                case 3: Case2_WaitPlayer(); break;
                case 4: Case3_ResolveTargets(); break;
                case 5: SnapAll("P2-RELOAD"); Next(); break;
                case 6:
                    Log("WT P2-DONE 回读完成，请把 P1-AFTER 与 P2-RELOAD 四行逐条比对");
                    _done = true;
                    break;
            }
        }

        private static void Case0_Report2()
        {
            string sid, slot;
            if (ScanSaves(true, out sid, out slot)) { _storageId = sid; }
            if (StorageOverride != null) _storageId = StorageOverride;
            _loadSlotId = ManualSlotId(SlotIndex);
            Log("WT P2: 计划 storage=" + _storageId + " 载入测试槽=" + _loadSlotId);
            Next();
        }

        private static void Case1_LoadSave2()
        {
            var mgr = Game.Model.GameStoreManager.Instance;
            try { _noAutoSave = mgr.SuppressAutoSave(); Log("WT P2: 已抑制自动存档"); }
            catch (Exception e) { Log("WT P2: SuppressAutoSave 失败 " + e.Message); }

            if (_storageId != null)
            {
                try { mgr.LoadStorage(_storageId); Log("WT P2: LoadStorage 返回 SelectedStorageId=" + Safe(() => mgr.SelectedStorageId)); }
                catch (Exception e) { Log("WT P2: LoadStorage 异常 " + e.GetType().Name + ": " + e.Message); }
            }

            try
            {
                bool rc = mgr.LoadCurrentManualSlot(SlotIndex);
                Log("WT P2: LoadCurrentManualSlot(" + SlotIndex + ") rc=" + rc);
            }
            catch (Exception e) { Log("WT P2: LoadCurrentManualSlot 异常 " + e.GetType().Name + ": " + e.Message); }

            if (Player() == null)
            {
                try
                {
                    var store = Game.Model.GameStoreManager.CurrentStore;
                    if (store == null) Log("WT P2: 兜底跳过（CurrentStore=null）");
                    else
                    {
                        bool rc2 = store.LoadManualSlot(SlotIndex);
                        Log("WT P2: 兜底 CurrentStore.LoadManualSlot(" + SlotIndex + ") rc=" + rc2);
                    }
                }
                catch (Exception e) { Log("WT P2: 兜底 LoadManualSlot 异常 " + e.GetType().Name + ": " + e.Message); }
            }

            _deadline = Now + 180f;
            Log("WT P2: 等待世界载入…（若超时请人工载入 " + _loadSlotId + "）");
            Next();
        }

        // ------------------------------------------------------------- snapshots
        private static void SnapAll(string tag)
        {
            Log("WT " + tag + " QUEST    " + QuestSnap());
            Log("WT " + tag + " CURRENCY " + CurrencySnap());
            Log("WT " + tag + " ATTR     " + AttrSnap());
            Log("WT " + tag + " INTIMACY " + IntimacySnap());
        }

        private static string QuestSnap()
        {
            try
            {
                if (Player() == null) return Q(QuestId).Value + " state=<no-player>";
                var qm = Game.QuestManager.Instance;
                string state = qm.GetQuestState(Q(QuestId)).ToString();
                var cfg = qm.GetQuestConfig(Q(QuestId));
                int n = (cfg == null || cfg.objectives == null) ? 0 : cfg.objectives.Count;
                int settled = 0;
                for (int i = 0; i < n; i++) if (qm.IsObjectiveSettled(Q(QuestId), i)) settled++;
                return Q(QuestId).Value + " state=" + state + " settled=" + settled + "/" + n;
            }
            catch (Exception e) { return Q(QuestId).Value + " <err:" + e.Message + ">"; }
        }

        private static string CurrencySnap()
        {
            try
            {
                var bag = Bag();
                if (bag == null) return "item=" + _currencyItem + " count=<no-bag>";
                if (_currencyItem < 0) return "item=- count=<none>";
                return "item=" + _currencyItem + " count=" + bag.GetItemCount(new LubanDatas.TbItemId(_currencyItem));
            }
            catch (Exception e) { return "item=" + _currencyItem + " <err:" + e.Message + ">"; }
        }

        private static string AttrSnap()
        {
            try
            {
                var p = Player();
                if (p == null) return "id=" + _attrId + " value=<no-player>";
                if (_attrId == int.MinValue) return "id=- value=<none>";
                var key = new LubanDatas.TbInteractAttributeId(_attrId);
                return "id=" + _attrId + " value=" + p.GetInteractAttributeValue(key);
            }
            catch (Exception e) { return "id=" + _attrId + " <err:" + e.Message + ">"; }
        }

        private static string IntimacySnap()
        {
            try
            {
                if (_npcId < 0) return "npc=- value=<none>";
                var npc = FindNpc(_npcId);
                if (npc == null) return "npc=" + _npcId + " value=<not-in-world>";
                return "npc=" + _npcId + " value=" + npc.Intimacy + " max=" + npc.MaxIntimacy;
            }
            catch (Exception e) { return "npc=" + _npcId + " <err:" + e.Message + ">"; }
        }

        private static Game.Model.NpcModel FindNpc(int cfgId)
        {
            try
            {
                var w = World();
                if (w == null) return null;
                return w.GetNpc(new LubanDatas.TbNpcBaseCfgId(cfgId), true);
            }
            catch { return null; }
        }

        private static void Noop() { }

        private static string Safe(Func<string> f)
        {
            try { return f() ?? "null"; } catch (Exception e) { return "<err:" + e.Message + ">"; }
        }
    }
}
