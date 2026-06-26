using Godot;

namespace TopographicMap;

// One map view: a ColorRect running the unified topographic shader over a pan/zoom
// window. The look (palette, height range, contour interval, line style) lives on the
// ColorRect's ShaderMaterial as shader parameters; this script only feeds the runtime
// inputs the material cannot hold: the shared height buffer, the per-cell contour segment
// texture, and the pan/zoom window with its constant-line-width scale. Tint and contour
// lines are produced entirely in the shader, so there is no contour geometry to manage.
[Tool]
public partial class TopographicView : ColorRect
{
    [Export] public Texture2D HeightBuffer { get; set; }

    // Per-cell contour segment texture (from the compositor). The shader samples it for
    // exact per-pixel vector line distance.
    [Export] public Texture2D SegmentBuffer { get; set; }

    private ShaderMaterial Mat => Material as ShaderMaterial;

    public override void _Ready() => Apply();

    // Bind the runtime textures into the shader uniforms. Safe to call repeatedly.
    public void Apply()
    {
        if (Mat == null)
        {
            return;
        }

        if (HeightBuffer != null)
        {
            Mat.SetShaderParameter("height_buffer", HeightBuffer);
        }

        if (SegmentBuffer != null)
        {
            Mat.SetShaderParameter("segments", SegmentBuffer);
        }
    }

    // Center and span are in buffer-UV space. px_per_uv converts a UV-space distance to
    // screen pixels at the current zoom, so lines stay a constant screen width: one UV unit
    // spans Size.X / span screen pixels.
    public void SetWindow(Vector2 center, float span)
    {
        if (Mat == null)
        {
            return;
        }

        float clampedSpan = Mathf.Max(span, 0.00001f);
        Mat.SetShaderParameter("window_center", center);
        Mat.SetShaderParameter("window_span", new Vector2(clampedSpan, clampedSpan));
        Mat.SetShaderParameter("px_per_uv", Size.X / clampedSpan);
    }
}
