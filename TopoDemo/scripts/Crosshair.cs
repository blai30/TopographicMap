using Godot;

namespace TopographicMap.TopoDemo;

// A simple center-screen flight reticle. Self-managing: it is visible only while the mouse
// is captured (free-look play), so it hides at startup before the first click, on Escape,
// and whenever the world map is open, since all of those release the mouse. It fills the
// screen via full-rect anchors and draws at its center; mouse_filter is set to Ignore in the
// scene so it never eats clicks.
public partial class Crosshair : Control
{
    [Export] public Color LineColor = new(1.0f, 1.0f, 1.0f, 0.6f);
    [Export] public float Gap = 6.0f;
    [Export] public float Length = 10.0f;
    [Export] public float Thickness = 2.0f;
    [Export] public float DotRadius = 1.5f;

    public override void _Process(double delta)
    {
        Visible = Input.MouseMode == Input.MouseModeEnum.Captured;
    }

    public override void _Draw()
    {
        var center = Size * 0.5f;

        // Four ticks around a center gap, plus a center dot.
        DrawLine(center + new Vector2(-Gap - Length, 0.0f), center + new Vector2(-Gap, 0.0f), LineColor, Thickness);
        DrawLine(center + new Vector2(Gap, 0.0f), center + new Vector2(Gap + Length, 0.0f), LineColor, Thickness);
        DrawLine(center + new Vector2(0.0f, -Gap - Length), center + new Vector2(0.0f, -Gap), LineColor, Thickness);
        DrawLine(center + new Vector2(0.0f, Gap), center + new Vector2(0.0f, Gap + Length), LineColor, Thickness);
        DrawCircle(center, DotRadius, LineColor);
    }
}
