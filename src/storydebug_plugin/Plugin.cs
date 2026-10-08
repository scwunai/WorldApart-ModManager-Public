using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using LocalModManager.Abstractions;
using UnityEngine;

namespace LocalModManager
{
    /// <summary>
    /// v13：调试/剧情工具**独立插件**。从 `LocalModManager`（MOD 管理器）拆出来，
    /// 只负责"改动运行时状态"的调试能力，不碰 MOD 管线：
    ///
    ///   F9 剧情调试窗口（剧情跳跃 / 玩家参数 / NPC 社交，列表均为下拉菜单）
    ///   DebugActions —— 传送（官方全链）、剧情跳跃
    ///   WriteTest    —— 存档三写入往返测试
    ///   ReadOnlyLoad —— 只读载档 harness（含开机流程事件等待）
    ///
    /// 与 MOD 管理器完全解耦（无任何交叉引用）；两者共用游戏自身的 ModRegistry/AssetOverlay，
    /// 但各自的 DLL、GUID、日志源独立。
    /// 环境开关全部沿用：MOD_DEBUG / MOD_DEBUG_* / MOD_F9_RENDERTEST /
    /// MOD_DEBUG_TPMODE / MOD_DEBUG_TPLWAIT / MOD_DEBUG_TPDIAG / MOD_DEBUG_LOADAT[_EVT|_TIMEOUT]。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class StoryDebugPlugin : BasePlugin, IManagedFeaturePlugin
    {
        public const string PluginGuid = "local.storydebug";
        public const string PluginName = "Story Debug Tool";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Logger;

        /// <summary>v14 P2: 本插件实例，供 F9 侧读取 IManagedFeaturePlugin 契约值（冒烟断言用）。</summary>
        internal static StoryDebugPlugin Self;

        public override void Load()
        {
            Logger = Log;
            Self = this;
            ClassInjector.RegisterTypeInIl2Cpp<StoryDebugBehaviour>();
            AddComponent<StoryDebugBehaviour>();
            // 调试侧的两个只读钩子（各自有环境开关，默认关）
            try { TeleportDiag.Install(); }
            catch (Exception e) { Logger.LogWarning("[DBG] TPDIAG install failed: " + e.Message); }
            try { BootWatch.Install(); }
            catch (Exception e) { Logger.LogWarning("[BOOT] install failed: " + e.Message); }

            Logger.LogInfo("[DBG] build stamp 20261008-0100 v14-featureapi");
            Logger.LogInfo(PluginName + " " + PluginVersion + " loaded (F9 toggles the window)");
        }

        // ================================================================
        // v14 P2: IManagedFeaturePlugin —— 让本插件出现在 MOD 管理页的
        // 「BepInEx 功能插件」分区里，开关**只控 F9 窗口显隐**：
        // 插件不卸载、MonoBehaviour 不销毁、三个调试驱动（WriteTest /
        // DebugActions / ReadOnlyLoad）照常 tick，只是不再绘制窗口。
        // 契约本体在所引用的 LocalModManager.Abstractions.dll 里，见
        // docs/BepInEx-Feature-Plugin-API.md。
        // ================================================================
        public string FeatureId { get { return PluginGuid; } }
        public string DisplayName { get { return PluginName; } }
        public string Description { get { return "F9 剧情调试窗口：剧情跳跃 / 玩家参数 / NPC 社交"; } }
        public string FeatureVersion { get { return PluginVersion; } }
        public bool DesiredEnabled { get { return DebugTool.FeatureEnabled; } }
        public FeaturePluginState State
        {
            get { return DebugTool.FeatureEnabled ? FeaturePluginState.Running : FeaturePluginState.Stopped; }
        }
        public string StatusMessage { get { return DebugTool.FeatureStatusText(); } }
        public void SetEnabled(bool enabled) { DebugTool.SetFeatureEnabled(enabled); }
    }

    /// <summary>
    /// 驱动调试工具的那一个 MonoBehaviour：Update 跑三个测试驱动，OnGUI 画 F9 窗口。
    /// （MOD 管理器侧的 ManagerBehaviour 只画 F10 窗口，两者互不调用。）
    /// </summary>
    public class StoryDebugBehaviour : MonoBehaviour
    {
        public StoryDebugBehaviour(IntPtr ptr) : base(ptr) { }

        // 三个测试驱动（WriteTest / DebugActions / ReadOnlyLoad）由 DebugTool.OnGui()
        // 统一 tick —— 与 v12（ManagerBehaviour.OnGUI → DebugTool.OnGui）逐行等价，
        // 不在这里再 tick 一次，避免同一帧被推进两遍。
        private void OnGUI()
        {
            try { DebugTool.OnGui(); }
            catch (Exception e)
            {
                try { StoryDebugPlugin.Logger.LogWarning("[DBG] draw failed: " + e.Message); } catch { }
            }
        }
    }
}
