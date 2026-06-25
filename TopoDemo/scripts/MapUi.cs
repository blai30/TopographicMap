using Godot;

namespace TopographicMap.TopoDemo;

// Drives the HUD map. The corner minimap is a player-centred crop of the map
// SubViewport texture. The full-screen world map shows the same texture with
// mouse zoom and pan. The player marker is a node in the scene rendered into the
// map texture (see the Marker node under the player), so no marker code lives here.
public partial class MapUi : Control
{
    [Export] public SubViewport MapViewport;
    [Export] public TextureRect Minimap;
    [Export] public TextureRect WorldMapImage;
    [Export] public Control WorldMapRoot;
    [Export] public Node3D Player;
    [Export] public float TerrainSize = 1536.0f;
    [Export] public float MinimapWorldSpan = 220.0f;

    // World map zoom/pan tuning. Zoom is a display multiplier on the fitted map.
    [Export] public float InitialZoom = 1.8f;
    [Export] public float MinZoom = 1.0f;
    [Export] public float MaxZoom = 6.0f;
    [Export] public float ZoomStep = 1.15f;

    private AtlasTexture _atlas;
    private Texture2D _mapTexture;

    private float _zoom = 1.8f;
    private Vector2 _panUv = new(0.5f, 0.5f); // world UV shown at the screen centre
    private bool _dragging;

    public override void _Ready()
    {
        _mapTexture = MapViewport.GetTexture();
        _atlas = new() { Atlas = _mapTexture };
        Minimap.Texture = _atlas;

        WorldMapImage.Texture = _mapTexture;
        WorldMapImage.ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize;
        WorldMapImage.StretchMode = TextureRect.StretchModeEnum.Scale;
        WorldMapImage.AnchorLeft = 0;
        WorldMapImage.AnchorTop = 0;
        WorldMapImage.AnchorRight = 0;
        WorldMapImage.AnchorBottom = 0;

        WorldMapRoot.Visible = false;
    }

    public override void _Process(double delta)
    {
        UpdateMinimap();
        if (WorldMapRoot.Visible)
        {
            UpdateWorldMap();
        }
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
                _panUv -= motion.Relative / MapDisplaySize();
                ClampPan();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Equal or Key.KpAdd }:
                ZoomAt(WorldMapRoot.Size * 0.5f, ZoomStep);
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Minus or Key.KpSubtract }:
                ZoomAt(WorldMapRoot.Size * 0.5f, 1.0f / ZoomStep);
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
        if (_atlas.Atlas == null)
        {
            return;
        }

        var texSize = _atlas.Atlas.GetSize();
        var uv = WorldToUv(Player.GlobalPosition);
        float spanPx = MinimapWorldSpan / TerrainSize * texSize.X;
        _atlas.Region = new(
            uv.X * texSize.X - spanPx * 0.5f,
            uv.Y * texSize.Y - spanPx * 0.5f,
            spanPx,
            spanPx);
    }

    private void UpdateWorldMap()
    {
        float display = MapDisplaySize();
        WorldMapImage.Position = WorldMapRoot.Size * 0.5f - _panUv * display;
        WorldMapImage.Size = new(display, display);
    }

    // Display size in pixels of the (square) full map at the current zoom.
    private float MapDisplaySize() => Mathf.Min(WorldMapRoot.Size.X, WorldMapRoot.Size.Y) * _zoom;

    private Vector2 WorldToUv(Vector3 world) =>
        new(world.X / TerrainSize + 0.5f, world.Z / TerrainSize + 0.5f);

    // Zoom while keeping the world point under screenPos fixed on screen.
    private void ZoomAt(Vector2 screenPos, float factor)
    {
        float oldDisplay = MapDisplaySize();
        Vector2 topLeft = WorldMapRoot.Size * 0.5f - _panUv * oldDisplay;
        Vector2 uvUnderCursor = (screenPos - topLeft) / oldDisplay;

        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);

        float newDisplay = MapDisplaySize();
        _panUv = (WorldMapRoot.Size * 0.5f - screenPos) / newDisplay + uvUnderCursor;
        ClampPan();
    }

    private void ClampPan() =>
        _panUv = new(Mathf.Clamp(_panUv.X, 0.0f, 1.0f), Mathf.Clamp(_panUv.Y, 0.0f, 1.0f));
}
