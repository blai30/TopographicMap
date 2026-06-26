![Wide topographic map banner: dense contour lines over rich relief in a sepia ink-on-paper style](screenshots/banner.png)

# Topographic Map (Godot 4.7, C#)

An all-GPU topographic map for Godot: a depth-derived height buffer, a hypsometric (elevation) color tint, and crisp constant-width contour lines, drawn in one shader with no CPU work and no bake step. This repository contains the reusable addon (`addons/topographic/`) plus a demo project (`TopoDemo/`) showing it as a minimap and a pan/zoom world map.

## Gradient presets

Every map uses a `GradientTexture1D` palette that maps elevation to color, shared by both the hypsometric tint and the contour lines. The addon ships twelve presets (set one on the material's `elevation_gradient`); the same terrain through each:

|                                                                                                 |                                                                                           |                                                                   |
| ----------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `classic_ink` ![classic_ink preset](screenshots/preset-classic_ink.png)                         | `sepia_vintage` ![sepia_vintage preset](screenshots/preset-sepia_vintage.png)             | `grayscale` ![grayscale preset](screenshots/preset-grayscale.png) |
| `hypsometric_classic` ![hypsometric_classic preset](screenshots/preset-hypsometric_classic.png) | `hypsometric_atlas` ![hypsometric_atlas preset](screenshots/preset-hypsometric_atlas.png) | `nautical` ![nautical preset](screenshots/preset-nautical.png)    |
| `heatmap` ![heatmap preset](screenshots/preset-heatmap.png)                                     | `magma` ![magma preset](screenshots/preset-magma.png)                                     | `alpine` ![alpine preset](screenshots/preset-alpine.png)          |
| `blueprint` ![blueprint preset](screenshots/preset-blueprint.png)                               | `matrix` ![matrix preset](screenshots/preset-matrix.png)                                  | `viridis` ![viridis preset](screenshots/preset-viridis.png)       |

## Requirements

- **Godot 4.7+, .NET / C# edition** (the build that supports C#).
- **.NET SDK 10.0** (the project targets `net10.0`).
- **Forward+** renderer (the producer uses compute shaders).

## Run the demo

1. Open the Godot 4.7 .NET editor and **Import** this folder (`project.godot`).
2. Let it build the C# assembly once (or click **Build** / run `dotnet build`).
3. Press **Play** (F5). `TopoDemo/scenes/DemoMinimap.tscn` is the main scene. To preview the topographic effect on its own, open `TopoDemo/scenes/DemoTerrain.tscn` instead.

Move with `W` `A` `S` `D`, look with the mouse, and press `M` (or `Tab`) to open the full-screen world map. See the [demo walkthrough](docs/topographic-demo-walkthrough.md) for the full controls and a tour.

## Documentation

- [Addon README](addons/topographic/README.md): install, quickstart, the full parameter reference, tuning recipes, game integration, and troubleshooting. Start here to use the addon in your own project.
- [Demo walkthrough](docs/topographic-demo-walkthrough.md): a guided tour of the demo project and how to learn from it.
- [Architecture notes](docs/topographic-map-architecture.md): how the system works internally and the design reasoning behind it.

## License

MIT. See [`addons/topographic/LICENSE`](addons/topographic/LICENSE).
