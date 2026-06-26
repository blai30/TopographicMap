using Godot;

namespace TopographicMap;

// Single source of truth for the topographic map's look: palette, elevation range,
// contour interval/major spacing, and line style. One .tres shared by the producer
// (height range + interval) and the consumer views (everything), so the palette and
// levels live in exactly one place.
[GlobalClass]
public partial class TopographicSettings : Resource
{
    [Export] public GradientTexture1D ColorRamp { get; set; }
    [Export] public float HeightMin { get; set; } = -40.0f;
    [Export] public float HeightMax { get; set; } = 110.0f;
    [Export] public float ContourInterval { get; set; } = 10.0f;
    [Export] public int MajorEvery { get; set; } = 5;
    [Export] public float MinorWidthPx { get; set; } = 1.1f;
    [Export] public float MajorWidthPx { get; set; } = 1.9f;
    [Export] public float ContourDarken { get; set; } = 0.6f;
}
