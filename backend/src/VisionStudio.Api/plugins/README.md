# VisionStudio Runtime Plugin Root

V0.59 supports managed signed packages plus two execution isolation modes.

## Managed signed packages (preferred)

Install `.vspkg` files through **Plugins → Plugin Package Manager** or the `/api/plugin-packages/*` APIs. The immutable package versions live under `data/plugin-packages`; only the selected Active Version is materialized into this runtime `plugins/<pluginId>` directory. Managed runtime directories contain `.visionstudio-managed-package` and must not be edited by hand. Upgrades/rollbacks of already loaded code are applied on restart.

## Unmanaged compatibility folders

The V0.53 manual layout remains supported:

```text
plugins/
  sample.math/
    plugin.json
    VisionStudio.Plugin.Sample.dll
    ...dependencies
```

`plugin.json` schema v2 declares package identity, entry assembly, SDK API compatibility and optional entry-assembly SHA-256. The plugin DLL implements `IVisionPluginV2` and references only `VisionStudio.Abstractions`.

The legacy `plugins/MyPlugin/MyPlugin.dll` folder-only layout remains accepted as API-v1 compatibility.

AssemblyLoadContext isolates dependencies; it does not sandbox plugin permissions. Install only trusted code.

Worker-isolated SDK 2.0 packages set `"isolationMode": "WorkerProcess"`; see `PLUGIN_WORKER.md`.

V0.59 WorkerProcess packages may additionally set `workerPoolSize` and `workerSharedMemoryThresholdBytes`. Pool/transport changes for an already active package are restart-only; see `PLUGIN_WORKER.md`.
