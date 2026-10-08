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

## Versioning policy

The contract surface is **frozen**. Never rename, reorder, retype or change the meaning
of an existing member of `IManagedFeaturePlugin` or `FeaturePluginState`; a compiled
feature plugin binds to those by name and signature, and BepInEx assemblies are never
unloaded, so a break cannot be repaired at runtime.

Additive evolution only:

- **A new capability is a new interface**, declared in the same assembly
  (e.g. `IManagedFeaturePluginV2`). The manager probes for it with `as` / `is` and falls
  back to the older surface when a plugin does not implement the newer one. Plugins that
  only implement the older interface keep working unchanged.
- **Adding a member to an existing interface is a breaking change**, even though it looks
  additive: every already-compiled plugin that implements it would immediately fail to
  load. Do not do it.
- `FeatureId` is the identity used to key UI rows and to route `SetEnabled`; its meaning
  and uniqueness must not change. `DisplayName`, `Description`, `FeatureVersion`,
  `StatusMessage` are presentation-only and may be improved, but a plugin must stay
  correct when the manager renders them differently.
- Ship a new interface together with a new assembly `Version` for
  `LocalModManager.Abstractions.dll`, so the deployed contract can be told apart.

Cross-repository alignment: the interface source is kept **byte-for-byte aligned** with
the IndexTTS repository (see below). Any change to this contract must be versioned and
delivered to the IndexTTS author in the same change.

## IndexTTS reference implementation

The first implementation is in the A1 IndexTTS repository under `src/managed-feature-api/` and `src/Plugin.cs`. Its `Stage3Mvp.Enabled` config remains the source of desired state, so standalone operation keeps working. The interface source is intentionally kept byte-for-byte aligned in both repositories; changes to the API should be versioned and delivered to the IndexTTS author at the same time.
