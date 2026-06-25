# Topographic Map (Godot 4.7+)

A depth-based topographic post-process for Godot.

## Requirements

- Godot 4.7+, **.NET / C# edition** (the effect is a C# script).
- **Forward+** or **Mobile** renderer.

## Install

1. Copy the `addons/topographic/` folder into your project (Forward+ or Mobile renderer, C# edition).
2. Build the C# assembly once.

## Usage

1. Put the terrain you want mapped on a dedicated render layer.
2. Create a `SubViewport` with an orthographic `Camera3D` looking straight down, with its `cull_mask` set to the terrain layer (plus a marker layer if used).
3. Create a `Compositor`, add a `TopographicCompositorEffect` to its effects, and assign the compositor to the top-down camera. Set `CameraY`, `NearPlane`, and `FarPlane` to bracket the terrain height range; set `HeightMin`, `HeightMax`, and `ContourInterval` for the elevation banding.
4. Display the `SubViewport`'s `ViewportTexture` in a `TextureRect`.

## Gradient presets

The `ColorRamp` property takes a `GradientTexture1D` that maps elevation
(`HeightMin`..`HeightMax`) to color. Ready-made presets live in `gradients/`:
`hypsometric_classic`, `hypsometric_atlas`, `alpine`, `sepia_vintage`,
`grayscale`, `viridis`, `blueprint`, `heatmap`, and `nautical`. Drop one into
the `ColorRamp` slot, or duplicate and edit it. When `ColorRamp` is left empty a
built-in hypsometric ramp is used.

## License

MIT. See `LICENSE`.
