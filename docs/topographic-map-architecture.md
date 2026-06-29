# Topographic Map: Architecture and Engineering Notes

This document captures how the topographic map works, the decisions behind it, and the gotchas worth knowing before changing anything. It reflects the state after the move to analytic GPU vector contour lines.

## What it is

A topographic map rendered from any 3D terrain the game shows: a hypsometric (elevation-colored) tint with contour lines, shown as a corner minimap and a full-screen pan/zoom world map. The reusable system lives in `addons/topographic/`; the demo wiring lives in `TopoDemo/`.

## High-level architecture

The map is produced on the GPU and consumed by one shader. There is no CPU contour work and no baked contour asset. The contour lines are analytic vector geometry: the consumer computes the exact distance to the nearest contour segment per display pixel, so lines are resolution independent.

```
PRODUCER (on the orthographic map camera, 2048x2048 SubViewport)
  TopDownCamera (ortho, top-down)  -->  depth buffer
  TopographicCompositorEffect (compute, PreTransparent) runs, in one render callback:
    1. height pass: depth -> height texture R = normalized height, G = coverage mask
    1b. (optional) height blur: separable box blur of R, in place, when ContourSmoothness > 0
    2. seed pass:   per-cell marching squares -> the contour SEGMENT (both endpoints, in
                    UV) crossing each grid cell, written to a persistent segment texture
  Result: a persistent RGBA16F height texture holds R = height, G = mask, plus a separate
  RGBA32F segment texture holding (x0,y0,x1,y1) per cell (x0 < 0 means no contour in that
  cell). Both are wrapped in Texture2Drds so a canvas shader can sample them directly.

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

- `TopographicCompositorEffect.cs` + `depth_to_height.glsl` (height) + `contour_seed.glsl` (per-cell segment): the producer. A `CompositorEffect` at `PreTransparent` that runs the height pass then the seed pass, writing the per-cell contour segments into a persistent `RGBA32F` texture it owns (created lazily, sized to the buffer, freed on predelete). The height pass writes normalized height + coverage mask into a second persistent `RGBA16F` texture (not the SubViewport color), and both are exposed as `Texture2Drd` properties (`SegmentTexture`, `HeightTexture`) whose RIDs the compositor sets, so a canvas shader can sample them directly. Exported params: `HeightMin`, `HeightMax`, `ContourInterval` (used by the seed pass). The camera rig (height Y, near/far) is not exported: the render callback reads it from `RenderData.GetRenderSceneData()` each frame (`GetCamTransform().Origin.Y`, plus near/far derived from the orthographic projection's z-column, since `GetZNear`/`GetZFar` assume a perspective frustum), so it tracks the actual map camera and cannot drift. The seed pass also bakes `(height_min, height_max, contour_interval, 1.0)` into the segment texture's last texel `(size-1, size-1)` so the consumer can read the elevation model back with one `texelFetch`. The compositor is not a `[Tool]`, so it runs only at runtime, never in the editor. It disposes the per-frame RD wrapper objects (`RenderSceneBuffersRD`, `RDUniform`) deterministically so the GC finalizer does not free RIDs off the render thread (Godot #104263), and frees its RIDs on the render thread via `RenderingServer.CallOnRenderThread`, driven early from a node's `_ExitTree` since predelete runs too late at shutdown.
- `topographic.gdshader`: the unified consumer shader. Samples `height_buffer` and `segments` over `window_center`/`window_span`; outputs the stepped tint from `R` and the analytic vector contour line from the segment texture. Uniforms are grouped in the inspector (via `group_uniforms`): **Elevation** (`elevation_gradient`), **Contours** (`lines_per_major`, `minor_line_width_px`, `major_line_width_px`) with a **Color** subgroup (`line_color`, `line_color_from_gradient`, `line_gradient_lightness`, `line_gradient_shift`), and **Runtime** (`height_buffer`, `segments`, `window_center`, `window_span`). The look params (the gradient and the line styling) live directly on each map ColorRect's `ShaderMaterial`. The Runtime inputs are bound in the inspector, not by a consumer script: `height_buffer` and `segments` are each bound to a shared `Texture2DRD` `.tres` (the same two resources assigned to the compositor's `HeightTexture` and `SegmentTexture`). The elevation model is no longer a uniform: the compositor bakes it into the segment texture's metadata texel and the shader reads it back. `px_per_uv` is no longer a uniform either: the shader derives it in-fragment as `1.0 / fwidth(UV.x) / window_span.x`. Only the window (`window_center`/`window_span`) is moved at run time, by the consuming game's own code (`MapUi` in the demo).
- `marker_overlay.gdshader` (canvas_item): SDF arrow for the player marker, `fwidth`-antialiased, drawn into a small UI `Control` rotated to the player's heading.
- `gradients/`: the preset `GradientTexture1D` palettes (`blueprint`, `sepia_vintage`, and the rest) shared by the tint and the lines.

The addon ships no consumer or binder script. A consumer is a bare `ColorRect` running `topographic.gdshader` with its inputs wired in the inspector (see the binding contract below), so there is nothing in the addon to instantiate beyond the compositor and the shaders.

### Inspector binding contract (no consumer script)

A consuming scene wires the producer and each map entirely in the inspector:

- A `SubViewport` holding a top-down orthographic `Camera3D` whose `Compositor` carries a `TopographicCompositorEffect`. The camera rendering into the SubViewport is the only link between them (see Godot rendering gotchas).
- Two `Texture2DRD` resources saved as standalone `.tres` files: one assigned to the compositor's `SegmentTexture` and each consumer material's `segments` uniform, the other to the compositor's `HeightTexture` and each consumer's `height_buffer` uniform. These are the shared channels: the compositor sets their RIDs, every consumer samples them. They are allocated on the compositor's first render and sampled only after `HasProduced` (see the Texture2Drd gotchas).
- The gradient, the line styling, and the initial `window_center`/`window_span` authored on the consumer's `ShaderMaterial`.

The elevation model and `px_per_uv` are not wired anywhere: the consumer reads the elevation model back from the segment texture's metadata texel, and derives `px_per_uv` in-fragment. The only thing left to code is moving the window each frame (pan/zoom and the player marker), which is the consuming game's own concern, shown by the demo's `MapUi`.

Demo (`TopoDemo/`):

- `scripts/MapUi.cs`: orchestration. The two maps are bare `ColorRect`s whose textures are bound in the inspector (and whose elevation model the shader reads back from the segment texture), so `MapUi` never touches those. It owns the pan/zoom window state and pushes the window into each map's `window_center`/`window_span` via `SetWindow`, updates the two marker overlays each frame, and keeps the `MapCompositor` export only to gate the minimap reveal on `HasProduced`. The line width stays constant in screen pixels with no code here, because the shader derives `px_per_uv` itself from `fwidth(UV.x)` and `window_span`. The marker heading comes from the player's yaw (`MarkerRotation()`, currently `-Player.GlobalRotation.Y`; flip the sign if the arrow points backward).
- `scenes/DemoMinimap.tscn`: the `MapView` SubViewport + `TopDownCamera` + compositor, the two map `ColorRect`s (each running `topographic.gdshader` with a `Marker` child overlay), and the HUD. The shared `Texture2DRD`s (`map_segment_texture.tres` and `map_height_texture.tres`) are assigned both to the compositor effect's `SegmentTexture`/`HeightTexture` and to each material's `segments`/`height_buffer` uniforms in the scene. `MapUi` references the compositor effect (`map_view_compositor_effect.tres`) only to gate the minimap reveal on `HasProduced`.
- `scenes/DemoTerrain.tscn`: a single baked terrain + top-down camera + a full-screen `topographic.gdshader` `ColorRect`, driven by `scripts/DemoTerrain.cs`. It is the one-stop testing scene: view mode (no arg) renders the schematic torture terrain (`torture_heightmap.exr`, the stress rig described below) live when you press Play, while `-- banner` and `-- presets` swap the terrain material back to the smooth `banner_heightmap.exr` to regenerate the README screenshots (only the gradient changes between shots). The swap is one `SetShaderParameter("heightmap", ...)` in `DemoTerrain.cs` via its `Terrain` export.
- `scripts/TerrainBaker.cs`: edit-time, headless, CPU tool that bakes every committed terrain: the demo continent (`heightmap.exr` 512x512 + `terrain_collision.res`), `banner_heightmap.exr` (the smooth glamour terrain), and `torture_heightmap.exr` (the stress rig). The demo and banner terrains use smooth single-octave simplex (no ridged noise or domain warp) so the contours read as smooth ripples; the torture terrain instead sums disjoint analytic shapes for hard vertical relief. Not shipped in the running game. It does NOT touch contours (those are a runtime GPU derivation). Run with `godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs`.

### Torture terrain (validation rig)

`torture_heightmap.exr` (1024x1024, baked by `TerrainBaker.cs`, rendered by `DemoTerrain.tscn` view mode) is the honest stress terrain for the Phase 1 robustness work, where the smooth glamour terrains hide every hard case. It is a schematic test pattern: six spatially disjoint analytic zones on a 3-by-2 grid over a flat base at +5 (offset off the contour grid so flat regions read as clean no-contour bands), each summed onto the base so only one zone is nonzero at any point. Feature elevations are aligned to the 10-unit `ContourInterval`, so contours land on the relief where intended. The smooth `banner_heightmap.exr` remains the terrain for the README glamour shots.

| Zone         | Feature                           | Elevations            | Exercises                          |
| ------------ | --------------------------------- | --------------------- | ---------------------------------- |
| Mesa         | Flat top, vertical cliff walls    | top +85, foot +5      | Slope suppression (cliff smear); plateau = no contours |
| Staircase    | Five flat treads, vertical risers | treads 5,25,45,65,85  | Hard steps; min-zoom moire on risers |
| Cone + ridge | Steep peak with a ridge spur      | summit +105           | Tight nested contours near the summit |
| Basin        | Flat floor, steep walls           | floor -25             | Flat basin = no contours; packed walls |
| Col / saddle | Two peaks, pass on the +40 line   | peaks +80, pass +40   | Saddle cells (the +40 contour forms the X-crossing) |
| Smooth patch | Gentle single-octave simplex      | +10..+50              | Moire baseline (many close smooth rings) |

## Hard rules and constraints

- The topographic effect lives only on the orthographic map camera (via the `Compositor` on `TopDownCamera`). It must never affect the main gameplay view or the editor's main 3D viewport. The map renders only at runtime: the compositor is not a `[Tool]` and the consumer ColorRects are saved hidden, revealed on `HasProduced` once the game runs.
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

- A `Camera3D` renders into its nearest ancestor `Viewport`/`SubViewport` (going toward the root), automatically and with no script. This is the only thing wiring `TopDownCamera` to the `MapView` SubViewport: there is no reference between them, just the nesting, so the camera must stay inside the SubViewport's subtree (move it out and it renders into the main window instead). The reverse link does not exist: nothing auto-associates a consumer with a viewport's texture. Consumers do not reference the SubViewport at all; they sample the shared `Texture2DRD` height buffer and segment texture that the compositor (attached to the camera nested in the SubViewport) writes, bound to each material in the inspector. With multiple map SubViewports each renders independently from the camera nested inside it, with no crosstalk. Documented under Godot's "Using Viewports" tutorial.
- The height buffer is NOT a `ViewportTexture` of the map SubViewport; it is a shared `Texture2DRD` the compositor writes, exactly like the segment texture. The `depth_to_height` pass stores normalized height + mask into this `Texture2DRD` (via the compositor's `HeightTexture` property), and each consumer material binds the same `.tres` to its `height_buffer` uniform. The SubViewport still exists, because the compositor needs the camera's depth render, but its color output is no longer sampled by anyone. This replaced an earlier design that read the SubViewport color back through a `ViewportTexture`, which had an editor-only quirk: a `ViewportTexture` resolves its `viewport_path` one frame late when a scene is opened in the editor, so the map `ColorRect` drew once before the texture was ready and logged "Path to node is invalid: '<SubViewport>'" then "Uniforms were never supplied for set (1)". With a `Texture2DRD` there is no path to resolve, so that burst is gone in both the editor and the game.
- `use_hdr_2d = true` on the `MapView` SubViewport is required. Without it the viewport texture is 8-bit, which crushes the normalized height to 256 levels and shows as terraced contours in flat areas.
- The map camera needs a linear-tonemap environment override (`Environment_map`, `background_mode = 1`, `tonemap_mode = 0`, the default so Godot does not serialize it). Otherwise the scene's ACES tonemap distorts the stored height values nonlinearly.
- `render_target_update_mode` integer values: `Disabled = 0`, `Once = 1`, `WhenVisible = 2`, `WhenParentVisible = 3`, `Always = 4`. The producer uses `Once` (1): the terrain is static, so the height + seed passes run one frame and stop. Raising it regenerates the segments automatically for dynamic terrain.
- A `canvas_item` fragment shader cannot use an early `return` (Godot errors). Compute the result in branches and write `COLOR` once.

Exposing an RD texture to a canvas shader (Texture2Drd):

- The compositor's segment texture AND its height buffer are both `RenderingDevice` textures, not Godot `Texture2D`s. To let a canvas shader sample them, the compositor takes a `Texture2DRD` resource per output (its `SegmentTexture` and `HeightTexture` properties) and sets `TextureRdRid` on each when it (re)creates the RD texture. Those same `.tres` resources are bound to each material's `segments` and `height_buffer` uniforms in the inspector, so the compositor and the consumers share two resources with no script wiring them. Both run on the same `RenderingServer.GetRenderingDevice()`, so the RIDs are valid for sampling.
- The RD textures must be created with `SamplingBit` usage (in addition to `StorageBit` so the compute passes can write them), or the canvas shader cannot sample them.
- A consumer must not draw while sampling these textures before the producer's first render: with a `Texture2Drd`'s RID not yet live, the canvas shader cannot supply its sampler uniform and the draw fails with "Uniforms were never supplied for set (1)". The defense is `HasProduced`: the compositor exposes it (set at the end of its first render callback), every consumer ColorRect is saved hidden, and the consumer scripts reveal each only after it is true, so no consumer ever draws before the producer runs (the world map stays hidden until opened, well after production). The shader treats `seg.x <= 0` as "no contour", covering empty marching-squares cells and any out-of-range fetch.
- Reassigning a `Texture2DRD`'s RID (the first render sizing the shared textures to the SubViewport) invalidates a consumer's cached uniform set for one frame and would trip "set (1)" (Godot #118292). Because the map is runtime-only and every consumer is gated on `HasProduced`, no consumer draws during that first render, so the swap is never observed. This is why the compositor and its consumers should stay out of the editor: the editor would draw a consumer with no `HasProduced` gate, so it would sample during that first render and trip the warning. Keep the compositor non-`[Tool]` and the consumer ColorRects saved hidden.
- When replacing an RD texture (on (re)create or resize), create the new texture and set `TextureRdRid` to it BEFORE freeing the old RD texture. The `Texture2Drd` wrapper still references the old RID through its internal RenderingServer texture, so freeing the old RD texture first makes the setter operate on a freed RID and crashes with "Attempted to free invalid ID". On teardown, clear each wrapper (`TextureRdRid = new Rid()`) first, then free the RD texture.
- Sample the segment texture with `filter_nearest` and read exact texels with `texelFetch`: each texel holds raw endpoint coordinates, which must not be bilinearly interpolated between cells.

Analytic vector lines:

- The seed pass uses a discrete band comparison (per-cell marching squares for the level crossing the cell), which is robust on flat ground precisely because there is no gradient division.
- Seed SEGMENTS, not points. An earlier point-per-cell seed measured distance to the nearest point, and midpoints between points read as farther, so lines DASHED at high zoom. The contour segment per cell (per-cell marching squares) with exact point-to-segment distance gives true distance to the line.
- The per-pixel neighborhood search radius scales with zoom: `radius = ceil((max_width_px + 1.5) / px_per_uv * tex_size)`, clamped to `[1, 8]` cells. At gameplay zoom a constant-width line spans about one cell (radius 1-2); at min zoom (full world) the line can cover several cells, so the radius widens to its cap. The clamp bounds the worst-case per-pixel cost.
- The distance is in UV units; to get a constant screen width, scale by `px_per_uv` (screen pixels per UV unit). The shader derives this itself per fragment as `1.0 / fwidth(UV.x) / window_span.x`: `1.0 / fwidth(UV.x)` is the rect's pixel width, and dividing by the window span converts UV-of-buffer to UV-of-rect, so no driver has to push the value. The line uses a tight 1px anti-alias (`clamp(width - dist_px + 0.5, 0, 1)`), not a wide soft falloff, so it is crisp rather than blurry. No signing is needed: the distance is to the real segment geometry, so it is small only at real lines (no phantom mid-band line).
- Band-edge quality at high zoom: a hard `floor(h/interval)` band fill shows the buffer's texel facets when zoomed in. Placing and anti-aliasing the band color step at the line (using the same live distance, with side below/above from `sign(h - round(h/interval)*interval)`) makes band edges as smooth as the lines and coincident with them. The level and blend jumps at mid-band cancel, so the fill color stays continuous there. This is safe (it is a fill-edge AA, not a constant-width line, so the flat-ground gradient instability does not apply).
- Saddle cells (a cell with two contour crossings) are not currently handled: the seed pass stores one segment per cell (the first two crossings found). Saddles are rare at the buffer resolution; handling them would need a second segment per cell.

Optional height smoothing:

- `ContourSmoothness` (compositor export, `0..8` texels, default `4`; `0` = off) inserts an optional separable box blur of the height buffer's `R` channel between the height pass and the seed pass (`height_blur.glsl`, run horizontal then vertical through a scratch RGBA16F target back into the height texture, with a barrier between each). It is for rough/high-frequency terrain where the raw contours come out jagged.
- It is done in the producer, on the shared height buffer, on purpose: both the contour lines (seed pass) and the tint bands (consumer sampling `R`) read this same buffer, so blurring it once smooths both together and keeps them aligned, with no change to `contour_seed.glsl` or `topographic.gdshader`. A consumer-side blur would be per-pixel expensive and could not smooth the already-seeded segment lines. The coverage mask `G` is carried through unblurred. When `0`, the blur passes are skipped entirely, so the pipeline is byte-for-byte the original.

## Performance and load behavior

- The producer runs two compute passes (height + seed) over the 2048x2048 buffer. Because the terrain is static (`update_mode = Once`), this runs once at load in well under a millisecond. There is no per-frame producer cost and effectively no startup flash. (The dropped jump-flood was ~11 extra passes, so the producer is now cheaper than the SDF version.)
- The consumer is a single canvas shader sampling two textures; pan/zoom only changes uniforms. The per-pixel line search is cheap at gameplay zoom (radius 1-2). At the absolute worst case (min zoom, full-screen world map, radius 8) the line shader adds roughly 0.7 ms/frame over a no-search baseline on an RTX 5090; at gameplay zoom it is a small fraction of that. This is a map overlay, not a hot gameplay path.
- There is no CPU contour work, no buffer readback, and no committed contour asset to load or keep in sync.

## Tuning parameters

- The look parameters live on each map's `ShaderMaterial` as shader parameters: `elevation_gradient` (the palette, shared by tint and lines), `lines_per_major`, `minor_line_width_px`, `major_line_width_px`, and the line-color controls (`line_color`, `line_color_from_gradient`, `line_gradient_lightness`, `line_gradient_shift`). The two maps (minimap, world map) have separate materials, so set each one (there is no shared settings resource). The elevation model is not authored on the material: it is owned by the compositor, baked into the segment texture, and read back by the consumer (see below), so there is no value to keep in sync.
- Contour line color: `line_color_from_gradient` blends between a fixed `line_color` (0) and a gradient-derived color (1). The gradient-derived color samples the palette at the line's elevation (offset by `line_gradient_shift`) and applies `line_gradient_lightness` (negative darkens toward black, positive lightens toward white, so dynamic lines stay legible on both light and dark palettes). Example: the demo minimap uses the dark `blueprint` palette with `line_color = white`, `line_color_from_gradient = 0` for white lines; the world map stays gradient-derived (`line_color_from_gradient = 1`) with `line_gradient_lightness` and `line_gradient_shift` tuned for a subtly hue-shifted line.
- The compositor effect is the single owner of `HeightMin`/`HeightMax`/`ContourInterval` (plus the camera-rig params). The seed pass bakes them into the segment texture's metadata texel `(size-1, size-1)`, and each consumer reads them back with one `texelFetch`. No script pushes them and no value is authored on the material, so producer and consumer always agree with nothing to hand-sync.
- The palette is the `elevation_gradient` `GradientTexture1D`. The demo's minimap uses `blueprint` and its world map uses `sepia_vintage`; the addon ships a dozen preset gradients in `addons/topographic/gradients/`.
- The marker overlays expose `marker_color` and `outline_color` (on the material) and `MarkerScreenSize` (on `MapUi`).

## Known limitations and follow-ups

- The contour segments are at the buffer resolution (2048). Lines are crisp and exactly constant-width at any zoom (the distance is computed analytically), but the line PATH is a piecewise-linear polyline through texel-resolution crossings, which is the terrain's effective detail limit anyway. At extreme zoom the polyline's straight chords become visible; that is the data resolution, not a rendering artifact.
- Saddle cells store only one segment (see Gotchas).
- The producer reconstructs height from depth for an orthographic projection; a perspective map camera would need the reconstruction adjusted.
- The marker is a constant-size, `fwidth`-antialiased SDF arrow UI overlay on each map (`marker_overlay.gdshader`). Verify the heading sign once in-editor.
- Instance uniforms (dead end, recorded so it is not retried). Moving the runtime values onto the node as per-instance uniforms looked like a way to bind them without a script and without authoring on the material, but it does not pay off. Instance uniforms serialize the same way regular uniforms do (clearing one reverts it to the shader's declared default and still serializes that default), so they do not give a "clean" unwritten slot. They relocate the fields from the material inspector to the node rather than hiding them. And they cannot hold textures, so the height buffer and the segment texture could never be instance uniforms anyway. Combined with the requirement that the shader work standalone (a bare `ColorRect` with inspector-bound inputs, no driver), the runtime values stay regular uniforms, and the elevation model lives in the segment-texture metadata texel rather than on the node.
