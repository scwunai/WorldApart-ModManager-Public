using System;
using System.Collections.Generic;
using LocalModManager.Abstractions;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using Game.Mod;
using A1.UPF.Runtime.Events;

namespace LocalModManager
{
    /// <summary>
    /// Logs every time the game itself calls ModBootstrap.OnGameLoaded() and
    /// re-applies enabled mods right after, so we learn whether the game's own
    /// lifecycle ever applies our patches.
    /// </summary>
    [HarmonyPatch(typeof(ModBootstrap), nameof(ModBootstrap.OnGameLoaded))]
    static class OnGameLoadedHook
    {
        static void Postfix()
        {
            Plugin.Logger.LogInfo("[HOOK] ModBootstrap.OnGameLoaded fired");
            ModSync.Apply("[HOOK-post]");
        }
    }

    /// <summary>
    /// Q1: puts a "MOD 管理" entry into the settings page's LEFT TAB COLUMN,
    /// alongside 音量设置 / 显示设置 / 系统设置, directly below 系统设置.
    /// v14 adds a second, parallel tab "BepInEx 功能插件" directly below it: same
    /// template geometry, same pointer gate, its own page (see ModPageController).
    ///
    /// v4 injected a row into the SystemPage content area instead; the client
    /// reported that row was not clickable and the shape was wrong. That row is
    /// therefore removed on sight (legacy node "MODEntry") and the entry is now
    /// cloned from a real tab node so it inherits the column's layout and style.
    ///
    /// Tab nodes are identified at runtime by their Button.onClick pointing at the
    /// SettingsPanelViewModel; their common parent is the tab column. If that
    /// heuristic finds nothing, the whole panel hierarchy is dumped to
    /// docs/实施计划与报告/v5_q1_recon.txt (MOD_Q1_RECON=1) so the real structure can be read off
    /// rather than guessed.
    /// </summary>
    [HarmonyPatch(typeof(Game.UI.UPFLogic.Settings.SettingsPanel))]
    static class SettingsEntryHook
    {
        private static bool _cleaned;
        private static string _lastReason = "";

        // Kept so the label and position can be re-asserted: Unity runs its layout
        // pass after we place the node, and the panel's localization may rewrite
        // the text when the page is enabled.
        //
        // v14: 两个标签（MOD 管理 / BepInEx 功能插件）共用同一套创建、保活、定位与
        // 点击门代码。手抄第二份创建代码迟早会让两边的几何或门禁走样，所以抽成一个
        // Tab 描述；MOD 标签的节点名、日志前缀与文案全部保持原样，基线输出逐字不变。
        private sealed class Tab
        {
            internal string NodeName;   // 节点名，"已存在则复用"靠它查找
            internal string Text;       // 每帧保活的文案
            internal string Tag;        // 点击日志前缀；MOD 标签必须是 "MOD"
            internal bool Diag;         // 只有 MOD 标签接入 TabDiag，避免抢它的 _btnId 绑定
            internal ModPageController.PageMode Mode;
            internal UnityEngine.Events.UnityAction OnClick;
            internal RectTransform Rt;
            internal TMPro.TextMeshProUGUI Label;
        }

        private static readonly Tab ModTab = new Tab
        {
            NodeName = "MODTab", Text = "MOD 管理", Tag = "MOD", Diag = true,
            Mode = ModPageController.PageMode.Packages
        };

        private static readonly Tab FeatureTab = new Tab
        {
            NodeName = "FeatureTab", Text = "BepInEx 功能插件", Tag = "FEATURES", Diag = false,
            Mode = ModPageController.PageMode.Features
        };

        private static readonly Tab[] _tabs = { ModTab, FeatureTab };

        private static RectTransform _navSystemRect;
        private static float _maintainAt;
        private static float _injectAt = -1f;
        private static float _diagAt;
        private static Game.UI.UPFLogic.Settings.SettingsPanel _panel;

        /// <summary>The live settings panel instance, so an env-gated self test can
        /// open the MOD page without anyone clicking through the menus.</summary>
        internal static Game.UI.UPFLogic.Settings.SettingsPanel CurrentPanel { get { return _panel; } }

        /// <summary>Diagnostic read-out for the MOD_TAB_DIAG click line.</summary>
        internal static bool IsPanelShowing()
        {
            try { return _panel != null && _panel.IsShowing; } catch { return false; }
        }

        /// <summary>Diagnostic accessor for the MOD_TAB_DIAG logging / gate self test.</summary>
        internal static RectTransform ModTabRect { get { return ModTab.Rt; } }

        /// <summary>Accessor for the v14 P6 self test of the second tab (BepInEx 功能插件).</summary>
        internal static RectTransform FeatureTabRect { get { return FeatureTab.Rt; } }

        /// <summary>Called from ManagerBehaviour.Update; cheap and fully guarded.</summary>
        internal static void Maintain()
        {
            try
            {
                if (ModTab.Rt == null) return;
                if (Time.unscaledTime < _maintainAt) return;
                _maintainAt = Time.unscaledTime + 0.25f;

                for (int i = 0; i < _tabs.Length; i++)
                    if (_tabs[i].Label != null && _tabs[i].Label.text != _tabs[i].Text)
                        _tabs[i].Label.text = _tabs[i].Text;

                PlaceBelowNav();

                // Placement diagnostics for 300 s after each injection. The UPF
                // world-rect question is settled by walking the ancestor chain:
                // wherever lossyScale collapses to 0 is where the visual tree
                // detaches from RectTransform space.
                if (_injectAt > 0f && Time.unscaledTime - _injectAt < 300f
                    && Time.unscaledTime >= _diagAt && _navSystemRect != null)
                {
                    _diagAt = Time.unscaledTime + 5f;
                    ChainDiag();
                }

                ModPageController.Tick();
                if (TabDiag.GateTest) TabDiag.GateTick();
                if (TabDiag.FeatureTabTest) TabDiag.FeatureTabTick();   // v14 P6: 第二标签页自测
            }
            catch { }
        }

        private static int _chainDumps;
        private static bool _rectLogged;
        private static bool _featRectLogged;   // v14: 功能插件标签的位置诊断只打一次
        private static float _lastPointerDownAt = -1e6f;

        /// <summary>
        /// One-shot-per-open dump of navSystem's ancestor chain: anchoredPosition,
        /// lossyScale and rect per level, plus UPFRectTransformStyle's public
        /// layout values where present. Tells us exactly where the UPF visual
        /// tree detaches from RectTransform space (lossyScale 0) and what the
        /// layout engine thinks the offsets are.
        /// </summary>
        private static void ChainDiag()
        {
            try
            {
                if (_chainDumps >= 6 || _navSystemRect == null) return;
                bool showing = false;
                try { showing = _panel != null && _panel.IsShowing; } catch { }
                if (!showing) return;    // only interesting while on screen
                _chainDumps++;
                Plugin.Logger.LogInfo(string.Format("[SETTINGS] chain inst={0} dump={1}",
                    _panel != null ? _panel.GetInstanceID() : 0, _chainDumps));
                Transform t = _navSystemRect;
                int level = 0;
                while (t != null && level < 10)
                {
                    var rt = t as RectTransform;
                    string ap = "?", ls = "?", rect = "?", upf = "";
                    try { if (rt != null) ap = rt.anchoredPosition.ToString("F1"); } catch { }
                    try { ls = t.lossyScale.ToString("F2"); } catch { }
                    try
                    {
                        if (rt != null)
                            rect = rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0");
                    }
                    catch { }
                    try
                    {
                        var style = t.GetComponent<A1.UPF.Runtime.Style.Transform.UPFRectTransformStyle>();
                        if (style != null)
                        {
                            Vector2 lap = style.LayoutAnchoredPosition;
                            Vector2 sc = style.Scale;
                            upf = " UPF[layoutAp=(" + lap.ToString("F1") + ") scale=(" + sc.ToString("F2") + ")]";
                        }
                    }
                    catch { }
                    bool active = true;
                    try { active = t.gameObject.activeSelf; } catch { }
                    Plugin.Logger.LogInfo(string.Format(
                        "[SETTINGS]   L{0} {1} active={2} ap=({3}) lossyScale=({4}) rect={5}{6}",
                        level, t.name, active, ap, ls, rect, upf));
                    t = t.parent;
                    level++;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SETTINGS] ChainDiag: " + e.Message); }
        }

        /// <summary>
        /// The tab column's positions are layout-baked: every sibling sits at
        /// localPosition (0,0) with its own anchors, so anchoredPosition math based
        /// on sibling anchoredPosition deltas (the v5/v6 approach) pushed MODTab to
        /// the bottom edge of the dialog. Anchor it geometrically instead: keep it
        /// the sibling right under 系统设置 and put MODTab's top-center exactly on
        /// navSystem's bottom-center, converted into the column's space.
        /// </summary>
        /// <summary>
        /// UPF bakes the tab column a few seconds after OnShow (chain diag 18:01:
        /// raw state ap=(0,0) rect=100x100, baked navSystem ap=(99,-194)
        /// rect=198x64, stable afterwards). Until the baked values exist MODTab
        /// stays hidden; afterwards it copies navSystem's anchors/pivot/size and
        /// sits exactly one row below it. Re-asserted every 0.25 s, which also
        /// follows any UPF re-layout.
        ///
        /// v14: 逐个标签往下排——第一个（MOD 管理）落在 navSystem 正下方的基线位置，
        /// 第二个（BepInEx 功能插件）再往下同样一行，几何全部复制自 navSystem，
        /// 所以两个标签大小、锚点、视觉来源完全一致。
        /// </summary>
        private static void PlaceBelowNav()
        {
            if (ModTab.Rt == null || _navSystemRect == null) return;
            try
            {
                var navRt = _navSystemRect;
                bool baked = navRt.rect.width > 50f && navRt.anchoredPosition.sqrMagnitude > 0.5f;
                float y = navRt.anchoredPosition.y;
                for (int i = 0; i < _tabs.Length; i++)
                {
                    var tab = _tabs[i];
                    if (tab.Rt == null) continue;
                    if (!baked)
                    {
                        if (tab.Rt.gameObject.activeSelf) tab.Rt.gameObject.SetActive(false);
                        continue;
                    }
                    if (!tab.Rt.gameObject.activeSelf) tab.Rt.gameObject.SetActive(true);

                    int wantIndex = navRt.GetSiblingIndex() + 1 + i;
                    if (tab.Rt.GetSiblingIndex() != wantIndex)
                        tab.Rt.SetSiblingIndex(wantIndex);

                    ModPageController.CopyRect(navRt, tab.Rt);
                    var ap = navRt.anchoredPosition;
                    y -= navRt.rect.height + 4f;      // 逐行下移，沿用基线的一行间距
                    tab.Rt.anchoredPosition = new Vector2(ap.x, y);
                    if (i == 0 && TabDiag.Enabled && !_rectLogged)
                    {
                        _rectLogged = true;
                        TabDiag.LogRect("TABRECT", tab.Rt);
                        TabDiag.LogRect("NAVRECT", _navSystemRect);
                    }
                    if (i == 1 && TabDiag.Enabled && !_featRectLogged)
                    {
                        _featRectLogged = true;
                        // 名字刻意不含 "TABRECT"：v9 的验收脚本按 'TABRECT unity=' 取坐标，
                        // 多一条同前缀的行会让它瞄到错误的标签。
                        TabDiag.LogRect("FEATRECT", tab.Rt);
                    }
                }
            }
            catch { }
        }

        internal static readonly bool Recon =
            Environment.GetEnvironmentVariable("MOD_Q1_RECON") == "1";

        [HarmonyPatch(nameof(Game.UI.UPFLogic.Settings.SettingsPanel.OnShow))]
        [HarmonyPostfix]
        static void OnShowPostfix(Game.UI.UPFLogic.Settings.SettingsPanel __instance) { TryInject(__instance, "OnShow"); }

        [HarmonyPatch(nameof(Game.UI.UPFLogic.Settings.SettingsPanel.Start))]
        [HarmonyPostfix]
        static void StartPostfix(Game.UI.UPFLogic.Settings.SettingsPanel __instance) { TryInject(__instance, "Start"); }

        static void TryInject(Game.UI.UPFLogic.Settings.SettingsPanel panel, string source)
        {
            try
            {
                _panel = panel;
                var root = panel.transform;
                if (root == null) { Note(source + ": panel.transform is null"); return; }

                if (!_cleaned) { _cleaned = true; RemoveLegacyEntry(root); }
                if (Recon) Q1Recon.DumpOnce(root, source);

                var vm = panel._viewModel;
                if (vm == null) { Note(source + ": _viewModel is null"); return; }

                Transform template = null, container = null;
                int count = 0;
                // Verified layout of this build (MOD_Q1_RECON dump):
                //   SettingsPanel(Clone)/Frame#modal/Frame#3/Frame#0/
                //     {Frame#navAudio, Frame#navDisplay, Frame#navControl(inactive),
                //      Frame#navSystem}
                // The tab column is found by locating Frame#navSystem anywhere under
                // the panel and taking its parent, so the intermediate path does not
                // have to be hardcoded.
                var sys = FindDeep(root, "Frame#navSystem");
                var col = sys != null ? sys.parent : null;
                if (col != null) { container = col; template = sys; count = col.childCount; }
                else Note(source + ": Frame#navSystem not found (children of root: " + ChildNames(root) + ")");
                if (container == null) FindTabColumn(root, ref template, ref container, ref count);
                if (container == null || template == null)
                {
                    Note(source + ": tab column not found (run with MOD_Q1_RECON=1 for a hierarchy dump)");
                    return;
                }
                _navSystemRect = template.GetComponent<RectTransform>();
                _panel = panel;

                // v14: 两个标签走同一条注入路径——已存在就复用，缺失就按同一个模板建。
                UnityEngine.UI.Image navImg = null, navBg = null;
                TMPro.TextMeshProUGUI navLabel = null;
                var created = new List<Tab>();
                for (int i = 0; i < _tabs.Length; i++)
                {
                    var tab = _tabs[i];
                    var existing = FindChild(container, tab.NodeName);
                    if (existing != null)
                    {
                        // Injection is per panel instance: the settings panel is
                        // recreated on every open, so a previous instance's MODTab is
                        // long gone. If this instance already has one, adopt it and
                        // leave; geometry is re-asserted by PlaceBelowNav().
                        int oldId = TabDiag.Id(tab.Rt);
                        tab.Rt = existing.GetComponent<RectTransform>();
                        tab.Label = existing.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                        if (tab.Diag && TabDiag.Enabled)
                        {
                            TabDiag.Bind(existing.GetComponent<UnityEngine.UI.Button>());
                            TabDiag.LogReinject(source + ":adopt-existing", oldId, TabDiag.Id(existing));
                        }
                        continue;
                    }

                    // 视觉模板只在真要建节点时读，复用路径与基线一样不碰模板（读坏了
                    // 也只是少两个标签的贴图，不该让整次注入抛出去）。
                    if (navLabel == null)
                    {
                        navImg = template.GetComponent<UnityEngine.UI.Image>();
                        var navBgT = FindChild(template, "__BackgroundImage");
                        navBg = navBgT != null ? navBgT.GetComponent<UnityEngine.UI.Image>() : null;
                        navLabel = template.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
                    }

                    created.Add(tab);
                    var go = CreateTabNode(container, template, navImg, navBg, navLabel, tab);
                    if (tab.Diag && TabDiag.Enabled)
                        TabDiag.LogReinject(source + ":create", TabDiag.Id(tab.Rt), TabDiag.Id(go));
                }

                // Both tabs already on this panel instance: nothing to add, and the
                // baseline adopt path returned here without re-logging the injection.
                if (created.Count == 0) return;

                _injectAt = Time.unscaledTime;
                _chainDumps = 0;
                _rectLogged = false;
                _featRectLogged = false;
                Maintain();
                _maintainAt = 0f;
                Maintain();
                Plugin.Logger.LogInfo("[SETTINGS] tab column after insert: " + ChildGeometry(container));
                for (int i = 0; i < created.Count; i++)
                {
                    var tab = created[i];
                    int images = 2;
                    bool addedBtn = true;
                    int had = 0;
                    Plugin.Logger.LogInfo("[SETTINGS] " + tab.Tag + " tab added via " + source
                                          + " (container='" + container.name + "', childCount=" + count
                                          + ", template='" + template.name + "', templateListeners=" + had
                                          + ", addedButton=" + addedBtn + ", raycastImages=" + images + ")");
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[SETTINGS] tab injection failed at " + source + ": " + e);
            }
        }

        /// <summary>
        /// S2 fix: do NOT clone navSystem. A clone carries its UPF element
        /// identity (A1ElementIdentity / style drivers), and the UPF layout
        /// engine then treats the clone as another instance of the same tab
        /// and repositions/restyles it every frame - the v7 build ended up
        /// parked at the column's raw anchor point, invisible. Build a plain
        /// uGUI node instead, copying only visuals (bg sprite, canvas alpha,
        /// label font/size/color); PlaceBelowNav() asserts geometry once the
        /// column has a baked rect. Two tabs call this with the same template,
        /// so 两个标签的外观来自同一份来源。
        /// </summary>
        private static GameObject CreateTabNode(Transform container, Transform template,
            UnityEngine.UI.Image navImg, UnityEngine.UI.Image navBg,
            TMPro.TextMeshProUGUI navLabel, Tab tab)
        {
            var go = new GameObject(tab.NodeName);
            go.AddComponent<RectTransform>();
            go.AddComponent<UnityEngine.UI.Image>();
            go.AddComponent<UnityEngine.UI.Button>();
            go.AddComponent<CanvasGroup>();
            go.transform.SetParent(container, false);
            ModPageController.CopyRect(template.GetComponent<RectTransform>(),
                                       (RectTransform)go.transform);

            var own = go.GetComponent<UnityEngine.UI.Image>();
            own.raycastTarget = true;
            if (navBg != null) { own.sprite = navBg.sprite; own.color = navBg.color; own.type = navBg.type; }
            else if (navImg != null) own.color = navImg.color;
            var cg = go.GetComponent<CanvasGroup>();
            cg.alpha = 0.82f;   // matches the native tabs' CanvasGroup(a=0.82)

            var label = ModPageController.MakeLabel(go.transform, "Text", tab.Text, navLabel);

            tab.Rt = (RectTransform)go.transform;
            tab.Label = label;

            // Own uGUI Button over the copied raycast Image.
            var btn = go.GetComponent<UnityEngine.UI.Button>();
            btn.targetGraphic = own;
            btn.onClick.RemoveAllListeners();
            // 委托在注入时才建（此刻 IL2CPP 一定已就绪），不在静态初始化里建。
            if (tab.OnClick == null)
                tab.OnClick = (UnityEngine.Events.UnityAction)(() => OnTabClicked(tab));
            btn.onClick.AddListener(tab.OnClick);
            // Without this the EventSystem can reach the new Button through
            // keyboard/gamepad navigation and fire onClick on Submit, which
            // showed up as spurious "MOD tab clicked" lines with no mouse input.
            try
            {
                btn.transition = UnityEngine.UI.Selectable.Transition.None;
                var nav = btn.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                btn.navigation = nav;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SETTINGS] navigation reset: " + e.Message); }
            return go;
        }

        /// <summary>Direct-child lookup by name; Transform.Find proved unreliable here.</summary>
        internal static Transform FindChild(Transform parent, string name)
        {
            if (parent == null) return null;
            try
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var c = parent.GetChild(i);
                    if (c != null && c.name == name) return c;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Depth-first lookup by node name anywhere under the given root.</summary>
        internal static Transform FindDeep(Transform t, string name)
        {
            if (t == null) return null;
            try
            {
                if (t.name == name) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    var hit = FindDeep(t.GetChild(i), name);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static string ChildNames(Transform parent)
        {
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; i < parent.childCount && i < 12; i++) sb.Append(parent.GetChild(i).name).Append('|');
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>name@x,y(size) per child - objective evidence of the tab placement.</summary>
        private static string ChildGeometry(Transform parent)
        {
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; i < parent.childCount && i < 12; i++)
                {
                    var c = parent.GetChild(i);
                    var rt = c.TryCast<RectTransform>();
                    sb.Append(c.name).Append('@').Append(rt != null ? rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0") : "?").Append(' '); 
                }
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>v4's content-area row must not come back.</summary>
        private static void RemoveLegacyEntry(Transform root)
        {
            try
            {
                var legacy = FindChild(root, "MODEntry");
                if (legacy != null)
                {
                    UnityEngine.Object.Destroy(legacy.gameObject);
                    Plugin.Logger.LogInfo("[SETTINGS] removed legacy MODEntry row (v4 content-area entry)");
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SETTINGS] legacy cleanup failed: " + e.Message); }
        }

        /// <summary>
        /// A tab is a node whose Button.onClick has a persistent target of type
        /// SettingsPanelViewModel. The column is the parent owning the most such
        /// nodes; the template is the last one found there (系统设置 sits at the
        /// bottom, so cloning it puts MOD 管理 directly below it).
        /// </summary>
        private static void FindTabColumn(Transform t, ref Transform template, ref Transform container, ref int best)
        {
            if (t == null) return;
            try
            {
                var btn = t.GetComponent<UnityEngine.UI.Button>();
                if (btn != null && IsTabButton(btn))
                {
                    var parent = t.parent;
                    int siblings = 0;
                    for (int c = 0; c < parent.childCount; c++)
                        if (parent.GetChild(c).GetComponent<UnityEngine.UI.Button>() != null) siblings++;
                    if (siblings >= best && t.name != "MODTab") { best = siblings; container = parent; template = t; }
                }
                for (int i = 0; i < t.childCount; i++) FindTabColumn(t.GetChild(i), ref template, ref container, ref best);
            }
            catch { }
        }

        private static bool IsTabButton(UnityEngine.UI.Button btn)
        {
            try
            {
                int n = btn.onClick.GetPersistentEventCount();
                for (int i = 0; i < n; i++)
                {
                    var tgt = btn.onClick.GetPersistentTarget(i);
                    if (tgt != null && tgt.TryCast<Game.UI.UPFLogic.Settings.SettingsPanelViewModel>() != null)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static int ForceRaycast(GameObject go)
        {
            int n = 0;
            try
            {
                var imgs = go.GetComponentsInChildren<UnityEngine.UI.Image>(true);
                if (imgs == null) return 0;
                for (int i = 0; i < imgs.Length; i++)
                {
                    var img = imgs[i];
                    if (img == null) continue;
                    img.raycastTarget = true;
                    n++;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SETTINGS] ForceRaycast: " + e.Message); }
            return n;
        }

        private static void Note(string reason)
        {
            if (reason == _lastReason) return;
            _lastReason = reason;
            Plugin.Logger.LogInfo("[SETTINGS] " + reason);
        }

        /// <summary>
        /// 两个标签共用的点击入口：日志前缀由 tab.Tag 决定（MOD 标签输出与基线逐字一致），
        /// 指针门对两个标签一视同仁——没有真实按下的 onClick.Invoke() 不许切页。
        /// </summary>
        private static void OnTabClicked(Tab tab)
        {
            try
            {
                if (TabDiag.Enabled && tab.Diag)
                    TabDiag.LogClick(tab.Rt != null ? tab.Rt.GetComponent<UnityEngine.UI.Button>() : null,
                                     tab.Rt != null ? tab.Rt.gameObject : null);
                Plugin.Logger.LogInfo("[SETTINGS] " + tab.Tag + " tab clicked t="
                                      + Time.unscaledTime.ToString("F2")
                                      + " mouseDown=" + Input.GetMouseButton(0));

                // v9 P1 gate. A real uGUI click is always preceded, in the same
                // 0.3 s window, by a physical left-button press (ManagerBehaviour.Update
                // records it). Every other way to reach Button.onClick - Submit from
                // keyboard/gamepad navigation, or any code that calls
                // Press()/onClick.Invoke() directly - has no such press and is dropped
                // here. Gamepad/keyboard users keep the F10 window.
                float since = Time.unscaledTime - _lastPointerDownAt;
                bool pressedRecently = since >= 0f && since <= 0.3f;
                if (!pressedRecently && !Input.GetMouseButtonUp(0))
                {
                    Plugin.Logger.LogInfo("[SETTINGS] " + tab.Tag + " tab click ignored (no real pointer) t="
                                          + Time.unscaledTime.ToString("F2")
                                          + " sinceMouseDown=" + (since > 1e5f ? "never" : since.ToString("F3"))
                                          + " mouseUp0=" + Input.GetMouseButtonUp(0));
                    return;
                }

                ModPageController.Toggle(_panel, tab.Mode);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[SETTINGS] " + tab.Tag + " tab click handler failed: " + e); }
        }

        /// <summary>Records the last physical left-button press seen this frame; the
        /// P1 gate in OnTabClicked requires one within 0.3 s of the click.</summary>
        internal static void NoteMouseDown()
        {
            try { _lastPointerDownAt = Time.unscaledTime; } catch { }
        }
    }

    /// <summary>
    /// v9 P0 — spurious "MOD tab clicked / mouseDown=False" hunt. Inert unless
    /// MOD_TAB_DIAG=1: with the switch off nothing is logged and the extra Unity
    /// event-system hooks are never installed, so the shipped click path keeps its
    /// current cost (one environment read at class init).
    ///
    /// With the switch on, every fire of the MOD tab's onClick prints one
    /// [DBG] TABCLICK line (frame, EventSystem selection, pointer state, instance
    /// ids, duplicate-node counts, first stack frames) and Button.Press /
    /// Button.OnPointerClick / Selectable.OnSubmit / UnityEvent.Invoke are patched
    /// so the *route* that reached onClick is visible even though il2cpp frames do
    /// not appear in a managed stack trace.
    /// </summary>
    static class TabDiag
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_TAB_DIAG") == "1";

        private static int _btnId;
        private static IntPtr _onClickPtr;
        private static int _lastClickFrame = -100000;
        private static float _lastClickTime = -100000f;
        private static int _evSeq;

        // H4 A/B data: the last time the v8 wheel hook (F2) saw a tick.
        internal static int LastWheelFrame = -100000;
        internal static float LastWheelTime = -100000f;

        internal static void Bind(UnityEngine.UI.Button btn)
        {
            try
            {
                _btnId = btn == null ? 0 : btn.GetInstanceID();
                _onClickPtr = btn == null ? IntPtr.Zero : btn.onClick.Pointer;
            }
            catch { }
        }

        internal static int Id(UnityEngine.Object o)
        {
            try { return o == null ? 0 : o.GetInstanceID(); } catch { return 0; }
        }

        private static bool IsOurs(UnityEngine.Component c)
        {
            try { return c != null && _btnId != 0 && c.GetInstanceID() == _btnId; }
            catch { return false; }
        }

        private static string Stack()
        {
            try
            {
                var raw = Environment.StackTrace;
                if (string.IsNullOrEmpty(raw)) return "-";
                var lines = raw.Split('\n');
                var sb = new StringBuilder();
                int n = 0;
                for (int i = 0; i < lines.Length && n < 5; i++)
                {
                    string s = lines[i].Trim();
                    if (s.Length == 0 || s.StartsWith("at " + typeof(TabDiag).FullName)) continue;
                    if (s.StartsWith("at System.Environment") || s.StartsWith("at LocalModManager.TabDiag")) continue;
                    if (s.StartsWith("at ")) s = s.Substring(3);
                    int paren = s.IndexOf('(');
                    if (paren > 0) s = s.Substring(0, paren);
                    if (sb.Length > 0) sb.Append(" < ");
                    sb.Append(s);
                    n++;
                }
                return sb.Length == 0 ? "-" : sb.ToString();
            }
            catch { return "-"; }
        }

        /// <summary>One line per onClick fire.</summary>
        internal static void LogClick(UnityEngine.UI.Button btn, UnityEngine.GameObject go)
        {
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                string sel = "null", module = "null", selId = "0";
                bool pointerOver = false;
                try
                {
                    if (es != null)
                    {
                        var s = es.currentSelectedGameObject;
                        if (s != null) { sel = s.name; selId = s.GetInstanceID().ToString(); }
                        var m = es.currentInputModule;
                        if (m != null) module = m.GetType().Name;
                        pointerOver = es.IsPointerOverGameObject();
                    }
                }
                catch (Exception e) { module = "<err:" + e.GetType().Name + ">"; }

                int btnCount = 0, imgCount = 0, nodes = 0, persistent = 0;
                try { var bs = go.GetComponents<UnityEngine.UI.Button>(); btnCount = bs == null ? 0 : bs.Length; } catch { }
                try { var is_ = go.GetComponents<UnityEngine.UI.Image>(); imgCount = is_ == null ? 0 : is_.Length; } catch { }
                try { persistent = btn == null ? 0 : btn.onClick.GetPersistentEventCount(); } catch { }
                try
                {
                    var p = go.transform.parent;
                    for (int i = 0; p != null && i < p.childCount; i++)
                        if (p.GetChild(i).name == "MODTab") nodes++;
                }
                catch { }

                int frame = 0; float now = 0f;
                try { frame = Time.frameCount; now = Time.unscaledTime; } catch { }

                var sb = new StringBuilder();
                sb.Append("[DBG] TABCLICK t=").Append(now.ToString("F3"))
                  .Append(" frame=").Append(frame)
                  .Append(" dframe=").Append(frame - _lastClickFrame)
                  .Append(" dt=").Append((now - _lastClickTime).ToString("F3"))
                  .Append(" down=").Append(Input.GetMouseButton(0))
                  .Append(" down0=").Append(Input.GetMouseButtonDown(0))
                  .Append(" up0=").Append(Input.GetMouseButtonUp(0))
                  .Append(" selected=").Append(sel).Append("#").Append(selId)
                  .Append(" module=").Append(module)
                  .Append(" pointerOverUI=").Append(pointerOver)
                  .Append(" appFocused=").Append(Application.isFocused)
                  .Append(" settingsOpen=").Append(SettingsEntryHook.IsPanelShowing())
                  .Append(" btnId=").Append(_btnId)
                  .Append(" btns=").Append(btnCount).Append(" imgs=").Append(imgCount)
                  .Append(" modTabNodes=").Append(nodes)
                  .Append(" persistentListeners=").Append(persistent)
                  .Append(" lastWheelDt=").Append((now - LastWheelTime).ToString("F2"))
                  .Append(" stack=").Append(Stack());
                Plugin.Logger.LogInfo(sb.ToString());

                _lastClickFrame = frame;
                _lastClickTime = now;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] TABCLICK log failed: " + e.Message); }
        }

        /// <summary>Route evidence: which Unity entry point reached the tab.</summary>
        internal static void NoteEvent(string where, UnityEngine.Component c, string extra)
        {
            if (!IsOurs(c)) return;
            Note(where, extra);
        }

        internal static void NoteOurEventInvoked()
        {
            Note("UnityEvent.Invoke(our onClick)", "");
        }

        private static void Note(string where, string extra)
        {
            try
            {
                _evSeq++;
                Plugin.Logger.LogInfo("[DBG] TABEV #" + _evSeq + " " + where
                                      + " frame=" + Time.frameCount
                                      + " down0=" + Input.GetMouseButtonDown(0)
                                      + " up0=" + Input.GetMouseButtonUp(0)
                                      + " focused=" + Application.isFocused
                                      + (string.IsNullOrEmpty(extra) ? "" : " " + extra));
            }
            catch { }
        }

        /// <summary>True when the UnityEvent firing is the MOD tab's own onClick.</summary>
        internal static bool IsOurEvent(UnityEngine.Events.UnityEvent e)
        {
            try { return _onClickPtr != IntPtr.Zero && e != null && e.Pointer == _onClickPtr; }
            catch { return false; }
        }

        /// <summary>MOD_TAB_GATE_TEST=1: exercises the P1 gate on the deployed binary
        /// without any OS input - A and C must be dropped (no press recorded), B must
        /// work (a press was recorded microseconds earlier). This is the automated
        /// stand-in for R2 while this machine's session has no foreground window.</summary>
        internal static readonly bool GateTest =
            Environment.GetEnvironmentVariable("MOD_TAB_GATE_TEST") == "1";

        private static int _gateStep;
        private static float _gateAt;

        internal static void GateTick()
        {
            if (!GateTest) return;
            try
            {
                var rt = SettingsEntryHook.ModTabRect;
                var btn = rt == null ? null : rt.GetComponent<UnityEngine.UI.Button>();
                if (btn == null) return;
                if (_gateAt == 0f) { _gateAt = Time.unscaledTime + 6f; return; }
                if (Time.unscaledTime < _gateAt) return;
                switch (_gateStep)
                {
                    case 0:
                        _gateStep = 1; _gateAt = Time.unscaledTime + 2f;
                        Plugin.Logger.LogInfo("[DBG] GATETEST A: onClick.Invoke() with no pointer press (expect ignored)");
                        btn.onClick.Invoke();
                        break;
                    case 1:
                        _gateStep = 2; _gateAt = Time.unscaledTime + 3f;
                        Plugin.Logger.LogInfo("[DBG] GATETEST B1: NoteMouseDown() then onClick.Invoke() (expect MOD page shown)");
                        SettingsEntryHook.NoteMouseDown();
                        btn.onClick.Invoke();
                        break;
                    case 2:
                        _gateStep = 3; _gateAt = Time.unscaledTime + 3f;
                        Plugin.Logger.LogInfo("[DBG] GATETEST B2: NoteMouseDown() then onClick.Invoke() (expect MOD page hidden)");
                        SettingsEntryHook.NoteMouseDown();
                        btn.onClick.Invoke();
                        break;
                    case 3:
                        _gateStep = 4; _gateAt = Time.unscaledTime + 2f;
                        Plugin.Logger.LogInfo("[DBG] GATETEST C: onClick.Invoke() 3 s after the press (expect ignored)");
                        btn.onClick.Invoke();
                        break;
                    case 4:
                        _gateStep = 5;
                        Plugin.Logger.LogInfo("[DBG] GATETEST done");
                        break;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] GATETEST failed: " + e.Message); _gateStep = 5; }
        }

        /// <summary>MOD_FEATURE_TAB_SELFTEST=1 (v14 P6): drives the second tab
        /// （「BepInEx 功能插件」）through the very same v9 pointer gate as GATETEST, so the
        /// new tab is verifiable without OS input: B1 must switch to the feature page
        /// （期望 [FEATURES] page built + [MODPAGE] shown … page=features），B2 must hide it
        /// （期望 [MODPAGE] hidden）。</summary>
        internal static readonly bool FeatureTabTest =
            Environment.GetEnvironmentVariable("MOD_FEATURE_TAB_SELFTEST") == "1";

        private static int _ftStep;
        private static float _ftAt;

        internal static void FeatureTabTick()
        {
            if (!FeatureTabTest) return;
            try
            {
                var rt = SettingsEntryHook.FeatureTabRect;
                var btn = rt == null ? null : rt.GetComponent<UnityEngine.UI.Button>();
                if (btn == null) return;
                if (_ftAt == 0f) { _ftAt = Time.unscaledTime + 8f; return; }
                if (Time.unscaledTime < _ftAt) return;
                switch (_ftStep)
                {
                    case 0:
                        _ftStep = 1; _ftAt = Time.unscaledTime + 3f;
                        Plugin.Logger.LogInfo("[DBG] FEATTAB B1: NoteMouseDown() then onClick.Invoke() (expect feature page shown)");
                        SettingsEntryHook.NoteMouseDown();
                        btn.onClick.Invoke();
                        break;
                    case 1:
                        _ftStep = 2; _ftAt = Time.unscaledTime + 3f;
                        Plugin.Logger.LogInfo("[DBG] FEATTAB B2: NoteMouseDown() then onClick.Invoke() (expect feature page hidden)");
                        SettingsEntryHook.NoteMouseDown();
                        btn.onClick.Invoke();
                        break;
                    case 2:
                        _ftStep = 3;
                        Plugin.Logger.LogInfo("[DBG] FEATTAB done");
                        break;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] FEATTAB failed: " + e.Message); _ftStep = 3; }
        }

        /// <summary>MOD_TAB_DIAG input probe. Logs mouse presence and position every
        /// 5 s, plus every left-button down/up, so a synthetic-input acceptance run
        /// can prove whether the game sees legacy Input events at all.</summary>
        internal static void Tick()
        {
            try
            {
                if (Input.GetMouseButtonDown(0))
                {
                    _downs++;
                    var p = Input.mousePosition;
                    Plugin.Logger.LogInfo("[DBG] INPUT LMB down at (" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ")");
                }
                if (Input.GetMouseButtonUp(0))
                {
                    _ups++;
                    var p = Input.mousePosition;
                    Plugin.Logger.LogInfo("[DBG] INPUT LMB up at (" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ")");
                }
                if (Time.unscaledTime < _probeAt) return;
                _probeAt = Time.unscaledTime + 5f;
                var mp = Input.mousePosition;
                string over = "?", hit = "?";
                try
                {
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es != null)
                    {
                        over = es.IsPointerOverGameObject().ToString();
                        var res = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                        var ped = new PointerEventData(es);
                        ped.position = new Vector2(mp.x, mp.y);
                        es.RaycastAll(ped, res);
                        hit = res.Count == 0 ? "none" : res[0].gameObject.name;
                    }
                }
                catch (Exception e) { hit = "<err:" + e.GetType().Name + ">"; }
                Plugin.Logger.LogInfo("[DBG] INPUT probe mousePresent=" + Input.mousePresent
                                      + " pos=(" + mp.x.ToString("F0") + "," + mp.y.ToString("F0") + ")"
                                      + " prev=(" + _lastMouse.x.ToString("F0") + "," + _lastMouse.y.ToString("F0") + ")"
                                      + " downs=" + _downs + " ups=" + _ups
                                      + " focused=" + Application.isFocused
                                      + " overUI=" + over + " hit=" + hit);
                _lastMouse = mp;
            }
            catch { }
        }

        private static Vector3 _lastMouse;
        private static float _probeAt;
        private static int _downs, _ups;

        /// <summary>Screen position of a tab node, so the acceptance script can aim a
        /// real mouse click. Unity screen space is bottom-left origin.</summary>
        internal static void LogRect(string label, RectTransform rt)
        {
            try
            {
                if (rt == null) return;
                Vector3 p = rt.TransformPoint(rt.rect.center);
                Plugin.Logger.LogInfo("[DBG] " + label + " unity=(" + p.x.ToString("F0") + "," + p.y.ToString("F0") + ")"
                                      + " winY=" + (Screen.height - p.y).ToString("F0")
                                      + " size=(" + rt.rect.width.ToString("F0") + "x" + rt.rect.height.ToString("F0") + ")"
                                      + " screen=" + Screen.width + "x" + Screen.height
                                      + " active=" + rt.gameObject.activeSelf);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] " + label + " failed: " + e.Message); }
        }

        /// <summary>Re-injection bookkeeping (Maintain / panel rebuild).</summary>
        internal static void LogReinject(string reason, int oldId, int newId)
        {
            try
            {
                Plugin.Logger.LogInfo("[DBG] TABREINJECT reason=" + reason
                                      + " oldId=" + oldId + " newId=" + newId
                                      + " frame=" + Time.frameCount
                                      + " t=" + Time.unscaledTime.ToString("F3"));
            }
            catch { }
        }
    }

    /// <summary>
    /// The extra Unity event-system hooks, installed only when MOD_TAB_DIAG=1.
    /// Each is a postfix/prefix that reads one IntPtr and returns, so they cannot
    /// change event routing; they only make the route visible in the log.
    /// </summary>
    static class TabDiagHooks
    {
        internal static int Installed;

        internal static void Install()
        {
            var h = new Harmony("local.modmanager.tabdiag");
            Try(h, typeof(UnityEngine.UI.Button), "Press", nameof(PressPost), null);
            Try(h, typeof(UnityEngine.UI.Button), "OnPointerClick", nameof(PointerClickPost),
                new[] { typeof(PointerEventData) });
            Try(h, typeof(UnityEngine.UI.Selectable), "OnSubmit", nameof(SubmitPost),
                new[] { typeof(BaseEventData) });
            Try(h, typeof(UnityEngine.EventSystems.EventSystem), "SetSelectedGameObject", nameof(SetSelectedPost),
                new[] { typeof(UnityEngine.GameObject), typeof(BaseEventData) });
            try
            {
                var m = AccessTools.Method(typeof(UnityEngine.Events.UnityEvent), "Invoke");
                if (m == null) Plugin.Logger.LogWarning("[DBG] TABDIAG UnityEvent.Invoke not found");
                else
                {
                    h.Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(TabDiagHooks), nameof(UnityEventPre))));
                    Installed++;
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] TABDIAG UnityEvent.Invoke patch failed: " + e.Message); }
            Plugin.Logger.LogInfo("[DBG] TABDIAG hooks installed=" + Installed);
        }

        private static void Try(Harmony h, Type t, string method, string postfix, Type[] argTypes)
        {
            try
            {
                var m = argTypes == null ? AccessTools.Method(t, method) : AccessTools.Method(t, method, argTypes);
                if (m == null) { Plugin.Logger.LogWarning("[DBG] TABDIAG " + t.Name + "." + method + " not found"); return; }
                h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(TabDiagHooks), postfix)));
                Installed++;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[DBG] TABDIAG " + t.Name + "." + method + " patch failed: " + e.Message); }
        }

        private static void PressPost(UnityEngine.UI.Button __instance)
        {
            TabDiag.NoteEvent("Button.Press", __instance, "");
        }

        private static void PointerClickPost(UnityEngine.UI.Button __instance, PointerEventData eventData)
        {
            string extra = "";
            try
            {
                if (eventData != null)
                {
                    var hit = eventData.pointerCurrentRaycast.gameObject;
                    extra = "pointerId=" + eventData.pointerId
                            + " button=" + eventData.button
                            + " clickCount=" + eventData.clickCount
                            + " pressPos=" + eventData.pressPosition
                            + " pos=" + eventData.position
                            + " hit=" + (hit == null ? "null" : hit.name);
                }
            }
            catch { }
            TabDiag.NoteEvent("Button.OnPointerClick", __instance, extra);
        }

        private static void SubmitPost(UnityEngine.UI.Selectable __instance)
        {
            TabDiag.NoteEvent("Selectable.OnSubmit", __instance, "");
        }

        private static void SetSelectedPost(UnityEngine.GameObject __0)
        {
            try
            {
                if (__0 != null && __0.name == "MODTab")
                    Plugin.Logger.LogInfo("[DBG] TABEV SetSelectedGameObject -> " + __0.name
                                          + "#" + __0.GetInstanceID() + " frame=" + Time.frameCount);
            }
            catch { }
        }

        private static bool UnityEventPre(UnityEngine.Events.UnityEvent __instance)
        {
            if (TabDiag.IsOurEvent(__instance)) TabDiag.NoteOurEventInvoked();
            return true;
        }
    }

    /// <summary>
    /// S2: native MOD management page, living inside the settings panel as a
    /// fifth content page next to the game's own audio/display/control/system
    /// pages. No extra window: the MOD 管理 left-tab (SettingsEntryHook) shows
    /// and hides this page.
    ///
    /// CRITICAL: nothing here is cloned from the game's UI. A clone of a UPF node
    /// carries the original's UPF element identity (A1ElementIdentity / style
    /// drivers), and the UPF layout engine then repositions and restyles the
    /// clone every frame - the v7 MODTab built by cloning navSystem ended up at
    /// the column's raw anchor point and was invisible. Everything is therefore
    /// built from scratch as plain uGUI (fresh GameObjects + Image/Button/TMP),
    /// copying ONLY baked RectTransform geometry and sprite/color/font references
    /// from templates. Build runs on first click, when the panel has real rects.
    ///
    /// Layout: the page root copies pageSystem's RectTransform; rows stack
    /// vertically from the Frame#confirmMapTravelTimeCard template slot
    /// (background + 24pt TMP label + switch = the game's own row look). One
    /// title row per package, one switch row per unit. The knob is driven
    /// manually (position + tint) and re-asserted once a second while visible.
    ///
    /// Page switching: showing the MOD page sets the four native pages inactive;
    /// clicking any native tab makes the VM change ActivePage, which Tick()
    /// detects and hides the MOD page (anti cross-talk).
    ///
    /// v14: 这一页现在按 PageMode 画两种内容——Packages（包/单元行，即 MOD 管理页）
    /// 与 Features（BepInEx 功能插件页）。左栏两个标签各点各的模式，行在切页时重建，
    /// 模板几何、滚动条、开关绘制与 1 Hz 刷新全部共用，所以两页外观与行为一致。
    /// </summary>
    static class ModPageController
    {
        private class Row
        {
            internal string ModId, UnitId;
            internal int Npc;
            internal bool On;
            internal bool IsPackage;   // master switch for the whole package
            internal string FeatureId;  // kind 4: which managed feature this row toggles
            internal int Kind;         // 1 = package header, 2 = unit row, 3 = section title, 4 = feature row
            internal string PkgOf;     // unit row -> owning package ModId
            internal RectTransform RootRt;
            internal TMPro.TextMeshProUGUI Arrow;   // package header expand arrow
            internal TMPro.TextMeshProUGUI Label;
            internal string LabelText;
            internal RectTransform SwRt, KnobRt;
            internal UnityEngine.UI.Image KnobImage;
            internal Color KnobOnColor;
        }

        /// <summary>
        /// v14: 同一套构建/布局/滚动/开关代码画两种页。Packages = 原有的 MOD 管理页
        /// （包/单元行，不再含功能插件分区），Features = BepInEx 功能插件页（Kind 4 行）。
        /// 左栏两个标签各自指定模式，切页只是换一批行。
        /// </summary>
        internal enum PageMode { Packages, Features }

        private const string PageName = "MODPage";
        private const string HeaderText = "MOD 管理";
        private const string FeatureHeaderText = "BepInEx 功能插件";
        private static PageMode _mode = PageMode.Packages;
        private static bool _featurePageBuiltLogged;

        private static GameObject _page;
        private static RectTransform _pageRt;
        private static Game.UI.UPFLogic.Settings.SettingsPanelViewModel _vm;
        // Baked geometry/sprite sources captured at build time (first click, when
        // the panel has real rects). Only RectTransform values and Image sprite /
        // color are copied - never any UPF component (see class remark above).
        private static RectTransform _rowTemplateRt;          // Frame#confirmMapTravelTimeCard
        private static UnityEngine.UI.Image _rowBgTemplate;   // row __BackgroundImage
        private static TMPro.TextMeshProUGUI _rowLabelStyle;  // row label TMP
        private static RectTransform _swTemplateRt;           // Frame#swConfirmMapTravelTime
        private static UnityEngine.UI.Image _swHitTemplate;   // switch hit Image
        private static UnityEngine.UI.Image _swBgTemplate;    // switch __BackgroundImage
        private static RectTransform _knobTemplateRt;         // Frame#swConfirmMapTravelTimeKnob
        private static UnityEngine.UI.Image _knobImageTemplate; // knob __BackgroundImage
        private static RectTransform _headerTemplateRt;       // pageSystem/Frame#0
        private static TMPro.TextMeshProUGUI _headerLabelStyle; // txtNavSystem
        private static float _rowHeight = 60f;
        private static float _pkgRowH = 100f;
        private static float _nextRowY;
        private static readonly List<Row> _rows = new List<Row>();
        private static readonly Dictionary<string, bool> _expanded = new Dictionary<string, bool>();
        private static bool _visible;
        private static float _refreshAt;
        private static string _openOnActivePage = "";
        private static TMPro.TextMeshProUGUI _headerLabel;
        private static RectTransform _contentPageRt;   // live pageSystem (geometry fallback)
        private static RectTransform _viewportRt;      // scroll viewport (visible window)
        private static RectTransform _headerRt;        // our header row root
        private static readonly List<RectTransform> _rowRoots = new List<RectTransform>();
        // Scroll support (see docs/实施计划与报告/MOD页滚动修复_实施报告.md). Content is the rows'
        // parent and the only node that moves; its top edge is the page's top edge,
        // so Relayout's row maths (y measured from the container's top edge) stays
        // exactly as it was.
        private static RectTransform _contentRt;
        private static UnityEngine.UI.ScrollRect _scrollRect;  // F1 wheel sink (native)
        private static UnityEngine.UI.Scrollbar _modScrollbar; // MOD list's own scrollbar
        // The game's own vertical scrollbar belongs to the native pages. It is a
        // sibling of the Viewport, i.e. it draws on top of our page, so it is parked
        // while the MOD page is up and put back exactly as found on Hide.
        private static UnityEngine.UI.ScrollRect _gameScrollRect;
        private static UnityEngine.UI.Scrollbar _gameScrollbar;
        private static bool _gameScrollbarWasActive;
        private static bool _gameScrollbarParked;
        private static float _contentHeight = 52f;     // stacked height of the visible rows
        private static float _lastReportedScroll = -1f;
        private static int _scrollEvents;

        // Wheel feel. Measured on this build: ONE physical detent (WHEEL_DELTA 120 via
        // SendInput) arrives as 360 units of PointerEventData.scrollDelta - 12 injected
        // detents produced 6 events of -720. ScrollRect moves the content by
        // delta * scrollSensitivity, so the old 0.55 moved 198 px per detent, i.e. 40%
        // of the 492 px page - that read as "scrolling a whole page per notch".
        // Half a row per detent keeps consecutive notches heavily overlapped.
        private const float DeltaPerWheelDetent = 360f;
        private const float RowStepPerDetent = 0.5f;

        /// <summary>点标签：同页再点=收起（基线行为）；从另一页点过来=切页。</summary>
        internal static void Toggle(Game.UI.UPFLogic.Settings.SettingsPanel panel, PageMode mode)
        {
            if (_visible && _mode == mode) { Hide(); return; }
            Show(panel, mode);
        }

        /// <summary>MOD_PAGE_SELFTEST 的入口，与基线一样打开包管理页。</summary>
        internal static void Show(Game.UI.UPFLogic.Settings.SettingsPanel panel)
        {
            Show(panel, PageMode.Packages);
        }

        internal static void Show(Game.UI.UPFLogic.Settings.SettingsPanel panel, PageMode mode)
        {
            try
            {
                if (panel == null) { Plugin.Logger.LogWarning("[MODPAGE] Show: panel is null"); return; }
                if (!Build(panel)) return;

                _openOnActivePage = _vm != null ? SafeActivePage() : "";
                // Overlay only: do NOT SetActive(false) the native pages. That
                // reverted VM.ActivePage to the default ('audio'), which made
                // Tick auto-close the MOD page and left natives hidden -> black
                // content. The page's opaque Bg image covers them instead.

                _page.SetActive(true);
                SetMode(mode);     // 换一批行：切页时另一页的行在这里被清掉
                if (_scrollRect != null)   // F3: a freshly opened page starts at the top
                {
                    try { _scrollRect.StopMovement(); } catch { }
                }
                if (_contentRt != null) _contentRt.anchoredPosition = Vector2.zero;
                Relayout();          // re-copy geometry now that UPF has baked real rects
                _visible = true;
                SyncGameScrollbar(); // put the native pages' scrollbar away
                _refreshAt = 0f;   // force an immediate row refresh
                Plugin.Logger.LogInfo("[MODPAGE] shown (native pages hidden, page=" + ModeName(mode)
                                      + ", rows=" + _rows.Count
                                      + ", openOnActivePage='" + _openOnActivePage + "', contentH="
                                      + _contentHeight.ToString("F0") + ")");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] Show failed: " + e); }
        }

        internal static void Hide()
        {
            try
            {
                _visible = false;
                if (_page != null) _page.SetActive(false);
                SyncGameScrollbar();   // give the native pages their scrollbar back
                Plugin.Logger.LogInfo("[MODPAGE] hidden");
            }
            catch { }
        }

        // ================================ scroll support (F1 / F3) ============
        // F1 wheel sink: a REAL uGUI ScrollRect lives on our page root. uGUI delivers
        // a wheel tick by walking up from whatever was raycast (ExecuteEvents
        // .ExecuteHierarchy) and stopping at the first GameObject that handles
        // IScrollHandler. Our page root is an ancestor of every row, so it stops
        // there and the game's own settings ScrollRect (Frame#pageShell, two levels
        // up) never sees the event. That is the whole fix for the tab flip, because
        // the flip is driven by ScrollRect.OnScroll ->
        // SettingsScrollController.OnScrolled ->
        // SettingsPanelViewModel.OnViewportTopPageChanged -> set_ActivePage.
        //
        // A native ScrollRect is used rather than a custom IScrollHandler component:
        // Il2CppInterop ships il2cpp interfaces de-interfaceified, so a managed class
        // cannot be wired into the native vtable slot for OnScroll (measured: the
        // class is found by ExecuteEvents and consumes the event, but its managed
        // method is never called). The native component has no such problem and also
        // gives clamped movement and drag scrolling for free.
        //
        // F2 (NativeWheelConsumerHook) is the backstop for the one case the sink
        // cannot cover: a tick that is raycast onto the Viewport instead of onto our
        // page. It only ever touches ScrollRect.OnScroll - never a click path.

        /// <summary>Slides the scroll window after a collapse/expand or a re-stack:
        /// Content keeps its stacked height, and the ScrollRect's own Clamped mode
        /// pulls the window back in range if the content shrank. Also emits the
        /// scroll trace used as acceptance evidence.</summary>
        private static void SyncContentSize()
        {
            if (_contentRt == null) return;
            try
            {
                var sd = _contentRt.sizeDelta;
                if (Mathf.Abs(sd.y - _contentHeight) > 0.5f)
                    _contentRt.sizeDelta = new Vector2(sd.x, _contentHeight);

                if (_scrollRect != null)
                    _scrollRect.scrollSensitivity =
                        (_rowHeight + 2f) * RowStepPerDetent / DeltaPerWheelDetent;

                float vp = _pageRt != null ? _pageRt.rect.height : 0f;
                float max = Mathf.Max(0f, _contentHeight - vp);
                // top-pivot content: y = 0 is the top row, y = max is the last row
                float y = _contentRt.anchoredPosition.y;
                if (y < 0f) y = 0f;
                if (y > max) y = max;
                if (Mathf.Abs(y - _lastReportedScroll) > 0.5f)
                {
                    _lastReportedScroll = y;
                    _scrollEvents++;
                    Plugin.Logger.LogInfo("[MODPAGE] scroll y=" + y.ToString("F0")
                                          + " max=" + max.ToString("F0")
                                          + " contentH=" + _contentHeight.ToString("F0")
                                          + " vp=" + vp.ToString("F0")
                                          + " n=" + _scrollEvents);
                }
            }
            catch { }
        }

        /// <summary>
        /// The game's vertical scrollbar. ScrollRect.verticalScrollbar is NOT wired in
        /// this build (measured: null, while the node itself exists), so fall back to
        /// the node itself - "Scrollbar Vertical", a sibling of the Viewport under
        /// Frame#pageShell.
        /// </summary>
        private static UnityEngine.UI.Scrollbar FindNativeScrollbar(
            Game.UI.UPFLogic.Settings.SettingsPanel panel)
        {
            try
            {
                if (_gameScrollRect != null)
                {
                    var s = _gameScrollRect.verticalScrollbar;
                    if (s != null) { Plugin.Logger.LogInfo("[MODPAGE] native scrollbar via ScrollRect.verticalScrollbar"); return s; }
                }
            }
            catch { }
            try
            {
                Transform parent = _viewportRt != null ? _viewportRt.parent : null;
                if (parent != null)
                {
                    var t = FindChild(parent, "Scrollbar Vertical");
                    if (t != null)
                    {
                        var s = t.GetComponent<UnityEngine.UI.Scrollbar>();
                        if (s != null) { Plugin.Logger.LogInfo("[MODPAGE] native scrollbar via sibling lookup of the Viewport"); return s; }
                    }
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform c = null;
                        try { c = parent.GetChild(i); } catch { break; }
                        if (c == null) continue;
                        var s = c.GetComponent<UnityEngine.UI.Scrollbar>();
                        if (s != null) { Plugin.Logger.LogInfo("[MODPAGE] native scrollbar via first Scrollbar child of '" + parent.name + "'"); return s; }
                    }
                }
            }
            catch { }
            try
            {
                if (panel != null)
                {
                    var t = FindDeepTolerant(panel.transform, n => n == "Scrollbar Vertical");
                    if (t != null)
                    {
                        var s = t.GetComponent<UnityEngine.UI.Scrollbar>();
                        if (s != null) { Plugin.Logger.LogInfo("[MODPAGE] native scrollbar via deep lookup"); return s; }
                    }
                }
            }
            catch { }
            Plugin.Logger.LogWarning("[MODPAGE] native scrollbar not found; using the plain fallback look");
            return null;
        }

        /// <summary>
        /// MOD list's own vertical scrollbar. Built as fresh uGUI (never a clone of a
        /// game node - a clone would carry UPF element identity and be re-laid-out
        /// every frame), copying only the game's own scrollbar geometry and sprites.
        /// Template, measured on this build: "Scrollbar Vertical" under
        /// Frame#pageShell, 12x492, anchors (1,0)-(1,1), pivot (1,1), track sprite
        /// scrollbar-track-bg, direction TopToBottom, with scrollbarArrowUp /
        /// scrollbarArrowDown and a Sliding Area / Handle child pair.
        /// </summary>
        private static void BuildModScrollbar(UnityEngine.UI.ScrollRect sr, RectTransform pageRt,
                                              Game.UI.UPFLogic.Settings.SettingsPanel panel)
        {
            try
            {
                if (_viewportRt == null) return;
                try { _gameScrollRect = _viewportRt.parent.GetComponent<UnityEngine.UI.ScrollRect>(); }
                catch { }
                var tpl = FindNativeScrollbar(panel);
                _gameScrollbar = tpl;

                var go = new GameObject("ModScrollbar");
                go.AddComponent<RectTransform>();
                var trackImg = go.AddComponent<UnityEngine.UI.Image>();
                var sb = go.AddComponent<UnityEngine.UI.Scrollbar>();
                go.transform.SetParent(pageRt, false);
                var rt = (RectTransform)go.transform;

                RectTransform tplRt = tpl != null ? tpl.GetComponent<RectTransform>() : null;
                if (tplRt != null) CopyRect(tplRt, rt);
                else
                {
                    rt.anchorMin = new Vector2(1f, 0f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(1f, 1f);
                    rt.sizeDelta = new Vector2(12f, 0f);
                    rt.anchoredPosition = Vector2.zero;
                }

                // arrow caps (purely visual, copied from the template)
                if (tpl != null)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Transform c = null;
                        try { c = tpl.transform.GetChild(i); } catch { break; }
                        if (c == null || c.name == null || !c.name.StartsWith("scrollbarArrow")) continue;
                        var srcImg = c.GetComponent<UnityEngine.UI.Image>();
                        var aGo = new GameObject(c.name);
                        aGo.AddComponent<RectTransform>();
                        var aImg = aGo.AddComponent<UnityEngine.UI.Image>();
                        aGo.transform.SetParent(rt, false);
                        if (srcImg != null)
                        {
                            CopyRect(srcImg.GetComponent<RectTransform>(), (RectTransform)aGo.transform);
                            aImg.sprite = srcImg.sprite;
                            aImg.color = srcImg.color;
                            aImg.type = srcImg.type;
                        }
                        aImg.raycastTarget = false;
                    }
                }

                // Sliding Area -> Handle, geometry copied from the template pair
                var slideGo = new GameObject("Sliding Area");
                slideGo.AddComponent<RectTransform>();
                var slideRt = (RectTransform)slideGo.transform;
                slideGo.transform.SetParent(rt, false);
                slideRt.anchorMin = Vector2.zero;
                slideRt.anchorMax = Vector2.one;
                slideRt.offsetMin = new Vector2(0f, 8f);
                slideRt.offsetMax = new Vector2(0f, -8f);
                if (tpl != null)
                {
                    var tplSlide = FindChild(tpl.transform, "Sliding Area");
                    var tplSlideRt = AsRt(tplSlide);
                    if (tplSlideRt != null) CopyRect(tplSlideRt, slideRt);
                }

                var handleGo = new GameObject("Handle");
                handleGo.AddComponent<RectTransform>();
                var handleImg = handleGo.AddComponent<UnityEngine.UI.Image>();
                handleGo.transform.SetParent(slideRt, false);
                var handleRt = (RectTransform)handleGo.transform;
                handleRt.anchorMin = Vector2.zero;
                handleRt.anchorMax = Vector2.one;
                handleRt.offsetMin = Vector2.zero;
                handleRt.offsetMax = Vector2.zero;
                if (tpl != null)
                {
                    var tplHandle = tpl.handleRect;
                    if (tplHandle != null)
                    {
                        CopyRect(tplHandle, handleRt);
                        var tImg = tplHandle.GetComponent<UnityEngine.UI.Image>();
                        if (tImg != null)
                        {
                            handleImg.sprite = tImg.sprite;
                            handleImg.color = tImg.color;
                            handleImg.type = tImg.type;
                        }
                    }
                }
                if (handleImg.sprite == null)
                {
                    var tImg = tpl != null && tpl.targetGraphic != null
                        ? tpl.targetGraphic.TryCast<UnityEngine.UI.Image>() : null;
                    if (tImg != null)
                    {
                        handleImg.sprite = tImg.sprite;
                        handleImg.color = tImg.color;
                        handleImg.type = tImg.type;
                    }
                }
                handleImg.raycastTarget = true;

                if (tpl != null)
                {
                    trackImg.sprite = tpl.GetComponent<UnityEngine.UI.Image>() != null
                        ? tpl.GetComponent<UnityEngine.UI.Image>().sprite : null;
                    trackImg.color = tpl.GetComponent<UnityEngine.UI.Image>() != null
                        ? tpl.GetComponent<UnityEngine.UI.Image>().color : Color.white;
                    trackImg.type = tpl.GetComponent<UnityEngine.UI.Image>() != null
                        ? tpl.GetComponent<UnityEngine.UI.Image>().type : UnityEngine.UI.Image.Type.Sliced;
                }
                // Direction is deliberately NOT copied. The game's node is serialised
                // as TopToBottom, but its ScrollRect has verticalScrollbar == null, so
                // nothing ever drives it and that value is meaningless. ScrollRect
                // writes value = 1 at the top, which only puts the handle at the top
                // when the scrollbar is BottomToTop - copying TopToBottom made the
                // MOD list's bar work upside down.
                sb.direction = UnityEngine.UI.Scrollbar.Direction.BottomToTop;
                trackImg.raycastTarget = true;

                sb.handleRect = handleRt;
                sb.targetGraphic = handleImg;
                sb.numberOfSteps = 0;

                _modScrollbar = sb;
                if (sr != null)
                {
                    sr.verticalScrollbar = sb;
                    sr.verticalScrollbarVisibility = UnityEngine.UI.ScrollRect.ScrollbarVisibility.AutoHide;
                    sr.verticalScrollbarSpacing = 0f;
                }
                Plugin.Logger.LogInfo("[MODPAGE] MOD scrollbar built (template="
                    + (tpl == null ? "<none, plain fallback>" : tpl.gameObject.name)
                    + ", dir=" + sb.direction + ", rect=" + rt.rect.width.ToString("F0") + "x"
                    + rt.rect.height.ToString("F0") + ")");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] scrollbar build failed: " + e.Message); }
        }

        /// <summary>Park / restore the game's own scrollbar so exactly one vertical
        /// scrollbar is on screen. Fully reversible: the ScrollRect reference and the
        /// GameObject's active state are both put back on Hide.</summary>
        private static void SyncGameScrollbar()
        {
            try
            {
                if (_gameScrollRect == null && _gameScrollbar == null) return;
                if (_visible)
                {
                    if (!_gameScrollbarParked)
                    {
                        _gameScrollbarParked = true;
                        if (_gameScrollbar != null)
                        {
                            _gameScrollbarWasActive = _gameScrollbar.gameObject.activeSelf;
                            _gameScrollbar.gameObject.SetActive(false);
                        }
                        // stop the game's ScrollRect from re-showing it while we cover the page
                        if (_gameScrollRect != null) _gameScrollRect.verticalScrollbar = null;
                    }
                    else if (_gameScrollbar != null && _gameScrollbar.gameObject.activeSelf)
                    {
                        _gameScrollbar.gameObject.SetActive(false);   // re-assert, cheap
                    }
                }
                else if (_gameScrollbarParked)
                {
                    _gameScrollbarParked = false;
                    if (_gameScrollRect != null) _gameScrollRect.verticalScrollbar = _gameScrollbar;
                    if (_gameScrollbar != null) _gameScrollbar.gameObject.SetActive(_gameScrollbarWasActive);
                }
            }
            catch { }
        }

        /// <summary>Is 't' the given node or below it? Instance ids, because interop
        /// wrappers do not compare reliably with ==.</summary>
        private static bool IsUnder(Transform t, Transform root)
        {
            try
            {
                if (t == null || root == null) return false;
                int id = root.GetInstanceID();
                int n = 0;
                while (t != null && n < 16)
                {
                    if (t.GetInstanceID() == id) return true;
                    t = t.parent;
                    n++;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// F2 entry point, called from the Harmony prefix on ScrollRect.OnScroll.
        /// Returns true only when the wheel tick belongs to the MOD page; then the
        /// game's handler is skipped and the tab cannot flip. Clicks and drags use
        /// different uGUI event interfaces and are never touched, so clicking a
        /// native tab still switches the page and still closes the MOD page.
        /// </summary>
        internal static bool ConsumeNativeWheel(UnityEngine.UI.ScrollRect sr,
                                               UnityEngine.EventSystems.PointerEventData data)
        {
            try
            {
                if (!_visible || _contentRt == null || data == null) return false;
                // our own sink already handled it - never block that one
                if (sr != null && _scrollRect != null
                    && sr.GetInstanceID() == _scrollRect.GetInstanceID()) return false;

                UnityEngine.GameObject hit = null;
                try { hit = data.pointerCurrentRaycast.gameObject; } catch { }
                if (hit == null) return false;
                Transform ht = null;
                try { ht = hit.transform; } catch { }
                // our page covers the whole viewport while it is shown, so a tick that
                // lands on either belongs to us
                if (!IsUnder(ht, _pageRt) && !IsUnder(ht, _viewportRt)) return false;
                Plugin.Logger.LogInfo("[MODPAGE] wheel blocked (" + (ht == null ? "?" : ht.name)
                                      + ") dy=" + data.scrollDelta.y.ToString("F0"));
                return true;
            }
            catch { }
            return false;
        }

        /// <summary>Called from SettingsEntryHook.Maintain at 0.25 s intervals.</summary>
        internal static void Tick()
        {
            try
            {
                if (_page == null) { _visible = false; return; }

                if (_visible)
                {
                    // Native tab click -> VM.ActivePage moved away -> close MOD page.
                    string ap = SafeActivePage();
                    if (_openOnActivePage != "" && ap != "" && ap != _openOnActivePage)
                    {
                        Plugin.Logger.LogInfo("[MODPAGE] native page switch detected ('" + ap
                                              + "' != '" + _openOnActivePage + "'), closing");
                        Hide();
                        return;
                    }
                    if (!_page.activeSelf) _page.SetActive(true);   // UPF binding re-assert
                    Relayout();   // template rects may have baked after Build
                    Diag();       // 1 Hz render-state dump while visible
                    string title = ModeTitle(_mode);
                    if (_headerLabel != null && _headerLabel.text != title)
                        _headerLabel.text = title;
                    if (Time.unscaledTime >= _refreshAt)
                    {
                        _refreshAt = Time.unscaledTime + 1f;
                        FeaturePluginRegistry.Refresh();   // 1 Hz: cheap now (type cached, see registry)
                        // 只有功能页才补行：包管理页里不该出现功能插件行（v14）。
                        if (_mode == PageMode.Features) AddMissingFeatureRows();
                        RefreshRows();
                    }
                }
                else if (_page.activeSelf)
                {
                    _page.SetActive(false);
                }
            }
            catch { }
        }

        /// <summary>UPF bakes real RectTransform values a few seconds after the panel
        /// opens; Build (first click) may have captured raw 100x100 defaults, leaving
        /// our page a tiny box at the anchor point. Re-copy the LIVE template geometry
        /// and re-stack rows. Idempotent and cheap (a dozen rect copies).</summary>
        private static void Relayout()
        {
            try
            {
                if (_pageRt == null || _rowTemplateRt == null) return;

                if (_viewportRt != null && _viewportRt.rect.width > 50f)
                    CopyRect(_viewportRt, _pageRt);
                else if (_contentPageRt != null && _contentPageRt.rect.width > 50f)
                    CopyRect(_contentPageRt, _pageRt);

                float h = _rowTemplateRt.rect.height;
                if (h > 10f) _rowHeight = h;

                // Top-anchored, horizontally-stretched rows: no scroll-space coords.
                // Only VISIBLE rows consume a slot, so collapsing a package pulls
                // the rows below it up. Unit rows are indented under their package.
                float top = 52f;
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (row == null || row.RootRt == null) continue;
                    if (!row.RootRt.gameObject.activeSelf) continue;
                    float rowH = row.Kind == 1 ? _pkgRowH
                               : row.Kind == 3 ? _rowHeight
                               : row.Kind == 4 ? FeatureRowHeight
                               : _rowHeight;
                    float indent = row.Kind == 2 ? 40f : 0f;
                    var rt = row.RootRt;
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(1f, 1f);
                    rt.pivot = new Vector2(0.5f, 1f);
                    rt.sizeDelta = new Vector2(-16f - indent, rowH);
                    rt.anchoredPosition = new Vector2(indent * 0.5f, -top);
                    top += rowH + 2f;
                }

                if (_headerRt != null)
                {
                    _headerRt.anchorMin = new Vector2(0f, 1f);
                    _headerRt.anchorMax = new Vector2(1f, 1f);
                    _headerRt.pivot = new Vector2(0.5f, 1f);
                    _headerRt.sizeDelta = new Vector2(-16f, 44f);
                    _headerRt.anchoredPosition = new Vector2(0f, -4f);
                }

                // F3: 'top' is the stacked height of the visible rows; it resizes the
                // scroll content so a collapse/expand (which re-runs Relayout) can
                // never leave the page scrolled past the new end. The row maths above
                // is untouched - this only reads its terminal value.
                _contentHeight = top;
                SyncContentSize();
                SyncGameScrollbar();
                // NOTE: switch/knob geometry is intentionally NOT re-copied here.
                // Relayout ran every 0.25 s and reset the knob to the template
                // position while Paint moved it to the state side at 1 Hz - the
                // fight showed up as the knob jumping. Build-time copy only; the
                // knob position is owned exclusively by Paint.
            }
            catch { }
        }

        private static float _nextDiag;

        /// <summary>1 Hz while visible: dump the render state (geometry, world
        /// position, actives, image color/sprite, TMP font/color) so an invisible
        /// page can be told apart: off-screen vs transparent vs missing font.</summary>
        private static void Diag()
        {
            try
            {
                if (Time.unscaledTime < _nextDiag) return;
                _nextDiag = Time.unscaledTime + 1f;
                if (_pageRt == null) return;
                var sb = new StringBuilder();
                sb.Append("[MODPAGE] diag");
                DumpRt(_pageRt, " page", sb);
                if (_rowRoots.Count > 0) DumpRt(_rowRoots[0], " row0", sb);
                try
                {
                    if (_rowRoots.Count > 0)
                    {
                        var img = _rowRoots[0].GetComponent<UnityEngine.UI.Image>();
                        if (img != null)
                            sb.Append(" row0img[a=").Append(img.color.a.ToString("F2"))
                              .Append(" sprite=").Append(img.sprite == null ? "null" : "set")
                              .Append(" enabled=").Append(img.enabled).Append("]");
                    }
                }
                catch { }
                try
                {
                    if (_headerLabel != null)
                        sb.Append(" hdr[a=").Append(_headerLabel.color.a.ToString("F2"))
                          .Append(" font=").Append(_headerLabel.font == null ? "null" : "set")
                          .Append(" text='").Append(_headerLabel.text).Append("']");
                }
                catch { }
                try
                {
                    var p = _pageRt.parent;
                    int steps = 0;
                    while (p != null && steps < 6)
                    {
                        sb.Append(" < ").Append(p.name)
                          .Append(p.gameObject.activeSelf ? "(on)" : "(OFF)");
                        p = p.parent; steps++;
                    }
                }
                catch { }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static void DumpRt(RectTransform rt, string tag, StringBuilder sb)
        {
            try
            {
                sb.Append(tag).Append("[ap=").Append(rt.anchoredPosition.ToString("F0"))
                  .Append(" size=").Append(rt.rect.width.ToString("F0")).Append("x")
                  .Append(rt.rect.height.ToString("F0"))
                  .Append(" world=").Append(((Vector2)rt.transform.position).ToString("F0"))
                  .Append(" active=").Append(rt.gameObject.activeInHierarchy).Append("]");
            }
            catch { }
        }

        private static string SafeActivePage()
        {
            try { return _vm != null ? (_vm.ActivePage ?? "") : ""; }
            catch { return ""; }
        }

        private static void TrySetActive(Game.UI.UPFLogic.Settings.SettingsPanelViewModel vm,
                                         string prop, bool active)
        {
            try
            {
                if (vm == null) return;
                RectTransform rt = null;
                switch (prop)
                {
                    case "AudioPage": rt = vm.AudioPage; break;
                    case "DisplayPage": rt = vm.DisplayPage; break;
                    case "ControlPage": rt = vm.ControlPage; break;
                    case "SystemPage": rt = vm.SystemPage; break;
                }
                if (rt != null && rt.gameObject != null && rt.gameObject.activeSelf != active)
                    rt.gameObject.SetActive(active);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] set " + prop + ": " + e.Message); }
        }

        /// <summary>One-shot at Build: dump world rects of all VM pages, the scroll
        /// viewport and the content container, plus the canvas render mode - so the
        /// actually-visible window can be located instead of guessed.</summary>
        private static void GeoProbe(Game.UI.UPFLogic.Settings.SettingsPanel panel,
                                     Transform pageSystem)
        {
            try
            {
                var sb = new StringBuilder("[MODPAGE] geo");
                DumpW(_vm != null ? _vm.AudioPage : null, "audio", sb);
                DumpW(_vm != null ? _vm.DisplayPage : null, "display", sb);
                DumpW(_vm != null ? _vm.ControlPage : null, "control", sb);
                DumpW(_vm != null ? _vm.SystemPage : null, "system", sb);
                var vp = pageSystem;
                int steps = 0;
                while (vp != null && steps < 8)
                {
                    if (vp.name != null && vp.name.StartsWith("Viewport")) break;
                    vp = vp.parent; steps++;
                }
                DumpW(AsRt(vp), "viewport", sb);
                DumpW(AsRt(pageSystem.parent), "content", sb);
                try
                {
                    var canvas = panel.GetComponentInParent<UnityEngine.Canvas>(true);
                    if (canvas != null)
                        sb.Append(" canvas[mode=").Append(canvas.renderMode.ToString())
                          .Append(" sort=").Append(canvas.sortingOrder).Append("]");
                }
                catch { }
                Plugin.Logger.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static void DumpW(RectTransform rt, string tag, StringBuilder sb)
        {
            try
            {
                if (rt == null) { sb.Append(' ').Append(tag).Append("=<null>"); return; }
                var r = rt.rect;
                Vector2 c = rt.TransformPoint(new Vector3(r.x + r.width * 0.5f,
                                                          r.y + r.height * 0.5f, 0f));
                sb.Append(' ').Append(tag).Append("[center=").Append(c.ToString("F0"))
                  .Append(" size=").Append(r.width.ToString("F0")).Append("x")
                  .Append(r.height.ToString("F0"))
                  .Append(" active=").Append(rt.gameObject.activeInHierarchy).Append("]");
            }
            catch { }
        }

        private static bool Build(Game.UI.UPFLogic.Settings.SettingsPanel panel)
        {
            if (_page != null) return true;

            var root = panel.transform;
            _vm = panel._viewModel;

            // The DFS name search lands on a recycled/off-screen copy parked
            // outside the viewport (diag: world (2120,-798)). The VM exposes the
            // LIVE page RectTransforms - use SystemPage as the ground truth and
            // only fall back to the name search if the VM route fails.
            Transform pageSystem = null;
            try
            {
                if (_vm != null && _vm.SystemPage != null)
                    pageSystem = _vm.SystemPage;
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] vm.SystemPage: " + e.Message); }
            string pageSrc = "vm.SystemPage";
            if (pageSystem == null)
            {
                pageSystem = FindDeep(root, "Frame#pageSystem");
                if (pageSystem == null) pageSystem = FindDeep(root, "pageSystem");   // name variants
                pageSrc = "name-search";
            }
            if (pageSystem == null) { Plugin.Logger.LogWarning("[MODPAGE] pageSystem not found"); return false; }
            _contentPageRt = AsRt(pageSystem);
            var content = pageSystem.parent;
            if (content == null) { Plugin.Logger.LogWarning("[MODPAGE] pageSystem has no parent"); return false; }
            Plugin.Logger.LogInfo("[MODPAGE] page source: " + pageSrc + " ('" + pageSystem.name
                                  + "' parent='" + content.name + "')");
            GeoProbe(panel, pageSystem);

            // ---- capture baked templates (Build runs on first click, so the panel
            // has real rects by then; we copy geometry + sprite/color only) ----
            // The recon names are direct children of pageSystem (verified by the
            // children dump). A deep recursive search can be aborted wholesale by
            // one misbehaving UPF node (its .name getter throws and the outer
            // try/catch turns it into "not found"), so match direct children
            // first - each child is probed independently and a bad one is skipped.
            var rowTemplate = AsRt(FindChildSafe(pageSystem,
                n => n == "Frame#confirmMapTravelTimeCard"));
            if (rowTemplate == null)
                rowTemplate = AsRt(FindChildSafe(pageSystem,
                    n => n.Contains("confirmMapTravelTimeCard")));
            if (rowTemplate == null)
                rowTemplate = AsRt(FindDeepTolerant(pageSystem,
                    n => n.StartsWith("Frame#") && n.EndsWith("Card")));
            if (rowTemplate == null)
                rowTemplate = AsRt(FindDeepTolerant(pageSystem,
                    n => n.StartsWith("Frame#sw") && !n.Contains("Knob")));
            if (rowTemplate == null)
            {
                // Rich diagnostic: prove the lookup context - child count, each
                // child's exact name, its length, and the equality verdict
                // computed right here, ruling out stale-DLL and hidden chars.
                var dbg = new StringBuilder();
                int cn = -1;
                try { cn = pageSystem.childCount; } catch (Exception e) { dbg.Append("count ex:" + e.GetType().Name + "; "); }
                dbg.Append("count=").Append(cn).Append("; ");
                for (int i = 0; i < cn && i < 15; i++)
                {
                    string nm = "<err>";
                    try { var c = pageSystem.GetChild(i); nm = c.name ?? "<null-name>"; }
                    catch (Exception e) { nm = "<ex:" + e.GetType().Name + ">"; }
                    dbg.Append('[').Append(i).Append("]'").Append(nm).Append("' len=")
                       .Append(nm.Length).Append(" eq=")
                       .Append(nm == "Frame#confirmMapTravelTimeCard").Append("; ");
                }
                Plugin.Logger.LogWarning("[MODPAGE] row template not found; " + dbg);
                return false;
            }
            _rowTemplateRt = rowTemplate;
            var rowBgT = FindChild(rowTemplate, "__BackgroundImage");
            _rowBgTemplate = rowBgT != null ? rowBgT.GetComponent<UnityEngine.UI.Image>() : null;
            _rowLabelStyle = rowTemplate.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);

            var swT = AsRt(FindChildSafe(rowTemplate,
                n => n.StartsWith("Frame#sw") && !n.Contains("Knob")));
            if (swT == null)
                swT = AsRt(FindDeepTolerant(rowTemplate,
                    n => n.StartsWith("Frame#sw") && !n.Contains("Knob")));
            if (swT == null)
            {
                Plugin.Logger.LogWarning("[MODPAGE] switch template not found in row '"
                                         + rowTemplate.name + "'");
                return false;
            }
            _swTemplateRt = swT;
            _swHitTemplate = swT.GetComponent<UnityEngine.UI.Image>();
            var swBgT = FindChild(swT, "__BackgroundImage");
            _swBgTemplate = swBgT != null ? swBgT.GetComponent<UnityEngine.UI.Image>() : null;

            var knobT = AsRt(FindChildSafe(swT, n => n.Contains("Knob")));
            if (knobT == null)
                knobT = AsRt(FindDeepTolerant(swT, n => n.Contains("Knob")));
            if (knobT == null)
            {
                Plugin.Logger.LogWarning("[MODPAGE] knob template not found in row '"
                                         + rowTemplate.name + "'");
                return false;
            }
            _knobTemplateRt = knobT;
            _knobImageTemplate = knobT.GetComponentInChildren<UnityEngine.UI.Image>(true);
            Plugin.Logger.LogInfo("[MODPAGE] templates captured: row='" + rowTemplate.name
                                  + "' sw='" + swT.name + "' knob='" + knobT.name + "'");

            var headerTemplate = AsRt(FindChild(pageSystem, "Frame#0"));
            _headerTemplateRt = headerTemplate;
            _headerLabelStyle = headerTemplate != null
                ? headerTemplate.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null;

            // ---- page root: parent under the scroll Viewport so the page sits in
            // the VISIBLE window. Native pages live in scroll-space (canvas y
            // 900..3000, geo probe) and only enter view via scrolling; our earlier
            // copies of their parked rects put the MOD page far off-screen.
            // An opaque background covers the native pages without touching their
            // active state - SetActive(false) reverted VM.ActivePage, which
            // auto-closed the MOD page and left the content area black.
            Transform vp = pageSystem; int vpSteps = 0;
            while (vp != null && vpSteps < 8)
            {
                if (vp.name != null && vp.name.StartsWith("Viewport")) break;
                vp = vp.parent; vpSteps++;
            }
            _viewportRt = null;
            if (vp != null && vp.name != null && vp.name.StartsWith("Viewport"))
            {
                _viewportRt = AsRt(vp);
                content = vp;
            }
            var go = new GameObject(PageName);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(content, false);
            _pageRt = (RectTransform)go.transform;
            if (_viewportRt != null) CopyRect(_viewportRt, _pageRt);
            else CopyRect(pageSystem.GetComponent<RectTransform>(), _pageRt);
            _page = go;

            // F1: the page root is the wheel sink - a native uGUI ScrollRect here is
            // where uGUI's ExecuteEvents walk stops, so the wheel never reaches the
            // game's settings ScrollRect on Frame#pageShell. See the scroll-support
            // block in this class for why a native component is used.
            var scrollRt = go.AddComponent<UnityEngine.UI.ScrollRect>();
            _scrollRect = scrollRt;

            // Opaque backdrop over the native pages; also blocks raycasts to them.
            var bgGo = new GameObject("Bg");
            bgGo.AddComponent<RectTransform>();
            bgGo.AddComponent<UnityEngine.UI.Image>();
            bgGo.transform.SetParent(_pageRt, false);
            var bgRt = (RectTransform)bgGo.transform;
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
            var bgImg = bgGo.GetComponent<UnityEngine.UI.Image>();
            bgImg.color = new Color(0.10f, 0.09f, 0.08f, 1f);
            bgImg.raycastTarget = true;
            bgImg.type = UnityEngine.UI.Image.Type.Sliced;

            // ---- scroll content container (F1) -------------------------------
            // Rows are re-parented here and this is the ONLY node the wheel moves.
            // Its top edge is pinned to the page's top edge - which is what
            // Relayout's row maths already assumes (first row at y = -52), so that
            // code is not touched at all. The game's Viewport above us carries a
            // RectMask2D (R2 probe), so rows past the page bottom are clipped by it
            // without us adding a mask.
            var contentGo = new GameObject("Content");
            contentGo.AddComponent<RectTransform>();
            contentGo.transform.SetParent(_pageRt, false);
            var contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = new Vector2(0f, 512f);   // SyncContentSize() owns the height
            contentRt.anchoredPosition = Vector2.zero;
            _contentRt = contentRt;

            try
            {
                scrollRt.content = contentRt;
                scrollRt.horizontal = false;
                scrollRt.vertical = true;
                scrollRt.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
                scrollRt.inertia = false;
                scrollRt.scrollSensitivity = 0.6f;    // refreshed per row height in Relayout
                Plugin.Logger.LogInfo("[MODPAGE] scroll sink installed on page root (native ScrollRect)");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[MODPAGE] scroll sink setup failed: " + e.Message);
            }

            // Opaque strip exactly as tall as the header band: the header itself has
            // no background, so without this the rows sliding up under it would
            // collide with the title. Sits above Content, below the header, and does
            // not eat raycasts (the wheel must still reach Bg / the rows).
            var hbgGo = new GameObject("HeaderBg");
            hbgGo.AddComponent<RectTransform>();
            hbgGo.AddComponent<UnityEngine.UI.Image>();
            hbgGo.transform.SetParent(_pageRt, false);
            var hbgRt = (RectTransform)hbgGo.transform;
            hbgRt.anchorMin = new Vector2(0f, 1f);
            hbgRt.anchorMax = new Vector2(1f, 1f);
            hbgRt.pivot = new Vector2(0.5f, 1f);
            hbgRt.sizeDelta = new Vector2(0f, 52f);
            hbgRt.anchoredPosition = Vector2.zero;
            var hbgImg = hbgGo.GetComponent<UnityEngine.UI.Image>();
            hbgImg.color = new Color(0.10f, 0.09f, 0.08f, 1f);
            hbgImg.raycastTarget = false;

            // ---- header: copied rect + separator image + native-style title ----
            var headerGo = new GameObject("Frame#0");
            headerGo.AddComponent<RectTransform>();
            headerGo.transform.SetParent(_pageRt, false);
            var headerRt = (RectTransform)headerGo.transform;
            _headerRt = headerRt;
            if (_headerTemplateRt != null)
            {
                // Explicit top-bar layout - the template rect lives in scroll-space
                // and cannot be copied (that parked the header off-screen too).
                headerRt.anchorMin = new Vector2(0f, 1f);
                headerRt.anchorMax = new Vector2(1f, 1f);
                headerRt.pivot = new Vector2(0.5f, 1f);
                headerRt.sizeDelta = new Vector2(-16f, 44f);
                headerRt.anchoredPosition = new Vector2(0f, -4f);
                var sepT = FindChild(_headerTemplateRt, "Frame#1");
                if (sepT != null)
                {
                    var sepGo = new GameObject("Frame#1");
                    sepGo.AddComponent<RectTransform>();
                    sepGo.AddComponent<UnityEngine.UI.Image>();
                    sepGo.transform.SetParent(headerRt, false);
                    var sepRt = (RectTransform)sepGo.transform;
                    sepRt.anchorMin = new Vector2(0f, 0f);
                    sepRt.anchorMax = new Vector2(1f, 0f);
                    sepRt.pivot = new Vector2(0.5f, 0f);
                    sepRt.sizeDelta = new Vector2(-16f, 2f);
                    sepRt.anchoredPosition = Vector2.zero;
                    var srcSep = sepT.GetComponentInChildren<UnityEngine.UI.Image>(true);
                    var sepImg = sepGo.GetComponent<UnityEngine.UI.Image>();
                    if (srcSep != null)
                    {
                        sepImg.sprite = srcSep.sprite;
                        sepImg.color = srcSep.color;
                        sepImg.type = srcSep.type;
                    }
                    sepImg.raycastTarget = false;
                }
            }
            _headerLabel = MakeLabel(headerRt, "Text", HeaderText, _headerLabelStyle);

            // ---- rows: stacked vertically from the template row's slot ----
            _rowHeight = rowTemplate.rect.height > 10f ? rowTemplate.rect.height : 60f;
            _nextRowY = rowTemplate.anchoredPosition.y;
            FeaturePluginRegistry.Refresh();

            if (ManagerBehaviour.Instance == null)
                Plugin.Logger.LogWarning("[MODPAGE] ManagerBehaviour not up yet");

            // v14: 行不再一次建死。SetMode 按页模式重建（包/单元 vs 功能插件），
            // 两页共用下面的模板几何、滚动条与行样式。
            SetMode(PageMode.Packages);

            // The scrollbar is built LAST on purpose: it must be the topmost child of
            // the page, otherwise the opaque Bg / HeaderBg (both created earlier) draw
            // over its top band - which cut the handle in half and hid the up arrow.
            BuildModScrollbar(scrollRt, _pageRt, panel);

            go.SetActive(false);
            SyncExpand();
            Plugin.Logger.LogInfo("[MODPAGE] page built under '" + content.name
                                  + "' (rows=" + _rows.Count + ", headerLabel=" + (_headerLabel != null)
                                  + ", rowHeight=" + _rowHeight.ToString("F0") + ")");
            return true;
        }

        /// <summary>
        /// 按页模式重建行：清掉上一页的行，再按 mode 造一批。两页共用同一套模板几何
        /// （_rowTemplateRt / _swTemplateRt / …）、同一个滚动条与同样的开关绘制，所以
        /// 两个标签页的外观与行为一致；切页只是换一批行。
        /// </summary>
        private static void SetMode(PageMode mode)
        {
            try
            {
                if (_contentRt == null) return;
                _mode = mode;
                FeaturePluginRegistry.Refresh();
                ClearRows();
                if (_rowTemplateRt != null) _nextRowY = _rowTemplateRt.anchoredPosition.y;
                if (mode == PageMode.Features)
                {
                    AddMissingFeatureRows();
                    if (!_featurePageBuiltLogged)
                    {
                        _featurePageBuiltLogged = true;
                        Plugin.Logger.LogInfo("[FEATURES] page built (rows=" + _rows.Count + ", page=features)");
                    }
                }
                else BuildPackageRows();
                if (_headerLabel != null) _headerLabel.text = ModeTitle(mode);
                SyncExpand();
                Relayout();
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] set mode failed: " + e.Message); }
        }

        /// <summary>清空当前页的行节点。先 SetActive(false) 再 Destroy：Destroy 到帧末
        /// 才生效，不隐藏的话旧行会在这一帧和新行叠在一起闪一下。</summary>
        private static void ClearRows()
        {
            for (int i = 0; i < _rowRoots.Count; i++)
            {
                try
                {
                    var rt = _rowRoots[i];
                    if (rt == null) continue;
                    rt.gameObject.SetActive(false);
                    UnityEngine.Object.Destroy(rt.gameObject);
                }
                catch { }
            }
            _rowRoots.Clear();
            _rows.Clear();
        }

        /// <summary>包/单元行（Kind 1/2）：MOD 管理页的内容，v14 起不再含功能插件分区。</summary>
        private static void BuildPackageRows()
        {
            var mb = ManagerBehaviour.Instance;
            if (mb == null) return;   // "ManagerBehaviour not up yet" 已在 Build 里报过
            foreach (var p in mb.CurrentSnapshot())
            {
                if (p == null) continue;
                AddTitleRow(p.ModId, p.Title, p.RootDir, p.Author, p.Version);
                int n = p.Units == null ? 0 : p.Units.Count;
                for (int u = 0; u < n; u++)
                {
                    var un = p.Units[u];
                    if (un == null) continue;
                    int npc = ModRegistry.UnitEntryNpcId(un);
                    AddUnitRow(p.ModId, un.UnitId, npc, un.Type.ToString());
                }
            }
        }

        /// <summary>切页日志用的页名（[MODPAGE] 前缀不变，只多一个 page= 字段）。</summary>
        private static string ModeName(PageMode mode)
        {
            return mode == PageMode.Features ? "features" : "packages";
        }

        /// <summary>页面标题：两个标签页各用各的（沿用原生 header 机制）。</summary>
        private static string ModeTitle(PageMode mode)
        {
            return mode == PageMode.Features ? FeatureHeaderText : HeaderText;
        }

        /// <summary>Fresh uGUI row root (bg sprite copied), stacked below the previous row.</summary>
        private static RectTransform NewRowRoot(string name, float height)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.AddComponent<UnityEngine.UI.Image>();
            go.transform.SetParent(_contentRt != null ? (Transform)_contentRt : (Transform)_pageRt, false);
            var rt = (RectTransform)go.transform;
            CopyRect(_rowTemplateRt, rt);
            rt.anchoredPosition = new Vector2(_rowTemplateRt.anchoredPosition.x, _nextRowY);
            _nextRowY -= height + 2f;
            _rowRoots.Add(rt);
            var img = go.GetComponent<UnityEngine.UI.Image>();
            if (_rowBgTemplate != null)
            {
                img.sprite = _rowBgTemplate.sprite;
                img.color = _rowBgTemplate.color;
                img.type = _rowBgTemplate.type;
            }
            img.raycastTarget = false;
            return rt;
        }

        /// <summary>Package header row: taller, with an expand arrow, a one-line
        /// description and the master switch. Clicking the row (outside the
        /// switch) expands/collapses the unit rows below it.</summary>
        private static void AddTitleRow(string modId, string title, string rootDir,
                                        string author, string version)
        {
            _pkgRowH = _rowHeight + 36f;
            var rt = NewRowRoot("ModPkgRow", _pkgRowH);
            string desc = ReadDescription(rootDir);
            if (string.IsNullOrEmpty(desc))
                desc = "作者：" + (author ?? "-") + "    版本：" + (version ?? "-");

            var row = new Row
            {
                Kind = 1, ModId = modId, UnitId = "", Npc = 0, IsPackage = true,
                Label = null, LabelText = "", RootRt = rt
            };
            row.Arrow = MakeLabelAt(rt, "Arrow", "▼", _rowLabelStyle,
                                    new Vector2(8f, _pkgRowH - 34f), new Vector2(-2000f, -8f), 0.9f);
            var titleL = MakeLabelAt(rt, "Title", modId + "  ·  " + title, _rowLabelStyle,
                                     new Vector2(44f, _pkgRowH * 0.5f - 4f), new Vector2(-140f, -6f), 1f);
            var descL = MakeLabelAt(rt, "Desc", desc, _rowLabelStyle,
                                    new Vector2(44f, 6f), new Vector2(-16f, -_pkgRowH * 0.5f + 4f), 0.72f, 0.6f);
            try { titleL.alignment = TMPro.TextAlignmentOptions.Left; } catch { }
            try { descL.alignment = TMPro.TextAlignmentOptions.Left; } catch { }

            // Whole-row click target (the switch sits on top and keeps its own
            // button, so clicks on it never reach this one).
            var rowImg = rt.GetComponent<UnityEngine.UI.Image>();
            rowImg.raycastTarget = true;
            var rowBtn = rt.gameObject.AddComponent<UnityEngine.UI.Button>();
            rowBtn.targetGraphic = rowImg;
            try
            {
                rowBtn.transition = UnityEngine.UI.Selectable.Transition.None;
                var nav = rowBtn.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                rowBtn.navigation = nav;
            }
            catch { }
            string id = modId;
            rowBtn.onClick.RemoveAllListeners();
            rowBtn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => TogglePackage(id)));

            AddSwitch(rt, row);
        }

        /// <summary>The game's ModManifest schema has no description field, so we
        /// read a free-form "description" key straight from mod.json (the game's
        /// reader ignores unknown fields).</summary>
        private static string ReadDescription(string rootDir)
        {
            try
            {
                if (string.IsNullOrEmpty(rootDir)) return "";
                var path = System.IO.Path.Combine(rootDir, "mod.json");
                if (!System.IO.File.Exists(path)) return "";
                var json = System.IO.File.ReadAllText(path);
                var m = System.Text.RegularExpressions.Regex.Match(
                    json, "\"description\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
            return "";
        }

        private static bool IsExpanded(string modId)
        {
            bool v;
            if (_expanded.TryGetValue(modId, out v)) return v;
            return true;   // default: expanded
        }

        private static void TogglePackage(string modId)
        {
            try
            {
                _expanded[modId] = !IsExpanded(modId);
                SyncExpand();
                Relayout();
                Plugin.Logger.LogInfo("[MODPAGE] package '" + modId + "' "
                                      + (_expanded[modId] ? "expanded" : "collapsed"));
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] expand failed: " + e.Message); }
        }

        /// <summary>Apply expand/collapse state: unit row visibility + arrows.</summary>
        private static void SyncExpand()
        {
            try
            {
                for (int i = 0; i < _rows.Count; i++)
                {
                    var row = _rows[i];
                    if (row == null) continue;
                    if (row.Kind == 2 && row.RootRt != null)
                        row.RootRt.gameObject.SetActive(IsExpanded(row.PkgOf ?? ""));
                    else if (row.Kind == 1 && row.Arrow != null)
                    {
                        string t = IsExpanded(row.ModId) ? "▼" : "▶";
                        if (row.Arrow.text != t) row.Arrow.text = t;
                    }
                }
            }
            catch { }
        }

        /// <summary>Label with an explicit rect (offsets against full-stretch
        /// anchors), used where the shared MakeLabel's fixed insets don't fit.</summary>
        private static TMPro.TextMeshProUGUI MakeLabelAt(Transform parent, string name,
            string text, TMPro.TextMeshProUGUI style, Vector2 offMin, Vector2 offMax,
            float fontScale = 1f, float alpha = 1f)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.AddComponent<TMPro.TextMeshProUGUI>();
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            var label = go.GetComponent<TMPro.TextMeshProUGUI>();
            label.text = text;
            label.raycastTarget = false;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;
            if (style != null)
            {
                try
                {
                    label.font = style.font;
                    if (style.fontSize > 0) label.fontSize = style.fontSize * fontScale;
                    var sc = style.color;
                    label.color = new Color(sc.r, sc.g, sc.b, sc.a * alpha);
                    label.alignment = style.alignment;
                    label.fontStyle = style.fontStyle;
                }
                catch { }
            }
            return label;
        }

        // ---- v14: BepInEx 功能插件页（契约见 abstractions/IManagedFeaturePlugin.cs）----
        // 这些行自成一个标签页（PageMode.Features），MOD 管理页里不再出现。

        /// <summary>Feature rows carry two lines of text (name+state / detail), so they
        /// need more height than a unit row. Mirrors the template-derived _rowHeight.</summary>
        private static float FeatureRowHeight { get { return Mathf.Max(_rowHeight * 1.6f, 96f); } }

        /// <summary>Appends a row for every discovered managed feature that has no row
        /// yet. Only adds: 功能页重建后、或页面显示期间新加载的插件都能补齐，
        /// 已有行的状态由 1 Hz 的 RefreshRows 同步。</summary>
        private static void AddMissingFeatureRows()
        {
            bool added = false;
            foreach (var item in FeaturePluginRegistry.Current)
            {
                if (item == null || item.Feature == null) continue;
                bool exists = false;
                foreach (var existingRow in _rows)
                    if (existingRow != null && existingRow.Kind == 4 && existingRow.FeatureId == item.Feature.FeatureId)
                    { exists = true; break; }
                if (exists) continue;
                var rt = NewRowRoot("FeaturePluginRow", FeatureRowHeight);
                string text = FeatureLabel(item.Feature);
                var label = MakeLabel(rt, "Text", text, _rowLabelStyle, 140f);
                if (label != null) label.enableWordWrapping = true;
                var row = new Row { Kind = 4, FeatureId = item.Feature.FeatureId, Label = label, LabelText = text, On = item.Feature.DesiredEnabled, RootRt = rt };
                AddSwitch(rt, row);
                added = true;
            }
            if (added) Relayout();
        }

        /// <summary>Two-line label: "Name  vX  [State]" + StatusMessage (or Description).</summary>
        private static string FeatureLabel(IManagedFeaturePlugin feature)
        {
            string description = feature.Description ?? "";
            string status = feature.StatusMessage ?? "";
            string detail = status.Length > 0 ? status : description;
            if (detail.Length > 100) detail = detail.Substring(0, 97) + "...";
            return (feature.DisplayName ?? feature.FeatureId) + "  v" + (feature.FeatureVersion ?? "?")
                + "  [" + feature.State + "]\n" + detail;
        }

        private static void AddUnitRow(string modId, string unitId, int npc, string typeName)
        {
            var rowRt = NewRowRoot("ModUnitRow", _rowHeight);
            string labelText = unitId + "  [" + typeName + "]";
            // right inset keeps the text clear of the switch on the right edge
            var label = MakeLabel(rowRt, "Text", labelText, _rowLabelStyle, 140f);
            var row = new Row
            {
                Kind = 2, PkgOf = modId,
                ModId = modId, UnitId = unitId, Npc = npc,
                Label = label, LabelText = labelText, RootRt = rowRt
            };
            AddSwitch(rowRt, row);
        }

        /// <summary>Switch (hit image + bg + knob + button) with the template's
        /// visuals, wired to OnRowClicked(row). Fresh uGUI only - nothing UPF on
        /// it, so nothing repositions it or forwards clicks elsewhere.</summary>
        private static void AddSwitch(RectTransform rowRt, Row row)
        {
            var swGo = new GameObject("swMod");
            swGo.AddComponent<RectTransform>();
            swGo.AddComponent<UnityEngine.UI.Image>();
            swGo.AddComponent<UnityEngine.UI.Button>();
            swGo.transform.SetParent(rowRt, false);
            var swRt = (RectTransform)swGo.transform;
            CopyRect(_swTemplateRt, swRt);
            var swImg = swGo.GetComponent<UnityEngine.UI.Image>();
            if (_swHitTemplate != null)
            {
                swImg.sprite = _swHitTemplate.sprite;
                swImg.color = _swHitTemplate.color;
                swImg.type = _swHitTemplate.type;
            }
            swImg.raycastTarget = true;

            if (_swBgTemplate != null)
            {
                var bgGo = new GameObject("__BackgroundImage");
                bgGo.AddComponent<RectTransform>();
                bgGo.AddComponent<UnityEngine.UI.Image>();
                bgGo.transform.SetParent(swRt, false);
                CopyRect(_swBgTemplate.GetComponent<RectTransform>(), (RectTransform)bgGo.transform);
                var bgImg = bgGo.GetComponent<UnityEngine.UI.Image>();
                bgImg.sprite = _swBgTemplate.sprite;
                bgImg.color = _swBgTemplate.color;
                bgImg.type = _swBgTemplate.type;
                bgImg.raycastTarget = false;
            }

            RectTransform knobRt = null;
            UnityEngine.UI.Image knobImage = null;
            Color knobOn = Color.white;
            if (_knobTemplateRt != null)
            {
                var knobGo = new GameObject("knob");
                knobGo.AddComponent<RectTransform>();
                knobGo.AddComponent<UnityEngine.UI.Image>();
                knobGo.transform.SetParent(swRt, false);
                knobRt = (RectTransform)knobGo.transform;
                CopyRect(_knobTemplateRt, knobRt);
                knobImage = knobGo.GetComponent<UnityEngine.UI.Image>();
                if (_knobImageTemplate != null)
                {
                    knobImage.sprite = _knobImageTemplate.sprite;
                    knobImage.color = _knobImageTemplate.color;
                    knobImage.type = _knobImageTemplate.type;
                    knobOn = _knobImageTemplate.color;
                }
                knobImage.raycastTarget = false;
            }

            var btn = swGo.GetComponent<UnityEngine.UI.Button>();
            btn.targetGraphic = swImg;
            try
            {
                btn.transition = UnityEngine.UI.Selectable.Transition.None;
                var nav = btn.navigation;
                nav.mode = UnityEngine.UI.Navigation.Mode.None;
                btn.navigation = nav;
            }
            catch { }

            row.SwRt = swRt; row.KnobRt = knobRt;
            row.KnobImage = knobImage; row.KnobOnColor = knobOn;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => OnRowClicked(row)));
            _rows.Add(row);
        }

        private static void OnRowClicked(Row row)
        {
            try
            {
                if (row.Kind == 4)
                {
                    // Feature rows never touch the mod registry: the owning plugin
                    // decides what the toggle means (see IManagedFeaturePlugin).
                    if (!FeaturePluginRegistry.SetEnabled(row.FeatureId, !row.On))
                        Plugin.Logger.LogWarning("[FEATURES] no feature accepted request: " + row.FeatureId);
                    return;
                }
                row.On = !row.On;
                var mb = ManagerBehaviour.Instance;
                if (mb != null)
                {
                    if (row.IsPackage)
                    {
                        // master switch: apply to every unit of the package
                        foreach (var p in mb.CurrentSnapshot())
                        {
                            if (p == null || p.ModId != row.ModId || p.Units == null) continue;
                            foreach (var un in p.Units)
                            {
                                if (un == null) continue;
                                mb.ApplyEntry(row.ModId, un.UnitId,
                                              ModRegistry.UnitEntryNpcId(un), row.On);
                            }
                        }
                    }
                    else mb.ApplyEntry(row.ModId, row.UnitId, row.Npc, row.On);
                }
                Paint(row);
                Plugin.Logger.LogInfo("[MODPAGE] toggled " + row.ModId + "|" + row.UnitId
                                      + (row.IsPackage ? " (package)" : "")
                                      + "|" + row.Npc + " -> " + row.On);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[MODPAGE] toggle failed: " + e); }
        }

        private static bool PackageAllOn(ManagerBehaviour mb, string modId)
        {
            try
            {
                foreach (var p in mb.CurrentSnapshot())
                {
                    if (p == null || p.ModId != modId || p.Units == null) continue;
                    foreach (var un in p.Units)
                        if (!mb.EntryEnabled(modId, un.UnitId,
                                             ModRegistry.UnitEntryNpcId(un)))
                            return false;
                    return true;   // package found
                }
            }
            catch { }
            return false;
        }

        /// <summary>1 Hz while visible: re-read registry state and re-assert every
        /// knob, which also undoes any UPF binding writes on the cloned nodes.</summary>
        private static void RefreshRows()
        {
            var mb = ManagerBehaviour.Instance;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row == null) continue;
                try
                {
                    if (row.Label != null && row.Label.text != row.LabelText)
                        row.Label.text = row.LabelText;
                    if (row.Kind == 4)
                    {
                        // Live state comes from the feature itself, not from the registry.
                        foreach (var item in FeaturePluginRegistry.Current)
                        {
                            if (item.Feature.FeatureId != row.FeatureId) continue;
                            row.On = item.Feature.DesiredEnabled;
                            string label = FeatureLabel(item.Feature);
                            if (row.Label != null && row.Label.text != label) row.Label.text = label;
                            break;
                        }
                    }
                    else if (mb != null)
                        row.On = row.IsPackage
                            ? PackageAllOn(mb, row.ModId)
                            : mb.EntryEnabled(row.ModId, row.UnitId, row.Npc);
                    Paint(row);
                }
                catch { }
            }
        }

        private static void Paint(Row row)
        {
            try
            {
                if (row.KnobImage != null)
                {
                    Color c = row.KnobOnColor;
                    row.KnobImage.color = row.On
                        ? c
                        : new Color(c.r * 0.35f, c.g * 0.35f, c.b * 0.35f, c.a);
                }
                if (row.KnobRt != null && row.SwRt != null)
                {
                    float swW = row.SwRt.rect.width;
                    float kW = row.KnobRt.rect.width;
                    if (swW > kW + 4f)
                    {
                        float x = swW * 0.5f - kW * 0.5f - 2f;
                        if (!row.On) x = -x;
                        // Anchor the knob to the switch centre so the template's
                        // parked position can never show through between ticks.
                        try
                        {
                            row.KnobRt.anchorMin = new Vector2(0.5f, 0.5f);
                            row.KnobRt.anchorMax = new Vector2(0.5f, 0.5f);
                            row.KnobRt.pivot = new Vector2(0.5f, 0.5f);
                        }
                        catch { }
                        var ap = row.KnobRt.anchoredPosition;
                        if (Mathf.Abs(ap.x - x) > 0.5f || Mathf.Abs(ap.y) > 0.5f)
                            row.KnobRt.anchoredPosition = new Vector2(x, 0f);
                    }
                }
            }
            catch { }
        }

        private static Transform FindChild(Transform parent, string name)
        {
            return SettingsEntryHook.FindChild(parent, name);
        }

        private static Transform FindDeep(Transform t, string name)
        {
            return SettingsEntryHook.FindDeep(t, name);
        }

        /// <summary>GetChild() hands back Transform-typed interop wrappers; 'as
        /// RectTransform' then silently yields null even when the native object IS
        /// a RectTransform. TryCast resolves against the native class instead.</summary>
        private static RectTransform AsRt(Transform t)
        {
            if (t == null) return null;
            try { return t.TryCast<RectTransform>(); } catch { return null; }
        }

        /// <summary>Direct-child name match where each child is probed in its own
        /// try/catch - one node with a throwing .name getter cannot abort the rest.</summary>
        private static Transform FindChildSafe(Transform parent, Func<string, bool> match)
        {
            if (parent == null || match == null) return null;
            int n = 0;
            try { n = parent.childCount; } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform ch = null;
                string nm = null;
                try { ch = parent.GetChild(i); } catch { continue; }
                if (ch == null) continue;
                try { nm = ch.name; } catch { continue; }
                if (nm != null && match(nm)) return ch;
            }
            return null;
        }

        /// <summary>Depth-first name match with per-branch fault isolation: a subtree
        /// whose accessors throw is skipped, the search continues in siblings.</summary>
        private static Transform FindDeepTolerant(Transform t, Func<string, bool> match)
        {
            if (t == null || match == null) return null;
            string nm = null;
            try { nm = t.name; } catch { return null; }
            if (nm != null && match(nm)) return t;
            int n = 0;
            try { n = t.childCount; } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                Transform ch = null;
                try { ch = t.GetChild(i); } catch { continue; }
                var hit = FindDeepTolerant(ch, match);
                if (hit != null) return hit;
            }
            return null;
        }

        private static Transform FindDeepContains(Transform t, string sub)
        {
            if (t == null) return null;
            try
            {
                if (t.name != null && t.name.Contains(sub)) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    var hit = FindDeepContains(t.GetChild(i), sub);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static Transform FindDeepSwitch(Transform t)
        {
            if (t == null) return null;
            try
            {
                if (t.name != null && t.name.StartsWith("Frame#sw") && !t.name.Contains("Knob")) return t;
                for (int i = 0; i < t.childCount; i++)
                {
                    var hit = FindDeepSwitch(t.GetChild(i));
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        private static string ChildrenOf(Transform t)
        {
            var sb = new StringBuilder();
            try
            {
                if (t == null) return "<null>";
                for (int i = 0; i < t.childCount && i < 12; i++)
                    sb.Append(t.GetChild(i).name).Append('|');
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>Copies the full RectTransform layout (anchors, pivot, size,
        /// position, rotation, scale) from one transform to another.</summary>
        internal static void CopyRect(RectTransform src, RectTransform dst)
        {
            if (src == null || dst == null) return;
            try
            {
                dst.anchorMin = src.anchorMin;
                dst.anchorMax = src.anchorMax;
                dst.pivot = src.pivot;
                dst.sizeDelta = src.sizeDelta;
                dst.anchoredPosition = src.anchoredPosition;
                dst.localRotation = src.localRotation;
                dst.localScale = src.localScale;
            }
            catch { }
        }

        /// <summary>
        /// Builds a plain uGUI label, copying font / size / color / alignment /
        /// material from a template TMP so CJK glyphs come from the game's own
        /// font asset. Used for every text the plugin adds anywhere.
        /// </summary>
        internal static TMPro.TextMeshProUGUI MakeLabel(Transform parent, string name,
                                                        string text, TMPro.TextMeshProUGUI styleSource,
                                                        float rightInset = 16f)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.AddComponent<TMPro.TextMeshProUGUI>();
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            var label = go.GetComponent<TMPro.TextMeshProUGUI>();
            label.text = text;
            label.raycastTarget = false;
            // Explicit full-width rect - copying the template TMP's rect gives a
            // narrow box sized for the template's own short text, which wraps and
            // spills out of the row ("文字出格").
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(16f, 2f);
            rt.offsetMax = new Vector2(-rightInset, -2f);
            if (styleSource != null)
            {
                try
                {
                    label.font = styleSource.font;
                    if (styleSource.fontSize > 0) label.fontSize = styleSource.fontSize;
                    label.color = styleSource.color;
                    label.alignment = styleSource.alignment;
                    label.fontStyle = styleSource.fontStyle;
                }
                catch { }
            }
            // full-width rect without wrap keeps one line; no enableWordWrap in
            // this interop surface, overflow is clipped by the rect inset instead
            return label;
        }
    }

    /// <summary>
    /// F2: backstop for the wheel path. Our own native ScrollRect on the page root
    /// normally swallows the tick first; this covers the case where the tick is
    /// raycast onto the Viewport rather than onto the page, which would otherwise
    /// let the game's settings ScrollRect move the native pages.
    ///
    /// Only ScrollRect.OnScroll is patched - the wheel entry point. Tab clicks use
    /// Button.onClick and drags use IBeginDrag/IDrag, so the "click a native tab to
    /// close the MOD page" behaviour is untouched by construction.
    /// </summary>
    static class NativeWheelConsumerHook
    {
        static bool Prefix(UnityEngine.UI.ScrollRect __instance, PointerEventData data)
        {
            if (TabDiag.Enabled)
                try { TabDiag.LastWheelFrame = Time.frameCount; TabDiag.LastWheelTime = Time.unscaledTime; } catch { }
            return !ModPageController.ConsumeNativeWheel(__instance, data);
        }
    }

    /// <summary>
    /// Q2 E-V1: video-replacement route probe.
    ///
    /// Verified by disassembly - VideoResourcePlayer.ResolveResourcePath (RVA
    /// 0x1ABBCB0) consults, in order:
    ///   1. Game.Mod.AssetOverlay.TryResolveVideoOverride   @ 0xD4C750
    ///   2. DlcContentManager.ResolveVideoBundle            @ 0x15FEFC0
    ///   3. VideoResourceBuildPaths.GetBuildOutputDirectory @ 0x1ABB810
    ///
    /// So the Mod video override is the FIRST branch, and it is reachable with
    /// public APIs only - no YooAsset ResourcePackage construction and no delegate
    /// marshalling - which is exactly what makes it the route M3 should use.
    /// This probe exercises it and records the before/after resolution.
    /// </summary>
    static class VideoExp
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_EXP_VIDEO") == "1";

        private static bool _done;

        /// <summary>The test package lives in the user's LocalLow folder. Resolve it from the
        /// current profile so the same build works on any account and no account name is
        /// baked into the assembly (v14: the old literal fallback was removed).</summary>
        internal static string DefaultDir
        {
            get
            {
                try
                {
                    string p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrEmpty(p))
                        return Path.Combine(p, @"AppData\LocalLow\Nuverse\WorldApart\Mods\local.expvideo");
                }
                catch { }
                // v14: 兜底不再写死账户名；退到临时目录下的同名结构（只在 MOD_EXP_VIDEO 调试时用到）
                try { return Path.Combine(Path.GetTempPath(), "Nuverse", "WorldApart", "Mods", "local.expvideo"); }
                catch { return "local.expvideo"; }
            }
        }

        /// <summary>MOD_EXP_VIDEO=2：只装 resolver 观察钩子，**不做任何自注册**
        /// （v11 P1 验证游戏自带官方通道用）。
        /// MOD_EXP_CHANNEL=&lt;bundlePath&gt;：用游戏自己的 `VideoBundlePlayer.LoadBundle` 驱动一次
        /// 官方加载链（会经过 `ResolveResourcePath`），20 s 后 `UnloadBundle` 回收；
        /// 全程不碰 `AssetOverlay` 注册表。</summary>
        internal static readonly bool HookOnly =
            Environment.GetEnvironmentVariable("MOD_EXP_VIDEO") == "2";

        private static readonly string Channel =
            Environment.GetEnvironmentVariable("MOD_EXP_CHANNEL");

        private static int _chStep;
        private static float _chAt;
        private static Il2CppSystem.Threading.Tasks.Task<bool> _chTask;

        /// <summary>官方通道驱动（只在 MOD_EXP_VIDEO=2 + MOD_EXP_CHANNEL 设置时工作）。</summary>
        internal static void ChannelTick()
        {
            if (!HookOnly || string.IsNullOrEmpty(Channel)) return;
            try
            {
                if (_chStep == 0) { _chAt = Time.unscaledTime + 20f; _chStep = 1; return; }
                if (Time.unscaledTime < _chAt) return;
                if (_chStep == 1)
                {
                    _chStep = 2; _chAt = Time.unscaledTime + 8f;
                    // 先做只读体检：官方通道到底注册了没有
                    bool before = false;
                    try { string d; before = Game.Mod.AssetOverlay.TryResolveVideoOverride(Channel, out d); }
                    catch (Exception e) { Plugin.Logger.LogWarning("[EXP-CH] TryResolveVideoOverride 异常 " + e.Message); }
                    Plugin.Logger.LogInfo("[EXP-CH] 体检: IsWorldLoaded=" + Game.Mod.ModOverlayCoordinator.IsWorldLoaded
                                          + " HasAnyOverride=" + Game.Mod.AssetOverlay.HasAnyOverride
                                          + " TryResolveVideoOverride('" + Channel + "') BEFORE=" + before);
                    // 逐层体检官方收集链：ActiveUnits(AssetOverride) → 每个 unit 的 RootDir → CollectOverrides
                    try
                    {
                        var reg = Game.Mod.ModRegistry.Instance;
                        var units = reg == null ? null : reg.ActiveUnits(Game.Mod.ModUnitType.AssetOverride);
                        Plugin.Logger.LogInfo("[EXP-CH] ActiveUnits(AssetOverride)=" + (units == null ? -1 : units.Count));
                        // 逐个 unit 体检（不看 ActiveUnits 的过滤，直接看注册表原始状态）
                        try
                        {
                            // 与插件 PackageSnapshot 同款：Reg.Packages 是 Il2Cpp 只读列表代理，
                            // 直接 Count 不可靠，先 TryCast 成 List 再枚举。
                            var ro = reg == null ? null : reg.Packages;
                            var lst = ro == null ? null : ro.TryCast<Il2CppSystem.Collections.Generic.List<Game.Mod.ModPackage>>();
                            Plugin.Logger.LogInfo("[EXP-CH] Packages 代理=" + (ro == null ? "null" : ro.GetType().Name)
                                                  + " TryCastList=" + (lst == null ? "null" : lst.Count.ToString()));
                            if (lst != null)
                                for (int pi = 0; pi < lst.Count; pi++)
                                {
                                    var p = lst[pi];
                                    if (p == null) continue;
                                    var pkUnits = p.Units;
                                    Plugin.Logger.LogInfo("[EXP-CH]   raw pkg=" + p.ModId + " kind=" + p.Kind
                                                          + " units=" + (pkUnits == null ? -1 : pkUnits.Count));
                                    if (pkUnits == null) continue;
                                    for (int ui = 0; ui < pkUnits.Count; ui++)
                                    {
                                        var u = pkUnits[ui];
                                        if (u == null) continue;
                                        bool en = false;
                                        try { en = reg.IsEntryEnabled(p.ModId, u.UnitId, u.TargetNpcId); } catch { }
                                        bool ex = false;
                                        try { ex = System.IO.Directory.Exists(u.RootDir); } catch { }
                                        Plugin.Logger.LogInfo("[EXP-CH]     raw unit unit=" + u.UnitId
                                                              + " type=" + u.Type + " npc=" + u.TargetNpcId
                                                              + " root='" + u.RootDir + "' exists=" + ex
                                                              + " IsEntryEnabled=" + en);
                                    }
                                }
                        }
                        catch (Exception e4) { Plugin.Logger.LogWarning("[EXP-CH] raw unit 体检异常 " + e4.GetType().Name + ": " + e4.Message); }
                        if (units != null)
                            for (int i = 0; i < units.Count; i++)
                            {
                                var u = units[i].Item2;
                                bool ex = false;
                                try { ex = System.IO.Directory.Exists(u.RootDir); } catch { }
                                Plugin.Logger.LogInfo("[EXP-CH]   unit=" + u.UnitId + " type=" + u.Type
                                                      + " root='" + u.RootDir + "' exists=" + ex);
                                try
                                {
                                    var ov = Game.Mod.ModAssetApplier.CollectOverrides(u.RootDir);
                                    Plugin.Logger.LogInfo("[EXP-CH]   CollectOverrides('" + u.RootDir + "') -> "
                                                          + (ov == null ? -1 : ov.Count));
                                    if (ov != null)
                                        for (int k = 0; k < ov.Count && k < 6; k++)
                                            Plugin.Logger.LogInfo("[EXP-CH]     entry key='" + ov[k].Key
                                                                  + "' isVideo=" + ov[k].IsVideoBundle
                                                                  + " hasSeg=" + ov[k].HasSegmentsJson
                                                                  + " path='" + ov[k].AbsolutePath + "'");
                                }
                                catch (Exception e3) { Plugin.Logger.LogWarning("[EXP-CH]   CollectOverrides 异常 " + e3.Message); }
                            }
                    }
                    catch (Exception e2) { Plugin.Logger.LogWarning("[EXP-CH] ActiveUnits 体检异常 " + e2.Message); }
                    if (!before)
                    {
                        // 官方扫描没自动跑（harness 直调载档绕过了游戏的入世界事件）→
                        // 用游戏自己的 Sync() 手动触发一次官方通道（计划书允许"触发 Rescan"）
                        Plugin.Logger.LogInfo("[EXP-CH] 官方扫描未自动发生，调 ModOverlayCoordinator.Sync()（官方触发）");
                        try { Game.Mod.ModOverlayCoordinator.Sync(); }
                        catch (Exception e) { Plugin.Logger.LogWarning("[EXP-CH] Sync 异常 " + e.Message); }
                        bool after = false;
                        try { string d2; after = Game.Mod.AssetOverlay.TryResolveVideoOverride(Channel, out d2); }
                        catch { }
                        Plugin.Logger.LogInfo("[EXP-CH] Sync 之后 TryResolveVideoOverride('" + Channel + "') = " + after
                                              + " HasAnyOverride=" + Game.Mod.AssetOverlay.HasAnyOverride);
                    }
                    return;
                }
                if (_chStep == 2)
                {
                    _chStep = 3; _chAt = Time.unscaledTime + 25f;
                    var p = UnityEngine.Object.FindObjectOfType<Game.VideoResourcePlayer>();
                    if (p == null) { Plugin.Logger.LogWarning("[EXP-CH] 场景里没有 VideoResourcePlayer 实例"); return; }
                    Plugin.Logger.LogInfo("[EXP-CH] 调游戏自己的 VideoBundlePlayer.LoadBundle('" + Channel + "')");
                    var t = p.LoadBundle(Channel);
                    _chTask = t;
                    Plugin.Logger.LogInfo("[EXP-CH] LoadBundle 任务已返回 type=" + (t == null ? "null" : t.GetType().Name)
                                          + "（结果在下一轮体检打印）");
                    return;
                }
                if (_chStep == 3)
                {
                    _chStep = 4;
                    try
                    {
                        if (_chTask == null) Plugin.Logger.LogWarning("[EXP-CH] 任务缺失");
                        else Plugin.Logger.LogInfo("[EXP-CH] LoadBundle 结果: IsCompleted=" + _chTask.IsCompleted
                                                   + " IsFaulted=" + _chTask.IsFaulted
                                                   + " Result=" + (_chTask.IsCompleted && !_chTask.IsFaulted
                                                                   ? _chTask.Result.ToString() : "<pending/faulted>"));
                    }
                    catch (Exception e5) { Plugin.Logger.LogWarning("[EXP-CH] 任务结果读取异常 " + e5.Message); }
                }
                if (_chStep == 4)
                {
                    _chStep = 5;
                    var p = UnityEngine.Object.FindObjectOfType<Game.VideoResourcePlayer>();
                    if (p == null) { Plugin.Logger.LogWarning("[EXP-CH] 卸载时找不到实例"); return; }
                    Plugin.Logger.LogInfo("[EXP-CH] 调 UnloadBundle('" + Channel + "')（可逆性）");
                    p.UnloadBundle(Channel);
                }
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Plugin.Logger.LogWarning("[EXP-CH] 失败 " + inner.GetType().Name + ": " + inner.Message);
                _chStep = 9;
            }
        }

        /// <summary>Called from Plugin.Load when MOD_EXP_VIDEO=1 or =2: hooks the game's own
        /// video-loading resolver so a real playback records which directory it consulted.
        /// Not installed otherwise. =2 never touches the AssetOverlay override registry.</summary>
        internal static void Install()
        {
            try
            {
                var m = AccessTools.Method(typeof(Game.VideoResourcePlayer), "ResolveResourcePath",
                                           new[] { typeof(string) });
                if (m == null) { Plugin.Logger.LogWarning("[EXP] VideoResourcePlayer.ResolveResourcePath not found"); return; }
                var h = new Harmony("local.modmanager.expvideo");
                h.Patch(m, postfix: new HarmonyMethod(AccessTools.Method(typeof(VideoExp), nameof(ResolvePost))));
                Plugin.Logger.LogInfo("[EXP] hooked " + m.DeclaringType.FullName + ".ResolveResourcePath");
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[EXP] ResolveResourcePath hook failed: " + e.Message); }
        }

        private static void ResolvePost(string bundlePath, ref string __result)
        {
            try
            {
                Plugin.Logger.LogInfo("[EXP-RESOLVE] ResolveResourcePath('" + bundlePath + "') -> "
                                      + (__result ?? "<null>"));
            }
            catch { }
        }

        internal static void Run()
        {
            if (!Enabled || _done) return;
            _done = true;
            var L = Plugin.Logger;
            try
            {
                string over = Environment.GetEnvironmentVariable("MOD_EXP_VIDEO_DIR");
                if (string.IsNullOrEmpty(over)) over = DefaultDir;

                foreach (var raw in new[] { "cine/100000:1", "cine\\100000:1", "Cine/100000:1" })
                    L.LogInfo("[EXP] NormalizeBundlePath('" + raw + "') = '"
                              + Game.Mod.AssetOverlay.NormalizeBundlePath(raw) + "'");

                const string loc = "cine/100000:1";
                const string file = "Video_cine_100000_bank_1.mp4";
                foreach (var rel in new[] { "cine/100000/" + file, "100000/" + file, file })
                {
                    var f = Path.Combine(over, rel.Replace('/', Path.DirectorySeparatorChar));
                    var fi = new FileInfo(f);
                    L.LogInfo("[EXP] test asset " + rel + " exists=" + fi.Exists
                              + " size=" + (fi.Exists ? fi.Length.ToString() : "-"));
                }

                string dir;
                bool before = Game.Mod.AssetOverlay.TryResolveVideoOverride(loc, out dir);
                L.LogInfo("[EXP] TryResolveVideoOverride('" + loc + "') BEFORE = " + before
                          + " dir=" + (dir ?? "<null>"));

                Game.Mod.AssetOverlay.RegisterVideoOverride(loc, over);
                string dir2;
                bool after = Game.Mod.AssetOverlay.TryResolveVideoOverride(loc, out dir2);
                L.LogInfo("[EXP] AFTER RegisterVideoOverride('" + loc + "', '" + over + "') = "
                          + after + " dir=" + (dir2 ?? "<null>")
                          + " HasAnyOverride=" + Game.Mod.AssetOverlay.HasAnyOverride);

                // The registration check above calls the overlay directly. To show the
                // *game's* video-loading path consulting it, drive the real method:
                // VideoResourcePlayer.ResolveResourcePath is the branch disassembly
                // (RVA 0x1ABBCB0) puts the override in.
                try
                {
                    var vp = UnityEngine.Object.FindObjectOfType<Game.VideoResourcePlayer>();
                    if (vp == null) L.LogInfo("[EXP] no VideoResourcePlayer instance in this scene; the ResolveResourcePath hook stays armed for the in-game test");
                    else
                    {
                        var m = AccessTools.Method(typeof(Game.VideoResourcePlayer), "ResolveResourcePath",
                                                   new[] { typeof(string) });
                        var res = m.Invoke(vp, new object[] { loc });
                        L.LogInfo("[EXP] invoked VideoResourcePlayer.ResolveResourcePath('" + loc + "') = "
                                  + (res == null ? "<null>" : res.ToString()));
                    }
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    L.LogWarning("[EXP] ResolveResourcePath drive failed: " + inner.GetType().Name + ": " + inner.Message);
                }

                // Documents that the GameResourceManager resolver is NOT a mod channel
                // in this build (no registered external resolver -> NotOwned).
                try
                {
                    YooAsset.ResourcePackage pkg;
                    var st = Game.GameResourceManager.ResolveExternalRawFilePackage(over + "/x.mp4", out pkg);
                    L.LogInfo("[EXP] ResolveExternalRawFilePackage -> " + st
                              + " pkg=" + (pkg == null ? "<null>" : "set"));
                }
                catch (Exception e) { L.LogWarning("[EXP] ResolveExternalRawFilePackage probe: " + e.Message); }

                L.LogInfo("[EXP] E-V1 probe done; override intentionally left registered for the client test");
            }
            catch (Exception e) { L.LogWarning("[EXP] E-V1 failed: " + e); }
        }
    }


    /// <summary>Q1 recon: one-shot hierarchy dump of the settings panel.</summary>
    static class Q1Recon
    {
        private static bool _done;
        private static StringBuilder _sb;

        internal static void DumpOnce(Transform root, string source)
        {
            if (_done) return;
            _done = true;
            try
            {
                _sb = new StringBuilder();
                _sb.Append("=== Q1 settings-panel hierarchy dump (source=").Append(source)
                   .Append(", ").Append(DateTime.Now.ToString("HH:mm:ss")).Append(") ===\n");
                Walk(root, 0);
                string path = Path.Combine(GameRoot(), "v5_q1_recon.txt");   // v14 脱敏：不再写进名为 K 的内部目录
                File.WriteAllText(path, _sb.ToString(), new UTF8Encoding(false));
                Plugin.Logger.LogInfo("[Q1] hierarchy dumped to " + path);
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[Q1] recon failed: " + e); }
        }

        private static string GameRoot()
        {
            var p = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            return Path.GetDirectoryName(p);
        }

        private static void Walk(Transform t, int depth)
        {
            if (t == null || depth > 12) return;
            try
            {
                string pad = new string(' ', depth * 2);
                var rt = t.TryCast<RectTransform>();
                string geo = rt != null
                    ? " size=" + rt.sizeDelta.x.ToString("F0") + "x" + rt.sizeDelta.y.ToString("F0")
                      + " pos=" + rt.anchoredPosition.x.ToString("F0") + "," + rt.anchoredPosition.y.ToString("F0")
                    : "";
                _sb.Append(pad).Append(t.name).Append("  active=").Append(t.gameObject.activeSelf)
                   .Append(geo).Append("  [").Append(Components(t.gameObject)).Append("]\n");
                for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1);
            }
            catch { }
        }

        private static string Components(GameObject go)
        {
            var sb = new StringBuilder();
            try
            {
                var b = go.GetComponent<UnityEngine.UI.Button>();
                if (b != null)
                {
                    int n = b.onClick.GetPersistentEventCount();
                    sb.Append("Button(persistent=").Append(n);
                    for (int i = 0; i < n; i++)
                        sb.Append(",tgt").Append(i).Append('=').Append(TargetName(b.onClick.GetPersistentTarget(i)));
                    sb.Append(",interactable=").Append(b.interactable).Append(") ");
                }
                var img = go.GetComponent<UnityEngine.UI.Image>();
                if (img != null)
                    sb.Append("Image(raycast=").Append(img.raycastTarget)
                      .Append(",sprite=").Append(img.sprite != null)
                      .Append(",color=").Append(ColorStr(img.color)).Append(") ");
                var tmp = go.GetComponent<TMPro.TextMeshProUGUI>();
                if (tmp != null)
                    sb.Append("TMP(text='").Append(tmp.text).Append("',size=").Append(tmp.fontSize)
                      .Append(",color=").Append(ColorStr(tmp.color)).Append(") ");
                var tog = go.GetComponent<UnityEngine.UI.Toggle>();
                if (tog != null) sb.Append("Toggle(isOn=").Append(tog.isOn).Append(") ");
                var hlg = go.GetComponent<UnityEngine.UI.HorizontalLayoutGroup>();
                if (hlg != null) sb.Append("HLayout(spacing=").Append(hlg.spacing)
                    .Append(",align=").Append(hlg.childAlignment).Append(") ");
                var vlg = go.GetComponent<UnityEngine.UI.VerticalLayoutGroup>();
                if (vlg != null) sb.Append("VLayout(spacing=").Append(vlg.spacing)
                    .Append(",align=").Append(vlg.childAlignment).Append(") ");
                var sr = go.GetComponent<UnityEngine.UI.ScrollRect>();
                if (sr != null) sb.Append("ScrollRect ");
                var cg = go.GetComponent<CanvasGroup>();
                if (cg != null) sb.Append("CanvasGroup(a=").Append(cg.alpha).Append(") ");
            }
            catch (Exception e) { sb.Append("err:").Append(e.Message); }
            return sb.ToString().Trim();
        }

        // NOTE: an attempt to list every component by il2cpp class name (via
        // il2cpp_class_get_type + a raw Il2CppSystem.Type proxy + GameObject
        // .GetComponents(Type)) crashed the game with an access violation in
        // coreclr. It is not needed for Q1: the nav tabs carry no uGUI Button at
        // all, so the clone gets its own Button over its own raycast Image.

        private static string TargetName(UnityEngine.Object o)
        {
            if (o == null) return "null";
            try
            {
                if (o.TryCast<Game.UI.UPFLogic.Settings.SettingsPanelViewModel>() != null) return "SettingsPanelViewModel";
                var go = o.TryCast<GameObject>();
                if (go != null) return "GO:" + go.name;
                var c = o.TryCast<Component>();
                if (c != null) return "Comp:" + c.name;
            }
            catch { }
            return "?";
        }

        private static string ColorStr(Color c)
        {
            return ((int)(c.r * 255)) + "," + ((int)(c.g * 255)) + "," + ((int)(c.b * 255)) + "," + ((int)(c.a * 255));
        }
    }

    /// <summary>
    /// Applies pending mod changes the way the game's (hidden) workshop UI would.
    ///
    /// ModBootstrap.ApplyChanges() returns the list of tables that need a full
    /// scene re-enter. When that list is NON-empty the native code deliberately
    /// skips ConfigManager.RebuildTablesWithMods(), i.e. the hot tables are not
    /// baked either; the caller is expected to re-enter the game first. Nothing
    /// in this build ever does that because the workshop entry point is hidden,
    /// so we perform the re-enter step ourselves by calling the public
    /// ModBootstrap.OnGameLoaded(), which does run RebuildTablesWithMods().
    /// </summary>
    static class ModSync
    {
        private static bool _inApply;

        internal static void Apply(string tag)
        {
            if (_inApply) return;
            _inApply = true;
            try
            {
                var applied = ModBootstrap.ApplyChanges();
                Log(tag + ": ApplyChanges -> ", applied);

                int n = applied == null ? -1 : applied.Count;
                if (n > 0)
                {
                    Plugin.Logger.LogInfo(tag + ": " + n + " table(s) deferred (need re-enter): "
                                          + Preview(applied) + " -> running ModBootstrap.OnGameLoaded() to rebuild tables with mods");
                    Plugin.Logger.LogInfo(tag + ": table byte cache: " + InvalidateTableCache());
                    ModBootstrap.OnGameLoaded();
                    Plugin.Logger.LogInfo(tag + ": " + ForceReloadTables());
                    Log(tag + ": after OnGameLoaded ApplyChanges -> ", ModBootstrap.ApplyChanges());
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(tag + ": mod sync failed: " + e.Message);
            }
            finally
            {
                _inApply = false;
            }
        }

        /// <summary>
        /// ConfigManager.tableBytes memoises the raw (already overlay-patched)
        /// bytes per table, so once a table has been built the overlay is never
        /// consulted again and a mod can be enabled but never disabled inside the
        /// same session. Dropping the cache makes the next rebuild re-read each
        /// table through ModConfigOverlay.
        /// </summary>
        private static string InvalidateTableCache()
        {
            try
            {
                var cm = Game.ConfigManager.Instance;
                int before = -1;
                try { before = cm.tableBytes == null ? -1 : cm.tableBytes.Count; } catch { }
                if (cm.tableBytes != null) cm.tableBytes.Clear();
                try { if (cm.tableFileWriteTicks != null) cm.tableFileWriteTicks.Clear(); } catch { }
                return "cachedTables=" + before + " -> cleared";
            }
            catch (Exception e) { return "clear failed: " + e.Message; }
        }

        /// <summary>
        /// RebuildTablesWithMods() ends up in ReloadTables(incremental: false),
        /// which skips tables whose source file has not changed - so a mod that
        /// has just been turned OFF keeps its already-baked rows. Forcing the mod
        /// patched tables through ConfigManager.ForceReloadTables() makes the
        /// toggle take effect in both directions.
        ///
        /// The list is taken from ModConfigOverlay.PatchedTableNames (which is the
        /// only authoritative source); PatchedTableNames is exposed as an
        /// IEnumerable proxy rather than a List, so it is walked through its
        /// il2cpp enumerator. Only if that yields nothing do we fall back to the
        /// literal list, and the log says which path was used.
        /// </summary>
        private static readonly string[] FallbackPatchedTables =
        {
            "tbnpcbasecfg", "tbnpcaipersona"
        };

        private static List<string> PatchedTableNames()
        {
            var res = new List<string>();
            try
            {
                var seq = ModConfigOverlay.PatchedTableNames;
                if (seq == null) return res;
                // The generic IEnumerator<T> proxy exposes only Current; MoveNext
                // lives on the non-generic il2cpp IEnumerator, so walk through that.
                var gen = seq.GetEnumerator();
                if (gen == null) return res;
                var en = gen.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (en != null && en.MoveNext())
                {
                    var cur = en.Current;
                    if (cur == null) continue;
                    string n = cur.ToString();
                    if (!string.IsNullOrEmpty(n)) res.Add(n);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("PatchedTableNames walk failed: " + e.Message);
            }
            return res;
        }

        private static string ForceReloadTables()
        {
            try
            {
                var cm = Game.ConfigManager.Instance;
                var names = PatchedTableNames();
                string src = "overlay";
                if (names.Count == 0)
                {
                    src = "fallback-literal";
                    foreach (var t in FallbackPatchedTables) names.Add(t);
                }
                var forced = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var t in names) forced.Add(t);
                bool ok = cm.ForceReloadTables(
                    forced.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
                return "ForceReloadTables(" + src + ": " + string.Join(",", names) + ") -> " + ok;
            }
            catch (Exception e) { return "ForceReloadTables failed: " + e.Message; }
        }

        private static string Preview(Il2CppSystem.Collections.Generic.List<string> list)
        {
            string s = "";
            int n = list == null ? 0 : list.Count;
            for (int i = 0; i < n && i < 5; i++)
            {
                string v = null;
                try { v = list[i]; } catch { }
                s += (i == 0 ? "" : "; ") + (v ?? "?");
            }
            return s;
        }

        private static void Log(string tag, Il2CppSystem.Collections.Generic.List<string> applied)
        {
            if (applied == null) { Plugin.Logger.LogInfo(tag + "null"); return; }
            int n = applied.Count;
            Plugin.Logger.LogInfo(tag + n + " item(s)" + (n > 0 ? " | " + Preview(applied) : ""));
        }
    }
    /// <summary>
    /// Wakes up the game's native Game.Mod pipeline (dead because the workshop
    /// entry point is hidden) and exposes an F10 IMGUI manager so every Mod unit
    /// can be toggled at runtime.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "local.modmanager";
        public const string PluginName = "Mod Manager";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Logger;

        public override void Load()
        {
            Logger = Log;
            ClassInjector.RegisterTypeInIl2Cpp<ManagerBehaviour>();
            AddComponent<ManagerBehaviour>();

            try
            {
                var harmony = new Harmony("local.modmanager.hooks");
                harmony.PatchAll();
                int patched = 0;
                try
                {
                    foreach (var t in new[] { typeof(ModBootstrap),
                                              typeof(Game.UI.UPFLogic.Settings.SettingsPanel) })
                        foreach (var m in t.GetMethods())
                            if (Harmony.GetPatchInfo(m) != null) patched++;
                }
                catch { }
                Logger.LogInfo("Harmony hooks installed (OnGameLoaded watcher + settings entry); patched methods visible=" + patched);
                Logger.LogInfo("[MODPAGE] build stamp 20261007-1930 v13-split");
            }
            catch (Exception e)
            {
                Logger.LogWarning("Harmony patch failed: " + e.Message);
            }

            // F2 backstop, patched on its own so a failure here (a Unity built-in is
            // a different beast from the game's own types) cannot take the hooks
            // above down with it.
            try
            {
                var m = AccessTools.Method(typeof(UnityEngine.UI.ScrollRect), "OnScroll");
                if (m == null) Logger.LogWarning("[MODPAGE] ScrollRect.OnScroll not found; F2 disabled");
                else
                {
                    var p = AccessTools.Method(typeof(NativeWheelConsumerHook), "Prefix");
                    new Harmony("local.modmanager.scrollblock").Patch(m, prefix: new HarmonyMethod(p));
                    Logger.LogInfo("[MODPAGE] F2 native wheel block installed on "
                                   + m.DeclaringType.FullName + ".OnScroll");
                }
            }
            catch (Exception e)
            {
                Logger.LogWarning("[MODPAGE] F2 native wheel block failed: " + e.Message);
            }

            // v9 P0: the spurious-click reconnaissance hooks. Installed only with
            // MOD_TAB_DIAG=1, so the shipped binary routes events exactly as before.
            if (TabDiag.Enabled)
            {
                try { TabDiagHooks.Install(); }
                catch (Exception e) { Logger.LogWarning("[DBG] TABDIAG install failed: " + e.Message); }
            }

            // P6: with MOD_EXP_VIDEO=1 the game's own video resolver is hooked so a
            // real playback records that it consulted the AssetOverlay override.
            if (VideoExp.Enabled || VideoExp.HookOnly)
            {
                try { VideoExp.Install(); }
                catch (Exception e) { Logger.LogWarning("[EXP] hook install failed: " + e.Message); }
            }

            // v13：传送链追踪钩子（TeleportDiag）与开机观测（BootWatch）随调试代码一起
            // 迁到独立插件 LocalStoryDebug.dll；MOD 管理器这边不再安装任何调试钩子。
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded (F10 toggles the window)");
        }
    }

    public class ManagerBehaviour : MonoBehaviour
    {
        private const int MarkerNpcId = 100000;
        private const string MarkerLanguage = "zh-Hans";
        private const string MarkerText = "MODTAG";
        private const string MarkerGlyph = "【MOD生效·本地】";
        private const string StoryMod = "local.storytest";
        private const string BaseCfgUnit = "basecfg_patch";
        /// <summary>
        /// Set MOD_REV_TEST=1 to have the plugin run the off/on reversibility
        /// check by itself four self-check rounds in (it flips mods, so it is
        /// opt-in). The same check is always available as an F10 button.
        /// </summary>
        private static readonly bool AutoRevTest =
            Environment.GetEnvironmentVariable("MOD_REV_TEST") == "1";

        /// <summary>
        /// MOD_PERF_LEGACY_PERIODIC=1 restores the pre-P1.2 behaviour (a 10 s
        /// re-apply loop) so the performance A/B can be measured on one binary.
        /// </summary>
        private static readonly bool LegacyPeriodic =
            Environment.GetEnvironmentVariable("MOD_PERF_LEGACY_PERIODIC") == "1";

        /// <summary>MOD_P2_SELFTEST=1 opens the settings page once, for P2 verification.</summary>
        private static readonly bool P2SelfTest =
            Environment.GetEnvironmentVariable("MOD_P2_SELFTEST") == "1";

        /// <summary>MOD_PAGE_SELFTEST=1 additionally opens the MOD page once, so the
        /// scroll fix can be exercised without menu clicking.</summary>
        private static readonly bool PageSelfTest =
            Environment.GetEnvironmentVariable("MOD_PAGE_SELFTEST") == "1";

        private bool _scanTried;
        private bool _featureScanTried;
        private float _selfCheckAt = -1f;
        private int _checkRounds;
        private bool _revDone;
        private bool _p2Tried;
        private bool _pageTried;
        private float _pageTryAt = -1f;
        private float _legacyNext;
        private string _status = "waiting for the game to settle...";

        // P1.2: the window is hidden by default and its package list is refreshed
        // at most once per second instead of on every IMGUI event.
        private bool _show;
        private float _snapAt = -1f;
        private List<ModPackage> _snap = new List<ModPackage>();
        private Rect _win = new Rect(40f, 40f, 580f, 560f);
        private Vector2 _scroll = Vector2.zero;

        public ManagerBehaviour(IntPtr ptr) : base(ptr) { }

        /// <summary>Set in Update so non-Unity code (the settings entry) can reach us.</summary>
        internal static ManagerBehaviour Instance;

        internal void TogglePanel()
        {
            _show = !_show;
            if (_show) { RefreshSnapshotIfDue(); _status = "opened from F10"; }
        }

        /// <summary>
        /// Idempotent open, used by the settings tab. The tab is a plain uGUI
        /// Button on a cloned node and Unity can deliver several click events in a
        /// burst, so the entry must not toggle (that would make the panel flicker
        /// shut again); F10 keeps the toggle behaviour for debugging.
        /// </summary>
        internal void ShowPanel()
        {
            if (!_show) _show = true;
            RefreshSnapshotIfDue();
            _status = "opened from settings tab";
        }

        private static ModRegistry Reg
        {
            get { return ModRegistry.Instance; }
        }

        /// <summary>
        /// ModRegistry.Packages is an Il2CppSystem IReadOnlyList, whose interop
        /// proxy exposes no Count; the backing object is a List, so try that first
        /// and fall back to the indexer.
        /// </summary>
        private static List<ModPackage> PackageSnapshot()
        {
            var res = new List<ModPackage>();
            try
            {
                var ro = Reg.Packages;
                if (ro == null) return res;
                var lst = ro.TryCast<Il2CppSystem.Collections.Generic.List<ModPackage>>();
                if (lst != null)
                {
                    for (int i = 0; i < lst.Count; i++) res.Add(lst[i]);
                    return res;
                }
                for (int i = 0; i < 4096; i++)
                {
                    ModPackage p = ro[i];
                    if (p == null) break;
                    res.Add(p);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("PackageSnapshot: " + e.Message);
            }
            return res;
        }

        private void Update()
        {
            try
            {
                if (Instance == null) Instance = this;
                if (Input.GetKeyDown(KeyCode.F10)) _show = !_show;
                if (Input.GetMouseButtonDown(0)) SettingsEntryHook.NoteMouseDown();   // P1 gate source
                SettingsEntryHook.Maintain();
                if (!_featureScanTried && Time.time > 5f)
                {
                    // v14: one-shot discovery log so "did the page see my feature plugin?"
                    // is answerable from LogOutput.log without opening F10.
                    _featureScanTried = true;
                    FeaturePluginRegistry.Refresh();
                    var features = new List<string>();
                    foreach (var item in FeaturePluginRegistry.Current) features.Add(item.Feature.FeatureId);
                    Plugin.Logger.LogInfo("[FEATURES] discovered " + features.Count + " managed feature plugin(s): " + string.Join(", ", features));
                }
                if (TabDiag.Enabled) TabDiag.Tick();                                  // MOD_TAB_DIAG input probe

                if (!_scanTried && Time.time > 5f)
                {
                    _scanTried = true;
                    DoInitialScan();
                }

                // P1.2: no periodic re-apply any more. Mod changes are applied by
                // the F10/settings toggle, by the game itself calling
                // ModBootstrap.OnGameLoaded() (Harmony postfix), or by the manual
                // "Re-scan" button. SelfCheck is on demand only (after a toggle).
                // MOD_PERF_LEGACY_PERIODIC=1 restores the old 10 s loop so the
                // performance A/B can be run on the very same binary.
                if (LegacyPeriodic && Time.time > 5f)
                {
                    if (_legacyNext == 0f) _legacyNext = Time.time + 6f;
                    if (Time.time > _legacyNext)
                    {
                        _legacyNext = Time.time + 10f;
                        _checkRounds++;
                        ModSync.Apply("legacy-re-apply");
                        SelfCheck("round " + _checkRounds);
                        if (AutoRevTest && _checkRounds == 4) ReversibilityTest();
                    }
                }
                else if (_selfCheckAt > 0f && Time.time > _selfCheckAt)
                {
                    _selfCheckAt = -1f;
                    _checkRounds++;
                    SelfCheck("check " + _checkRounds);
                }

                if (AutoRevTest && !_revDone && Time.time > 30f)
                {
                    _revDone = true;
                    ReversibilityTest();
                }

                // P2 acceptance aid: MOD_P2_SELFTEST=1 opens the game's own settings
                // page so the entry injection can be verified from the log without
                // anyone having to click through the menus.
                if (P2SelfTest && !_p2Tried && Time.time > 20f)
                {
                    _p2Tried = true;
                    try
                    {
                        Plugin.Logger.LogInfo("[SETTINGS] self-test: opening PanelId.SettingsPanel (1003) via Game.UIManager");
                        Game.UIManager.Instance.Open(Game.PanelId.SettingsPanel, null, false, null, null);
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger.LogWarning("[SETTINGS] self-test open failed: " + e);
                    }
                }

                // Scroll-fix acceptance aid: MOD_PAGE_SELFTEST=1 opens the MOD
                // management page once (the settings panel has to be up first, e.g.
                // via MOD_P2_SELFTEST=1), so the wheel behaviour can be checked
                // without anyone clicking through the menus.
                if (PageSelfTest && !_pageTried && Time.time > 24f)
                {
                    if (_pageTryAt < 0f) _pageTryAt = Time.time + 3f;
                    else if (Time.time > _pageTryAt)
                    {
                        var p = SettingsEntryHook.CurrentPanel;
                        if (p == null) { _pageTryAt = Time.time + 2f; }
                        else
                        {
                            _pageTried = true;
                            try
                            {
                                Plugin.Logger.LogInfo("[MODPAGE] self-test: opening the MOD page");
                                ModPageController.Show(p);
                            }
                            catch (Exception e)
                            {
                                Plugin.Logger.LogWarning("[MODPAGE] self-test open failed: " + e);
                            }
                        }
                    }
                }

                if (_scanTried && Time.time > 25f) VideoExp.Run();
                if (_scanTried) VideoExp.ChannelTick();   // v11 P1: 官方通道驱动（MOD_EXP_VIDEO=2）
                if (_scanTried) VideoBridge.Tick();       // v12 P1b: 官方 API 桥接（MOD_EXP_BRIDGE=0 可关）
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("Update failed: " + e);
            }
        }

        private void DoInitialScan()
        {
            try
            {
                var reg = Reg;
                reg.EnsureInitialized();
                reg.LoadPrefs();
                reg.Rescan();
                EnableEverythingByDefault(reg);
                ModSync.Apply("initial apply");
                reg.SavePrefs();

                var pkgs = PackageSnapshot();
                _status = "pipeline awake: " + pkgs.Count + " package(s)";
                Plugin.Logger.LogInfo(_status);
                foreach (var p in pkgs) LogPackage(reg, p);
                DeepProbe();
                _selfCheckAt = Time.time + 8f;
            }
            catch (Exception e)
            {
                _status = "pipeline init FAILED: " + e.Message;
                Plugin.Logger.LogError(_status);
                Plugin.Logger.LogError(e);
            }
        }

        /// <summary>
        /// First-sight initialization only. The game never shows its workshop UI,
        /// so nothing in-game can turn the mods on; we therefore seed the initial
        /// enabled state ourselves ONCE per package (marker file .lm_init in the
        /// package root). The seed value comes from mod.json "defaultEnabled"
        /// (default true; example/risky mods ship with false). After the marker
        /// exists we never touch the state again — user toggles in the MOD page
        /// or F10 window own it from then on.
        /// </summary>
        private void EnableEverythingByDefault(ModRegistry reg)
        {
            int changed = 0;
            foreach (var p in PackageSnapshot())
            {
                string root = null;
                try { root = p.RootDir; } catch { }
                string marker = null;
                if (!string.IsNullOrEmpty(root))
                {
                    try { marker = Path.Combine(root, ".lm_init"); } catch { }
                }
                if (marker == null || File.Exists(marker)) continue;   // seen before

                bool defOn = ReadDefaultEnabledJson(root);
                int n = p.Units == null ? 0 : p.Units.Count;
                for (int u = 0; u < n; u++)
                {
                    var un = p.Units[u];
                    if (un == null) continue;
                    try
                    {
                        int npc = ModRegistry.UnitEntryNpcId(un);
                        reg.SetEntryEnabled(p.ModId, un.UnitId, npc, defOn);
                        changed++;
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning("seed " + p.ModId + ": " + e.Message); }
                }
                try { File.WriteAllText(marker, DateTime.UtcNow.ToString("o") + " defaultEnabled=" + defOn); }
                catch (Exception e) { Plugin.Logger.LogWarning("marker " + p.ModId + ": " + e.Message); }
                Plugin.Logger.LogInfo("first sight of " + p.ModId + ": defaultEnabled=" + defOn);
            }
            if (changed > 0) Plugin.Logger.LogInfo("startup default: seeded " + changed + " unit(s)");
        }

        /// <summary>mod.json "defaultEnabled": false -> off; absent/malformed -> on.</summary>
        private static bool ReadDefaultEnabledJson(string rootDir)
        {
            try
            {
                string path = Path.Combine(rootDir, "mod.json");
                if (!File.Exists(path)) return true;
                string text = File.ReadAllText(path);
                return !System.Text.RegularExpressions.Regex.IsMatch(
                    text, "\"defaultEnabled\"\\s*:\\s*false",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch { return true; }
        }

        private static string OverlayHasAny()
        {
            try { return ModConfigOverlay.HasAny.ToString(); } catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        private static string PatchedTables()
        {
            try
            {
                var names = ModConfigOverlay.PatchedTableNames;
                if (names == null) return "<null>";
                var lst = names.TryCast<Il2CppSystem.Collections.Generic.List<string>>();
                if (lst == null) return "<not-a-list>";
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lst.Count; i++) sb.Append(lst[i]).Append(',');
                return sb.Length == 0 ? "<empty>" : sb.ToString();
            }
            catch (Exception e) { return "<err:" + e.Message + ">"; }
        }

        /// <summary>
        /// Reads the live tbNpcBaseCfg row for the marker NPC straight out of the
        /// game's config tables, so the report can prove the patch is in effect
        /// without relying only on the on-screen profile page.
        /// </summary>
        private void SelfCheck(string tag)
        {
            string plot;
            bool hit = ReadMarker(out plot);
            if (plot == null)
            {
                Plugin.Logger.LogWarning("SELFCHECK[" + tag + "]: no tbNpcBaseCfg row for npc " + MarkerNpcId);
                return;
            }
            Plugin.Logger.LogInfo("SELFCHECK[" + tag + "]: npc " + MarkerNpcId + " propPlot.zh-Hans = " + plot);
            Plugin.Logger.LogInfo("SELFCHECK[" + tag + "]: marker present = " + hit);
        }

        private bool ReadMarker(out string plot)
        {
            plot = null;
            try
            {
                var tbl = Game.ConfigManager.Instance.Tables.TbNpcBaseCfg;
                var row = tbl.GetOrDefault(new LubanDatas.TbNpcBaseCfgId(MarkerNpcId));
                if (row == null) return false;
                plot = row.propPlot.GetText(MarkerLanguage);
                return plot != null && plot.Contains(MarkerText) && plot.Contains(MarkerGlyph);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("ReadMarker failed: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// Off -> Apply -> expect marker gone; On -> Apply -> expect marker back.
        /// This is the "双向可逆" check from the plan, driven from inside the game
        /// so it lands in LogOutput.log without needing manual GUI clicks.
        /// </summary>
        private void ReversibilityTest()
        {
            var L = Plugin.Logger;
            L.LogInfo("REV: ===== reversibility test =====");
            L.LogInfo("REV: step0 (unchanged)            marker=" + ReadMarker(out _) + " overlay.HasAny=" + OverlayHasAny());
            Apply(StoryMod, BaseCfgUnit, 0, false);
            L.LogInfo("REV: step1 (basecfg_patch OFF)    marker=" + ReadMarker(out _)
                      + " tablesBuiltWithMods=" + Game.ConfigManager.Instance.tablesBuiltWithMods
                      + " overlay.HasAny=" + OverlayHasAny());
            Apply(StoryMod, BaseCfgUnit, 0, true);

            // Does the overlay itself ever forget a patch? Turn *everything* off.
            SetAllEntries(false);
            ModSync.Apply("rev-all-off");
            L.LogInfo("REV: step3 (ALL mods OFF)         marker=" + ReadMarker(out _)
                      + " overlay.HasAny=" + OverlayHasAny()
                      + " activeTablePatchUnits=" + ActiveTablePatchUnits());
            SetAllEntries(true);
            ModSync.Apply("rev-all-on");
            L.LogInfo("REV: step4 (all back ON)          marker=" + ReadMarker(out _)
                      + " overlay.HasAny=" + OverlayHasAny()
                      + " activeTablePatchUnits=" + ActiveTablePatchUnits());
            L.LogInfo("REV: ===== reversibility test end =====");
        }

        private void SetAllEntries(bool enabled)
        {
            foreach (var p in PackageSnapshot())
            {
                int n = p.Units == null ? 0 : p.Units.Count;
                for (int u = 0; u < n; u++)
                {
                    var un = p.Units[u];
                    if (un == null) continue;
                    try
                    {
                        int npc = ModRegistry.UnitEntryNpcId(un);
                        Reg.SetEntryEnabled(p.ModId, un.UnitId, npc, enabled);
                    }
                    catch (Exception e) { Plugin.Logger.LogWarning("SetAllEntries: " + e.Message); }
                }
            }
            try { Reg.SavePrefs(); } catch { }
        }

        private int ActiveTablePatchUnits()
        {
            try
            {
                var a = Reg.ActiveUnits(ModUnitType.TablePatch);
                return a == null ? -1 : a.Count;
            }
            catch { return -2; }
        }

        /// <summary>
        /// Walks the patch pipeline stage by stage from inside the game so the
        /// report can point at the exact stage that drops the patch.
        /// </summary>
        private void ProbeEnum(string tag, string dir)
        {
            try
            {
                string failure = null;
                var list = ModTablePatch.EnumerateIn(dir, out failure);
                Plugin.Logger.LogInfo("PROBE-ENUM[" + tag + "] " + dir + " -> "
                                      + (list == null ? "null" : list.Count + " patch(es)")
                                      + " failure=" + (failure ?? "<null>"));
                if (list == null) return;
                for (int k = 0; k < list.Count && k < 6; k++)
                {
                    var mp = list[k];
                    int ups = mp.Upsert == null ? -1 : mp.Upsert.Count;
                    Plugin.Logger.LogInfo("PROBE-ENUM[" + tag + "]    table='" + mp.Table
                                          + "' file=" + mp.SourceFile + " upsert=" + ups
                                          + " keyFields=" + (mp.KeyFields == null ? "null" : string.Join(",", mp.KeyFields)));
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("PROBE-ENUM[" + tag + "] threw: " + e.Message); }
        }

        private void DeepProbe()
        {
            var L = Plugin.Logger;
            try
            {
                var reg = Reg;

                // 1) which table-patch units does the registry consider active?
                try
                {
                    var act = reg.ActiveUnits(ModUnitType.TablePatch);
                    L.LogInfo("PROBE: ActiveUnits(TablePatch) count=" + (act == null ? -1 : act.Count));
                    if (act != null)
                        for (int i = 0; i < act.Count; i++)
                        {
                            var t = act[i];
                            L.LogInfo("PROBE:   active[" + i + "] " + t.Item1.ModId + " / "
                                      + t.Item2.UnitId + " root=" + t.Item2.RootDir);
                        }
                }
                catch (Exception e) { L.LogWarning("PROBE ActiveUnits: " + e.Message); }

                // 2) table tier classification
                try
                {
                    L.LogInfo("PROBE: IsHot(tbnpcbasecfg)=" + ModTableTiers.IsHot("tbnpcbasecfg")
                              + " IsHot(tbnpcaipersona)=" + ModTableTiers.IsHot("tbnpcaipersona")
                              + " IsHot(tbmainmenubutton)=" + ModTableTiers.IsHot("tbmainmenubutton"));
                    L.LogInfo("PROBE: Of(tbnpcbasecfg)=" + ModTableTiers.Of("tbnpcbasecfg")
                              + " Of(tbmainmenubutton)=" + ModTableTiers.Of("tbmainmenubutton"));
                }
                catch (Exception e) { L.LogWarning("PROBE tiers: " + e.Message); }

                // 3) can the patch files actually be enumerated / parsed?
                //    ModTablePatch.EnumerateIn(root) itself appends "tables", so
                //    probe both the configured unit root and the package root.
                foreach (var p in PackageSnapshot())
                {
                    string pkgRoot = p.RootDir;
                    ProbeEnum("pkgRoot", pkgRoot);
                    ProbeEnum("pkgRoot/tables", System.IO.Path.Combine(pkgRoot, "tables"));
                    int n = p.Units == null ? 0 : p.Units.Count;
                    for (int u = 0; u < n; u++)
                    {
                        var un = p.Units[u];
                        if (un == null || un.Type != ModUnitType.TablePatch) continue;
                        ProbeEnum("unitRoot(" + un.UnitId + ")", un.RootDir);
                    }
                }

                // 4) did the tables come out with mods applied?
                try
                {
                    var cm = Game.ConfigManager.Instance;
                    L.LogInfo("PROBE: tablesBuiltWithMods=" + cm.tablesBuiltWithMods
                              + " CachedTableCount=" + cm.CachedTableCount);
                    var tbl = cm.Tables.TbNpcBaseCfg;
                    L.LogInfo("PROBE: TbNpcBaseCfg._overrides="
                              + (tbl._overrides == null ? "null" : tbl._overrides.Count.ToString()));
                    L.LogInfo("PROBE: TbNpcBaseCfg.DataList="
                              + (tbl.DataList == null ? "null" : tbl.DataList.Count.ToString()));
                }
                catch (Exception e) { L.LogWarning("PROBE cm: " + e.Message); }
            }
            catch (Exception e) { L.LogError("DeepProbe failed: " + e); }
        }

        private void LogPackage(ModRegistry reg, ModPackage p)
        {
            int n = p.Units == null ? 0 : p.Units.Count;
            bool hasPatch = false;
            try { hasPatch = reg.HasEnabledPatch(p); } catch { }
            Plugin.Logger.LogInfo(string.Format(
                "package {0} title='{1}' origin={2} kind={3} units={4} hasEnabledPatch={5}",
                p.ModId, p.Title, p.Origin, p.Kind, n, hasPatch));
            for (int u = 0; u < n; u++)
            {
                var un = p.Units[u];
                if (un == null) continue;
                int npc = ModRegistry.UnitEntryNpcId(un);
                bool on = false;
                try { on = reg.IsEntryEnabled(p.ModId, un.UnitId, npc); } catch { }
                Plugin.Logger.LogInfo(string.Format(
                    "   unit {0} type={1} npc={2} enabled={3}", un.UnitId, un.Type, npc, on));
            }
        }

        private void Apply(string modId, string unitId, int npcId, bool enabled)
        {
            try
            {
                var reg = Reg;
                reg.SetEntryEnabled(modId, unitId, npcId, enabled);
                bool now = false;
                try { now = reg.IsEntryEnabled(modId, unitId, npcId); } catch { }
                reg.SavePrefs();
                ModSync.Apply("toggle");
                Plugin.Logger.LogInfo("toggled " + modId + "|" + unitId + "|" + npcId + " -> " + enabled
                                      + " (IsEntryEnabled now=" + now + ", overlay.HasAny=" + OverlayHasAny()
                                      + ", patchedTables=" + PatchedTables() + ")");
                _selfCheckAt = Time.time + 3f;
            }
            catch (Exception e)
            {
                _status = "toggle failed: " + e.Message;
                Plugin.Logger.LogError(_status + "\n" + e);
            }
        }

        // ---- internal API for the settings-page MOD manager (S2) ----

        /// <summary>Throttled package snapshot, shared with the IMGUI window.</summary>
        internal List<ModPackage> CurrentSnapshot()
        {
            RefreshSnapshotIfDue();
            return _snap;
        }

        /// <summary>Toggle one registry entry from the settings-page switch rows.</summary>
        internal void ApplyEntry(string modId, string unitId, int npcId, bool enabled)
        {
            Apply(modId, unitId, npcId, enabled);
        }

        internal bool EntryEnabled(string modId, string unitId, int npcId)
        {
            try { return Reg.IsEntryEnabled(modId, unitId, npcId); }
            catch { return false; }
        }

        private void OnGUI()
        {
            if (!_show) return;
            RefreshSnapshotIfDue();
            GUI.skin.label.fontSize = 13;
            GUI.skin.button.fontSize = 13;
            GUI.skin.toggle.fontSize = 13;
            _win = GUI.Window(0x4B49, _win, (GUI.WindowFunction)WindowBody, "Mod Manager");
        }

        /// <summary>
        /// P1.2: the package list is a cross-interop enumeration, so it is fetched
        /// at most once per second and reused by every IMGUI event of that second.
        /// </summary>
        private void RefreshSnapshotIfDue()
        {
            if (_snapAt > 0f && Time.unscaledTime < _snapAt) return;
            _snapAt = Time.unscaledTime + 1f;
            _snap = PackageSnapshot();
        }

        private void WindowBody(int id)
        {
            try
            {
                GUILayout.BeginVertical();
                GUILayout.Label("status: " + _status);
                GUILayout.Label("F10 = show/hide");

                if (GUILayout.Button("Re-scan + ApplyChanges", GUILayout.Height(26)))
                {
                    try
                    {
                        var reg = Reg;
                        reg.Rescan();
                        ModSync.Apply("rescan");
                        _status = "rescanned: " + PackageSnapshot().Count + " package(s)";
                    }
                    catch (Exception e) { _status = "rescan failed: " + e.Message; }
                }

                if (GUILayout.Button("Deep probe (pipeline stages)", GUILayout.Height(26)))
                {
                    DeepProbe();
                    _status = "deep probe done";
                }

                if (GUILayout.Button("Reversibility test (off -> on)", GUILayout.Height(26)))
                {
                    ReversibilityTest();
                    _status = "reversibility test done";
                }

                if (GUILayout.Button("Force OnGameLoaded() + Apply", GUILayout.Height(26)))
                {
                    try
                    {
                        ModBootstrap.OnGameLoaded();
                        ModSync.Apply("forced OnGameLoaded");
                        _selfCheckAt = Time.time + 3f;
                        _status = "OnGameLoaded forced";
                    }
                    catch (Exception e) { _status = "OnGameLoaded failed: " + e.Message; }
                }

                GUILayout.Space(4);
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

                try
                {
                    var reg = Reg;
                    foreach (var p in _snap)
                    {
                        GUILayout.Label("-- " + p.ModId + "  (" + p.Title + ")");
                        int n = p.Units == null ? 0 : p.Units.Count;
                        if (n == 0) GUILayout.Label("     (no units)");

                        for (int u = 0; u < n; u++)
                        {
                            var un = p.Units[u];
                            if (un == null) continue;
                            int npc = ModRegistry.UnitEntryNpcId(un);
                            bool on;
                            try { on = reg.IsEntryEnabled(p.ModId, un.UnitId, npc); }
                            catch { on = false; }

                            GUILayout.BeginHorizontal();
                            GUILayout.Space(16);
                            string label = (on ? "[x] " : "[  ] ") + un.UnitId + " [" + un.Type + "] npc=" + npc;
                            if (GUILayout.Button(label, GUILayout.ExpandWidth(false)))
                            {
                                Plugin.Logger.LogInfo("GUI click on " + p.ModId + "|" + un.UnitId
                                    + " event=" + Event.current.type + " mouse=" + Event.current.mousePosition);
                                Apply(p.ModId, un.UnitId, npc, !on);
                                GUI.changed = true;
                            }
                            GUILayout.EndHorizontal();
                        }
                        GUILayout.Space(6);
                    }
                }
                catch (Exception e)
                {
                    GUILayout.Label("error: " + e.Message);
                }

                GUILayout.EndScrollView();
                GUILayout.EndVertical();
                GUI.DragWindow();
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError("WindowBody failed: " + e);
            }
        }
    }
}
