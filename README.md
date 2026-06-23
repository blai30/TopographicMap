# Topographic Camera Shader (Godot 4.7, C#)

A distributable, drop-in **topographic-map post-process** for Godot. It recolors whatever a camera renders into a **flat, monochrome, stepped topographic map**: a single ink hue on a paper background, elevation quantized into flat shade steps (light = high, dark = low) with a contour line at every step and bold index lines every few steps. No relief or hillshading, just a clean stylized "world map" look.

The effect reads **only the depth buffer**, so the scene needs no special materials, and it ships as a `CompositorEffect` you assign to a camera with no extra code.

## The product: a reusable addon

The effect lives in **`addons/topographic/`** as a self-contained `CompositorEffect`. Drop that folder into any Godot project and assign `topographic_compositor.tres` to a `Camera3D`'s **Compositor** property, no code required. See **`addons/topographic/README.md`** for install steps and the full parameter reference. That addon is the point of this repository; everything else exists to showcase it.

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
3. Press **Play** (F5). `scenes/Main.tscn` is the main scene.

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

- `scripts/TerrainGenerator.cs` builds an island mesh from `FastNoiseLite` with a radial falloff (world Y roughly **-10 to +67**), returning a `TerrainBake`.
- `scripts/TerrainBaker.cs` (a `[Tool]` node in `scenes/BakeTerrain.tscn`) bakes that mesh to `assets/terrain.res` and fits the effect's elevation range on `addons/topographic/topographic_compositor.tres`.
- `scenes/Main.tscn` is authored with the terrain, light, player, the two map `SubViewport`s, and the HUD. `scripts/TopographicCameraShader.cs` handles terrain collision, the minimap follow, the world-map overlay, and the shader toggles.
- `addons/topographic/TopographicEffect.cs` is the `CompositorEffect` assigned to both map cameras via `topographic_compositor.tres`. It runs `addons/topographic/topographic.glsl`, a compute shader that reads only the depth buffer, reconstructs each pixel's world height, normalizes it between `MinElevation` and `MaxElevation`, quantizes it into `Levels` flat shade steps (high = light `PaperColor`, low = dark `InkColor`), and draws a contour line at every step (bold index lines every `MajorEvery` steps).

## Tuning the effect

The shipped defaults are the C# property initializers on `TopographicEffect`; edit them there or on the effect inside `addons/topographic/topographic_compositor.tres`. Full reference in the addon's own README. Key points:

- **Hue**: `InkColor` (also the contour-line color, keep it dark) and `PaperColor`. For a black map, set `InkColor` near black and `PaperColor` light.
- **Steps**: `Levels` sets how many flat shades / contour lines there are. `FillLow` / `FillHigh` set the low and high ends of the fill ramp. Keep `FillLow` above ~0.2 so the fill stays lighter than the contour ink, otherwise contours vanish in low areas.
- **Lines**: `MajorEvery`, `MinorWidthPx`, `MajorWidthPx`, `MinorFade`, and the two opacities control the contours. `ContoursEnabled` off gives pure flat bands; `MajorContoursEnabled` off drops just the bold index lines.
- **Ramp**: `SmoothRamp = true` gives a continuous gradient (the G key toggles this at runtime). `InvertRamp = true` flips high = dark.
- **Range**: `MinElevation` / `MaxElevation` must match your terrain's height range. Re-baking sets these automatically from the mesh.

The map cameras' **Near/Far** must bracket all terrain (nothing touching either plane) so depth maps cleanly to height.
