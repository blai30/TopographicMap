# Topographic Map (Godot 4.7+)

An all-GPU topographic map post-process for Godot: a depth-derived height buffer,
hypsometric (elevation) tint, and crisp constant-width contour lines, drawn in one
shader with no CPU work and no bake step.

## Requirements

- Godot 4.7+, **.NET / C# edition** (the compositor effect is a C# script).
- **Forward+** renderer (the producer uses a `CompositorEffect` with compute shaders).

## How it works

1. An orthographic top-down `Camera3D` renders the terrain into a `SubViewport`.
2. A `TopographicCompositorEffect` on that camera runs two compute passes over the depth
   buffer:
   - a height pass that writes `R` = normalized world height and `G` = coverage mask into
     the `RGBA16F` color image (`B`/`A` unused), and
   - a seed pass that runs per-cell marching squares and writes the contour segment
     crossing each grid cell (both endpoints, in UV) into a separate persistent `RGBA32F`
     texture, one `(x0, y0, x1, y1)` per cell. Both passes are GPU-only and need no
     committed asset.
3. A `ColorRect` running the `topographic_style` shader samples the height buffer and the
   segment texture over a pan/zoom window. For each pixel it takes the exact
   point-to-segment distance to the nearest cell's contour, so it draws crisp
   constant-width anti-aliased lines at any zoom, plus the stepped hypsometric tint. The
   line's elevation is read from the local height, so nothing is stored per line.

The look (palette, height range, contour interval, line widths) lives on each map's
`ShaderMaterial` as shader parameters; the compositor keeps its own
`HeightMin`/`HeightMax`/`ContourInterval` for the seed pass.

## Install

1. Copy the `addons/topographic/` folder into your project (Forward+, C# edition).
2. Build the C# assembly once.
3. Enable the plugin in Project Settings > Plugins (optional; `TopographicCompositorEffect`
   is a `[GlobalClass]` and usable without enabling it).

## Usage

1. Put the terrain you want mapped on a dedicated render layer.
2. Create a `SubViewport` with `use_hdr_2d` enabled and an orthographic `Camera3D`
   looking straight down, its `cull_mask` set to the terrain layer. Give the camera a
   linear-tonemap `Environment` (so stored height is not distorted) and set its
   `near`/`far`/`size` to bracket the terrain.
3. Create a `Compositor`, add a `TopographicCompositorEffect`, set its
   `HeightMin`/`HeightMax`/`ContourInterval` and the camera-rig params (`CameraY`,
   `NearPlane`, `FarPlane`, `DepthReversed`), and assign the compositor to the top-down
   camera.
4. Add a `ColorRect` wherever you want a map and give it a `ShaderMaterial` running
   `topographic_style.gdshader`. Set the look params on that material: `color_ramp`
   (a `GradientTexture1D`), `height_min`/`height_max`, `contour_interval`, `major_every`,
   the line widths (`minor_width_px`/`major_width_px`), and `contour_darken`. Keep
   `height_min`/`height_max`/`contour_interval` in sync with the compositor.
5. From code, bind `height_buffer` to the `SubViewport`'s `ViewportTexture` and `segments`
   to the compositor's `SegmentTexture`.
6. Each frame, set `window_center`/`window_span` to the buffer-UV window you want to show
   (a player-centered window for a minimap, a zoom/pan window for a world map) and set
   `px_per_uv = colorRect.Size.X / span` so the line width stays constant in screen pixels
   at any zoom. See `TopoDemo/scripts/MapUi.cs` for a worked example.

No bake, no `.res` to regenerate, no per-frame CPU cost. Change the terrain and the
lines follow automatically the next time the buffer renders.

## Gradient presets

The material's `color_ramp` parameter takes a `GradientTexture1D` that maps elevation
(`height_min`..`height_max`) to color. Ready-made presets live in `gradients/`:
`hypsometric_classic`, `hypsometric_deep`, `hypsometric_atlas`, `alpine`,
`sepia_vintage`, `grayscale`, `viridis`, `blueprint`, `heatmap`, and `nautical`. Drop
one into the `color_ramp` slot on the material, or duplicate and edit it. The same ramp
colors both the bands and the contour lines, so the map stays consistent.

## License

MIT. See `LICENSE`.
