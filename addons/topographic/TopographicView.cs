using Godot;

namespace TopographicMap;

// One map view: a ColorRect running the unified topographic shader over a pan/zoom
// window. Applies TopographicSettings to its material and feeds the window plus the
// constant-line-width scale. Tint and contour lines are produced entirely in the
// shader from the shared height buffer, so there is no contour geometry to manage.
[Tool]
public partial class TopographicView : ColorRect
{
    [Export] public TopographicSettings Settings { get; set; }
    [Export] public Texture2D HeightBuffer { get; set; }

    private ShaderMaterial Mat => Material as ShaderMaterial;

    public override void _Ready() => Apply();

    // Push the settings and buffer into the shader uniforms. Safe to call repeatedly.
    public void Apply()
    {
        if (Mat == null || Settings == null)
        {
            return;
        }

        if (HeightBuffer != null)
        {
            Mat.SetShaderParameter("height_buffer", HeightBuffer);
        }

        Mat.SetShaderParameter("color_ramp", Settings.ColorRamp);
        Mat.SetShaderParameter("height_min", Settings.HeightMin);
        Mat.SetShaderParameter("height_max", Settings.HeightMax);
        Mat.SetShaderParameter("contour_interval", Settings.ContourInterval);
        Mat.SetShaderParameter("major_every", (float)Settings.MajorEvery);
        Mat.SetShaderParameter("minor_width_px", Settings.MinorWidthPx);
        Mat.SetShaderParameter("major_width_px", Settings.MajorWidthPx);
        Mat.SetShaderParameter("contour_darken", Settings.ContourDarken);
    }

    // Center and span are in buffer-UV space. px_per_uv converts the SDF distance (in
    // UV units) to screen pixels at the current zoom, so lines stay a constant screen
    // width: one UV unit spans Size.X / span screen pixels.
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
