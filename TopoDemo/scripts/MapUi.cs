using Godot;

namespace TopographicMap.TopoDemo;

// Drives the HUD map. The corner minimap and the full-screen world map are both
// TopographicView nodes (ColorRects running the unified topographic shader) over the
// shared height buffer (the MapView SubViewport texture). Each sets a sampling window
// (center + span in buffer-UV space): the minimap is a fixed player-centered window,
// the world map is a zoom/pan window. The world map is kept a centered square so the
// square world is not stretched on a non-square screen.
public partial class MapUi : Control
{
    [Export] public SubViewport MapViewport;
    [Export] public TopographicView Minimap;
    [Export] public TopographicView WorldMapImage;
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

    public override void _Ready()
    {
        var heightBuffer = MapViewport.GetTexture();
        Minimap.HeightBuffer = heightBuffer;
        Minimap.Apply();
        WorldMapImage.HeightBuffer = heightBuffer;
        WorldMapImage.Apply();

        WorldMapRoot.Visible = false;

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

        if (!WorldMapRoot.Visible)
        {
            return;
        }

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
        Minimap.SetWindow(WorldToUv(Player.GlobalPosition), span);

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
        WorldMapImage.SetWindow(_panUv, span);

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

    private Vector2 WorldToUv(Vector3 world) =>
        new(world.X / TerrainSize + 0.5f, world.Z / TerrainSize + 0.5f);

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
