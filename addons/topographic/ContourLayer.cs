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
    [Export] public Color LineColor = new(0.12f, 0.09f, 0.06f);

    // When true and a ColorRamp is set, each line takes the ramp color at its
    // own elevation, darkened by ContourDarken. Otherwise LineColor is used.
    [Export] public bool ContourDynamic = true;
    [Export] public GradientTexture1D ColorRamp;
    [Export] public float ContourDarken = 0.6f;

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

            var color = LineColor;
            if (ContourDynamic && ramp != null)
            {
                color = ramp.Sample(polyline.Level) * ContourDarken;
                color.A = 1f;
            }

            float width = polyline.IsMajor ? MajorWidthPx : MinorWidthPx;
            DrawPolyline(points, color, width, true);
        }
    }
}
