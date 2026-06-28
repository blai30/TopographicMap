using Godot;

namespace TopographicMap;

// Binds the topographic shader's runtime inputs into this ColorRect's material, so the map
// renders in the editor while authoring and at run time without a separate driver doing the
// binding. The look parameters stay on the material; this node only feeds the inputs the
// material cannot hold: the per-cell segment texture and the height buffer (both runtime
// objects), plus the compositor-owned elevation model. At edit time it also sets a static
// preview window so the map frames sensibly with no driver running; at run time it leaves the
// window to the driver (pan/zoom).
[Tool]
[GlobalClass]
public partial class TopographicMapView : ColorRect
{
    // Source of the per-cell contour segment texture and the elevation model.
    [Export] public TopographicCompositorEffect Compositor;

    // SubViewport whose texture is the shared height buffer.
    [Export] public SubViewport HeightSource;

    // Editor-only preview window over the height buffer (center and span in buffer UV).
    [Export] public Vector2 PreviewWindowCenter = new(0.5f, 0.5f);

    [Export(PropertyHint.Range, "0.01,1,0.01")]
    public float PreviewWindowSpan = 1.0f;

    public override void _Ready()
    {
        BindInputs();
        if (Engine.IsEditorHint())
        {
            SetPreviewWindow();
        }
    }

    public override void _Process(double delta)
    {
        // In the editor the compositor can be recreated on reload or recompile, leaving a stale
        // SegmentTexture reference, and the rect can be resized; re-ensure both cheaply. At run
        // time the binding is set once in _Ready and the driver owns the window, so skip this.
        if (!Engine.IsEditorHint()) return;
        BindInputs();
        SetPreviewWindow();
    }

    // Feed the material the runtime inputs it cannot serialize. Null-tolerant so an
    // unconfigured node is harmless in the editor.
    private void BindInputs()
    {
        if (Material is not ShaderMaterial mat) return;

        if (Compositor != null)
        {
            mat.SetShaderParameter("height_min", Compositor.HeightMin);
            mat.SetShaderParameter("height_max", Compositor.HeightMax);
            mat.SetShaderParameter("contour_interval", Compositor.ContourInterval);

            // The segment Texture2Drd's RID is only live after the compositor's first render. In the
            // editor, bind it only once produced or the consumer's first draws spam "set (1)". At run
            // time the placeholder keeps the RID valid and _Process is skipped, so binding here is safe.
            if (!Engine.IsEditorHint() || Compositor.HasProduced)
            {
                mat.SetShaderParameter("segments", Compositor.SegmentTexture);
            }
        }

        if (HeightSource != null)
        {
            mat.SetShaderParameter("height_buffer", HeightSource.GetTexture());
        }
    }

    // Static preview framing for the editor: center on PreviewWindowCenter, span PreviewWindowSpan,
    // with px_per_uv matching the runtime convention (view width in pixels per UV unit) so the
    // contour line width reads correctly while authoring.
    private void SetPreviewWindow()
    {
        if (Material is not ShaderMaterial mat) return;

        float span = Mathf.Max(PreviewWindowSpan, 0.00001f);
        mat.SetShaderParameter("window_center", PreviewWindowCenter);
        mat.SetShaderParameter("window_span", new Vector2(span, span));
        mat.SetShaderParameter("px_per_uv", Size.X / span);
    }
}
