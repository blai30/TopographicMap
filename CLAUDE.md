# CLAUDE.md

All-GPU topographic map for Godot 4.7 (Mono/.NET, C#): an ortho top-down camera's depth buffer becomes a height buffer + per-cell contour-segment texture via a compute compositor effect, and one `canvas_item` shader draws the hypsometric tint plus analytic constant-width contour lines. Reusable addon (`addons/topographic/`) + demo (`TopoDemo/`).

## Hard rules (user-set)

- **No runtime asset generation.** Mesh, heightmap (`.exr`), collision (`HeightMapShape3D` `.res`) are static committed files baked at edit time by `TerrainBaker.cs`. Runtime may wire up effects, never synthesize meshes/images.
- **Justify on merit, not precedent.** "The old project did it this way" is not an argument.

## Build / run

- Build: `dotnet build TopographicMap.csproj`.
- Run: `& $GODOT --path . res://TopoDemo/scenes/DemoMinimap.tscn`. The compositor is compute, so `--headless` will not render it; run non-headless and screenshot to verify GPU work.
- Bake assets: `& $GODOT --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs`.
- README shots: `DemoTerrain.tscn -- banner` or `-- presets`.

## Architecture

Full doc: `docs/topographic-map-architecture.md` (read first); addon `README.md`; the `topographic-map-architecture` memory tracks deltas. Producer `TopographicCompositorEffect` (compute, `PreTransparent`) on the map SubViewport camera runs three passes: height (depth -> normalized height+mask, `RGBA16F`), optional separable blur, seed (per-cell marching squares -> the exact contour segment, `RGBA32F`). Both persistent textures are wrapped in `Texture2DRD`; consumer `topographic.gdshader` samples them over a window and outputs the tint plus exact point-to-segment contour lines (resolution-independent, no SDF/CPU/bake).

- **Script-free addon.** Addon ships only the compositor + compute shaders + `topographic.gdshader` + `marker_overlay.gdshader` + `gradients/`; no binder script. Consumers wire in the inspector: one shared `Texture2DRD` `.tres` assigned to both the compositor's `SegmentTexture`/`HeightTexture` and each material's `segments`/`height_buffer`. `MapUi.cs` only drives the window (`window_center`/`window_span`), markers, and `HasProduced` reveal gating.
- **Single-source elevation model.** The compositor owns `HeightMin`/`HeightMax`/`ContourInterval`; the seed pass bakes `(min,max,interval,1)` into the segment texture's last texel, read back via `texelFetch`. These are not consumer uniforms.
- World->buffer UV = `world.xz/1536 + 0.5`; normalized height = `(H+40)/150` over `[-40,110]`.

## Non-obvious gotchas (cost real time here)

- **Restart the editor after changing C# `[Export]`/`[GlobalClass]` types.** An external `dotnet build` does not reload the editor's loaded assembly; it then runs stale, throws `InvalidCastException` on a changed `[GlobalClass]` at scene load, and on re-save SILENTLY DROPS the unresolvable resource refs from the `.tscn` (destructive).
- **`Texture2DRD` lifetime.** On replace, set `wrapper.TextureRdRid = new` BEFORE freeing the old RID (else the setter hits a freed RID -> double free); on teardown clear the wrapper THEN free. Free RIDs on the render thread (`RenderingServer.CallOnRenderThread`); predelete is main-thread and errors otherwise.
- **First-frame "Uniforms were never supplied for set (1)".** A consumer can draw before the compositor's first render-once, sampling an empty RID. The fix is `volatile bool HasProduced`: the compositor sets it after its first render, consumers gate visibility on it and keep their ColorRects hidden until then (removing the gate re-errors the game every run). The shader treats `seg.x <= 0` as no-contour.
- **Runtime-only map: no editor preview.** The compositor is intentionally not a `[Tool]`, so it never runs in the editor; the consumer ColorRects are saved `visible = false` and the scripts reveal them on `HasProduced` once the game runs. Do not run the compositor or draw a consumer in the editor: the editor's cold-open draw trips `set (1)`/resize churn (Godot #118292).
- **`use_hdr_2d` REQUIRED on the map SubViewport** (8-bit quantizes height into terraced contours), plus a linear-tonemap env override on the map camera (`tonemap_mode=0`) or ACES distorts stored height.
- **Renaming `.glsl`/`.gdshader` outside the editor** stales the `.godot` UID cache and breaks a non-editor load: rename the file + its `.uid`/`.import` sidecars, fix path refs, delete `.godot/uid_cache.bin`, then `godot --headless --import --path .`. Prefer renaming inside the editor.
- `canvas_item` fragment shaders cannot early `return`.
- Bind `height_buffer`/`segments` in the inspector; never hand-write the `ViewportTexture`/`Texture2DRD` ref into a `.tscn`.

## Dead ends (do not retry)

- Implicit single-shader contours (distance/screen-gradient): 0/0 on flat ground, flicker.
- GPU SDF (jump-flood): a precomputed field quantizes and softens lines at zoom; analytic per-cell segments are sharper and simpler. Deleted.
- Point seeds: lines dash at zoom (use per-cell segments).
- `instance uniform` to hide runtime uniforms: does not hide (serializes the same, cannot hold textures). Elevation moved into the metadata texel instead.
- Shared `TopographicSettings` `[GlobalClass]` + per-view `TopographicView` script: dropped for inline material params + inspector wiring.
