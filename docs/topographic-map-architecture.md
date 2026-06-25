# Topographic Map: Architecture and Engineering Notes

This document captures how the topographic map works, the decisions behind it, and the gotchas worth knowing before changing anything. It reflects the state after the move to vector contour lines.

## What it is

A topographic map rendered from any 3D terrain the game shows: a hypsometric (elevation-colored) tint with contour lines, shown as a corner minimap and a full-screen pan/zoom world map. The reusable system lives in `addons/topographic/`; the demo wiring lives in `TopoDemo/`.

## High-level architecture

The map is produced in three stages, kept deliberately separate:

```
PRODUCER (on the orthographic map camera)
  TopDownCamera (ortho, top-down)  -->  depth buffer
  TopographicCompositorEffect (compute)  reconstructs world height
      -->  HEIGHT BUFFER: MapView SubViewport texture, 2048x2048, RGBA16F
           R = normalized height in [-40, 110], G = terrain/background mask

TINT (per consumer, per pixel)
  topographic_style.gdshader (canvas_item) samples the height buffer over a UV
  window and draws the stepped hypsometric tint. No contour code.

CONTOURS (vector)
  MapUi reads the height buffer back to the CPU once (main thread), then
  ContourExtractor + ContourField run Marching Squares per level on a background
  thread (levels in parallel), chaining and simplifying into polylines.
  ContourLayer (a Control) strokes those polylines for the current window with
  constant-pixel-width anti-aliased lines, redrawing only when the window moves.

MARKER (overlay)
  A constant-size SDF arrow (marker_overlay.gdshader) drawn into a small UI
  Control on top of each map, rotated to the player's heading.
```

The height buffer is the single shared data source. The tint samples it on the GPU; the contours are extracted from a one-time CPU readback of it. Because both come from the same buffer, lines and tint band edges align.

### Why this shape

- The tint is cheap and smooth as a per-pixel shader, and it has no flat-ground problem (a band fill does not need a gradient).
- Contour lines as vector geometry are smooth and constant-width on every slope with no tuning. The per-pixel approach we used first is fundamentally unstable on flat ground (see Design history).
- Reading the contours from the camera's height buffer (not a heightmap) keeps the addon general: it works for any geometry the camera renders, with no heightmap required.

## Component reference

Reusable addon (`addons/topographic/`):

- `TopographicCompositorEffect.cs` + `topographic.glsl`: the producer. A `CompositorEffect` running a compute shader at the `PreTransparent` stage. Reads the camera depth buffer, reconstructs world height (linear for an orthographic projection), and writes `R = normalized height`, `G = coverage mask` into the camera color buffer. Exported params: `HeightMin`, `HeightMax`, `CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`.
- `topographic_style.gdshader`: tint only. Samples `height_buffer` over `window_center`/`window_span`, reconstructs height, and outputs the stepped hypsometric band color sampled from `color_ramp`. No water special case: low ground simply takes the gradient's low colors.
- `MarchingSquares.cs` (pure C#, no Godot types): `ContourPoint` struct; `ExtractSegments(field, mask, cols, rows, level)` returns flat segment endpoint pairs in normalized `[0,1]` space; `ChainSegments(segments)` links them into polylines; `Simplify(points, epsilon)` runs Ramer-Douglas-Peucker to cut the dense per-cell point count.
- `ContourField.cs` (pure C#): `ContourPolyline` (points, level, major flag, bounding box) and `ContourField.Build(field, mask, cols, rows, heightMin, heightMax, interval, majorEvery, simplifyEpsilon)`. Levels are independent and extracted in parallel (`Parallel.For`); each polyline is simplified before storage.
- `ContourExtractor.cs` (pure C#): `Build(byte[] data, srcW, srcH, ...)` parses raw `Rgbaf` bytes (16 bytes/pixel, R height, G mask), optionally box-downsamples to `maxResolution`, and calls `ContourField.Build`. Pure so it can run on a background thread; the caller does the Godot-side image readback.
- `ContourLayer.cs` (Godot `Control`): holds a `ContourField`, a window (`SetWindow(center, span)`, which redraws only when the window actually changes), and draws on `_Draw` with `DrawPolyline(..., width_px, antialiased: true)`. Culls polylines by bounding box. Line color is either a static `LineColor` or, when `ContourDynamic` is true, the `ColorRamp` (a `GradientTexture1D`) sampled at the line's elevation and darkened by `ContourDarken`. Major lines use `MajorWidthPx`, minor lines `MinorWidthPx`.
- `marker_overlay.gdshader` (canvas_item): SDF arrow for the player marker, `fwidth`-antialiased, drawn into a small UI `Control` rotated to the player's heading.

Demo (`TopoDemo/`):

- `scripts/MapUi.cs`: orchestration. Reads the height buffer once, runs the contour build on a background thread (`Task.Run`) and assigns the result to the layers; owns the pan/zoom window state and drives the two tint materials, two `ContourLayer`s, and two marker overlays each frame. The marker heading comes from the player `Body` node's yaw (`MarkerRotation()`, currently `-PlayerBody.GlobalRotation.Y`; flip the sign if the arrow points backward).
- `scenes/Demo.tscn`: the `MapView` SubViewport + `TopDownCamera` + compositor, the two map `ColorRect`s (tint), each with a `Contours` child (`ContourLayer`) and a `Marker` child (overlay), and the HUD.
- `scripts/tools/TerrainBaker.cs`: edit-time tool that bakes `heightmap.exr` (512x512) and `terrain_collision.res`. Not shipped in the running game.

Tests (`tests/MarchingSquaresTests/`): a standalone console project that links the pure-C# files and asserts on small known grids. Run with `dotnet run --project tests/MarchingSquaresTests/MarchingSquaresTests.csproj` and expect `ALL PASS` (exit 0).

## Hard rules and constraints

- The topographic effect lives only on the orthographic map camera (via the `Compositor` on `TopDownCamera`). It must never affect the main gameplay view or the editor.
- The contour system reads the camera's height buffer, not a heightmap, so it works for any rendered geometry. Do not couple it to `heightmap.exr`.
- Terrain, heightmap, and collision are static committed files baked at edit time (`TerrainBaker`). No runtime generation of those assets. Contour polylines are a derived visualization built at load and are exempt.
- No emdash characters (or other AI-tell characters) in committed files, including comments. American English. `//` line comments.
- Coordinate conventions: world to buffer UV is `buffer_uv = world.xz / 1536 + 0.5`. Normalized height for world height `H` is `(H + 40) / 150` (range `[-40, 110]`).

## Design history (why it ended up here)

The contour line approach changed twice. The reasoning matters so the dead ends are not retried.

1. First the whole styled map (tint + contours + marker) was baked into one fixed 1024x1024 SubViewport texture and the UI magnified it. Magnifying a raster is why everything looked blocky. A shader is mathematical, but its output is sampled onto a finite pixel grid; once styled into a fixed-resolution image and upscaled, the crispness is gone.

2. Then the map was split into a height buffer (data) plus a per-pixel styling shader run at display resolution. Tint became crisp. Contour lines, drawn implicitly per pixel, did not. A constant-pixel-width implicit line needs to divide distance-to-level by the screen-space height gradient. On near-flat ground that gradient approaches zero, so the line is ill-conditioned: it dots, stipples, and flickers when panning. We fought this with height smoothing, an analytic gradient (sampling neighbor texels instead of screen derivatives), and a flat-area guard. Each helped but none fully fixed it, because the instability is inherent to implicit isoline rendering on flat terrain.

3. Finally the contour lines became vector geometry: extract the isolines with Marching Squares and stroke them with a constant pixel width. The width comes from the stroking, not from a gradient, so there is no flat-ground degeneracy. Lines are smooth and consistent on every slope with no parameters. This is how cartographic software draws contours. The tint stayed a per-pixel shader (it never had the line problem).

## Gotchas and obscure details

Godot rendering:

- `use_hdr_2d = true` on the `MapView` SubViewport is required. Without it the viewport texture is 8-bit, which crushes the normalized height to 256 levels (about 0.6 m per step). That quantization is invisible in the tint gradient but shows as terraced, jagged contours in flat areas where the surface barely changes across a band.
- The map camera needs a linear-tonemap environment override (`Environment_map`, `background_mode = 1`, `tonemap_mode = 0`, which is the default so Godot does not serialize it). Otherwise the scene's ACES tonemap distorts the stored height values nonlinearly.
- `render_target_update_mode` integer values: `Disabled = 0`, `Once = 1`, `WhenVisible = 2`, `WhenParentVisible = 3`, `Always = 4`. The producer uses `Once` (1): the terrain is static, so it renders one frame and stops. Re-rendering a static 2048x2048 buffer plus the compute every frame is wasted GPU work and was the likely cause of the window lingering on close.
- In C#, the enum is `SubViewport.UpdateMode.Once` (not `UpdateModeEnum`).
- A `canvas_item` fragment shader cannot use an early `return`. Godot reports "Using 'return' in the 'fragment' processor function is incorrect." Compute the result in branches and write `COLOR` once.
- Reading the height buffer back: set the SubViewport to `Once`, then `await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw)` a couple of times before `GetTexture().GetImage()`, so the render has completed.
- Reading pixels: convert the image to `Image.Format.Rgbaf` and parse the raw `GetData()` byte array (`BitConverter.ToSingle`, 16 bytes per pixel, R at offset 0, G at offset 4). Per-pixel `GetPixel` over millions of texels is slow enough to stall the load.
- `GradientTexture1D` is not a `Gradient`. To sample colors in C#, use its `.Gradient` property: `gradientTexture.Gradient.Sample(t)`. Multiplying the resulting `Color` by a float darkens all channels including alpha, so reset alpha to 1.

Marching Squares:

- Edge crossings are computed in a canonical corner order (the lower row-major corner first) so a shared edge yields identical points from both adjacent cells. This lets `ChainSegments` match endpoints exactly. Computing the same crossing from each cell in a different corner order can differ in the last float bit and break chaining; chaining also quantizes endpoints to a grid as a safety net.
- Saddle cases (5 and 10) emit two segments; the chosen resolution keeps the two same-side corners together. The choice is rarely visible.
- `ContourField.Build` derives levels from world heights at each interior multiple of the interval; a level is major when its index `round(H / interval)` is a multiple of `majorEvery`.

Alignment and resolution:

- Contours are extracted at the full buffer resolution (2048) so the line crossing matches the per-pixel tint's crossing; extracting at a lower resolution shifts the crossings slightly and the band color bleeds past the line.
- The contour line covers the hard tint band edge, so the band stepping needs no anti-aliasing of its own.
- Final detail is bounded by the terrain itself (the demo mesh is a 511-subdivision plane over a 512x512 heightmap). Contours are crisp at any zoom, but they trace that resolution; we accepted "crisp lines, current detail" rather than re-baking a higher-resolution heightmap.

Project and build:

- `Godot.NET.Sdk` compiles every `.cs` under the project directory. The standalone test project under `tests/` has its own entry point and links the pure sources directly, so it must be excluded from the game build with `<Compile Remove="tests/**/*.cs" />` in `TopographicMap.csproj`.
- The game targets `net8.0`; `dotnet build` does not need the net8 runtime installed. The console test project must target a runtime that is installed (net9 here) so `dotnet run` can launch.
- The world map is kept a centered square sized to the shorter screen dimension so the square world is not horizontally stretched on a non-square screen. Zoom and pan are expressed as the sampling window (`window_center`, `window_span`) shared by the tint shader and the contour layer; nothing magnifies a pre-rendered image.

## Performance and load behavior

- The contour build (readback parse + Marching Squares + chaining + simplification) is the only heavy one-time cost. It runs on a background thread via `Task.Run`; the continuation resumes on the main thread (Godot installs a synchronization context), so assigning `Field` and calling `QueueRedraw` after the `await` is main-thread-safe. Running it inline on the main thread caused a visible startup stutter.
- Within the build, the contour levels are extracted in parallel (`Parallel.For`), since each level is independent.
- Polylines are simplified with Ramer-Douglas-Peucker (`simplifyEpsilon`, default `0.00015` normalized, about 1px at max zoom, so visually lossless) so far fewer points are transformed and drawn each frame.
- `ContourLayer.SetWindow` redraws only when the window changes, so an idle open map costs nothing even though `_Process` calls it every frame.
- Remaining wait: because the contours derive from the height buffer, which needs a frame to render, they cannot be ready on the very first frame. The parallel build reduces the gap to a brief flash. The decided path to true zero-wait is baking (see follow-ups).

## Tuning parameters

- Contour appearance is on the two `Contours` (`ContourLayer`) nodes: `MinorWidthPx`, `MajorWidthPx`, `ContourDynamic` (gradient-derived vs static color), `ColorRamp`, `ContourDarken`, `LineColor`.
- Contour levels are on `MapUi`: `ContourInterval`, `MajorEvery`, `HeightMin`/`HeightMax`, and `ContourResolution` (extraction grid; lower is faster and smoother but misaligns the tint).
- The palette is the `color_ramp` gradient (a `GradientTexture1D`) assigned to both tint materials and both contour layers. It is the single source of all map color; water is just the gradient's low end. The demo uses `addons/topographic/gradients/hypsometric_deep.tres`; the addon ships several preset gradients in that folder.
- The marker overlays expose `marker_color` and `outline_color` (on the material) and `MarkerScreenSize` (on `MapUi`).

## Known limitations and follow-ups

- DECIDED NEXT STEP (not yet implemented): bake the contours at edit time so they are present on the first frame with zero runtime cost, consistent with how the heightmap and collision are already baked. Plan: add a serializable contour resource (flatten `ContourField` into packed arrays plus the level params; recompute bounding boxes and major flags on load), extend `TerrainBaker` to build the field from the committed `heightmap.exr` (bilinearly upsampled to 2048 so the line crossings still match the 2048 buffer tint and band edges do not bleed) and `ResourceSaver.Save` it, and have `MapUi` load that resource instantly instead of extracting at runtime. Keep the runtime `ContourExtractor` in the addon for the general (non-static or non-heightmap) case. The user runs the baker once (`godot --headless --path . --script res://TopoDemo/scripts/tools/TerrainBaker.cs`), same as the heightmap and collision.
- The player marker is done: a constant-size, `fwidth`-antialiased SDF arrow UI overlay on each map (`marker_overlay.gdshader`), replacing the removed in-world marker quad. The old `marker.gdshader` was deleted. Verify the heading sign once in-editor.
- Contour extraction is one-time (static terrain). `ContourField`/`MapUi` could expose a rebuild path for terrain that changes; real-time per-frame extraction of fast-changing terrain is not built.
- The addon's `TopographicCompositorEffect` still produces the height buffer and is in use; the old per-pixel contour code in the styling shader was removed.
