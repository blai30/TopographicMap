# Topographic Map: Architecture and Engineering Notes

This document captures how the topographic map works, the decisions behind it, and the gotchas worth knowing before changing anything. It reflects the state after the move to an all-GPU signed-distance-field (SDF) contour pipeline.

## What it is

A topographic map rendered from any 3D terrain the game shows: a hypsometric (elevation-colored) tint with contour lines, shown as a corner minimap and a full-screen pan/zoom world map. The reusable system lives in `addons/topographic/`; the demo wiring lives in `TopoDemo/`.

## High-level architecture

The map is produced on the GPU and consumed by one shader. There is no CPU contour work and no baked contour asset.

```
PRODUCER (on the orthographic map camera, RGBA16F 2048x2048 SubViewport)
  TopDownCamera (ortho, top-down)  -->  depth buffer
  TopographicCompositorEffect (compute, PreTransparent) runs, in one render callback:
    1. height pass:   depth -> R = normalized height, G = coverage mask
    2. seed pass:     band edges (floor(h/interval) differs from a neighbor) -> sub-texel
                      crossing positions as jump-flood seeds
    3. jump flood:    ~11 ping-pong passes propagate the nearest seed
    4. composite:     B = distance (UV units) to the nearest contour line
  Result: one buffer, R = height, G = mask, B = contour distance. (A is unused; an
  opaque viewport forces alpha to 1, so it cannot carry data.)

CONSUMER (per map: minimap, world map)
  TopographicView (a ColorRect with topographic_style.gdshader) samples that one buffer
  over its window (window_center, window_span) and outputs BOTH:
    - the stepped hypsometric tint (from R), and
    - constant-width anti-aliased contour lines (from B), thresholded by px_per_uv so the
      width is constant in screen pixels at any zoom. The line's level is round(h/interval)
      from the local height, so nothing is stored per line.

MARKER (overlay)
  A constant-size SDF arrow (marker_overlay.gdshader) drawn into a small UI Control on top
  of each map, rotated to the player's heading.
```

The height buffer is the single shared data source, and tint and lines come from the same buffer in the same shader pass, so they are always aligned and move together under pan/zoom.

### Why this shape

- A single shader reading only the raw height buffer cannot draw crisp constant-width lines: a constant-width implicit isoline needs `distance_to_level / screen_space_height_gradient`, which is ill-conditioned on flat ground (the gradient goes to zero, so the line dots and flickers). See Design history.
- Precomputing a contour SDF makes "distance to the nearest line" a robust texture lookup, and constant screen width comes from the zoom uniform (`px_per_uv`), not a per-pixel gradient. Flat ground is then fine.
- Building the SDF on the GPU (jump flood) means no CPU readback, no bake step, no committed contour asset, and no staleness: change the terrain and the lines follow the next time the buffer renders.

## Component reference

Reusable addon (`addons/topographic/`):

- `TopographicCompositorEffect.cs` + `topographic.glsl` (height) + `contour_seed.glsl` + `contour_jfa.glsl` + `contour_composite.glsl`: the producer. A `CompositorEffect` at `PreTransparent` that runs the height pass then the seed/jump-flood/composite passes, using two `RGBA32F` ping-pong textures it owns (created lazily, sized to the buffer, freed on predelete). Exported params: `HeightMin`, `HeightMax`, `ContourInterval` (used by the seed pass), plus camera-rig params `CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`.
- `topographic_style.gdshader`: the unified consumer shader. Samples `height_buffer` over `window_center`/`window_span`; outputs the stepped tint from `R` and the contour line from `B`. Uniforms: `color_ramp`, `height_min`, `height_max`, `contour_interval`, `window_center`, `window_span`, `px_per_uv`, `major_every`, `minor_width_px`, `major_width_px`, `contour_darken`.
- `TopographicSettings.cs` (Godot `Resource`, `[GlobalClass]`): the single source of truth for the look: `ColorRamp` (a `GradientTexture1D`), `HeightMin`, `HeightMax`, `ContourInterval`, `MajorEvery`, `MinorWidthPx`, `MajorWidthPx`, `ContourDarken`.
- `TopographicView.cs` (Godot `ColorRect`, `[Tool]`): one map view. `Apply()` pushes `Settings` (and the `HeightBuffer`) into its material; `SetWindow(center, span)` sets the window and `px_per_uv = Size.X / span` so the line width is constant in screen pixels.
- `marker_overlay.gdshader` (canvas_item): SDF arrow for the player marker, `fwidth`-antialiased, drawn into a small UI `Control` rotated to the player's heading.

Demo (`TopoDemo/`):

- `scripts/MapUi.cs`: orchestration. Sets each `TopographicView`'s `HeightBuffer` to the `MapView` viewport texture and calls `Apply()` at `_Ready`; owns the pan/zoom window state and drives `Minimap.SetWindow`/`WorldMapImage.SetWindow` and the two marker overlays each frame. The marker heading comes from the player `Body` node's yaw (`MarkerRotation()`, currently `-PlayerBody.GlobalRotation.Y`; flip the sign if the arrow points backward).
- `scenes/Demo.tscn`: the `MapView` SubViewport + `TopDownCamera` + compositor, the two map `TopographicView` `ColorRect`s (each with a `Marker` child overlay), the HUD, and the shared `assets/topographic_settings.tres`.
- `scripts/TerrainBaker.cs`: edit-time, headless, CPU tool that bakes `heightmap.exr` (512x512) and `terrain_collision.res`. Not shipped in the running game. It does NOT touch contours (those are a runtime GPU derivation). Run with `godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs`.

## Hard rules and constraints

- The topographic effect lives only on the orthographic map camera (via the `Compositor` on `TopDownCamera`). It must never affect the main gameplay view or the editor.
- The contour SDF derives from the camera's height buffer, not a heightmap, so it works for any rendered geometry. Do not couple it to `heightmap.exr`.
- Terrain, heightmap, and collision are static committed files baked at edit time (`TerrainBaker`). No runtime generation of those assets. The contour SDF is a runtime GPU derivation of the height buffer and is exempt.
- No emdash characters (or other AI-tell characters) in committed files, including comments. American English. `//` line comments in C#.
- Coordinate conventions: world to buffer UV is `buffer_uv = world.xz / 1536 + 0.5`. Normalized height for world height `H` is `(H + 40) / 150` (range `[-40, 110]`).

## Design history (why it ended up here)

The contour approach changed several times. The reasoning matters so the dead ends are not retried.

1. The whole styled map (tint + contours + marker) was baked into one fixed 1024x1024 SubViewport texture and the UI magnified it. Magnifying a raster is why everything looked blocky.

2. The map was split into a height buffer (data) plus a per-pixel styling shader run at display resolution. Tint became crisp. Contour lines, drawn implicitly per pixel, did not: a constant-pixel-width implicit line divides distance-to-level by the screen-space height gradient, which approaches zero on near-flat ground, so the line dots, stipples, and flickers. Height smoothing, an analytic gradient, and a flat-area guard each helped but none fully fixed it, because the instability is inherent to implicit isolines on flat terrain.

3. Contour lines became vector geometry: extract isolines with Marching Squares on a CPU readback of the buffer and stroke them as constant-width lines. Smooth and crisp, but it needed a one-time CPU extraction. To remove the startup flash, the field was then baked into a committed `.res`; baking from the heightmap misaligned the lines on gentle slopes (the buffer is the rendered mesh, which differs slightly from the heightmap; on gentle slopes a small height error becomes a large lateral line offset), so the bake had to read the camera buffer.

4. Finally the contours moved entirely onto the GPU as an SDF (this version). The compositor builds a contour distance field from the height buffer with a jump-flood pass, and the unified shader draws constant-width lines by thresholding that distance scaled by the zoom uniform. This eliminates the flat-ground problem (no gradient division), the CPU readback, the bake step, the committed asset, and the staleness, and unifies tint and lines into one shader sampling one buffer. It is the all-GPU realization of "tint and lines in one place."

## Gotchas and obscure details

Godot rendering:

- `use_hdr_2d = true` on the `MapView` SubViewport is required. Without it the viewport texture is 8-bit, which crushes the normalized height to 256 levels and shows as terraced contours in flat areas.
- The map camera needs a linear-tonemap environment override (`Environment_map`, `background_mode = 1`, `tonemap_mode = 0`, the default so Godot does not serialize it). Otherwise the scene's ACES tonemap distorts the stored height values nonlinearly.
- `render_target_update_mode` integer values: `Disabled = 0`, `Once = 1`, `WhenVisible = 2`, `WhenParentVisible = 3`, `Always = 4`. The producer uses `Once` (1): the terrain is static, so the whole compute sequence (height + SDF) runs one frame and stops. Raising it regenerates the SDF automatically for dynamic terrain.
- A `canvas_item` fragment shader cannot use an early `return` (Godot errors). Compute the result in branches and write `COLOR` once.

GPU SDF pipeline:

- The opaque map viewport FORCES the color buffer alpha to 1.0 after the compositor writes it, so the `A` channel cannot carry a payload (an early version stored the level index in `A` and it came back as a constant 1). The line's level is instead recomputed in the shader as `round(h / interval)` from the local height, since a pixel on a line is at that line's elevation.
- The SDF distance in `B` is in UV units (0..~1.4), not texels. To get a constant screen width, scale by `px_per_uv = view_size_px / window_span` (screen pixels per UV unit). Dividing by the buffer resolution instead under-scales by ~2048x and paints the whole map as line color.
- Jump flood needs ping-pong textures and a memory barrier between every pass; the compositor runs all passes in one `ComputeList` with `ComputeListAddBarrier` between dispatches. Each compute pass needs its own uniform set (use `UniformSetCacheRD.GetCache` per shader).
- The seed pass uses a discrete band comparison (`floor(h/interval)` differs from a neighbor), which is robust on flat ground precisely because there is no gradient division. Sub-texel crossing positions keep the lines smooth.

## Performance and load behavior

- The contour SDF is built entirely on the GPU in the compositor: a seed pass, ~11 jump-flood passes, and a composite, over the 2048x2048 buffer. Because the terrain is static (`update_mode = Once`), this runs once at load in a few milliseconds. There is no per-frame cost and effectively no startup flash.
- The consumer is a single canvas shader sampling one texture; pan/zoom only changes uniforms, so an idle or moving map costs one cheap shader.
- There is no CPU contour work, no buffer readback, and no committed contour asset to load or keep in sync.

## Tuning parameters

- All look parameters live on the `TopographicSettings` resource (`assets/topographic_settings.tres` in the demo): `ColorRamp` (the palette, shared by tint and lines), `HeightMin`/`HeightMax`, `ContourInterval`, `MajorEvery`, `MinorWidthPx`, `MajorWidthPx`, `ContourDarken`. Change them once and both maps follow.
- The compositor effect reads `HeightMin`/`HeightMax`/`ContourInterval` (assign the same `TopographicSettings`, or keep its exports in sync) plus the camera-rig params.
- The palette is the `ColorRamp` `GradientTexture1D`. The demo uses `addons/topographic/gradients/hypsometric_deep.tres`; the addon ships several preset gradients in that folder.
- The marker overlays expose `marker_color` and `outline_color` (on the material) and `MarkerScreenSize` (on `MapUi`).

## Known limitations and follow-ups

- The contour SDF resolution is the buffer resolution (2048). Lines are crisp and anti-aliased at any zoom (an SDF upscales well), but the line path follows texel-resolution seeds, which is the terrain's effective detail limit anyway.
- The producer reconstructs height from depth for an orthographic projection; a perspective map camera would need the reconstruction adjusted.
- The marker is a constant-size, `fwidth`-antialiased SDF arrow UI overlay on each map (`marker_overlay.gdshader`). Verify the heading sign once in-editor.
