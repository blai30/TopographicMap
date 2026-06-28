using Godot;

namespace TopographicMap;

// Binds the topographic shader's runtime inputs into this ColorRect's material, so the map
// renders in the editor while authoring and at run time without a separate driver doing the
// binding. The look parameters stay on the material; this node only feeds the inputs the
// material cannot hold: the per-cell segment texture and the height buffer (both runtime
// objects), plus the compositor-owned elevation model. In the editor it also derives px_per_uv
// from the authored window so contour lines preview at the right width; it never writes
// window_center or window_span (the driver owns those, and DemoTerrain reads window_span back).
// The editor-injected inputs are cleared on save (see _Notification) so the saved scene keeps
// only the authored look params.
[Tool]
[GlobalClass]
public partial class TopographicMapView : ColorRect
{
    // Source of the per-cell contour segment texture and the elevation model.
    [Export] public TopographicCompositorEffect Compositor;

    // SubViewport whose texture is the shared height buffer.
    [Export] public SubViewport HeightSource;

    public override void _Ready()
    {
        BindInputs();
    }

    public override void _Process(double delta)
    {
        // In the editor the compositor can be recreated on reload or recompile, leaving a stale
        // SegmentTexture reference, and the rect can be resized; re-ensure cheaply. At run time the
        // binding is set once in _Ready and the driver owns the window, so skip this.
        if (!Engine.IsEditorHint()) return;
        BindInputs();
    }

    public override void _Notification(int what)
    {
        // The editor serializes any shader parameter this node sets when the scene is saved. Clear
        // the code-injected inputs just before the save and restore them right after, so only the
        // authored look params (gradient, line widths, window) persist on disk.
        if (what == NotificationEditorPreSave)
        {
            ClearInputs();
        }
        else if (what == NotificationEditorPostSave)
        {
            BindInputs();
        }
    }

    // Feed the material the runtime inputs it cannot serialize. Null-tolerant so an unconfigured
    // node is harmless in the editor.
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

        // No driver runs in the editor to set the window, so derive px_per_uv (screen pixels per UV
        // unit) from the authored window span for a correct contour line width. window_center and
        // window_span are left as authored.
        if (Engine.IsEditorHint())
        {
            var windowSpan = mat.GetShaderParameter("window_span");
            if (windowSpan.VariantType == Variant.Type.Vector2)
            {
                float span = Mathf.Max(windowSpan.AsVector2().X, 0.00001f);
                mat.SetShaderParameter("px_per_uv", Size.X / span);
            }
        }
    }

    // Before a scene save, drop the code-injected inputs so live runtime values are not written to
    // disk. Texture parameters (segments, height_buffer) are fully removed by a null value; float
    // parameters cannot be (Godot reverts them to the shader's declared default and still
    // serializes that), so they settle at the harmless, stable shader defaults instead.
    private void ClearInputs()
    {
        if (Material is not ShaderMaterial mat) return;

        mat.SetShaderParameter("segments", default);
        mat.SetShaderParameter("height_buffer", default);
        mat.SetShaderParameter("height_min", default);
        mat.SetShaderParameter("height_max", default);
        mat.SetShaderParameter("contour_interval", default);
        mat.SetShaderParameter("px_per_uv", default);
    }
}
