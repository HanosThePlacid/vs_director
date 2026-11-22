# VS Director

See the [contributing guide](CONTRIBUTING.md) for pull requests, localization, saved data compatibility, and voice integration.

Game compatibility: the same package supports Vintage Story 1.22.3 and 1.23.0-pre.1, with VSImGui 1.2.7. The 1.23 adaptation covers replay serialization and block entities, camera FOV, offscreen terrain visibility, native text inputs, and workspace picking. Automated checks run the same DLL against both versions; actual multiplayer sessions and the complete in-game recording/export workflow still need manual verification. Later 1.23 builds require revalidation.

VS Director can be installed on the client without installing it on the server. The client still requires VSImGui 1.2.7. Personal cameras, paths, following loaded entities, the creation workspace, replay recording/playback and video export can operate locally. Recordings capture only chunks, entities and events received by the client; moving a camera does not load distant server terrain in this mode.

Installing VS Director on the server enables server-managed permissions, remote camera chunks/replay preloading, remote tracking and sound relay, and actor performance recording that temporarily moves the player or changes their collision dimensions. Server installation retains the declared VSImGui dependency. When the server provides the Director channel, its permission rules apply; a pending or delayed permission reply does not enable unrestricted local access. The package remains universal so the optional server components can run; `requiredOnServer: false` allows the client to remain enabled on servers without it. Servers that install the mod still require connecting clients to install it.

- Director workspace with saved camera positions, free cameras, and player or entity follow cameras.
- Camera paths with keyframe editing, curves, trimming, easing, automatic Bezier interpolation, looping, reverse playback, and speed controls.
- Client replay recording of entities, animations, held items, particles, positional audio, loaded chunks, and block changes.
- Replay playback with pause, seeking, speed adjustment, free cameras, and first-person or third-person actor views.
- Replay timeline editing with camera paths, scene elements, visual controls, and video export.
- Live offscreen recording from fixed, path, or follow cameras through OBS or directly to FFmpeg MP4.
- Dedicated OBS source plugin for offscreen video and spatial camera audio.
- Personal camera positions, server permissions, and remote camera chunk loading.
- Voice integration API for spatial PCM audio capture, replay, and export.
- English and Simplified Chinese interfaces.
- Blueprint projections: Add → Projection imports Buildwright `.vsbp` or native block-schematic `.json` files from the local library, game/mod templates, or a chosen file. Imports copy the source into the project and do not place real world blocks.
- Select a projection and use its context menu → Preview projection for an isolated rotating view. Drag to orbit, middle-drag to pan, and scroll to zoom; the right inspector controls lighting, background and GIF/APNG export (requires FFmpeg). The Tools panel exits preview and restores the previous timeline position.
- Capture a loaded world region into the Buildwright library, rotate/mirror complete projections, and attach editable particle effects. Automatic torch/fire/firepit particles are reference effects based on saved block variants, not live fuel or weather simulation.

Creation layout: Objects and Content are separate docks at the upper and lower left; Tools and Properties occupy the right, with the game viewport in the center and timeline below. All windows remain freely movable and dockable. The default layout has no editor tab stacks. Content navigation remembers each object’s section, emitter/block selection and scroll position; its search locates native and module parameters. Optional View/Bones/IK/Actions windows initially float. Layout version 4 migrates the creation docks once; subsequent opens preserve user placement.

Particle editing: select an emitter or child in Content, then choose emission/timing, shape/binding, motion, color/size, material, geometry, environment, evolution or child events. The inspector shows only that task. Effect settings and presets/source are separate entries; curves, gradients and JSON open on demand. Batch scope is explicit and resets on object changes.

Blueprint editing: Content provides Blocks, Region, growth, individual glass/edge layers, particles, physics, preview and output entries. Blocks opens a searchable list with a Back to contents action; the Objects dock remains available. Each material layer and its proportion share one inspector page; embedded particle schemes reuse the particle editor. Blocks support name/coordinate filtering, Ctrl/Shift selection, viewport picking and move/rotate/scale handles (including the isolated preview). The inspector groups cell edits, ranges, growth, materials, edge particles, physics, keys and files. Copy/delete/restore, integer region export, and extract/cut into independent Director presets are supported. Region capture is a workbench tool: right-drag the corners, release to confirm, left-click to clear, Esc to leave. It saves to the Buildwright library without placing blocks.

Growth includes appearance, seeded fill, fall/rain and two six-direction waves; five glass replacement layers and four priority edge layers use native block resources. Edge particles distribute a selected native/custom scheme over the twelve block edges, with count/speed/interval, tick lifetime override, color/rainbow/glow and outlines. Original scheme gravity, motion and child particles use the shared particle simulator. Beam/lightning/trail effects are attached as ordinary child effects rather than edge schemes. Automatic torch/fire particles follow edited/deleted source cells. The timeline groups structure parameters, step resource choices and optional per-block transform/visibility/override tracks; disabling per-block keys preserves the data. First transform keys use absolute local values; additive mode is available explicitly.

Visual physics uses a fixed 120 Hz simulation, terrain collision, separation, drag, spin, impact, gravity and bounce with seek snapshots and cancellation. It does not simulate block-to-block stacking. Physics is limited to 6,000 source/copy cells per structure; particle limits remain 6,000 per effect and 24,000 per project, including outlines. Geometry rebuilds and world capture are sliced across frames; the block list is virtualized and cached. Large animated structures still cost more than static structures.

Native VSBP/JSON exports contain static integer-position blocks only and reject individual display rotation/scale rather than silently dropping it. Director structure presets preserve source assets, cell overrides, clips, tracks, particle schemes and textures; the picker can browse/import them. External particle binding targets must belong to the exported hierarchy, and performance clips must be baked first. Existing version-1 projections require an explicit upgrade; opening does not migrate them.

Blueprint limitations: Embedded entities and surface decorations are retained but not displayed; missing or unsupported meshes are reported and block export. Native liquid geometry uses a static preview material, without the world's refraction/wave shader. Actual game/mod-specific visual fidelity still needs in-game verification.

Build release packages against the 1.22.3 SDK to retain a single binary for both game versions. `modinfo.json` therefore keeps `game: 1.22.3` as the minimum dependency. Run the repeatable compatibility checks with:

```powershell
./tools/TestGameCompatibility.ps1 -BaselineGamePath 'path/to/1.22.3' -NewGamePath 'path/to/1.23.0-pre.1' -VSImGuiPath 'path/to/vsimgui_1.2.7'
```

This builds once, checks native member references, and runs the full PatchProbe with each game's assemblies and assets in separate directories. Logs are written to `artifacts/compat*-probe.log`; no mod deployment is performed.
