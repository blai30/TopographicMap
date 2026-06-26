using Godot;

namespace TopographicMap.TopoDemo;

// Drives the HUD map. The corner minimap and the full-screen world map are both
// ColorRects running the unified topographic shader over the shared height buffer (the
// MapView SubViewport texture). This script feeds each ColorRect's ShaderMaterial the
// runtime inputs the material cannot hold: the shared height buffer, the per-cell contour
// segment texture, and a sampling window (center + span in buffer-UV space) with the
// constant-line-width scale. The minimap is a fixed player-centered window; the world map
// is a zoom/pan window kept a centered square so the square world is not stretched on a
// non-square screen.
public partial class MapUi : Control
{
    [Export] public SubViewport MapViewport;
    [Export] public TopographicCompositorEffect MapCompositor;
    [Export] public ColorRect Minimap;
    [Export] public ColorRect WorldMapImage;
    [Export] public Control WorldMapRoot;
    [Export] public Node3D Player;
    [Export] public float TerrainSize = 1536.0f;
    [Export] public float MinimapWorldSpan = 220.0f;

    // World map zoom. Zoom is how much of the world the window spans: span = 1/zoom.
    [Export] public float InitialZoom = 1.8f;
    [Export] public float MinZoom = 1.0f;
    [Export] public float MaxZoom = 6.0f;
    [Export] public float ZoomStep = 1.15f;

    [Export] public Control MinimapMarker;
    [Export] public Control WorldMapMarker;
    [Export] public Node3D PlayerBody;
    [Export] public float MarkerScreenSize = 24.0f;

    private float _zoom = 1.8f;
    private Vector2 _panUv = new(0.5f, 0.5f); // world UV at the world-map window center
    private bool _dragging;
    private bool _minimapRevealed;

    public override void _Ready()
    {
        var heightBuffer = MapViewport.GetTexture();
        var segments = MapCompositor?.SegmentTexture;
        BindTextures(Minimap, heightBuffer, segments);
        BindTextures(WorldMapImage, heightBuffer, segments);

        WorldMapRoot.Visible = false;

        // Keep the minimap hidden until the compositor has produced the segment texture
        // (see _Process). Drawing it before the producer's first render samples the segment
        // texture with no live RID and trips a "Uniforms were never supplied for set (1)"
        // draw error on the first frame.
        Minimap.Visible = false;

        SetupMarker(MinimapMarker);
        SetupMarker(WorldMapMarker);
    }

    private void SetupMarker(Control marker)
    {
        marker.Size = new(MarkerScreenSize, MarkerScreenSize);
        marker.PivotOffset = marker.Size * 0.5f;
    }

    // Player heading as a Control rotation. Body yaw is rotation about Y; on the
    // top-down map, screen rotation runs opposite world yaw. Flip the sign if the
    // arrow points the wrong way.
    private float MarkerRotation() => -PlayerBody.GlobalRotation.Y;

    public override void _Process(double delta)
    {
        if (!_minimapRevealed)
        {
            // Wait for the producer's first render before showing the minimap.
            if (MapCompositor is { HasProduced: false }) return;

            _minimapRevealed = true;
            Minimap.Visible = !WorldMapRoot.Visible;
        }

        UpdateMinimap();
        if (!WorldMapRoot.Visible) return;
        LayoutWorldMap();
        UpdateWorldMap();
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent.IsActionPressed("toggle_map"))
        {
            ToggleWorldMap();
            return;
        }

        if (!WorldMapRoot.Visible) return;

        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                ZoomAt(wheelUp.Position, ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                ZoomAt(wheelDown.Position, 1.0f / ZoomStep);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } leftButton:
                _dragging = leftButton.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                // Drag moves the world under the cursor: shift the window center
                // opposite the drag, scaled by the current span over the map size.
                _panUv -= motion.Relative / WorldMapImage.Size * WindowSpan();
                ClampPan();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Equal or Key.KpAdd }:
                ZoomAt(WorldMapImage.Position + WorldMapImage.Size * 0.5f, ZoomStep);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Minus or Key.KpSubtract }:
                ZoomAt(WorldMapImage.Position + WorldMapImage.Size * 0.5f, 1.0f / ZoomStep);
                break;
        }
    }

    private void ToggleWorldMap()
    {
        bool show = !WorldMapRoot.Visible;
        WorldMapRoot.Visible = show;
        Minimap.Visible = !show;
        if (show)
        {
            _zoom = InitialZoom;
            _panUv = WorldToUv(Player.GlobalPosition);
            ClampPan();
            LayoutWorldMap();
            UpdateWorldMap();
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else
        {
            _dragging = false;
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

    private void UpdateMinimap()
    {
        float span = MinimapWorldSpan / TerrainSize;
        SetWindow(Minimap, WorldToUv(Player.GlobalPosition), span);

        // The minimap is always centered on the player, so the marker sits at center.
        MinimapMarker.Position = Minimap.Size * 0.5f - MinimapMarker.Size * 0.5f;
        MinimapMarker.Rotation = MarkerRotation();
    }

    // Keep the world map a centered square sized to the shorter screen dimension.
    private void LayoutWorldMap()
    {
        float side = Mathf.Min(WorldMapRoot.Size.X, WorldMapRoot.Size.Y);
        WorldMapImage.Size = new(side, side);
        WorldMapImage.Position = (WorldMapRoot.Size - WorldMapImage.Size) * 0.5f;
    }

    private void UpdateWorldMap()
    {
        float span = WindowSpan();
        SetWindow(WorldMapImage, _panUv, span);

        // Place the marker at the player's position within the current window; hide
        // it when the player is outside the visible area.
        var rel = (WorldToUv(Player.GlobalPosition) - _panUv) / span + new Vector2(0.5f, 0.5f);
        bool onMap = rel.X is >= 0.0f and <= 1.0f && rel.Y is >= 0.0f and <= 1.0f;
        WorldMapMarker.Visible = onMap;
        if (!onMap) return;
        WorldMapMarker.Position = rel * WorldMapImage.Size - WorldMapMarker.Size * 0.5f;
        WorldMapMarker.Rotation = MarkerRotation();
    }

    // Fraction of the world the world-map window spans at the current zoom.
    private float WindowSpan() => Mathf.Min(1.0f, 1.0f / _zoom);

    private Vector2 WorldToUv(Vector3 world) => new(world.X / TerrainSize + 0.5f, world.Z / TerrainSize + 0.5f);

    // Bind the runtime textures the material can't hold into a map's shader. Safe to call
    // repeatedly; tolerates a null segment texture before the compositor's first render.
    private static void BindTextures(ColorRect view, Texture2D height, Texture2D segments)
    {
        if (view.Material is not ShaderMaterial mat) return;

        if (height != null)
        {
            mat.SetShaderParameter("height_buffer", height);
        }

        if (segments != null)
        {
            mat.SetShaderParameter("segments", segments);
        }
    }

    // Set a map's sampling window. Center and span are in buffer-UV space. px_per_uv
    // converts a UV-space distance to screen pixels at the current zoom so lines stay a
    // constant screen width: one UV unit spans view.Size.X / span screen pixels.
    private static void SetWindow(ColorRect view, Vector2 center, float span)
    {
        if (view.Material is not ShaderMaterial mat) return;

        float clampedSpan = Mathf.Max(span, 0.00001f);
        mat.SetShaderParameter("window_center", center);
        mat.SetShaderParameter("window_span", new Vector2(clampedSpan, clampedSpan));
        mat.SetShaderParameter("px_per_uv", view.Size.X / clampedSpan);
    }

    // Zoom while keeping the world point under screenPos fixed on screen.
    private void ZoomAt(Vector2 screenPos, float factor)
    {
        var screenUv = (screenPos - WorldMapImage.Position) / WorldMapImage.Size - new Vector2(0.5f, 0.5f);
        var uvUnderCursor = _panUv + screenUv * WindowSpan();

        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _panUv = uvUnderCursor - screenUv * WindowSpan();
        ClampPan();
    }

    private void ClampPan()
    {
        float half = WindowSpan() * 0.5f;
        float hi = 1.0f - half;
        if (half > hi)
        {
            _panUv = new(0.5f, 0.5f);
            return;
        }

        _panUv = new(Mathf.Clamp(_panUv.X, half, hi), Mathf.Clamp(_panUv.Y, half, hi));
    }
}
