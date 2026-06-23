using System.Linq;
using Godot;

namespace TopographicCameraShader.Demo;

public partial class TopographicCameraShader : Node3D
{
    private const float MapCameraHeight = 150f;
    private const float WorldMapMinSize = 30f;

    private const float WorldMapMaxSize = 470f;

    // Ortho size the world map opens at: zoomed in on the player, not the whole island.
    private const float WorldMapDefaultSize = 130f;

    [Export] public MeshInstance3D Terrain { get; set; }
    [Export] public Demo.PlayerController Player { get; set; }
    [Export] public Camera3D MinimapCamera { get; set; }
    [Export] public Camera3D MinimapMarkerCamera { get; set; }
    [Export] public Label Help { get; set; }

    [Export] public SubViewport WorldMapViewport { get; set; }
    [Export] public Camera3D WorldMapCamera { get; set; }
    [Export] public SubViewport WorldMapMarkerViewport { get; set; }
    [Export] public Camera3D WorldMapMarkerCamera { get; set; }
    [Export] public Control WorldMapOverlay { get; set; }
    [Export] public Control WorldMapTexture { get; set; }

    public bool MapOpen { get; private set; }

    private TopographicEffect _effect;
    private bool _dragging;
    private bool _worldMapInitialized;

    public override void _Ready()
    {
        BuildTerrainCollision();
        _effect = ResolveEffect(MinimapCamera);

        WorldMapOverlay.Visible = false;
        WorldMapViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        WorldMapMarkerViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        UpdateHelp();
    }

    private void BuildTerrainCollision()
    {
        var mesh = Terrain.Mesh;
        if (mesh == null || !mesh.HasMeta("height_field"))
        {
            GD.PrintErr("Terrain mesh has no height_field metadata; re-bake the terrain.");
            return;
        }

        float[] data = (float[])mesh.GetMeta("height_field");
        int size = (int)mesh.GetMeta("grid_size");
        float worldSize = (float)mesh.GetMeta("world_size");
        float step = worldSize / (size - 1);

        var shape = new HeightMapShape3D
        {
            MapWidth = size,
            MapDepth = size,
            MapData = data
        };

        var body = new StaticBody3D { Name = "TerrainBody" };
        body.AddChild(new CollisionShape3D
        {
            Shape = shape,
            Scale = new(step, 1f, step) // samples are 1 unit apart; Y stays 1 (heights in world units)
        });
        Terrain.AddChild(body);
    }

    private static TopographicEffect ResolveEffect(Camera3D camera) =>
        camera?.Compositor?.CompositorEffects.OfType<TopographicEffect>().FirstOrDefault();

    public override void _Process(double delta)
    {
        var playerPos = Player.GlobalPosition;
        MinimapCamera.Position = new(playerPos.X, MapCameraHeight, playerPos.Z);
        MinimapMarkerCamera.GlobalTransform = MinimapCamera.GlobalTransform;

        if (!MapOpen) return;
        WorldMapMarkerCamera.GlobalTransform = WorldMapCamera.GlobalTransform;
        WorldMapMarkerCamera.Size = WorldMapCamera.Size;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false } key)
        {
            if (key.Keycode is Key.M or Key.Tab)
            {
                ToggleMap();
                return;
            }

            if (MapOpen)
                HandleShaderToggle(key.Keycode);
            return;
        }

        if (!MapOpen)
            return;

        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
                _dragging = mb.Pressed;
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelUp)
                ZoomWorldMap(0.9f);
            else if (mb.Pressed && mb.ButtonIndex == MouseButton.WheelDown)
                ZoomWorldMap(1.1f);
        }
        else if (@event is InputEventMouseMotion motion && _dragging)
        {
            PanWorldMap(motion.Relative);
        }
    }

    private void ToggleMap()
    {
        MapOpen = !MapOpen;
        WorldMapOverlay.Visible = MapOpen;
        var mode = MapOpen ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
        WorldMapViewport.RenderTargetUpdateMode = mode;
        WorldMapMarkerViewport.RenderTargetUpdateMode = mode;

        // First open: frame on the player. Subsequent opens keep prior pan/zoom.
        if (MapOpen && !_worldMapInitialized)
        {
            var playerPos = Player.GlobalPosition;
            WorldMapCamera.Position = new(playerPos.X, MapCameraHeight, playerPos.Z);
            WorldMapCamera.Size = WorldMapDefaultSize;
            _worldMapInitialized = true;
        }

        Player.InputEnabled = !MapOpen;
        Input.MouseMode = MapOpen ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
        _dragging = false;
        UpdateHelp();
    }

    private void ZoomWorldMap(float factor)
    {
        WorldMapCamera.Size = Mathf.Clamp(WorldMapCamera.Size * factor, WorldMapMinSize, WorldMapMaxSize);
    }

    private void PanWorldMap(Vector2 pixelDelta)
    {
        // Camera moves opposite to drag (map follows cursor).
        var rect = WorldMapTexture.Size;
        if (rect.X <= 0f || rect.Y <= 0f)
            return;
        float worldPerPixelX = WorldMapCamera.Size * (rect.X / rect.Y) / rect.X;
        float worldPerPixelZ = WorldMapCamera.Size / rect.Y;
        WorldMapCamera.Position += new Vector3(
            -pixelDelta.X * worldPerPixelX,
            0f,
            -pixelDelta.Y * worldPerPixelZ);
    }

    private void HandleShaderToggle(Key keycode)
    {
        if (_effect == null)
            return;
        switch (keycode)
        {
            case Key.V: _effect.Enabled = !_effect.Enabled; break;
            case Key.C: _effect.ContoursEnabled = !_effect.ContoursEnabled; break;
            case Key.B: _effect.MajorContoursEnabled = !_effect.MajorContoursEnabled; break;
            case Key.G: _effect.SmoothRamp = !_effect.SmoothRamp; break;
            case Key.I: _effect.InvertRamp = !_effect.InvertRamp; break;
            default: return;
        }

        UpdateHelp();
    }

    private void UpdateHelp()
    {
        if (!MapOpen)
        {
            Help.Text =
                "FIRST-PERSON TOPO DEMO\n" +
                "\n" +
                "WASD     Move\n" +
                "Shift    Sprint\n" +
                "Space    Jump\n" +
                "Mouse    Look\n" +
                "M / Tab  World map\n" +
                "Esc      Release cursor";
            return;
        }

        Help.Text =
            "WORLD MAP\n" +
            "\n" +
            "Drag        Pan\n" +
            "Wheel       Zoom\n" +
            $"V  Shader         [{On(_effect?.Enabled ?? true)}]\n" +
            $"C  Contours       [{On(_effect?.ContoursEnabled ?? true)}]\n" +
            $"B  Major contours [{On(_effect?.MajorContoursEnabled ?? true)}]\n" +
            $"G  Ramp: {(_effect?.SmoothRamp ?? false ? "smooth " : "stepped")}\n" +
            $"I  Invert shades  [{On(_effect?.InvertRamp ?? false)}]\n" +
            "M / Tab     Close";

        string On(bool flag) => flag ? "ON" : "OFF";
    }
}
