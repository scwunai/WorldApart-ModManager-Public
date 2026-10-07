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

    internal static class FeaturePluginRegistry
    {
        private static readonly List<ManagedFeature> Items = new List<ManagedFeature>();
        internal static IReadOnlyList<ManagedFeature> Current { get { return Items; } }

        internal static void Refresh()
        {
            Items.Clear();
            try
            {
                Type chainloader = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    chainloader = assembly.GetType("BepInEx.Unity.IL2CPP.IL2CPPChainloader");
                    if (chainloader != null) break;
                }
                object infos = null;
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
