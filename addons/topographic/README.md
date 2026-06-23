# Topographic map effect (Godot 4.7+)

A depth-based topographic post-process for Godot. It recolors whatever a camera renders into a flat, stepped, monochrome topographic map (one ink hue on a paper background, elevation quantized into shade steps with contour lines), using only the depth buffer, so the scene needs no special materials.

It is a `CompositorEffect`, so it attaches to a camera as a resource with **no extra code**. Use it on a minimap/world-map `SubViewport` camera, or on the main camera for a full-screen effect.

## Requirements

- Godot 4.7+, **.NET / C# edition** (the effect is a C# script).
- **Forward+** or **Mobile** renderer. The **Compatibility** renderer is not supported (it handles depth-texture access differently).

## Install

Copy the `addons/topographic/` folder into your project. Build the C# solution once so `TopographicEffect` is registered.

## Use

1. Select the `Camera3D` you want the map effect on (for a minimap, the camera inside your map `SubViewport`).
2. In the inspector, set its **Compositor** property to `res://addons/topographic/topographic_compositor.tres` (or a duplicate, so per-camera tweaks do not affect other cameras sharing the resource).
3. Tune the look on the effect inside that resource (see below).

That is all. The effect runs only when that camera renders, so it does not touch other cameras or the editor preview.

## Parameters

Edited on the `TopographicEffect` inside the compositor resource.

| Group | Property | Meaning |
| --- | --- | --- |
| Ramp | `MinElevation` / `MaxElevation` | World-Y range the shade ramp spans. Set these to your terrain's height range. |
| Ramp | `Levels` | Number of flat shade steps / contour lines. The main "busyness" dial. |
| Ramp | `InkColor` / `PaperColor` | The map hue (low elevation) and background (high elevation). |
| Ramp | `FillLow` / `FillHigh` | Shade at the low and high ends of the ramp (overall contrast). |
| Ramp | `SmoothRamp` | Continuous gradient instead of discrete steps. |
| Ramp | `InvertRamp` | Flip the color-to-elevation mapping (high = dark). |
| Contours | `ContoursEnabled` | Master toggle for all contour lines. |
| Contours | `MajorContoursEnabled`, `MajorEvery` | Bold index lines every N steps. |
| Contours | `MinorWidthPx` / `MajorWidthPx` | Line widths in pixels. |
| Contours | `MinorOpacity` / `MajorOpacity` / `MinorFade` | Line opacity and slope-based fade. |
| Background | `BackgroundColor` | Color for pixels with no geometry (empty space). |

## Notes

- The effect self-frees its GPU resources when removed at runtime. At application shutdown the rendering device may be torn down first, so the resource IDs leak then; this is harmless, the process is exiting.
- Because it reads the depth buffer, geometry must sit strictly between the camera's near and far planes (nothing touching either plane), or it reads as empty background.
