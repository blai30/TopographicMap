![Topographic map of a large procedural landscape in the Classic Ink preset](screenshots/topographic-map.png)

# Topographic Map Camera Compositor Effect Shader (Godot 4.7, C#)

A **topographic-map post-process** for Godot. It recolors whatever a camera renders into a **flat, stepped topographic map**: elevation quantized into flat shade steps drawn from a color **gradient** (a 2-stop gradient gives a clean monochrome ink-on-paper look; multi-stop gradients give hypsometric sea-to-peak tints), with a contour line at every step and bold index lines every few steps (optional). No relief or hillshading, just a clean stylized "world map" look. Similar to the world map in *Breath of the Wild*. Ships with a ready camera prefab and presets (Classic Ink, Blueprint, Nautical, Heatmap).

The effect reads **only the depth buffer**, so the scene needs no special materials, and it ships as a `CompositorEffect` you assign to a camera with no extra code.

## Screenshots

### Presets

The addon ships four ready-made looks in `addons/topographic/presets/`. Each image below is the same depth-only render of the island, recolored by a different gradient.

| Classic Ink | Blueprint |
| --- | --- |
| ![Classic Ink preset: warm monochrome contours](screenshots/preset-classic_ink.png) | ![Blueprint preset: pale lines on navy](screenshots/preset-blueprint.png) |

| Nautical | Heatmap |
| --- | --- |
| ![Nautical preset: sea-to-peak hypsometric tints](screenshots/preset-nautical.png) | ![Heatmap preset: blue-to-red elevation heatmap](screenshots/preset-heatmap.png) |

The same map restyles live with the runtime toggles, no scene changes: stepped vs. smooth ramp (`G`), contours on/off (`C`), inverted shades (`I`).

### The first-person demo

Walk the procedural island in first person while the corner minimap renders the world as a live topographic map.

![First-person demo with the topographic minimap and HUD](screenshots/demo.png)

## The product: a reusable addon

The effect lives in **`addons/topographic/`** as a self-contained `CompositorEffect`. Drop that folder into any Godot project, then either instance the ready-made `TopographicCamera3D.tscn` prefab or assign one of the `presets/` compositors to a `Camera3D`'s **Compositor** property, no code required. See **`addons/topographic/README.md`** for install steps, presets, and the full parameter reference. That addon is the point of this repository; everything else exists to showcase it.

## The included demo

The rest of the repo is a small first-person showcase. You walk a procedural island in first person while two top-down orthographic cameras render the world through the effect: one feeds a corner **minimap** that follows you, the other a toggleable fullscreen **world map**. A procedural noise "island" mesh is baked to `assets/terrain.res`, so no external art assets are needed. The demo exists to exercise and demonstrate the addon, not as a product in its own right.

## Why a CompositorEffect, not a `.gdshader`?

The effect is a `CompositorEffect` running a compute shader, not a screen-space `.gdshader` on a fullscreen quad. Every reason serves the "drop in, no code" goal:

- **No scene footprint.** A `CompositorEffect` is a resource you assign to a camera's **Compositor** property. There is no fullscreen quad to add, no `BackBufferCopy` node to wire up, and no material to put on the terrain. A `.gdshader` post-process needs a host mesh or a viewport material that the consuming project has to set up itself, which defeats the point of a self-contained addon.
- **Per-camera, and only while rendering.** Because the effect is bound to a specific camera, it runs only when that camera renders. The demo applies it to the minimap and world-map `SubViewport` cameras while the main first-person camera stays untouched, and it never affects the editor's 3D preview. Scoping a `.gdshader` post-process to only some cameras is far more awkward.
- **Direct buffer access at a chosen pipeline stage.** The effect reconstructs world height from the depth buffer and recolors the color buffer at the `PostTransparent` stage. A `CompositorEffect` gives documented, direct access to those buffers at the exact stage you pick, rather than relying on `SCREEN_TEXTURE` / `DEPTH_TEXTURE` reads and draw-order tricks.

The tradeoff is that a compute shader has no fragment derivatives (no `fwidth`), so contour-line width is reconstructed by sampling neighbor texels, and the effect requires the **Forward+** or **Mobile** renderer (the **Compatibility** renderer handles depth-texture access differently and is not supported).

## Requirements

- **Godot 4.7, .NET / C# edition** (the build that supports C#)
- **.NET SDK 10.0** (the project targets `net10.0`)
- The **Forward+** or **Mobile** renderer (depth-texture access is required; the Compatibility renderer is not supported)

## Run the demo

1. Open the Godot 4.7 .NET editor and **Import** this folder (`project.godot`).
2. Let it build the C# assembly once (or click **Build** / run `dotnet build`).
3. Press **Play** (F5). `Demo/scenes/Demo.tscn` is the main scene.

## Controls

First person:

| Input        | Action                          |
|--------------|---------------------------------|
| WASD         | Move                            |
| Shift        | Sprint                          |
| Space        | Jump                            |
| Mouse        | Look                            |
| Esc          | Release / recapture the cursor  |
| M / Tab      | Open the world map              |

World map (while open): **drag** to pan, **wheel** to zoom, **M** / **Tab** to close. Shader toggles (affect both maps):

| Key | Toggle                          |
|-----|---------------------------------|
| V   | Topo shader on/off (off = lit)  |
| C   | Contour lines                   |
| B   | Bold major (index) contours     |
| G   | Stepped vs. smooth ramp         |
| I   | Invert the color-to-elevation   |

## How the demo is wired

- `Demo/scripts/TerrainGenerator.cs` builds an island mesh from `FastNoiseLite` with a radial falloff (world Y roughly **-10 to +67**), returning a `TerrainBake`. Its `TerrainSettings` knobs let the same generator bake a larger, higher-fidelity island for the marketing map.
- `Demo/scripts/TerrainBaker.cs` (a `[Tool]` node in `Demo/scenes/BakeTerrain.tscn`) bakes that mesh to `Demo/assets/terrain.res` and fits the effect's elevation range on `addons/topographic/topographic_compositor.tres`.
- `Demo/scenes/Demo.tscn` is authored with the terrain, light, player, the map `SubViewport`s, and the HUD. `Demo/scripts/Demo.cs` handles the minimap follow and the shader toggles, and owns a `WorldMapView` that drives the world-map overlay; terrain collision is a baked `HeightMapShape3D` referenced directly by the scene.
- `addons/topographic/TopographicEffect.cs` is the `CompositorEffect` assigned to both map cameras via `topographic_compositor.tres`. It runs `addons/topographic/topographic.glsl`, a compute shader that reads only the depth buffer, reconstructs each pixel's world height, normalizes it between `MinElevation` and `MaxElevation`, quantizes it into `Levels` flat shade steps colored by sampling the effect's `Gradient` (low elevation = the gradient's left end, high = its right end), and draws a contour line in `ContourColor` at every step (bold index lines every `MajorEvery` steps).

## Tuning the effect

The shipped defaults are the C# property initializers on `TopographicEffect`; edit them there or on the effect inside `addons/topographic/topographic_compositor.tres`. Pick a different starting look by assigning one of the `presets/` compositors. Full reference in the addon's own README. Key points:

- **Color**: `Gradient` is the single source of band color. Its left end colors the lowest elevation, its right end the highest. A 2-stop gradient gives a clean monochrome look; add stops for hypsometric tints. Edit it with Godot's gradient editor.
- **Lines**: `ContourColor` is the contour-line color; keep it distinct (usually darker) from the gradient bands it overlays. `MajorEvery`, `MinorWidthPx`, `MajorWidthPx`, `MinorFade`, and the two opacities control the contours. `ContoursEnabled` off gives pure flat bands; `MajorContoursEnabled` off drops just the bold index lines.
- **Steps**: `Levels` sets how many flat shades / contour lines there are.
- **Ramp**: `SmoothRamp = true` gives a continuous gradient (the G key toggles this at runtime). `InvertRamp = true` samples the gradient from the far end.
- **Range**: `MinElevation` / `MaxElevation` must match your terrain's height range. Re-baking sets these automatically from the mesh.

The map cameras' **Near/Far** must bracket all terrain (nothing touching either plane) so depth maps cleanly to height.
