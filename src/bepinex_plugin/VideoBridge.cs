using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace LocalModManager
{
    /// <summary>
    /// v12 P1b —— 官方 API 桥接。
    ///
    /// 背景（v11 实测）：游戏自己的"扫描/收集"这一半（`ModRegistry.ActiveUnits(AssetOverride)`
    /// → `ModOverlayCoordinator.Sync` → `ModAssetApplier.CollectDesired`）对
    /// `type=asset_override` 的单元一律不返回（`ActiveUnits(AssetOverride)=0`，而同一时刻
    /// `ActiveUnits(TablePatch)=2` 正常），因此玩家本地放的合规视频包永远不会被自动登记。
    /// 而"登记"这一半 —— `AssetOverlay.RegisterVideoOverride` —— **本身就是游戏自己的官方 API**
    /// （`ModAssetApplier.ApplyVideo` 内部调的就是它），消费端 `VideoResourcePlayer.ResolveResourcePath`
    /// 与反注册家族（`UnregisterVideoOverrideIf` 等）也都是官方的。
    ///
    /// 本类只做一件事：**代官方走漏的那一步**——把官方自己的扫描器
    /// `ModAssetApplier.CollectOverrides` 在合规单元上收出来的条目，用官方
    /// `AssetOverlay.RegisterVideoOverride` 登记进官方注册表；并按其差集语义反注册。
    /// 除这两个官方 API 外不引入任何旁路注册表。
    ///
    /// 开关：`MOD_EXP_BRIDGE=0` 整体关闭（关闭时会把本类此前登记的键全部反注册）。
    ///       `MOD_EXP_BRIDGE_PERIOD`（秒，默认 5）差集检查周期。
    /// </summary>
    static class VideoBridge
    {
        internal static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MOD_EXP_BRIDGE") != "0";

        private static readonly float Period = EnvF("MOD_EXP_BRIDGE_PERIOD", 5f);

        /// <summary>本类登记过的键 → 目录（只用于差集反注册，不参与解析）。</summary>
        private static readonly Dictionary<string, string> _mine = new Dictionary<string, string>();

        private static float _nextAt;
        private static int _lastDesiredCount = -1;
        private static string _lastSig = "";

        internal static string Status = "(未启用)";

        private static float EnvF(string k, float d)
        {
            float v;
            var s = Environment.GetEnvironmentVariable(k);
            return (s != null && float.TryParse(s, System.Globalization.NumberStyles.Float,
                                                System.Globalization.CultureInfo.InvariantCulture, out v)) ? v : d;
        }

        private static void Log(string s)
        {
            Status = s;
            try { Plugin.Logger.LogInfo("[BRIDGE] " + s); } catch { }
        }

        /// <summary>由 ManagerBehaviour.Update 调用；内部限频。</summary>
        internal static void Tick()
        {
            try
            {
                if (Time.unscaledTime < _nextAt) return;
                _nextAt = Time.unscaledTime + Period;
                Sync();
            }
            catch (Exception e) { Log("Tick 异常 " + e.GetType().Name + ": " + e.Message); }
        }

        /// <summary>重算"期望被桥接的键集合"，与已登记的做差集：新增登记、消失反注册。</summary>
        internal static void Sync()
        {
            var desired = new Dictionary<string, string>();

            if (Enabled)
            {
                try
                {
                    var reg = Game.Mod.ModRegistry.Instance;
                    var ro = reg == null ? null : reg.Packages;
                    var pkgs = ro == null ? null
                        : ro.TryCast<Il2CppSystem.Collections.Generic.List<Game.Mod.ModPackage>>();
                    if (pkgs != null)
                        for (int pi = 0; pi < pkgs.Count; pi++)
                        {
                            var p = pkgs[pi];
                            if (p == null || p.Units == null) continue;
                            for (int ui = 0; ui < p.Units.Count; ui++)
                            {
                                var u = p.Units[ui];
                                if (u == null) continue;
                                // 只兜 asset_override
                                if (u.Type != Game.Mod.ModUnitType.AssetOverride) continue;
                                bool en = false;
                                try { en = reg.IsEntryEnabled(p.ModId, u.UnitId, u.TargetNpcId); } catch { }
                                if (!en)
                                {
                                    Log("跳过（单元未启用）pkg=" + p.ModId + " unit=" + u.UnitId);
                                    continue;
                                }
                                CollectOne(p.ModId, u, desired);
                            }
                        }
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    Log("收集异常 " + inner.GetType().Name + ": " + inner.Message);
                }
            }

            string sig = Sig(desired);
            if (sig == _lastSig && _lastDesiredCount == desired.Count) return;   // 无变化不刷日志
            _lastSig = sig;
            _lastDesiredCount = desired.Count;

            // 新增 / 目录变更 → 登记（官方 API）
            foreach (var kv in desired)
            {
                string cur;
                if (_mine.TryGetValue(kv.Key, out cur) && cur == kv.Value) continue;
                try
                {
                    Game.Mod.AssetOverlay.RegisterVideoOverride(kv.Key, kv.Value);
                    _mine[kv.Key] = kv.Value;
                    Log("登记 key='" + kv.Key + "' dir='" + kv.Value + "'（官方 AssetOverlay.RegisterVideoOverride）");
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    Log("登记失败 key='" + kv.Key + "' " + inner.GetType().Name + ": " + inner.Message);
                }
            }

            // 不再期望 → 反注册（官方 API，按"期望目录"守卫，避免撤掉别人登记的同一个键）
            var drop = new List<string>();
            foreach (var kv in _mine)
            {
                string want;
                if (!desired.TryGetValue(kv.Key, out want) || want != kv.Value) drop.Add(kv.Key);
            }
            foreach (var k in drop)
            {
                try
                {
                    bool ok = Game.Mod.AssetOverlay.UnregisterVideoOverrideIf(k, _mine[k]);
                    Log("反注册 key='" + k + "' rc=" + ok + "（官方 UnregisterVideoOverrideIf）");
                }
                catch (Exception e)
                {
                    var inner = e.InnerException ?? e;
                    Log("反注册失败 key='" + k + "' " + inner.GetType().Name + ": " + inner.Message);
                }
                _mine.Remove(k);
            }

            Log("差集同步完成：期望=" + desired.Count + " 本插件登记=" + _mine.Count
                + " HasAnyOverride=" + SafeHasAny()
                + (Enabled ? "" : "（MOD_EXP_BRIDGE=0：不登记，只做反注册）"));
        }

        private static bool SafeHasAny()
        {
            try { return Game.Mod.AssetOverlay.HasAnyOverride; } catch { return false; }
        }

        private static string Sig(Dictionary<string, string> d)
        {
            var keys = new List<string>(d.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys) sb.Append(k).Append('=').Append(d[k]).Append(';');
            return sb.ToString();
        }

        /// <summary>单个 asset_override 单元：先用游戏自己的扫描器收，再按官方语义归一化键。</summary>
        private static void CollectOne(string modId, Game.Mod.ModUnit unit, Dictionary<string, string> desired)
        {
            try
            {
                var entries = Game.Mod.ModAssetApplier.CollectOverrides(unit.RootDir);
                if (entries == null || entries.Count == 0)
                {
                    Log("单元 pkg=" + modId + " unit=" + unit.UnitId + " root='" + unit.RootDir
                        + "' 未收出任何覆盖条目（布局需 root/assets 下含 *.mp4 + *.segments.json）");
                    return;
                }
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (e == null) continue;
                    string rawKey = e.Key;
                    string dir = e.AbsolutePath;
                    // 键必须走官方归一化语义（\ → /、Trim('/')；不剥 :clipId）
                    string key = null;
                    try { key = Game.Mod.AssetOverlay.NormalizeBundlePath(rawKey); }
                    catch (Exception ex) { Log("NormalizeBundlePath('" + rawKey + "') 异常 " + ex.Message); }
                    if (string.IsNullOrEmpty(key)) { Log("单元 pkg=" + modId + " 跳过空键（raw='" + rawKey + "'）"); continue; }

                    if (desired.ContainsKey(key))
                    {
                        Log("键冲突 key='" + key + "'（后到者 " + modId + "/" + unit.UnitId
                            + " 被忽略，保留先到者）");
                        continue;
                    }
                    desired[key] = dir;
                    Log("收集 pkg=" + modId + " unit=" + unit.UnitId
                        + " rawKey='" + rawKey + "' → key='" + key + "'"
                        + " isVideo=" + e.IsVideoBundle + " hasSegments=" + e.HasSegmentsJson
                        + " dir='" + dir + "'");
                }
            }
            catch (Exception e)
            {
                var inner = e.InnerException ?? e;
                Log("单元 " + unit.UnitId + " CollectOverrides 异常 " + inner.GetType().Name + ": " + inner.Message);
            }
        }
    }
}
