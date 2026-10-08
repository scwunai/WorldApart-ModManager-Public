using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace LocalModManager
{
    // =====================================================================
    // S3: F9 runtime debug tool  (see docs/实施计划与报告/实施报告v6r2.md)
    //
    // Three tabs - quest jump / player values / npc social - that write RUNTIME
    // state only. Nothing here opens, edits or deletes a save file, but the game's
    // own autosave can pick the results up, which is why the window carries a red
    // backup warning.
    //
    // Cost model (the plan requires "closed = zero cost"):
    //   MOD_DEBUG=0        -> the single call site returns on its first line
    //   enabled + closed   -> one IMGUI key-event check
    //   open               -> table rows are cached in managed lists, so no
    //                         per-frame cross-interop enumeration happens
    // Exceptions are caught per action and logged; a failed action never takes
    // the game down.
    // =====================================================================
    static class DebugTool
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_DEBUG") != "0";

        /// <summary>
        /// MOD_DEBUG_SELFTEST=1 makes the tool exercise one representative action
        /// per tab once, logging before/after and restoring every value it touched,
        /// so the evidence can be captured without anyone clicking the window.
        /// </summary>
        internal static readonly bool SelfTest =
            Environment.GetEnvironmentVariable("MOD_DEBUG_SELFTEST") == "1";

        private static bool _stDone;
        internal static bool Show;

        private const string Warn =
            "【存档风险】本工具只改运行时状态，不改存档文件；但游戏自动存档可能把改动写进存档，请先备份存档！";

        private static int _tab;
        private static Vector2 _scroll;
        private static string _status = "(就绪)";
        private static int _ops;

        private struct QuestRow { public int Id; public string Name; public string Group; }
        private struct NpcRow { public int Id; public string Name; public string Title; }
        private struct SpaceRow { public int Id; public string Name; public string Parent; }

        private static List<QuestRow> _qRows;
        private static readonly Dropdown _qDd = new Dropdown { Searchable = true };
        private static int _qSel = -1;          // 真实行下标（指向 _qRows）

        private static List<NpcRow> _nRows;
        private static readonly Dropdown _nDd = new Dropdown { Searchable = true };
        private static int _nSel = -1;          // 真实行下标（指向 _nRows）

        private static List<SpaceRow> _sRows;
        private static readonly Dropdown _sDd = new Dropdown { Searchable = true, ListHeight = 280f };
        private static int _sSel = -1;          // 真实行下标（指向 _sRows）

        // ---------------------------------------------------- v13: 通用下拉控件
        /// <summary>
        /// F9 下拉菜单（手写 IMGUI，不依赖 EditorGUILayout —— 运行时程序集里没有它）。
        /// · 收起态 = 一行按钮；展开态 = 搜索框 + 定高滚动列表。
        /// · 列表按 scroll.y **只绘制可见行**（虚拟化）：1875 条任务 / 1592 条 NPC /
        ///   1881 个空间每帧仍只布局约 15 行，不会像"全量 Toggle 平铺"那样把每帧
        ///   布局拖到几千行（那正是 v8 滚动卡顿的成因）。
        /// · 不截断：All 存全量标签，View 只是过滤结果，选中项回传真实行下标。
        /// </summary>
        private sealed class Dropdown
        {
            public bool Searchable;
            public float ListHeight = 220f;
            public bool Open;
            public Vector2 Scroll;
            public bool NeedRebuild = true;
            private string _search = "";
            public string Search
            {
                get { return _search; }
                set { if (value != _search) { _search = value; NeedRebuild = true; } }
            }
            public readonly List<string> All = new List<string>();
            public readonly List<int> View = new List<int>();
            public int SelView = -1;    // View 内的下标
            public int SelRow = -1;     // All / 真实表的行下标

            public void SetAll(List<string> labels)
            {
                All.Clear();
                if (labels != null) All.AddRange(labels);
                NeedRebuild = true;
            }

            private void Rebuild()
            {
                NeedRebuild = false;
                View.Clear();
                string f = (_search ?? "").Trim().ToLowerInvariant();
                for (int i = 0; i < All.Count; i++)
                {
                    if (f.Length > 0 && (All[i] ?? "").ToLowerInvariant().IndexOf(f) < 0) continue;
                    View.Add(i);
                }
                SelView = -1;
                for (int i = 0; i < View.Count; i++) if (View[i] == SelRow) { SelView = i; break; }
            }

            public void SyncView() { if (NeedRebuild) Rebuild(); }

            public void SelectRow(int row)
            {
                SelRow = row;
                SyncView();
                SelView = -1;
                for (int i = 0; i < View.Count; i++) if (View[i] == row) { SelView = i; break; }
            }
        }

        private const float DdRowH = 20f;

        /// <summary>画一个下拉。返回 true 表示本次产生了新的选中（调用方据此回写真实行下标）。</summary>
        private static bool DrawDropdown(Dropdown d, float width, string emptyHint)
        {
            if (d.All.Count == 0) { GUILayout.Label(emptyHint, GUILayout.Width(width)); return false; }
            d.SyncView();
            string cap = (d.SelRow >= 0 && d.SelRow < d.All.Count) ? d.All[d.SelRow]
                                                                  : "（未选择 / 共 " + d.All.Count + " 项）";
            if (GUILayout.Button(cap + (d.Open ? "  ▲" : "  ▼"), GUILayout.Width(width)))
            {
                d.Open = !d.Open;
                if (d.Open) d.Scroll = Vector2.zero;
            }
            if (!d.Open) return false;

            if (d.Searchable)
            {
                d.Search = GUILayout.TextField(d.Search ?? "", GUILayout.Width(width));
                d.SyncView();
            }
            GUILayout.Label("匹配 " + d.View.Count + " / 全部 " + d.All.Count + " 项"
                            + (d.SelRow >= 0 ? "（选中行 " + d.SelRow + "）" : ""), GUILayout.Width(width));

            bool changed = false;
            d.Scroll = GUILayout.BeginScrollView(d.Scroll, GUILayout.Width(width), GUILayout.Height(d.ListHeight));
            int n = d.View.Count;
            int first = Mathf.Max(0, Mathf.FloorToInt(d.Scroll.y / DdRowH));
            int last = Mathf.Min(n, first + Mathf.CeilToInt(d.ListHeight / DdRowH) + 1);
            if (first > 0) GUILayout.Space(first * DdRowH);          // 虚拟化：上方的行只占位不绘制
            for (int i = first; i < last; i++)
            {
                bool on = i == d.SelView;
                if (GUILayout.Toggle(on, d.All[d.View[i]], GUILayout.Height(DdRowH), GUILayout.ExpandWidth(true)) && !on)
                {
                    d.SelView = i;
                    d.SelRow = d.View[i];
                    changed = true;
                    d.Open = false;
                }
            }
            if (last < n) GUILayout.Space((n - last) * DdRowH);
            GUILayout.EndScrollView();
            return changed;
        }

        private static string _itemId = "";
        private static string _itemQty = "1000";
        private static string _attrId = "";
        private static string _attrVal = "100";
        private static string _intimacy = "100";
        private static string _teleportReason;

        private static string SpaceShortcuts()
        {
            // 取自示例 MOD 模板的 常见ID速查表.md（见仓库 docs/MOD开发文档/）
            return "10001 客栈大堂 | 100 河望镇 | 120 砚州 | 12006 比武场 | 444444 荒野岔口(测试空间)";
        }

        private static void Log(string s)
        {
            _status = s;
            _ops++;
            Observe(s);
            StoryDebugPlugin.Logger.LogInfo("[DBG] " + s);
        }

        private static void Fail(string what, Exception e) { Log(what + " 失败: " + e.Message); }

        private static int Int(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }

        /// <summary>The one and only call site hook (ManagerBehaviour.OnGUI).</summary>
        internal static void OnGui()
        {
            if (!Enabled) return;                       // MOD_DEBUG=0 -> zero cost
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F9)
            {
                // v14 P2: 被 MOD 管理页的「BepInEx 功能插件」开关关掉时，F9 不再开窗
                // （按键照样吞掉，游戏收不到 F9）。
                if (FeatureEnabled) Show = !Show;
                e.Use();
            }
            // P0 write-test driver: same single call site, no-op unless MOD_DEBUG_WRITETEST is set.
            WriteTest.Tick();
            DebugActions.Tick();                        // P1/P2 selftest driver (MOD_DEBUG_JUMPSELFTEST)
            ReadOnlyLoad.Tick();                        // P3/P4 cast: read-only slot load (MOD_DEBUG_LOADONLY)
            // MOD_DEBUG_SELFTEST=1 drives the same code paths headlessly so the evidence
            // can be captured without anyone clicking the window. It rides this single call
            // site on purpose; there is no second hook.
            if (SelfTest && !_stDone && !TableReady()) return;   // keep polling until tables exist
            if (SelfTest && !_stDone)
            {
                _stDone = true;
                try { RunSelfTest(); }
                catch (Exception ex) { StoryDebugPlugin.Logger.LogWarning("[DBG] selftest failed: " + ex); }
            }
            // v11 P0 冒烟：MOD_F9_RENDERTEST=1 时世界会话就绪后自动打开窗口
            RenderTestTick();
            // v14 P2 冒烟：MOD_FEATURE_SELFTEST=1 时把 IManagedFeaturePlugin 的开关语义跑一遍
            FeatureSelfTestTick();
            // closed -> one key check only；v14 P2: 开关关掉时同样不绘制（但上面的 tick 照跑）
            if (!Show || !FeatureEnabled) return;
            try { DrawCount++; DrawWindow(); }
            catch (Exception ex) { StoryDebugPlugin.Logger.LogWarning("[DBG] draw failed: " + ex); }
        }

        // ==================================================================
        // v14 P2: MOD 管理页「BepInEx 功能插件」开关的落点
        // ----------------------------------------------------------------
        // 契约由 LocalModManager.Abstractions.IManagedFeaturePlugin 定义（A 插件发现）。
        // 这里只做一件事：把 SetEnabled(bool) 映射到 F9 窗口的显隐。**不卸载插件、
        // 不销毁 MonoBehaviour、不停三个调试驱动**（WriteTest/DebugActions/ReadOnlyLoad
        // 照常 tick），因此关掉开关只是"看不见窗口"。
        // ==================================================================
        internal static bool FeatureEnabled = true;
        internal static int DrawCount;              // F9 窗口实际绘制的累计次数（冒烟断言用）

        internal static string FeatureStatusText()
        {
            return FeatureEnabled
                ? "F9 窗口：显示（开关开启）"
                : "F9 窗口：隐藏（MOD 管理页已关闭，插件仍在运行）";
        }

        internal static void SetFeatureEnabled(bool on)
        {
            FeatureEnabled = on;
            if (!on) Show = false;                  // 关开关就收起窗口；再打开不会自动弹出
            try
            {
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SetEnabled(" + (on ? "true" : "false")
                                                + ") -> " + FeatureStatusText());
            }
            catch { }
        }

        private static readonly bool FeatureSelfTest =
            Environment.GetEnvironmentVariable("MOD_FEATURE_SELFTEST") == "1";
        private static int _fstPhase;
        private static float _fstAt;
        private static int _fstDraw0;
        private static bool _fstFail;

        /// <summary>MOD_FEATURE_SELFTEST=1：三阶段跨帧自测（在世界会话就绪后开始）——
        /// ① 打印契约值（State/StatusMessage 必须非空）并 SetEnabled(false)；
        /// ② 0.5 s 后断言"关掉后一次都没绘制过"且窗口被收起，再 SetEnabled(true) + 打开窗口；
        /// ③ 0.5 s 后断言窗口重新被绘制，收尾记 PASS/FAIL。</summary>
        private static void FeatureSelfTestTick()
        {
            if (!FeatureSelfTest) return;
            var self = StoryDebugPlugin.Self;
            if (self == null) return;
            if (_fstPhase == 0)
            {
                if (Player() == null) return;       // 等世界会话
                _fstPhase = 1;
                _fstAt = Time.unscaledTime;
                string contract = "id=" + self.FeatureId + " name='" + self.DisplayName + "' ver=" + self.FeatureVersion
                                  + " state=" + self.State + " desired=" + self.DesiredEnabled
                                  + " status='" + self.StatusMessage + "' desc='" + self.Description + "'";
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SELFTEST begin " + contract);
                // 这一行就是 F10 分区里那一行会渲染出的两行文案（与 A 的 FeatureLabel 同格式）
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SELFTEST F10 行文案: "
                    + (self.DisplayName ?? self.FeatureId) + "  v" + (self.FeatureVersion ?? "?")
                    + "  [" + self.State + "] / " + (self.StatusMessage ?? ""));
                SetFeatureEnabled(false);
                _fstDraw0 = DrawCount;
                return;
            }
            if (_fstPhase == 1)
            {
                if (Time.unscaledTime - _fstAt < 0.5f) return;
                int delta = DrawCount - _fstDraw0;
                bool ok = delta == 0 && !Show;
                if (!ok) _fstFail = true;
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SELFTEST step1 SetEnabled(false): Show=" + Show
                    + " drawnDelta=" + delta + " (期望 0) -> " + (ok ? "OK" : "FAIL"));
                _fstPhase = 2;
                _fstAt = Time.unscaledTime;
                SetFeatureEnabled(true);
                Show = true;                        // 恢复后强制打开，验证"能再画出来"
                _fstDraw0 = DrawCount;
                return;
            }
            if (_fstPhase == 2)
            {
                if (Time.unscaledTime - _fstAt < 0.5f) return;
                int delta = DrawCount - _fstDraw0;
                bool ok = delta > 0;
                if (!ok) _fstFail = true;
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SELFTEST step2 SetEnabled(true): Show=" + Show
                    + " drawnDelta=" + delta + " (期望 >0) -> " + (ok ? "OK" : "FAIL"));
                Show = false;                       // 收尾，不留窗口
                _fstPhase = 3;
                bool nonEmpty = !string.IsNullOrEmpty(self.FeatureId) && !string.IsNullOrEmpty(self.DisplayName)
                                && !string.IsNullOrEmpty(self.StatusMessage) && !string.IsNullOrEmpty(self.Description);
                StoryDebugPlugin.Logger.LogInfo("[FEATURE] SELFTEST done " + (_fstFail ? "FAIL" : "PASS")
                    + " | 契约值非空=" + nonEmpty + " | 插件仍在运行=" + (State2() == "Running"));
            }
        }

        /// <summary>selftest 收尾读数：插件自身状态（Running/Stopped 由开关决定）。</summary>
        private static string State2()
        {
            try { return StoryDebugPlugin.Self == null ? "<null>" : StoryDebugPlugin.Self.State.ToString(); }
            catch { return "<err>"; }
        }

        private static void DrawWindow()
        {
            LoadUiPrefs();
            GUI.skin.label.fontSize = 13;
            GUI.skin.button.fontSize = 13;
            GUI.skin.textField.fontSize = 13;
            // IMGUI: the window rect is kept in a static so dragging persists, and is
            // persisted to BepInEx/config so position/size survive a restart (P3).
            _win = GUI.Window(0x4B44, _win, (GUI.WindowFunction)Body, "剧情调试 (F9)");
            SaveUiPrefsIfDue();
        }

        private static Rect _win = new Rect(360f, 60f, 660f, 560f);
        private static bool _rectLoaded;
        private static float _rectNextSave;
        private static Rect _rectSaved;

        private static string UiPrefsPath()
        {
            try { return System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "LocalModManager.debugui.txt"); }
            catch { return null; }
        }

        /// <summary>窗口位置/尺寸记忆（P3）：第一次绘制时从 BepInEx\config 读回。</summary>
        private static void LoadUiPrefs()
        {
            if (_rectLoaded) return;
            _rectLoaded = true;
            try
            {
                string p = UiPrefsPath();
                if (p == null || !System.IO.File.Exists(p)) return;
                var t = System.IO.File.ReadAllText(p).Trim().Split(' ');
                if (t.Length != 4) return;
                float x, y, w, h;
                if (float.TryParse(t[0], out x) && float.TryParse(t[1], out y)
                    && float.TryParse(t[2], out w) && float.TryParse(t[3], out h)
                    && w >= 480f && h >= 360f)
                {
                    _win = new Rect(x, y, w, h);
                    StoryDebugPlugin.Logger.LogInfo("[DBG] 已恢复窗口位置 " + _win);
                }
            }
            catch (Exception e) { StoryDebugPlugin.Logger.LogWarning("[DBG] 读取窗口位置失败: " + e.Message); }
        }

        private static void SaveUiPrefsIfDue()
        {
            if (Time.unscaledTime < _rectNextSave) return;
            _rectNextSave = Time.unscaledTime + 5f;
            if (_win.x == _rectSaved.x && _win.y == _rectSaved.y
                && _win.width == _rectSaved.width && _win.height == _rectSaved.height) return;
            try
            {
                string p = UiPrefsPath();
                if (p == null) return;
                System.IO.File.WriteAllText(p, _win.x + " " + _win.y + " " + _win.width + " " + _win.height);
                _rectSaved = _win;
            }
            catch (Exception e) { StoryDebugPlugin.Logger.LogWarning("[DBG] 保存窗口位置失败: " + e.Message); }
        }

        private static void Body(int id)
        {
            GUILayout.BeginVertical();

            var warn = new GUIStyle(GUI.skin.label);
            warn.normal.textColor = Color.red;
            warn.wordWrap = true;
            warn.fontSize = 13;
            GUILayout.Label(Warn, warn);

            GUILayout.Label("已执行操作: " + _ops + "   |   " + _status);
            if (WriteTest.Phase > 0) GUILayout.Label("[写入测试 phase=" + WriteTest.Phase + "] " + WriteTest.Status);
            if (DebugActions.SelfTest) GUILayout.Label("[P1/P2 联测] " + DebugActions.Status);

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_tab == 0, " 剧情跳跃 ", GUILayout.ExpandWidth(false))) _tab = 0;
            if (GUILayout.Toggle(_tab == 1, " 玩家参数 ", GUILayout.ExpandWidth(false))) _tab = 1;
            if (GUILayout.Toggle(_tab == 2, " NPC 社交 ", GUILayout.ExpandWidth(false))) _tab = 2;
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            var writable = Player() != null;
            // 左：执行区   右：观察区（P3 分栏）
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));

            GUILayout.BeginVertical(GUILayout.Width(392f));
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (RenderTest)
            {
                // v11 P0：冒烟模式把三个标签页各渲染一轮，单页异常只影响本页。
                // QuestTab 在 _qSel<0 时会提前 return（选不中任务就没有传送区），
                // 因此这里先选中第一行，确保覆盖到完整渲染路径 —— 越界那一段就在选中之后。
                EnsureQuests();
                EnsureNpcs();
                EnsureSpaces();
                // QuestTab 在未选中任务时会提前 return（选不中任务就没有传送区），
                // 因此这里先选中第一行，确保覆盖到完整渲染路径。
                if (_qSel < 0 && _qRows != null && _qRows.Count > 0) SelectQuest(0);
                // v13：三个下拉强制展开一轮，把"虚拟化列表"这条新渲染路径也纳入冒烟。
                _rtExpanded = true;
                _qDd.Open = true; _nDd.Open = true; _sDd.Open = true;
                for (int t = 0; t < 3; t++) { RenderTab(t); _rtTried |= (1 << t); }
                _qDd.Open = false; _nDd.Open = false; _sDd.Open = false;
                RenderTestReport();
            }
            else
            {
                RenderTab(_tab);
                if (_tabErr[_tab] != null) GUILayout.Label("【本页渲染异常】" + _tabErr[_tab]);
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();            // 执行区

            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("观察区（最近 " + _obs.Count + " 条）");
            _obsScroll = GUILayout.BeginScrollView(_obsScroll, GUILayout.ExpandHeight(true));
            for (int i = 0; i < _obs.Count; i++) GUILayout.Label(_obs[i]);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();            // 观察区

            GUILayout.EndHorizontal();

            // P3 尺寸记忆：右下角 20x20 拖动条
            var gizmo = new Rect(_win.width - 20f, _win.height - 20f, 20f, 20f);
            GUI.Box(gizmo, "");
            var ev = Event.current;
            if (ev != null && (ev.type == EventType.MouseDrag || ev.type == EventType.MouseDown)
                && gizmo.Contains(ev.mousePosition))
            {
                _win.width = Mathf.Max(480f, _win.width + ev.delta.x);
                _win.height = Mathf.Max(360f, _win.height + ev.delta.y);
                ev.Use();
            }

            GUILayout.EndVertical();
            GUI.DragWindow();
        }

        // ------------------------------------------------------- observation pane
        private static readonly List<string> _obs = new List<string>();
        private static Vector2 _obsScroll;
        private const int ObsMax = 400;

        // ------------------------------------------- v11 P0: 渲染隔离 + 冒烟
        /// <summary>MOD_F9_RENDERTEST=1：世界会话就绪后强制打开 F9 窗口，三个标签页各渲染一轮，
        /// 全部无异常记 `[F9RT] OK`，任一异常记 `[F9RT] FAIL &lt;页&gt; &lt;异常&gt;`。
        /// 这是为了堵住"GUI 渲染路径零自动化覆盖"的盲区（v10 的 harness 直调绕过了 IMGUI）。</summary>
        private static readonly bool RenderTest =
            Environment.GetEnvironmentVariable("MOD_F9_RENDERTEST") == "1";

        private static readonly string[] _tabErr = new string[3];
        private static readonly int[] _tabErrCount = new int[3];
        private static int _rtTried;
        private static bool _rtReported;
        private static bool _rtForceLogged;
        private static bool _rtExpanded;        // 冒烟是否覆盖到"下拉展开态"

        internal static bool RenderTestWanted { get { return RenderTest; } }

        /// <summary>把 F9 窗口打开（冒烟用），并在世界会话就绪时报一次 waiting/ready。</summary>
        internal static void RenderTestTick()
        {
            if (!RenderTest) return;
            if (Player() == null) return;          // 等世界会话
            if (!Show && FeatureEnabled) Show = true;
            if (!_rtForceLogged)
            {
                _rtForceLogged = true;
                StoryDebugPlugin.Logger.LogInfo("[F9RT] 世界会话就绪，强制打开 F9 窗口并渲染三个标签页");
            }
        }

        private static string TabName(int t)
        {
            return t == 0 ? "剧情跳跃" : (t == 1 ? "玩家参数" : "NPC 社交");
        }

        /// <summary>单页渲染。异常在此被吞掉：整帧 GUI 不再中止，窗口其余部分照常画。
        /// 同一条异常文本只在**首次出现**时记一行，避免每帧刷日志（v10 的 4995 行就是这样来的）。</summary>
        private static void RenderTab(int t)
        {
            try
            {
                if (t == 0) QuestTab();
                else if (t == 1) PlayerTab();
                else NpcTab();
                _tabErr[t] = null;
            }
            catch (Exception e)
            {
                string msg = e.GetType().Name + ": " + e.Message;
                if (msg != _tabErr[t])
                {
                    _tabErr[t] = msg;
                    _tabErrCount[t]++;
                    try
                    {
                        StoryDebugPlugin.Logger.LogError("[F9] 标签页 '" + TabName(t) + "' 渲染异常（第 "
                                               + _tabErrCount[t] + " 次，同文不再重复记）: " + msg);
                    }
                    catch { }
                }
            }
        }

        /// <summary>三页都渲染过一遍后，输出一行冒烟结论。</summary>
        private static void RenderTestReport()
        {
            if (_rtReported || _rtTried != 7) return;
            _rtReported = true;
            var sb = new StringBuilder();
            bool ok = true;
            for (int t = 0; t < 3; t++) if (_tabErr[t] != null) ok = false;
            if (ok) sb.Append("[F9RT] OK 三页渲染无异常");
            else
            {
                sb.Append("[F9RT] FAIL");
                for (int t = 0; t < 3; t++)
                    if (_tabErr[t] != null) sb.Append(" | ").Append(TabName(t)).Append(": ").Append(_tabErr[t]);
            }
            sb.Append(" | 下拉全量: 任务=").Append(_qRows == null ? "-1" : _qRows.Count.ToString())
              .Append(" NPC=").Append(_nRows == null ? "-1" : _nRows.Count.ToString())
              .Append(" 空间=").Append(_sRows == null ? "-1" : _sRows.Count.ToString())
              .Append(" | 未过滤可见行=任务").Append(_qDd.View.Count)
              .Append("/NPC").Append(_nDd.View.Count)
              .Append("/空间").Append(_sDd.View.Count)
              .Append(" | 展开态渲染=").Append(_rtExpanded ? "已覆盖" : "未覆盖");
            StoryDebugPlugin.Logger.LogInfo(sb.ToString());
        }

        /// <summary>Append one line to the observation pane (called by Log and by WriteTest).</summary>
        internal static void Observe(string line)
        {
            _obs.Add(line);
            if (_obs.Count > ObsMax) _obs.RemoveRange(0, _obs.Count - ObsMax);
            _obsScroll.y = float.MaxValue;      // keep the newest line in view
        }

        // ---------------------------------------------------------------- quest
        private static void EnsureQuests()
        {
            if (_qRows != null) return;
            _qRows = new List<QuestRow>();
            try
            {
                var list = Game.ConfigManager.Instance.Tables.TbQuest.DataList;
                for (int i = 0; i < list.Count; i++)
                {
                    var q = list[i];
                    if (q == null) continue;
                    string nm = "";
                    try { nm = q.name.GetText("zh-Hans"); } catch { }
                    _qRows.Add(new QuestRow { Id = q.id.Value, Name = nm ?? "", Group = q.taskGroup ?? "" });
                }
                var labels = new List<string>(_qRows.Count);
                for (int i = 0; i < _qRows.Count; i++)
                {
                    var r = _qRows[i];
                    labels.Add(r.Id + "  " + r.Name + (string.IsNullOrEmpty(r.Group) ? "" : "   [" + r.Group + "]"));
                }
                _qDd.SetAll(labels);
                Log("任务表已载入: " + _qRows.Count + " 行（全部进入下拉，无截断）");
            }
            catch (Exception ex) { Fail("载入任务表", ex); }
        }

        /// <summary>选中任务：同步真实行下标 _qSel 与下拉显示（手动点击与冒烟共用）。</summary>
        private static void SelectQuest(int row)
        {
            if (_qRows == null || row < 0 || row >= _qRows.Count) return;
            _qSel = row;
            _qDd.SelectRow(row);
        }

        private static void QuestTab()
        {
            EnsureQuests();
            EnsureSpaces();
            GUILayout.Label("任务下拉（展开后可在框内搜索 id / 名称 / 章节；全量 "
                            + (_qRows == null ? 0 : _qRows.Count) + " 条，不截断）");
            if (DrawDropdown(_qDd, 368f, "(任务表为空)")) _qSel = _qDd.SelRow;
            if (_qRows == null || _qSel < 0 || _qSel >= _qRows.Count)
            {
                GUILayout.Label("请在上方下拉中选择一个任务（共 " + (_qRows == null ? 0 : _qRows.Count) + " 条全量可选）");
                return;
            }
            var row = _qRows[_qSel];

            GUILayout.Space(6);
            GUILayout.Label("选中: " + row.Id + "  " + row.Name + "   [" + row.Group + "]");
            GUILayout.Label("当前状态: " + QuestStateOf(row.Id));

            bool canWrite = Player() != null;
            if (!canWrite) GUILayout.Label("【只读】未载入存档：写入按钮已灰显，先在游戏内载入存档");
            bool prevW = GUI.enabled;
            GUI.enabled = canWrite;

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("接受任务", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var qm = Game.QuestManager.Instance;
                    string before = QuestStateOf(row.Id);
                    bool ok = qm.DebugForceAcceptQuest(new LubanDatas.TbQuestId(row.Id));
                    Log("接受任务 " + row.Id + ": " + before + " -> " + QuestStateOf(row.Id) + " (rc=" + ok + ")");
                }
                catch (Exception ex) { Fail("接受任务", ex); }
            }
            if (GUILayout.Button("完成当前步骤", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var qm = Game.QuestManager.Instance;
                    var cfg = qm.GetQuestConfig(new LubanDatas.TbQuestId(row.Id));
                    int n = (cfg == null || cfg.objectives == null) ? 0 : cfg.objectives.Count;
                    int idx = -1;
                    for (int i = 0; i < n; i++)
                        if (!qm.IsObjectiveSettled(new LubanDatas.TbQuestId(row.Id), i)) { idx = i; break; }
                    if (idx < 0) { Log("完成当前步骤: 没有未结算的步骤 (objectives=" + n + ")"); }
                    else
                    {
                        qm.SettleObjectiveFromFlowVm(new LubanDatas.TbQuestId(row.Id), idx);
                        Log("完成步骤 " + row.Id + " #" + idx + " -> " + QuestStateOf(row.Id));
                    }
                }
                catch (Exception ex) { Fail("完成当前步骤", ex); }
            }
            if (GUILayout.Button("完成任务", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var qm = Game.QuestManager.Instance;
                    string before = QuestStateOf(row.Id);
                    qm.DebugForceCompleteQuest(new LubanDatas.TbQuestId(row.Id));
                    Log("完成任务 " + row.Id + ": " + before + " -> " + QuestStateOf(row.Id));
                }
                catch (Exception ex) { Fail("完成任务", ex); }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("提交任务", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var qm = Game.QuestManager.Instance;
                    string before = QuestStateOf(row.Id);
                    bool ok = qm.SubmitQuest(new LubanDatas.TbQuestId(row.Id));
                    Log("提交任务 " + row.Id + ": " + before + " -> " + QuestStateOf(row.Id) + " (rc=" + ok + ")");
                }
                catch (Exception ex) { Fail("提交任务", ex); }
            }
            if (GUILayout.Button("清除任务记录", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var qm = Game.QuestManager.Instance;
                    string before = QuestStateOf(row.Id);
                    bool ok = qm.RemoveQuestRecord(new LubanDatas.TbQuestId(row.Id));
                    Log("清除记录 " + row.Id + ": " + before + " -> " + QuestStateOf(row.Id) + " (rc=" + ok + ")");
                }
                catch (Exception ex) { Fail("清除记录", ex); }
            }
            GUILayout.EndHorizontal();
            GUI.enabled = prevW;

            // P1 空间传送 / P2 剧情跳跃：只有在载入存档后才可用，未载入时灰显并说明原因
            GUILayout.Space(6);
            GUILayout.Label("—— 空间传送（P1）——");
            GUILayout.Label("目标空间下拉（展开后可在框内搜索 id / 名称；全量 "
                            + (_sRows == null ? 0 : _sRows.Count) + " 条，不截断）");
            if (DrawDropdown(_sDd, 368f, "(空间表为空)")) _sSel = _sDd.SelRow;
            GUILayout.BeginHorizontal();
            GUI.enabled = canWrite;
            if (GUILayout.Button("传送到选中空间", GUILayout.ExpandWidth(false)))
            {
                if (_sRows == null || _sSel < 0 || _sSel >= _sRows.Count) Log("传送：还没有在下拉里选中空间");
                else DebugActions.Teleport(_sRows[_sSel].Id, true, true);
            }
            GUI.enabled = prevW;
            GUILayout.EndHorizontal();
            GUILayout.Label("常用: " + SpaceShortcuts(), GUILayout.ExpandWidth(false));
            GUILayout.Label("当前 " + DebugActions.SpaceIdText() + "（GotoSpace 为异步提交，迁移在 OnUpdate 跑完）");

            GUILayout.Space(6);
            GUILayout.Label("—— 剧情跳跃（P2，走 QuestManager.TryStartQuestProc）——");
            GUILayout.Label("任务 " + row.Id + " 的 flow: " + DebugActions.QuestFlowIds(row.Id));
            GUILayout.BeginHorizontal();
            GUI.enabled = canWrite;
            if (GUILayout.Button("执行该任务剧情流程", GUILayout.ExpandWidth(false)))
            {
                DebugActions.StartQuestProc(row.Id);
            }
            GUI.enabled = prevW;
            GUILayout.EndHorizontal();

            // 传送按钮的历史灰显原因（保留在窗口里，说明为什么不走 GotoSpaceCommand）
            GUILayout.Space(4);
            GUILayout.Label("  说明: " + (_teleportReason ?? TeleportReason()));

            GUILayout.Space(4);
            GUILayout.Label("任务状态枚举: " + Game.Model.QuestState.InProgress);
        }

        private static string QuestStateOf(int id)
        {
            try { return Game.QuestManager.Instance.GetQuestState(new LubanDatas.TbQuestId(id)).ToString(); }
            catch (Exception e) { return "<读取失败:" + e.Message + ">"; }
        }

        private static string TeleportReason()
        {
            _teleportReason =
                "P1 落地路线: SpaceManager.GotoSpace(TbSpaceId?, …) —— 地图旅行链的真实终点前一步"
                + "(BaseMapPoi.StartPoiTransferCore → SpaceManager.GotoSpaceInstance)，不需要 ProcContext。"
                + "旧路线 GotoSpaceCommand 已排除: 它的 ExecuteAsync(ProcContext) 只能由 FlowScheduler 虚调用，"
                + "且全镜像无直接调用者。直接调 GotoSpace 会跳过旅行耗时结算、引导提示与离场确认弹窗。";
            return _teleportReason;
        }

        // --------------------------------------------------------------- player
        private static void PlayerTab()
        {
            GUILayout.Label("当前玩家/世界: " + PlayerDesc());

            GUILayout.Space(4);
            GUILayout.Label("—— 货币 / 消耗品（读写背包物品）——");
            bool canWrite = Player() != null;
            if (!canWrite) GUILayout.Label("【只读】未载入存档：写入按钮已灰显，先在游戏内载入存档");
            bool prevW = GUI.enabled;
            GUI.enabled = canWrite;
            GUILayout.BeginHorizontal();
            GUILayout.Label("物品 id:", GUILayout.ExpandWidth(false));
            _itemId = GUILayout.TextField(_itemId ?? "", GUILayout.Width(100));
            GUILayout.Label("数量:", GUILayout.ExpandWidth(false));
            _itemQty = GUILayout.TextField(_itemQty ?? "", GUILayout.Width(80));
            if (GUILayout.Button("读取", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_itemId, -1);
                    long c = Bag().GetItemCount(new LubanDatas.TbItemId(id));
                    Log("物品 " + id + " 当前数量 = " + c);
                }
                catch (Exception ex) { Fail("读取物品", ex); }
            }
            if (GUILayout.Button("添加", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_itemId, -1), n = Int(_itemQty, 0);
                    long before = Bag().GetItemCount(new LubanDatas.TbItemId(id));
                    Bag().AddItem(new LubanDatas.TbItemId(id), n, Game.Model.ItemSourceType.GMCommand, null, null);
                    long after = Bag().GetItemCount(new LubanDatas.TbItemId(id));
                    Log("添加物品 " + id + " x" + n + ": " + before + " -> " + after);
                }
                catch (Exception ex) { Fail("添加物品", ex); }
            }
            if (GUILayout.Button("移除", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_itemId, -1), n = Int(_itemQty, 0);
                    var bag = Bag();
                    long before = bag.GetItemCount(new LubanDatas.TbItemId(id));
                    var item = FindBagItem(bag, id);
                    if (item == null) { Log("移除物品 " + id + ": 背包里没有该物品"); }
                    else
                    {
                        bool ok = bag.TryConsumeByUid(item.Uid, item.Count, n, Game.Model.ItemSourceType.GMCommand);
                        long after = bag.GetItemCount(new LubanDatas.TbItemId(id));
                        Log("移除物品 " + id + " x" + n + ": " + before + " -> " + after + " (rc=" + ok + ")");
                    }
                }
                catch (Exception ex) { Fail("移除物品", ex); }
            }
            if (GUILayout.Button("扫描背包里的货币物品", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var bag = Bag();
                    var items = bag.Items;
                    var seen = new List<string>();
                    for (int i = 0; i < items.Count && seen.Count < 6; i++)
                    {
                        var it = items[i];
                        if (it == null) continue;
                        if (!Game.Model.Components.BagModel.IsCurrencyItem(it.ItemId)) continue;
                        string s = it.ItemId.Value + "(x" + it.Count + ")";
                        if (!seen.Contains(s)) seen.Add(s);
                    }
                    Log("背包货币物品: " + (seen.Count == 0 ? "无" : string.Join(", ", seen.ToArray())));
                    if (seen.Count > 0 && string.IsNullOrEmpty(_itemId)) _itemId = seen[0].Split('(')[0];
                }
                catch (Exception ex) { Fail("扫描背包", ex); }
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("—— 交互属性（读写玩家属性）——");
            GUILayout.BeginHorizontal();
            GUILayout.Label("属性 id:", GUILayout.ExpandWidth(false));
            _attrId = GUILayout.TextField(_attrId ?? "", GUILayout.Width(100));
            GUILayout.Label("目标值:", GUILayout.ExpandWidth(false));
            _attrVal = GUILayout.TextField(_attrVal ?? "", GUILayout.Width(90));
            if (GUILayout.Button("读取", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_attrId, -1);
                    var p = Player();
                    Log("属性 " + id + " 值 = " + p.GetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id))
                        + " / 上限 " + p.GetInteractAttributeMaxValue(new LubanDatas.TbInteractAttributeId(id)));
                }
                catch (Exception ex) { Fail("读取属性", ex); }
            }
            if (GUILayout.Button("写入", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_attrId, -1), v = Int(_attrVal, 0);
                    var p = Player();
                    int before = p.GetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id));
                    p.SetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id), v);
                    int after = p.GetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id));
                    Log("属性 " + id + ": " + before + " -> " + after + " (目标 " + v + ")");
                }
                catch (Exception ex) { Fail("写入属性", ex); }
            }
            if (GUILayout.Button("+N", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int id = Int(_attrId, -1), v = Int(_attrVal, 0);
                    var p = Player();
                    int before = p.GetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id));
                    p.IncreaseInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id), v);
                    Log("属性 " + id + ": " + before + " -> "
                        + p.GetInteractAttributeValue(new LubanDatas.TbInteractAttributeId(id)) + " (+" + v + ")");
                }
                catch (Exception ex) { Fail("增加属性", ex); }
            }
            GUILayout.EndHorizontal();
            GUI.enabled = prevW;
        }

        private static Game.Model.PlayerModel Player()
        {
            return Game.Model.GameStoreManager.CurrentPlayer;
        }

        private static Game.Model.Components.BagModel Bag()
        {
            return Player().bag;
        }

        private static Game.Model.Components.BagItemBase FindBagItem(Game.Model.Components.BagModel bag, int itemId)
        {
            var items = bag.Items;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it != null && it.ItemId.Value == itemId) return it;
            }
            return null;
        }

        private static string PlayerDesc()
        {
            try
            {
                var p = Game.Model.GameStoreManager.CurrentPlayer;
                var w = Game.Model.GameStoreManager.CurrentGameWorld;
                if (p == null) return "<无玩家：请先载入存档>";
                return "玩家已载入, 世界 NPC 数 = " + (w != null && w.NpcModels != null ? w.NpcModels.Count.ToString() : "?");
            }
            catch (Exception e) { return "<未载入存档: " + e.Message + ">"; }
        }

        // ------------------------------------------------------------------ npc
        private static void EnsureNpcs()
        {
            if (_nRows != null) return;
            _nRows = new List<NpcRow>();
            try
            {
                var list = Game.ConfigManager.Instance.Tables.TbNpcBaseCfg.DataList;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r == null) continue;
                    string nm = "", ti = "";
                    try { nm = r.npcName.GetText("zh-Hans"); } catch { }
                    try { ti = r.npcTitle.GetText("zh-Hans"); } catch { }
                    _nRows.Add(new NpcRow { Id = r.id.Value, Name = nm ?? "", Title = ti ?? "" });
                }
                var labels = new List<string>(_nRows.Count);
                for (int i = 0; i < _nRows.Count; i++)
                {
                    var r = _nRows[i];
                    labels.Add(r.Id + "  " + r.Name + (string.IsNullOrEmpty(r.Title) ? "" : "   " + r.Title));
                }
                _nDd.SetAll(labels);
                Log("NPC 表已载入: " + _nRows.Count + " 行（全部进入下拉，无截断）");
            }
            catch (Exception ex) { Fail("载入 NPC 表", ex); }
        }

        /// <summary>TbSpace 全量进入"位置移动"下拉（id 名称 ↑父空间）。</summary>
        private static void EnsureSpaces()
        {
            if (_sRows != null) return;
            _sRows = new List<SpaceRow>();
            try
            {
                var list = Game.ConfigManager.Instance.Tables.TbSpace.DataList;
                for (int i = 0; i < list.Count; i++)
                {
                    var r = list[i];
                    if (r == null) continue;
                    string nm = "", pa = "";
                    try { nm = r.spaceName.GetText("zh-Hans"); } catch { }
                    try { var f = r.fatherSpace; pa = f.HasValue ? f.Value.Value.ToString() : ""; } catch { }
                    _sRows.Add(new SpaceRow { Id = r.id.Value, Name = nm ?? "", Parent = pa ?? "" });
                }
                var labels = new List<string>(_sRows.Count);
                for (int i = 0; i < _sRows.Count; i++)
                {
                    var r = _sRows[i];
                    labels.Add(r.Id + "  " + r.Name + (string.IsNullOrEmpty(r.Parent) ? "" : "   ↑" + r.Parent));
                }
                _sDd.SetAll(labels);
                Log("空间表已载入: " + _sRows.Count + " 行（全部进入下拉，无截断）");
            }
            catch (Exception ex) { Fail("载入空间表", ex); }
        }

        private static void NpcTab()
        {
            EnsureNpcs();
            GUILayout.Label("NPC 下拉（展开后可在框内搜索 id / 姓名 / 称号；全量 "
                            + (_nRows == null ? 0 : _nRows.Count) + " 条，不截断）");
            if (DrawDropdown(_nDd, 368f, "(NPC 表为空)")) _nSel = _nDd.SelRow;
            if (_nRows == null || _nSel < 0 || _nSel >= _nRows.Count)
            {
                GUILayout.Label("请在上方下拉中选择一个 NPC（共 " + (_nRows == null ? 0 : _nRows.Count) + " 条全量可选）");
                return;
            }
            var row = _nRows[_nSel];

            GUILayout.Space(6);
            GUILayout.Label("选中: " + row.Id + "  " + row.Name + "  " + row.Title);
            bool canWriteNpc = Player() != null;
            if (!canWriteNpc) GUILayout.Label("【只读】未载入存档：亲密度写入已灰显");
            GUILayout.BeginHorizontal();
            GUILayout.Label("亲密度增减:", GUILayout.ExpandWidth(false));
            _intimacy = GUILayout.TextField(_intimacy ?? "", GUILayout.Width(90));
            if (GUILayout.Button("读取亲密度", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    var npc = FindNpc(row.Id);
                    if (npc == null) Log("NPC " + row.Id + " 不在当前世界实例中（未生成/未驻留）");
                    else Log("NPC " + row.Id + " 亲密度 = " + npc.Intimacy + " / " + npc.MaxIntimacy
                             + "  等级:" + npc.GetIntimacyName());
                }
                catch (Exception ex) { Fail("读取亲密度", ex); }
            }
            bool prevNpc = GUI.enabled;
            GUI.enabled = canWriteNpc;
            if (GUILayout.Button("设置", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int v = Int(_intimacy, 0);
                    var npc = FindNpc(row.Id);
                    if (npc == null) Log("NPC " + row.Id + " 不在当前世界实例中，无法写入");
                    else
                    {
                        int before = npc.Intimacy;
                        npc.Intimacy = v;
                        Log("NPC " + row.Id + " 亲密度: " + before + " -> " + npc.Intimacy + " (设置 " + v + ")");
                    }
                }
                catch (Exception ex) { Fail("设置亲密度", ex); }
            }
            if (GUILayout.Button("增加", GUILayout.ExpandWidth(false)))
            {
                try
                {
                    int v = Int(_intimacy, 0);
                    var npc = FindNpc(row.Id);
                    if (npc == null) Log("NPC " + row.Id + " 不在当前世界实例中，无法写入");
                    else
                    {
                        int before = npc.Intimacy;
                        // native path: NpcModel.AddIntimacy(delta, actionType)
                        npc.AddIntimacy(v, LubanDatas.CommissionAffinityActionType.GIFT);
                        Log("NPC " + row.Id + " 亲密度(AddIntimacy/GIFT): " + before + " -> " + npc.Intimacy + " (+" + v + ")");
                    }
                }
                catch (Exception ex) { Fail("增加亲密度", ex); }
            }
            GUILayout.EndHorizontal();
            GUI.enabled = prevNpc;
            GUILayout.Label("注: \"设置\"用 NpcModel.Intimacy 的 public setter（绝对写入，不触发等级/好感事件）；"
                            + "\"增加\"走官方 NpcModel.AddIntimacy(delta, CommissionAffinityActionType.GIFT)。");
        }

        private static Game.Model.NpcModel FindNpc(int cfgId)
        {
            try
            {
                var w = Game.Model.GameStoreManager.CurrentGameWorld;
                if (w == null) return null;
                return w.GetNpc(new LubanDatas.TbNpcBaseCfgId(cfgId), true);
            }
            catch (Exception e) { Log("GetNpc(" + cfgId + ") 失败: " + e.Message); return null; }
        }

        // ------------------------------------------------------------- self test
        /// <summary>True once the config tables the tool reads are actually built.</summary>
        private static bool TableReady()
        {
            try
            {
                var c = Game.ConfigManager.Instance;
                return c != null && c.Tables != null
                    && c.Tables.TbQuest != null && c.Tables.TbNpcBaseCfg != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Walks every tab once: reads the two tables, filters them, then does one
        /// write round-trip per tab and puts the value back. Each step is isolated -
        /// a failure is logged and the remaining steps still run. Only runtime state
        /// is touched, never a save file.
        /// </summary>
        internal static void RunSelfTest()
        {
            Log("SELFTEST begin (runtime-only, every write is restored)");

            try { EnsureQuests(); } catch (Exception e) { Fail("SELFTEST 任务表", e); }
            try { EnsureNpcs(); } catch (Exception e) { Fail("SELFTEST NPC表", e); }
            try { EnsureSpaces(); } catch (Exception e) { Fail("SELFTEST 空间表", e); }
            Log("SELFTEST 任务表行数=" + (_qRows == null ? -1 : _qRows.Count)
                + " 首行=" + (_qRows != null && _qRows.Count > 0 ? _qRows[0].Id + " " + _qRows[0].Name : "-"));
            Log("SELFTEST NPC表行数=" + (_nRows == null ? -1 : _nRows.Count)
                + " 首行=" + (_nRows != null && _nRows.Count > 0 ? _nRows[0].Id + " " + _nRows[0].Name : "-"));
            Log("SELFTEST 空间表行数=" + (_sRows == null ? -1 : _sRows.Count)
                + " 首行=" + (_sRows != null && _sRows.Count > 0 ? _sRows[0].Id + " " + _sRows[0].Name : "-"));
            Log("SELFTEST 下拉全量: 任务=" + _qDd.All.Count + " NPC=" + _nDd.All.Count + " 空间=" + _sDd.All.Count);

            try
            {
                _qDd.Search = ""; _qDd.SyncView();
                int allQ = _qDd.View.Count;
                _qDd.Search = "邪"; _qDd.SyncView();
                Log("SELFTEST 任务搜索: 空关键词=" + allQ + " 关键词'邪'=" + _qDd.View.Count
                    + " 样例=" + (_qDd.View.Count > 0 ? _qDd.All[_qDd.View[0]] : "-"));
                _qDd.Search = "";

                _nDd.Search = ""; _nDd.SyncView();
                int allN = _nDd.View.Count;
                _nDd.Search = "苏"; _nDd.SyncView();
                Log("SELFTEST NPC搜索: 空关键词=" + allN + " 关键词'苏'=" + _nDd.View.Count
                    + " 样例=" + (_nDd.View.Count > 0 ? _nDd.All[_nDd.View[0]] : "-"));
                _nDd.Search = "";

                _sDd.Search = ""; _sDd.SyncView();
                Log("SELFTEST 空间下拉: 全量=" + _sDd.All.Count + " 可选=" + _sDd.View.Count);
                _sDd.Search = "客栈"; _sDd.SyncView();
                Log("SELFTEST 空间搜索: 关键词'客栈'=" + _sDd.View.Count
                    + " 样例=" + (_sDd.View.Count > 0 ? _sDd.All[_sDd.View[0]] : "-"));
                _sDd.Search = "";
            }
            catch (Exception e) { Fail("SELFTEST 搜索过滤", e); }

            Log("SELFTEST 玩家: " + PlayerDesc());
            Log("SELFTEST 传送按钮: canWrite=" + (Player() != null) + " 说明=" + TeleportReason());

            var p = Player();
            if (p == null)
            {
                Log("SELFTEST 未载入存档(主菜单): 跳过 属性/货币/亲密度 三个写入往返——只读链路已通过");
            }
            else
            {
                SelfTestAttribute(p);
                SelfTestCurrency();
                SelfTestIntimacy();
            }
            Log("SELFTEST done, ops=" + _ops);
        }

        private static void SelfTestAttribute(Game.Model.PlayerModel p)
        {
            try
            {
                var attrs = p.interactAttributes;
                var key = default(LubanDatas.TbInteractAttributeId);
                string src = "玩家运行时字典 interactAttributes";
                int count = attrs == null ? 0 : attrs.Count;
                if (count > 0)
                {
                    foreach (var kv in attrs) { key = kv.Key; break; }
                }
                else
                {
                    // fall back to the config table when the live dict cannot be walked
                    var rows = Game.ConfigManager.Instance.Tables.TbInteractAttribute.DataList;
                    if (rows == null || rows.Count == 0)
                    {
                        Log("SELFTEST 属性: interactAttributes 与 TbInteractAttribute 都为空, 跳过");
                        return;
                    }
                    key = rows[0].id;
                    src = "配置表 TbInteractAttribute";
                }
                int before = p.GetInteractAttributeValue(key);
                p.SetInteractAttributeValue(key, before + 10);
                int after = p.GetInteractAttributeValue(key);
                p.SetInteractAttributeValue(key, before);
                int back = p.GetInteractAttributeValue(key);
                Log("SELFTEST 属性 id=" + key.Value + " (来源=" + src + ", 字典" + count + "项)" + ": " + before
                    + " -> +10 -> " + after + " -> 还原 " + back + " [还原" + (back == before ? "成功" : "失败") + "]");
            }
            catch (Exception e) { Fail("SELFTEST 属性往返", e); }
        }

        private static void SelfTestCurrency()
        {
            try
            {
                var bag = Bag();
                if (bag == null || bag.Items == null)
                {
                    Log("SELFTEST 货币: 背包不可用, 跳过");
                    return;
                }
                var items = bag.Items;
                int itemId = -1;
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (it == null) continue;
                    if (Game.Model.Components.BagModel.IsCurrencyItem(it.ItemId)) { itemId = it.ItemId.Value; break; }
                }
                if (itemId < 0)
                {
                    Log("SELFTEST 货币: 背包内没有货币类物品(" + items.Count + " 格), 跳过");
                    return;
                }
                var tid = new LubanDatas.TbItemId(itemId);
                long before = bag.GetItemCount(tid);
                bag.AddItem(tid, 1000, Game.Model.ItemSourceType.GMCommand, null, null);
                long after = bag.GetItemCount(tid);
                long delta = after - before;
                string undo = "无可撤销项";
                if (delta > 0)
                {
                    var stack = FindBagItem(bag, itemId);
                    if (stack != null)
                    {
                        bool ok = bag.TryConsumeByUid(stack.Uid, stack.Count, (int)delta, Game.Model.ItemSourceType.GMCommand);
                        undo = "TryConsumeByUid rc=" + ok;
                    }
                }
                long back = bag.GetItemCount(tid);
                Log("SELFTEST 货币 item=" + itemId + ": " + before + " -> +1000 -> " + after
                    + " (delta=" + delta + ", " + undo + ") -> 还原 " + back
                    + " [还原" + (back == before ? "成功" : "未完全还原") + "]");
            }
            catch (Exception e) { Fail("SELFTEST 货币往返", e); }
        }

        private static void SelfTestIntimacy()
        {
            int[] probe = new int[] { 100000, 100001, 100002, 100003 };
            try
            {
                for (int i = 0; i < probe.Length; i++)
                {
                    var npc = FindNpc(probe[i]);
                    if (npc == null) { Log("SELFTEST 亲密度: NPC " + probe[i] + " 不在当前世界实例中"); continue; }
                    int before = npc.Intimacy;
                    npc.Intimacy = before + 100;
                    int after = npc.Intimacy;
                    npc.Intimacy = before;
                    int back = npc.Intimacy;
                    Log("SELFTEST 亲密度 NPC " + probe[i] + ": " + before + " -> +100 -> " + after
                        + " -> 还原 " + back + " [还原" + (back == before ? "成功" : "失败") + "]");
                    return;
                }
                Log("SELFTEST 亲密度: 探测的 4 个 NPC 都不在世界实例中, 跳过写入");
            }
            catch (Exception e) { Fail("SELFTEST 亲密度往返", e); }
        }
    }
}
