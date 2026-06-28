# Topographic Map (Godot 4.7+)

An all-GPU topographic map for Godot: a depth-derived height buffer, a hypsometric (elevation) color tint, and crisp constant-width contour lines, drawn in one shader with no CPU work and no bake step. Use it for a minimap, a full-screen pan/zoom world map, or any UI panel that shows the terrain as a contour map.

## Contents

- [What you get](#what-you-get)
- [Requirements](#requirements)
- [Install](#install)
- [Quickstart](#quickstart)
- [How it works](#how-it-works)
- [Parameter reference](#parameter-reference)
- [Tuning recipes](#tuning-recipes)
- [Using it in a game](#using-it-in-a-game)
- [Editor preview](#editor-preview)
- [Gradient presets](#gradient-presets)
- [Troubleshooting](#troubleshooting)
- [See also](#see-also)
- [License](#license)

## What you get

The system has two halves: a **producer** that turns your 3D terrain into map data on the GPU, and a **consumer** shader that draws the map from that data. You can have many consumers (a minimap and a world map, say) reading the same producer.

```
PRODUCER  (once, on a top-down camera)            CONSUMER  (per map, every frame)
  TopDownCamera (orthographic) renders terrain       a ColorRect with topographic.gdshader
        |  depth buffer                                    | samples height + segments
        v                                                  | over a pan/zoom window
  TopographicCompositorEffect (compute):              draws:
    height pass: depth  -> R = height, G = mask         - hypsometric tint (from height)
    seed pass:   height -> per-cell contour segments    - constant-width contour lines
        |                                                  |
        +------ height buffer + segment texture ----------+
```

Because both outputs come from the same camera and the tint and lines are computed in the same shader, the colors and the lines always line up and move together when you pan and zoom. The contour lines are analytic vector geometry, so they stay crisp and exactly one width at any zoom, with no baked asset to regenerate.

## Requirements

- Godot 4.7+, **.NET / C# edition** (the compositor effect is a C# script).
- **Forward+** renderer (the producer uses a `CompositorEffect` with compute shaders, which the Mobile and Compatibility renderers do not support).

## Install

1. Copy the `addons/topographic/` folder into your project.
2. Build the C# assembly once (Godot builds it on first run, or run `dotnet build`).
3. Enable the plugin in Project Settings > Plugins (optional: `TopographicCompositorEffect` is a `[GlobalClass]`, so it is usable whether or not the plugin is enabled).

## Quickstart

This is the minimum to get a working minimap. The [demo](#see-also) wires up the full minimap + world map; copy from it when you want the complete setup.

1. **Put the terrain on its own render layer.** Give the terrain `MeshInstance3D` a unique visual layer (for example layer 2) so the map camera can render only the terrain.
2. **Add the map camera in a SubViewport.** Create a `SubViewport` with `use_hdr_2d = true` (required, see [Troubleshooting](#troubleshooting)), then an orthographic `Camera3D` inside it looking straight down (`projection = Orthographic`, rotated to point down `-Y`). Set the camera `cull_mask` to the terrain layer, its `size` to span the terrain, and `near`/`far` to bracket the terrain's height range. Give the camera an `Environment` override with `background_mode = Color` and a linear tonemap so the stored height is not distorted.
3. **Attach the producer.** Create a `Compositor`, add a `TopographicCompositorEffect`, and assign the compositor to the camera. Set the compositor's `HeightMin`, `HeightMax`, `ContourInterval`, and the camera-rig params (`CameraY`, `NearPlane`, `FarPlane`, `DepthReversed`) to match your camera.
4. **Add a map ColorRect.** Put a `ColorRect` in your HUD and give it a `ShaderMaterial` running `topographic.gdshader`. Set the look params (see [Parameter reference](#parameter-reference)); at minimum pick an `elevation_gradient`. The height range and contour interval are owned by the compositor and pushed in at runtime (step 5), so you do not set them on the material. For a live editor preview and automatic binding, use a `TopographicMapView` node instead of a bare `ColorRect` (see [Editor preview](#editor-preview)).
5. **Bind and drive it from a script.** Once, bind the two runtime textures; every frame, set the window. See [Using it in a game](#using-it-in-a-game) for the code.

## How it works

1. An orthographic top-down `Camera3D` renders the terrain into a `SubViewport`.
2. `TopographicCompositorEffect` runs two compute passes over the camera depth buffer:
   - a **height pass** that writes `R` = normalized world height and `G` = a coverage mask into the `RGBA16F` color image (`B`/`A` unused), and
   - a **seed pass** that runs per-cell marching squares and writes the contour segment crossing each grid cell (both endpoints, in UV) into a separate persistent `RGBA32F` texture, one `(x0, y0, x1, y1)` per cell.
3. A `ColorRect` running `topographic.gdshader` samples the height buffer and the segment texture over a pan/zoom window. For each display pixel it takes the exact point-to-segment distance to the nearest cell's contour, so it draws crisp constant-width anti-aliased lines at any zoom, plus the stepped hypsometric tint. The line's elevation is read from the local height, so nothing is stored per line.

No bake, no `.res` to regenerate, no per-frame CPU cost. Change the terrain and the lines follow automatically the next time the buffer renders. For the full design and the reasoning behind it, see [the architecture notes](../../docs/topographic-map-architecture.md).

## Parameter reference

The **look** lives on each map's `ShaderMaterial` (the consumer). The **elevation model** (the height range and the contour interval) lives only on the compositor, which is its single owner; a driver script pushes it into each consumer at load, so there is nothing to keep in sync (see the [elevation model note](#elevation-model-note) below).

### Consumer: `topographic.gdshader` parameters

Grouped in the inspector exactly as below. Hover any parameter in the editor for its tooltip.

**Elevation**

| Parameter | Default | What it does |
| --- | --- | --- |
| `elevation_gradient` | (none) | A `GradientTexture1D` mapping elevation to color. Colors both the tint bands and the contour lines. Presets in `gradients/`. |

The height range (`height_min`/`height_max`) that this gradient spans is owned by the compositor and pushed in at runtime; see the **Runtime** group below.

**Contours**

| Parameter | Default | Range | What it does |
| --- | --- | --- | --- |
| `lines_per_major` | 5 | 1..20 | Every Nth line is drawn as a major (thicker) line. |
| `minor_line_width_px` | 0.6 | 0..8 | Minor line width, in screen pixels (constant at any zoom). |
| `major_line_width_px` | 0.8 | 0..8 | Major line width, in screen pixels. |

**Contours > Color**

| Parameter | Default | Range | What it does |
| --- | --- | --- | --- |
| `line_color` | white | (color) | The fixed custom line color, used when `line_color_from_gradient` is below 1. |
| `line_color_from_gradient` | 1.0 | 0..1 | Blends the line color between `line_color` (0) and a gradient-derived color (1). |
| `line_gradient_lightness` | -0.05 | -1..1 | For the gradient-derived color: negative darkens toward black, positive lightens toward white. |
| `line_gradient_shift` | -0.3 | -0.5..0.5 | For the gradient-derived color: offsets where the line samples the gradient, picking a neighboring elevation's hue. |

**Runtime** (`height_min`, `height_max`, `contour_interval`, `height_buffer`, `segments`, `window_center`, `window_span`, `px_per_uv`)

These are set from code, not authored on the material (see [Using it in a game](#using-it-in-a-game)). The elevation model (`height_min`, `height_max`, `contour_interval`) is pushed once from the compositor's matching exports so it always agrees with the producer; the textures and the window (`window_*`, `px_per_uv`) are bound by the driver script, the window every frame. Do not hand-edit any of them in the inspector; any value you set is overwritten at runtime.

### Producer: `TopographicCompositorEffect` exports

| Export | What it does |
| --- | --- |
| `HeightMin`, `HeightMax` | The world-height range the height pass normalizes into `[0, 1]`. The single owner of the range; pushed to each consumer's `height_min`/`height_max` at runtime. |
| `ContourInterval` | World-height step the seed pass uses to place contour segments. The single owner of the interval; pushed to each consumer's `contour_interval` at runtime. |
| `CameraY` | The map camera's height (Y). Used to reconstruct world height from depth. |
| `NearPlane`, `FarPlane` | The map camera's near/far planes. |
| `DepthReversed` | Whether the depth buffer is reversed-Z (Godot's default is `true`). |
| `ContourSmoothness` | Optional pre-seed blur of the height buffer, in buffer texels (`0..8`, default `4`; `0` = off). Smooths rough/high-frequency terrain into flowing contours without altering the terrain itself. Because the lines and the tint bands read the same buffer, both smooth together and stay aligned. Set to `0` for crisp, exact contours. |

<a id="elevation-model-note"></a>
> **Elevation model note.** `height_min`/`height_max`/`contour_interval` are owned solely by the compositor: the seed pass uses them to place the lines, and the consumer needs them for the tint and the line levels. The consumer does not author its own copy. A driver script reads them off the compositor and pushes them into each map material at load, so the lines and the color bands always coincide with nothing to keep in sync. See [Using it in a game](#using-it-in-a-game) for the binding code.

## Tuning recipes

- **Denser or sparser contours:** lower or raise `ContourInterval` on the compositor (its single owner; the consumers pick it up at load). Smaller interval = more lines.
- **Emphasize index contours:** raise `major_line_width_px` relative to `minor_line_width_px`, and set `lines_per_major` to taste (5 is a common cartographic choice).
- **Thinner, sharper lines:** lower both width params toward `0.5`. Widths are in screen pixels and stay constant at any zoom.
- **Classic look (dark lines on a light palette):** keep `line_color_from_gradient = 1` with a light gradient and `line_gradient_lightness` negative (darkens the band color into a line).
- **Fixed-color lines (for example white on a dark palette like `blueprint`):** set `line_color` to the color you want and `line_color_from_gradient = 0`.
- **Dynamic lines on a dark palette:** keep `line_color_from_gradient = 1` but make `line_gradient_lightness` positive so the lines lighten toward white instead of dying to black.
- **Shift the line hue away from its band:** nudge `line_gradient_shift` so each line samples a neighboring elevation's color.
- **Smooth, flowing contours on rough terrain:** the compositor's `ContourSmoothness` defaults to `4`, which blurs the height buffer before the seed pass so the lines and the tint bands smooth together without touching your terrain. Raise it toward `8` for rougher terrain, or set it to `0` for crisp, exact contours.
- **Change the whole color scheme:** swap the `elevation_gradient` for another preset, or edit a `GradientTexture1D` of your own (see [Gradient presets](#gradient-presets)).

## Using it in a game

The consumer shader needs three things from code: the elevation model and the two runtime textures (bound once from the compositor), and the sampling window (set every frame). Everything else (the gradient and the line styling) lives on the material.

```csharp
// Bind once (for example in _Ready). Push the elevation model from the compositor, its
// single owner, so the tint and the seeded lines always agree:
var material = (ShaderMaterial)mapRect.Material;
material.SetShaderParameter("height_min", compositor.HeightMin);
material.SetShaderParameter("height_max", compositor.HeightMax);
material.SetShaderParameter("contour_interval", compositor.ContourInterval);
// Bind the runtime textures:
material.SetShaderParameter("height_buffer", mapViewport.GetTexture());
material.SetShaderParameter("segments", compositor.SegmentTexture);

// Every frame, set the window the map should show. The window is in buffer-UV space:
//   center = the buffer-UV point at the middle of the view, each axis in [0, 1]
//   span   = how much of the buffer to show (1 = the whole map, smaller = zoomed in)
float span = ...;
Vector2 center = ...;
material.SetShaderParameter("window_center", center);
material.SetShaderParameter("window_span", new Vector2(span, span));
// px_per_uv keeps the line width constant on screen at any zoom:
material.SetShaderParameter("px_per_uv", mapRect.Size.X / span);
```

Mapping world position to buffer UV is `buffer_uv = world.xz / terrainSize + 0.5` (in the demo `terrainSize` is 1536), assuming the map camera is centered on the terrain.

- **Minimap:** a small fixed window centered on the player. Set `center = WorldToUv(playerPosition)` and a constant small `span` (the fraction of the world the minimap shows).
- **World map:** a pan/zoom window. Track a pan center and a zoom level, derive `span = 1 / zoom`, and update `center`/`span` as the player drags and scrolls.

**First-frame caveat.** The compositor renders once at load. A map that is visible at startup will try to sample the segment texture before the producer's first render and fail with "Uniforms were never supplied for set (1)". Guard it: the compositor exposes a `HasProduced` flag; keep any always-visible map hidden until `HasProduced` is true (a map that opens later, like a world map behind a key press, is fine). The demo's `MapUi` does exactly this.

For a complete, working implementation of both a minimap and a pan/zoom world map (including the constant-width math, the first-frame guard, and the player marker overlay), read `TopoDemo/scripts/MapUi.cs` and follow the [demo walkthrough](../../docs/topographic-demo-walkthrough.md).

## Editor preview

The binding above can run while you author, so the map renders in the editor without pressing Play. Use the `TopographicMapView` node, a `[Tool]` `ColorRect` subclass, in place of a bare map ColorRect: give it the same `ShaderMaterial`, then set its two exports, `Compositor` (the `TopographicCompositorEffect`) and `HeightSource` (the map `SubViewport`). It binds `segments`, `height_buffer`, and the elevation model for you, in the editor and at run time, so you no longer write the "bind once" code above. It does not own the window: a driver still sets `window_center`/`window_span`/`px_per_uv` each frame at run time. In the editor the node derives a preview `px_per_uv` from the `window_span` you author on the material (which also frames the preview).

- The producer's SubViewport must render in the editor for the preview to appear. With `render_target_update_mode = Always` the preview is live; with `Once` it renders when the scene loads.
- The node clears its injected inputs before a scene save and restores them after, so it does not write runtime values into your material. Texture params clear fully; the four float params settle at their shader defaults (a harmless Godot limitation, since float shader-param overrides cannot be erased).
- With either export unset the node is inert (no editor preview), so it is safe to drop in before wiring.

## Gradient presets

`elevation_gradient` takes a `GradientTexture1D` that maps elevation (`height_min`..`height_max`) to color. Ready-made presets live in `gradients/`: `hypsometric_classic`, `hypsometric_atlas`, `alpine`, `sepia_vintage`, `classic_ink`, `grayscale`, `viridis`, `blueprint`, `heatmap`, `magma`, `matrix`, and `nautical`. Drop one into the `elevation_gradient` slot on the material, or duplicate and edit it. The same ramp colors both the bands and the contour lines, so the map stays consistent.

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Contours look terraced or stepped in flat areas | The SubViewport is 8-bit. Set `use_hdr_2d = true` on the map SubViewport so the height is stored at full precision. |
| Colors or contour heights look wrong or washed out | The map camera is applying scene tonemapping to the stored height. Give the camera an `Environment` override with `background_mode = Color` and a linear (non-ACES) tonemap. |
| "Uniforms were never supplied for set (1)" on the first frame | A map drew before the producer's first render. Keep always-visible maps hidden until `compositor.HasProduced` is true (see [Using it in a game](#using-it-in-a-game)). |
| The color bands and the contour lines do not line up | The consumer's `height_min`/`height_max`/`contour_interval` were not pushed from the compositor, or a stale value was hand-authored on the material. Bind them from the compositor at load (see [Using it in a game](#using-it-in-a-game)). |
| Contour lines never appear / the map is a flat tint | The producer is not running. Confirm the renderer is Forward+, the compositor is on the map camera, the camera `cull_mask` includes the terrain layer, and the camera near/far/size actually frame the terrain. |
| Lines are invisible on a dark palette | They are gradient-derived and darkening to near-black. Set `line_color_from_gradient = 0` with a `line_color`, or make `line_gradient_lightness` positive. |
| The terrain changed but the map did not | The producer renders once (`render_target_update_mode = Once`). For dynamic terrain, raise the SubViewport's update mode so the passes re-run. |
| The map is blank in the editor (not playing) | Use a `TopographicMapView` node with its `Compositor` and `HeightSource` exports set (a bare `ColorRect` has nothing binding its runtime inputs at author time), and make sure the map SubViewport's `render_target_update_mode` renders in the editor. See [Editor preview](#editor-preview). |
| Renamed an addon file and the project fails to load | Renaming resource files outside the editor leaves stale `.import`/UID caches. Let the editor rescan (reopen it, or run `godot --headless --import`). |

## See also

- [Demo walkthrough](../../docs/topographic-demo-walkthrough.md): a guided tour of the demo project and how to learn from it.
- [Architecture notes](../../docs/topographic-map-architecture.md): how the system works internally and why it is built this way.

## License

MIT. See `LICENSE`.
