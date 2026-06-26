# Topographic Map: Architecture and Engineering Notes

This document captures how the topographic map works, the decisions behind it, and the gotchas worth knowing before changing anything. It reflects the state after the move to analytic GPU vector contour lines.

## What it is

A topographic map rendered from any 3D terrain the game shows: a hypsometric (elevation-colored) tint with contour lines, shown as a corner minimap and a full-screen pan/zoom world map. The reusable system lives in `addons/topographic/`; the demo wiring lives in `TopoDemo/`.

## High-level architecture

The map is produced on the GPU and consumed by one shader. There is no CPU contour work and no baked contour asset. The contour lines are analytic vector geometry: the consumer computes the exact distance to the nearest contour segment per display pixel, so lines are resolution independent.

```
PRODUCER (on the orthographic map camera, RGBA16F 2048x2048 SubViewport)
  TopDownCamera (ortho, top-down)  -->  depth buffer
  TopographicCompositorEffect (compute, PreTransparent) runs, in one render callback:
    1. height pass: depth -> color image R = normalized height, G = coverage mask
    2. seed pass:   per-cell marching squares -> the contour SEGMENT (both endpoints, in
                    UV) crossing each grid cell, written to a persistent segment texture
  Result: the color image holds R = height, G = mask, plus a separate RGBA32F segment
  texture holding (x0,y0,x1,y1) per cell (x0 < 0 means no contour in that cell). The
  segment texture is wrapped in a Texture2Drd so a canvas shader can sample it.

CONSUMER (per map: minimap, world map)
  Each map ColorRect (running topographic.gdshader) samples the height buffer
  and the segment texture over its window (window_center, window_span) and outputs BOTH:
    - the stepped hypsometric tint (from R), and
    - constant-width anti-aliased contour lines. For each display pixel it finds its cell,
      searches a small zoom-scaled neighborhood of cells, reads each cell's segment, takes
      the minimum exact point-to-segment distance, and thresholds it (scaled to screen
      pixels by px_per_uv) for a crisp constant-width line. The line's level is
      round(h/interval) from the local height, so nothing is stored per line.
  The hypsometric tint's band color step is placed and anti-aliased AT the contour line
  (using the same live distance), not at the faceted per-pixel height threshold, so band
  edges stay as smooth as the lines and always coincide with them.

MARKER (overlay)
  A constant-size SDF arrow (marker_overlay.gdshader) drawn into a small UI Control on top
  of each map, rotated to the player's heading.
```

The height buffer and the segment texture come from the same compositor over the same camera, and tint and lines are computed in the same shader pass, so they are always aligned and move together under pan/zoom.

### Why this shape

- A single shader reading only the raw height buffer cannot draw crisp constant-width lines: a constant-width implicit isoline needs `distance_to_level / screen_space_height_gradient`, which is ill-conditioned on flat ground (the gradient goes to zero, so the line dots and flickers). See Design history.
- Storing the contour SEGMENT per cell (from a discrete marching-squares band test) and measuring the exact point-to-segment distance per pixel makes "distance to the nearest line" a robust, gradient-free computation. Constant screen width comes from the zoom uniform (`px_per_uv`), not a per-pixel gradient, so flat ground is fine.
- The distance is computed live per display pixel from the real segment geometry, so it is resolution independent: there is no precomputed distance field to quantize, so lines stay crisp and exactly constant-width at any zoom with no texel facets. It is also simpler than a precomputed field: no jump-flood, no ping-pong textures, no composite pass.
- Building the segments on the GPU means no CPU readback, no bake step, no committed contour asset, and no staleness: change the terrain and the lines follow the next time the buffer renders.

## Component reference

Reusable addon (`addons/topographic/`):

- `TopographicCompositorEffect.cs` + `depth_to_height.glsl` (height) + `contour_seed.glsl` (per-cell segment): the producer. A `CompositorEffect` at `PreTransparent` that runs the height pass then the seed pass, writing the per-cell contour segments into a persistent `RGBA32F` texture it owns (created lazily, sized to the buffer, freed on predelete). The segment texture is exposed as `SegmentTexture` (a `Texture2Drd` whose RID the compositor sets) so a canvas shader can sample it. Exported params: `HeightMin`, `HeightMax`, `ContourInterval` (used by the seed pass), plus camera-rig params `CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`.
- `topographic.gdshader`: the unified consumer shader. Samples `height_buffer` and `segments` over `window_center`/`window_span`; outputs the stepped tint from `R` and the analytic vector contour line from the segment texture. Uniforms are grouped in the inspector (via `group_uniforms`): **Elevation** (`elevation_gradient`, `height_min`, `height_max`), **Contours** (`contour_interval`, `lines_per_major`, `minor_line_width_px`, `major_line_width_px`) with a **Color** subgroup (`line_color`, `line_color_from_gradient`, `line_gradient_lightness`, `line_gradient_shift`), and **Runtime** (`height_buffer`, `segments`, `window_center`, `window_span`, `px_per_uv`). The look params (Elevation + Contours) live directly on each map ColorRect's `ShaderMaterial`; the Runtime inputs the material cannot hold (the height buffer, the segment texture, and the window) are pushed in by `MapUi` each frame and should not be hand-edited.
- `marker_overlay.gdshader` (canvas_item): SDF arrow for the player marker, `fwidth`-antialiased, drawn into a small UI `Control` rotated to the player's heading.

Demo (`TopoDemo/`):

- `scripts/MapUi.cs`: orchestration. Holds the two map `ColorRect`s (minimap, world map) and binds the shader runtime inputs directly: at `_Ready` it sets each one's `height_buffer` to the `MapView` viewport texture and `segments` to the compositor's `SegmentTexture` (via the `MapCompositor` export) through its private `BindTextures` helper; it owns the pan/zoom window state and pushes the window into each map's `window_center`/`window_span`/`px_per_uv` via `SetWindow` (which computes `px_per_uv = view.Size.X / span` so the line width is constant in screen pixels) and updates the two marker overlays each frame. The marker heading comes from the player `Body` node's yaw (`MarkerRotation()`, currently `-PlayerBody.GlobalRotation.Y`; flip the sign if the arrow points backward).
- `scenes/Demo.tscn`: the `MapView` SubViewport + `TopDownCamera` + compositor, the two map `ColorRect`s (each running `topographic.gdshader` with a `Marker` child overlay), and the HUD. `MapUi` references the compositor effect (`map_view_compositor_effect.tres`) directly so it can bind its `SegmentTexture`.
- `scripts/TerrainBaker.cs`: edit-time, headless, CPU tool that bakes `heightmap.exr` (512x512) and `terrain_collision.res`. Not shipped in the running game. It does NOT touch contours (those are a runtime GPU derivation). Run with `godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs`.

## Hard rules and constraints

- The topographic effect lives only on the orthographic map camera (via the `Compositor` on `TopDownCamera`). It must never affect the main gameplay view or the editor.
- The contour segments derive from the camera's height buffer, not a heightmap, so they work for any rendered geometry. Do not couple them to `heightmap.exr`.
- Terrain, heightmap, and collision are static committed files baked at edit time (`TerrainBaker`). No runtime generation of those assets. The contour segments are a runtime GPU derivation of the height buffer and are exempt.
- No emdash characters (or other AI-tell characters) in committed files, including comments. American English. `//` line comments in C#.
- Coordinate conventions: world to buffer UV is `buffer_uv = world.xz / 1536 + 0.5`. Normalized height for world height `H` is `(H + 40) / 150` (range `[-40, 110]`).

## Design history (why it ended up here)

The contour approach changed several times. The reasoning matters so the dead ends are not retried.

1. The whole styled map (tint + contours + marker) was baked into one fixed 1024x1024 SubViewport texture and the UI magnified it. Magnifying a raster is why everything looked blocky.

2. The map was split into a height buffer (data) plus a per-pixel styling shader run at display resolution. Tint became crisp. Contour lines, drawn implicitly per pixel, did not: a constant-pixel-width implicit line divides distance-to-level by the screen-space height gradient, which approaches zero on near-flat ground, so the line dots, stipples, and flickers. Height smoothing, an analytic gradient, and a flat-area guard each helped but none fully fixed it, because the instability is inherent to implicit isolines on flat terrain.

3. Contour lines became vector geometry: extract isolines with Marching Squares on a CPU readback of the buffer and stroke them as constant-width lines. Smooth and crisp, but it needed a one-time CPU extraction. To remove the startup flash, the field was then baked into a committed `.res`; baking from the heightmap misaligned the lines on gentle slopes (the buffer is the rendered mesh, which differs slightly from the heightmap; on gentle slopes a small height error becomes a large lateral line offset), so the bake had to read the camera buffer. This was vector and crisp but baked and scattered across files.

4. The contours moved entirely onto the GPU as a signed distance field (SDF): the compositor built a contour distance field from the height buffer with a seed pass plus a jump-flood (~11 ping-pong passes) and a composite, and the unified shader drew constant-width lines by thresholding that distance. This removed the CPU readback, the bake, the committed asset, and the staleness, and fixed the flat-ground problem. But the distance field is quantized into the buffer grid, so at extreme zoom the bilinearly reconstructed line spreads and softens (it has a facet/blur cap at the texel resolution).

5. Finally the contours became analytic vector lines on the GPU (this version). The seed pass is kept (it already produces the exact per-cell contour segment), but the jump-flood and composite are dropped: instead of precomputing a distance FIELD, the segment texture is sampled directly by the canvas shader, which computes the EXACT point-to-segment distance per display pixel. This is resolution independent (no field quantization, no facet cap, constant width holds at any zoom), and simpler than the SDF (no jump-flood, no ping-pong, no composite). It is the all-GPU realization of "true vector lines, computed live."

## Gotchas and obscure details

Godot rendering:

- `use_hdr_2d = true` on the `MapView` SubViewport is required. Without it the viewport texture is 8-bit, which crushes the normalized height to 256 levels and shows as terraced contours in flat areas.
- The map camera needs a linear-tonemap environment override (`Environment_map`, `background_mode = 1`, `tonemap_mode = 0`, the default so Godot does not serialize it). Otherwise the scene's ACES tonemap distorts the stored height values nonlinearly.
- `render_target_update_mode` integer values: `Disabled = 0`, `Once = 1`, `WhenVisible = 2`, `WhenParentVisible = 3`, `Always = 4`. The producer uses `Once` (1): the terrain is static, so the height + seed passes run one frame and stop. Raising it regenerates the segments automatically for dynamic terrain.
- A `canvas_item` fragment shader cannot use an early `return` (Godot errors). Compute the result in branches and write `COLOR` once.

Exposing an RD texture to a canvas shader (Texture2Drd):

- The compositor's segment texture is a `RenderingDevice` texture, not a Godot `Texture2D`. To let a canvas shader sample it, the compositor wraps it in a `Texture2Drd` (`SegmentTexture`) and sets `SegmentTexture.TextureRdRid = _segments` when it (re)creates the RD texture. `MapUi` binds that `Texture2Drd` to each view's `segments` uniform. Both run on the same `RenderingServer.GetRenderingDevice()`, so the RID is valid for sampling.
- The RD texture must be created with `SamplingBit` usage (in addition to `StorageBit` so the compute pass can write it), or the canvas shader cannot sample it.
- A consumer must not draw while sampling the segment texture before the producer's first render: with the `Texture2Drd`'s RID not yet live, the canvas shader cannot supply its sampler uniform and the draw fails with "Uniforms were never supplied for set (1)". Two defenses: (1) the compositor exposes `HasProduced` (set at the end of its first render callback) and the always-visible minimap stays hidden until it is true, so the only startup consumer never draws before the producer runs (the world map is hidden until opened, well after production); (2) the compositor also creates a 1x1 placeholder segment texture in its constructor and sets `SegmentTexture.TextureRdRid` immediately, so the wrapper RID is never empty, replaced with the real buffer-sized texture on the first render. The shader treats `seg.x <= 0` as "no contour", which also covers the zero-initialized placeholder and any out-of-range fetch.
- When replacing the RD texture (placeholder to real size), create the new texture and set `SegmentTexture.TextureRdRid` to it BEFORE freeing the old RD texture. The `Texture2Drd` wrapper still references the old RID through its internal RenderingServer texture, so freeing the old RD texture first makes the setter operate on a freed RID and crashes with "Attempted to free invalid ID". On teardown, clear the wrapper (`SegmentTexture.TextureRdRid = new Rid()`) first, then free the RD texture.
- Sample the segment texture with `filter_nearest` and read exact texels with `texelFetch`: each texel holds raw endpoint coordinates, which must not be bilinearly interpolated between cells.

Analytic vector lines:

- The seed pass uses a discrete band comparison (per-cell marching squares for the level crossing the cell), which is robust on flat ground precisely because there is no gradient division.
- Seed SEGMENTS, not points. An earlier point-per-cell seed measured distance to the nearest point, and midpoints between points read as farther, so lines DASHED at high zoom. The contour segment per cell (per-cell marching squares) with exact point-to-segment distance gives true distance to the line.
- The per-pixel neighborhood search radius scales with zoom: `radius = ceil((max_width_px + 1.5) / px_per_uv * tex_size)`, clamped to `[1, 8]` cells. At gameplay zoom a constant-width line spans about one cell (radius 1-2); at min zoom (full world) the line can cover several cells, so the radius widens to its cap. The clamp bounds the worst-case per-pixel cost.
- The distance is in UV units; to get a constant screen width, scale by `px_per_uv = view_size_px / window_span` (screen pixels per UV unit). The line uses a tight 1px anti-alias (`clamp(width - dist_px + 0.5, 0, 1)`), not a wide soft falloff, so it is crisp rather than blurry. No signing is needed: the distance is to the real segment geometry, so it is small only at real lines (no phantom mid-band line).
- Band-edge quality at high zoom: a hard `floor(h/interval)` band fill shows the buffer's texel facets when zoomed in. Placing and anti-aliasing the band color step at the line (using the same live distance, with side below/above from `sign(h - round(h/interval)*interval)`) makes band edges as smooth as the lines and coincident with them. The level and blend jumps at mid-band cancel, so the fill color stays continuous there. This is safe (it is a fill-edge AA, not a constant-width line, so the flat-ground gradient instability does not apply).
- Saddle cells (a cell with two contour crossings) are not currently handled: the seed pass stores one segment per cell (the first two crossings found). Saddles are rare at the buffer resolution; handling them would need a second segment per cell.

## Performance and load behavior

- The producer runs two compute passes (height + seed) over the 2048x2048 buffer. Because the terrain is static (`update_mode = Once`), this runs once at load in well under a millisecond. There is no per-frame producer cost and effectively no startup flash. (The dropped jump-flood was ~11 extra passes, so the producer is now cheaper than the SDF version.)
- The consumer is a single canvas shader sampling two textures; pan/zoom only changes uniforms. The per-pixel line search is cheap at gameplay zoom (radius 1-2). At the absolute worst case (min zoom, full-screen world map, radius 8) the line shader adds roughly 0.7 ms/frame over a no-search baseline on an RTX 5090; at gameplay zoom it is a small fraction of that. This is a map overlay, not a hot gameplay path.
- There is no CPU contour work, no buffer readback, and no committed contour asset to load or keep in sync.

## Tuning parameters

- All look parameters live on each map's `ShaderMaterial` as shader parameters: `elevation_gradient` (the palette, shared by tint and lines), `height_min`/`height_max`, `contour_interval`, `lines_per_major`, `minor_line_width_px`, `major_line_width_px`, and the line-color controls (`line_color`, `line_color_from_gradient`, `line_gradient_lightness`, `line_gradient_shift`). The two maps (minimap, world map) have separate materials, so set each one (there is no shared settings resource).
- Contour line color: `line_color_from_gradient` blends between a fixed `line_color` (0) and a gradient-derived color (1). The gradient-derived color samples the palette at the line's elevation (offset by `line_gradient_shift`) and applies `line_gradient_lightness` (negative darkens toward black, positive lightens toward white, so dynamic lines stay legible on both light and dark palettes). Example: the demo minimap uses the dark `blueprint` palette with `line_color = white`, `line_color_from_gradient = 0` for white lines; the world map leaves the dynamic default (`line_color_from_gradient = 1`, `line_gradient_lightness = -0.4`).
- The compositor effect reads `HeightMin`/`HeightMax`/`ContourInterval` from its own exports plus the camera-rig params. Keep its `ContourInterval` and height range in sync with the materials' `contour_interval`/`height_min`/`height_max` so producer and consumer agree.
- The palette is the `ColorRamp` `GradientTexture1D`. The demo uses `addons/topographic/gradients/hypsometric_deep.tres`; the addon ships several preset gradients in that folder.
- The marker overlays expose `marker_color` and `outline_color` (on the material) and `MarkerScreenSize` (on `MapUi`).

## Known limitations and follow-ups

- The contour segments are at the buffer resolution (2048). Lines are crisp and exactly constant-width at any zoom (the distance is computed analytically), but the line PATH is a piecewise-linear polyline through texel-resolution crossings, which is the terrain's effective detail limit anyway. At extreme zoom the polyline's straight chords become visible; that is the data resolution, not a rendering artifact.
- Saddle cells store only one segment (see Gotchas).
- The producer reconstructs height from depth for an orthographic projection; a perspective map camera would need the reconstruction adjusted.
- The marker is a constant-size, `fwidth`-antialiased SDF arrow UI overlay on each map (`marker_overlay.gdshader`). Verify the heading sign once in-editor.
