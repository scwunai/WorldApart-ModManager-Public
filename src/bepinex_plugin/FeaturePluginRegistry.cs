using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using LocalModManager.Abstractions;

namespace LocalModManager
{
    internal sealed class ManagedFeature
    {
        internal string OwnerGuid;
        internal IManagedFeaturePlugin Feature;
    }

    /// <summary>
    /// Discovers BepInEx plugins that implement <see cref="IManagedFeaturePlugin"/> so the
    /// MOD page can show them as switchable rows.
    ///
    /// v14 (audit 6.5): the IL2CPPChainloader <c>Type</c> is resolved **once** and cached.
    /// Refresh() runs on the page's 1 Hz throttled branch, and the old code walked
    /// AppDomain.GetAssemblies() (154 interop assemblies) plus one Assembly.GetType call
    /// each, every second. Only the plugin *list* is rebuilt now - reflection over the
    /// (small) plugin table stays, the assembly sweep does not.
    /// </summary>
    internal static class FeaturePluginRegistry
    {
        private static readonly List<ManagedFeature> Items = new List<ManagedFeature>();
        internal static IReadOnlyList<ManagedFeature> Current { get { return Items; } }

        private static Type _chainloaderType;
        private static bool _chainloaderSearched;

        /// <summary>Resolve IL2CPPChainloader's Type at most once per process.</summary>
        private static Type ChainloaderType()
        {
            if (_chainloaderSearched) return _chainloaderType;
            _chainloaderSearched = true;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = assembly.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader");
                    if (t != null) { _chainloaderType = t; break; }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning("[FEATURES] chainloader type lookup failed: " + e.GetType().Name + ": " + e.Message);
            }
            return _chainloaderType;
        }

        internal static void Refresh()
        {
            Items.Clear();
            try
            {
                object infos = null;
                var chainloader = ChainloaderType();
                if (chainloader != null)
                {
                    object instance = null;
                    var singleton = chainloader.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (singleton != null) instance = singleton.GetValue(null, null);
                    if (instance == null)
                    {
                        var instanceField = chainloader.GetField("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (instanceField != null) instance = instanceField.GetValue(null);
                    }
                    infos = Read(instance, "Plugins");
                }
                if (infos == null)
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var fallback = assembly.GetType("BepInEx.Bootstrap.Chainloader");
                        if (fallback == null) continue;
                        var prop = fallback.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (prop != null) infos = prop.GetValue(null, null);
                        if (infos == null)
                        {
                            var field = fallback.GetField("PluginInfos", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            if (field != null) infos = field.GetValue(null);
                        }
                        break;
                    }
                }
                var entries = infos as IEnumerable;
                if (entries == null) return;
                foreach (var entry in entries)
                {
                    object info = entry;
                    object value = Read(entry, "Value");
                    if (value != null) info = value;
                    if (info == null) continue;
                    var feature = Read(info, "Instance") as IManagedFeaturePlugin;
                    if (feature == null || String.IsNullOrWhiteSpace(feature.FeatureId)) continue;
                    object metadata = Read(info, "Metadata");
                    Items.Add(new ManagedFeature { OwnerGuid = Convert.ToString(Read(metadata, "GUID")) ?? "", Feature = feature });
                }
            }
            catch (Exception e) { Plugin.Logger.LogWarning("[FEATURES] discovery failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private static object Read(object instance, string name)
        {
            if (instance == null) return null;
            var type = instance.GetType();
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null) return prop.GetValue(instance, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return field == null ? null : field.GetValue(instance);
        }

        internal static bool SetEnabled(string id, bool enabled)
        {
            foreach (var item in Items)
            {
                if (item.Feature.FeatureId != id) continue;
                try { item.Feature.SetEnabled(enabled); return true; }
                catch (Exception e) { Plugin.Logger.LogWarning("[FEATURES] " + id + " request failed: " + e.GetType().Name + ": " + e.Message); return false; }
            }
            return false;
        }
    }
}
