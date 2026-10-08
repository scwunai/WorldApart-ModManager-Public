namespace LocalModManager.Abstractions
{
    /// <summary>
    /// Lifecycle a managed feature reports through <see cref="IManagedFeaturePlugin.State"/>.
    /// The manager only ever sees this enum value; it never drives the transitions itself.
    /// </summary>
    public enum FeaturePluginState { Stopped, Starting, Running, Stopping, Failed }

    /// <summary>
    /// Contract for a BepInEx plugin that wants a switch row on the MOD管理 page.
    /// Toggling the row calls <see cref="SetEnabled"/>; the plugin decides what that
    /// means (the MOD manager never unloads a plugin and never writes its config).
    ///
    /// Versioning policy: see docs/BepInEx-Feature-Plugin-API.md.
    /// </summary>
    public interface IManagedFeaturePlugin
    {
        string FeatureId { get; }
        string DisplayName { get; }
        string Description { get; }
        string FeatureVersion { get; }
        bool DesiredEnabled { get; }
        FeaturePluginState State { get; }
        string StatusMessage { get; }
        void SetEnabled(bool enabled);
    }
}
