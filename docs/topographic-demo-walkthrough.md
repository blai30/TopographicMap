# Topographic Map: Demo Walkthrough

This is a guided tour of the demo project (`TopoDemo/`). It exists to show the `addons/topographic/` system wired into a real scene: a first-person player walking a baked continent, with a corner minimap and a toggleable full-screen pan/zoom world map. Read this to learn how the pieces connect and where to look when you build your own map.

If you only want the API, the [addon README](../addons/topographic/README.md) is the reference. If you want the internals and the design reasoning, see the [architecture notes](topographic-map-architecture.md). This document is the bridge between them: how the demo puts the addon to use.

## Run it

1. Open the project in the Godot 4.7 .NET editor and let it build the C# assembly once.
2. Press Play (F5). The main scene is `TopoDemo/scenes/DemoMinimap.tscn`.

Controls:

| Input | Action |
| --- | --- |
| `W` `A` `S` `D` | Move |
| Mouse | Look |
| `Shift` | Sprint |
| `Space` | Jump |
| `M` or `Tab` | Toggle the full-screen world map |
| `Esc` | Release / recapture the mouse |

The minimap sits in the top-right corner the whole time. Press `M` to open the world map, then drag to pan and scroll to zoom; the red arrow marks the player and rotates with the heading.

## The scene at a glance

```
Demo (Node3D)
├── Sun, WorldEnvironment                  scene lighting and sky
├── Water (MeshInstance3D)                 water plane (water_material -> water.gdshader)
├── Terrain (MeshInstance3D)               the continent mesh, on visual layer 2
├── TerrainBody / TerrainCollision         static collision (baked terrain_collision.res)
├── MapView (SubViewport, use_hdr_2d)      <- the PRODUCER lives here
│   └── TopDownCamera (Camera3D, ortho)       cull_mask = layer 2, Compositor attached
├── Player (CharacterBody3D)               PlayerController; Body node carries the heading
│   ├── CameraPivot (SpringArm3D) / MainCamera
│   └── Body / BodyMesh
└── Hud (CanvasLayer)
    └── MapUi (Control)                    <- the CONSUMER orchestration lives here
        ├── Minimap (ColorRect)               ShaderMaterial_minimap (topographic.gdshader)
        │   └── Marker (ColorRect)             marker_overlay.gdshader
        └── WorldMapOverlay (ColorRect)        full-screen dimmer / container
            └── WorldMap (ColorRect)           ShaderMaterial_worldmap (topographic.gdshader)
                └── Marker (ColorRect)         marker_overlay.gdshader
```

Two parts matter most. **`MapView`** holds the orthographic top-down camera with the `TopographicCompositorEffect` on its compositor; this is the producer that turns the terrain into the height buffer and segment texture. **`MapUi`** holds the two map `ColorRect`s and the script that binds the producer's outputs into their shaders and drives the pan/zoom window every frame.

Note the two layers under `WorldMapOverlay`: the overlay itself is a full-screen translucent-black `ColorRect` that dims the game behind the open world map, and `WorldMap` is the actual map (a centered square so the square world is not stretched on a non-square screen).

## Suggested reading order

1. **`TopoDemo/scripts/MapUi.cs`** first. This is the whole integration in one file: binding the textures, driving the window, placing the markers. If you read one thing, read this.
2. **`TopoDemo/scenes/DemoMinimap.tscn`** next, in the editor. Select `MapView/TopDownCamera` and look at its Compositor (the producer), then select `Hud/MapUi/Minimap` and `.../WorldMap` and look at their materials (the consumer look params).
3. **The addon** (`addons/topographic/`): `TopographicCompositorEffect.cs` (the producer) and `topographic.gdshader` (the consumer). The [addon README](../addons/topographic/README.md) explains each parameter.
4. **`TopoDemo/scripts/TerrainBaker.cs`** last, only if you care how the demo terrain was generated. It is an edit-time tool, not part of the running map.

## File-by-file guide

### `scripts/MapUi.cs` (the integration centerpiece)

Drives the HUD map. At `_Ready` it binds each map `ColorRect`'s `height_buffer` to the `MapView` viewport texture and `segments` to the compositor's `SegmentTexture` (its `BindTextures` helper). Each frame it computes a sampling window and pushes `window_center`/`window_span`/`px_per_uv` into the right material (its `SetWindow` helper), and positions the marker overlays. The minimap uses a fixed player-centered window; the world map uses a pan/zoom window with drag and scroll handling. It also holds the first-frame guard: the always-visible minimap stays hidden until `MapCompositor.HasProduced` is true, so it never samples the segment texture before the producer has run. This file is the concrete version of the [Using it in a game](../addons/topographic/README.md#using-it-in-a-game) section.

### `scenes/DemoMinimap.tscn` (the wiring)

The scene that connects everything. The producer side is `MapView` (a `use_hdr_2d` SubViewport at 2048x2048, set to render `Once`) with `TopDownCamera` (orthographic, `cull_mask` = the terrain layer, a linear-tonemap `Environment` override, and the compositor). The consumer side is under `Hud/MapUi`: two `ColorRect`s with their own `ShaderMaterial`s running `topographic.gdshader`, each with a marker child. `MapUi` exports point at these nodes and at the compositor resource so it can read `SegmentTexture`.

### `assets/map_view_compositor_effect.tres`

The `TopographicCompositorEffect` resource assigned to the map camera's compositor. Its `HeightMin`/`HeightMax`/`ContourInterval` and camera-rig exports are the producer half of the look; keep them matched with the two map materials.

### `assets/terrain_material.tres` + `shaders/terrain.gdshader`, `assets/water_material.tres` + `shaders/water.gdshader`

The ordinary 3D look of the terrain and water in the gameplay view. These are not part of the topographic addon; they are here so the demo world looks like something. The map does not read them; it reads the camera depth buffer.

### `assets/heightmap.exr`, `assets/terrain_collision.res`

Static baked outputs: the normalized heightmap that drives the terrain mesh displacement and the collision shape the player walks on. They are committed assets, produced by the baker below. The contour lines do not come from these; they are derived live from the rendered depth buffer.

### `scripts/TerrainBaker.cs` (edit-time only)

A headless command-line tool that generates the demo continent (smooth single-octave simplex relief, an ocean in the SW corner, a smooth inland lake, and a flattened spawn) and writes `heightmap.exr` (512x512) and `terrain_collision.res`. It also bakes `banner_heightmap.exr`, the terrain used by `DemoTerrain.tscn` and the README screenshots. It is never referenced by the game scene, so the shipped game contains no generator. Re-run it only if you want to change the terrain:

```
godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs
```

### `scripts/PlayerController.cs`

A standard first-person `CharacterBody3D` controller (move, sprint, jump, mouse-look with a third-person spring arm). The one detail that matters for the map: it rotates the `Body` node to the movement heading, and `MapUi` reads that node's yaw to rotate the player marker on both maps.

## How the data flows (concrete recap)

1. `MapView`'s `TopDownCamera` renders the `Terrain` (layer 2) into the SubViewport. Its depth buffer is the raw input.
2. `TopographicCompositorEffect` (on that camera) runs its two compute passes once, producing the height buffer (the SubViewport color texture) and the segment texture (`SegmentTexture`).
3. At `_Ready`, `MapUi` binds those two textures into both map materials.
4. Each frame, `MapUi` sets each map's window: the minimap to a small window centered on the player, the world map to its current pan/zoom window. The shader samples the height buffer and segment texture over that window and draws the tint and contour lines.
5. `MapUi` places each marker `ColorRect` at the player's screen position within the window and rotates it to the `Body` heading.

The producer runs once because the terrain is static. If you made the terrain dynamic, you would raise the SubViewport's update mode and the map would follow automatically, with no other change.

## Experiments to try

- **Change a palette.** Select `Hud/MapUi/Minimap`, open its material, and swap `elevation_gradient` for another preset from `addons/topographic/gradients/`. Do the same on `WorldMap` to see them differ independently.
- **Make the contours denser.** Lower `contour_interval` on both map materials *and* on `assets/map_view_compositor_effect.tres` (all three must match). Watch the lines multiply.
- **Recolor the lines.** On a map material, try `line_color_from_gradient = 0` with a `line_color` of your choice for fixed-color lines, or push `line_gradient_lightness` positive on a dark gradient.
- **Resize the minimap.** Change the `Minimap` `ColorRect`'s size; the lines stay the same screen-pixel width because `px_per_uv` is derived from the view size.
- **Add a third map.** Duplicate the `Minimap` node, give it a new `ShaderMaterial`, add it to `MapUi`'s exports, and drive it with its own window. Two consumers already share one producer, so a third is free.
- **Regenerate the terrain.** Tweak the noise in `TerrainBaker.cs`, re-run the baker, and reopen the scene. The map updates on its own because the contours are derived from whatever the camera renders.

## Where to go next

- [Addon README](../addons/topographic/README.md): the parameter reference, tuning recipes, and the integration API.
- [Architecture notes](topographic-map-architecture.md): how the producer and consumer work internally, the design history, and the gotchas behind the current shape.
