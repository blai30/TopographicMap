# Topographic Map (Godot 4.7+)

An all-GPU topographic map post-process for Godot: a depth-derived height buffer,
hypsometric (elevation) tint, and crisp constant-width contour lines, drawn in one
shader with no CPU work and no bake step.

## Requirements

- Godot 4.7+, **.NET / C# edition** (the compositor effect is a C# script).
- **Forward+** renderer (the producer uses a `CompositorEffect` with compute shaders).

## How it works

1. An orthographic top-down `Camera3D` renders the terrain into a `SubViewport`.
2. A `TopographicCompositorEffect` on that camera turns the depth buffer into a single
   `RGBA16F` data buffer: `R` = normalized height, `G` = coverage mask, `B` = distance
   to the nearest contour line, `A` is unused. The contour distance is built on the GPU
   with a jump-flood signed-distance pass, so it is robust on flat ground (no per-pixel
   gradient) and needs no committed asset.
3. A `TopographicView` (a `ColorRect` with the `topographic_style` shader) samples that
   buffer over a pan/zoom window and draws the stepped tint plus constant-width
   anti-aliased lines. The line's elevation is read from the local height, so nothing
   needs to be stored per line.

Everything that defines the look lives in one `TopographicSettings` resource.

## Install

1. Copy the `addons/topographic/` folder into your project (Forward+, C# edition).
2. Build the C# assembly once.
3. Enable the plugin in Project Settings > Plugins (optional; the nodes are
   `[GlobalClass]` and usable without enabling it).

## Usage

1. Put the terrain you want mapped on a dedicated render layer.
2. Create a `SubViewport` with `use_hdr_2d` enabled and an orthographic `Camera3D`
   looking straight down, its `cull_mask` set to the terrain layer. Give the camera a
   linear-tonemap `Environment` (so stored height is not distorted) and set its
   `near`/`far`/`size` to bracket the terrain.
3. Create a `TopographicSettings` resource (`.tres`): set the `ColorRamp`
   (a `GradientTexture1D`), the `HeightMin`/`HeightMax` range, `ContourInterval`,
   `MajorEvery`, and the line widths/darken.
4. Create a `Compositor`, add a `TopographicCompositorEffect`, assign your
   `TopographicSettings` to it (it reads `HeightMin`/`HeightMax`/`ContourInterval`),
   set the camera-rig params (`CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`), and
   assign the compositor to the top-down camera.
5. Add a `TopographicView` node (a `ColorRect`) wherever you want a map. Assign the same
   `TopographicSettings`, and set its `HeightBuffer` to the `SubViewport`'s
   `ViewportTexture` (or assign it in code). Call `view.Apply()` after setting the
   buffer.
6. Each frame, call `view.SetWindow(center, span)` with the buffer-UV window you want to
   show (a player-centered window for a minimap, a zoom/pan window for a world map). The
   view keeps the line width constant in screen pixels at any zoom.

No bake, no `.res` to regenerate, no per-frame CPU cost. Change the terrain and the
lines follow automatically the next time the buffer renders.

## Gradient presets

`TopographicSettings.ColorRamp` takes a `GradientTexture1D` that maps elevation
(`HeightMin`..`HeightMax`) to color. Ready-made presets live in `gradients/`:
`hypsometric_classic`, `hypsometric_deep`, `hypsometric_atlas`, `alpine`,
`sepia_vintage`, `grayscale`, `viridis`, `blueprint`, `heatmap`, and `nautical`. Drop
one into the `ColorRamp` slot, or duplicate and edit it. The same ramp colors both the
bands and the contour lines, so the map stays consistent.

## License

MIT. See `LICENSE`.
