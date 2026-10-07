# BepInEx feature plugin API

The settings page has two independent sections: native `mod.json` packages and BepInEx feature plugins. The manager discovers only plugins that implement `LocalModManager.Abstractions.IManagedFeaturePlugin`; it does not infer a switch for arbitrary DLLs and does not write plugin configuration.

## Contract

`src/bepinex_plugin/abstractions/LocalModManager.Abstractions.csproj` builds `LocalModManager.Abstractions.dll` for .NET 6. Install that DLL once in `BepInEx/plugins/`, beside the manager plugin. Feature plugins reference the assembly but do not embed its types. The IndexTTS standalone package also ships the same dependency, so IndexTTS loads without the manager.

Implement the interface on the BepInEx plugin instance:

- `FeatureId`: stable unique ID, preferably the plugin GUID.
- `DisplayName`, `Description`, `FeatureVersion`: UI metadata.
- `DesiredEnabled`: persisted user intent.
- `State`: `Stopped`, `Starting`, `Running`, `Stopping`, or `Failed`.
- `StatusMessage`: current readiness or failure detail.
- `SetEnabled(bool)`: accept quickly and perform slow work asynchronously. Calls should be idempotent. The implementation owns persistence and cleanup.

Assemblies remain loaded. Disabling a feature must stop its feature activity and owned processes without attempting to unload the BepInEx plugin assembly. The manager refreshes metadata/state while its settings page is visible and tolerates the feature plugin loading before or after the manager.

## IndexTTS reference implementation

The first implementation is in the A1 IndexTTS repository under `src/managed-feature-api/` and `src/Plugin.cs`. Its `Stage3Mvp.Enabled` config remains the source of desired state, so standalone operation keeps working. The interface source is intentionally kept byte-for-byte aligned in both repositories; changes to the API should be versioned and delivered to the IndexTTS author at the same time.
