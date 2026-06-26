using Godot;

namespace TopographicMap;

// Draws a ContourField over a normalized window onto this Control's rect, as
// constant-width anti-aliased strokes. Width is in screen pixels, so lines stay
// crisp at any zoom with no gradient and no parameters. Redraws only when the
// window changes.
public partial class ContourLayer : Control
{
    [Export] public float MinorWidthPx = 1.1f;
    [Export] public float MajorWidthPx = 1.9f;

    /// <summary>
    /// Solid override color for every contour line. Leave fully transparent
    /// (alpha 0, the default) to color lines dynamically from ColorRamp by
    /// elevation, falling back to black when no ramp is set. Any alpha above 0
    /// overrides the ramp with this solid color.
    /// WARNING: alpha doubles as the dynamic on/off switch, so dragging alpha
    /// down to fade the lines will instead re-enable dynamic coloring.
    /// </summary>
    [Export] public Color ContourLineColor = new(0f, 0f, 0f, 0f);

    [Export] public GradientTexture1D ColorRamp;
    [Export] public float ContourLightness = 0.7f;

    public ContourField Field;

    private Vector2 _windowCenter = new(0.5f, 0.5f);
    private float _windowSpan = 1.0f;

    public void SetWindow(Vector2 center, float span)
    {
        span = Mathf.Max(span, 0.00001f);
        // Skip redundant redraws: _Process drives this every frame, but the lines
        // only need redrawing when the view actually moves.
        if (_windowCenter.IsEqualApprox(center) && Mathf.IsEqualApprox(_windowSpan, span))
        {
            return;
        }

        _windowCenter = center;
        _windowSpan = span;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Field == null)
        {
            return;
        }

        var ramp = ColorRamp?.Gradient;
        var size = Size;
        float half = _windowSpan * 0.5f;
        float left = _windowCenter.X - half;
        float top = _windowCenter.Y - half;
        float right = _windowCenter.X + half;
        float bottom = _windowCenter.Y + half;

        foreach (var polyline in Field.Polylines)
        {
            if (polyline.MaxX < left || polyline.MinX > right
                                     || polyline.MaxY < top || polyline.MinY > bottom)
            {
                continue;
            }

            var points = new Vector2[polyline.Points.Count];
            for (int i = 0; i < polyline.Points.Count; i++)
            {
                var point = polyline.Points[i];
                float sx = (point.X - left) / _windowSpan * size.X;
                float sy = (point.Y - top) / _windowSpan * size.Y;
                points[i] = new(sx, sy);
            }

            var color = ContourLineColor;
            if (ContourLineColor.A <= 0f)
            {
                color = ramp != null ? ramp.Sample(polyline.Level) * ContourLightness : Colors.Black;
                color.A = 1f;
            }

            float width = polyline.IsMajor ? MajorWidthPx : MinorWidthPx;
            DrawPolyline(points, color, width, true);
        }
    }
}
