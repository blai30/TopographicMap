using Godot;
using System.Collections.Generic;

namespace TopographicMap.TopoDemo;

// A single baked terrain rendered through the topographic effect. Open this scene and press
// Play (F5) to view it live: with no command-line argument it just displays the map using the
// styling set on the scene's TopoRect material and compositor effect, so you can tweak those
// values in the editor and run to preview them.
//
// The same scene also produces the README screenshots when run with an argument after "--":
//   godot --path . res://TopoDemo/scenes/DemoTerrain.tscn -- banner    (banner-light + banner-dark)
//   godot --path . res://TopoDemo/scenes/DemoTerrain.tscn -- presets   (one PNG per gradient)
// The banner uses the scene's own styling (plus an inverted black/white dark variant), rendered
// supersampled then downscaled; the preset shots restyle the material per gradient. The terrain
// itself is the one baked heightmap.
// Needs a real GPU (the compositor is compute), so do not run with --headless.
public partial class DemoTerrain : Node3D
{
    [Export] public Camera3D TopDownCamera;
    [Export] public ColorRect TopoRect;
    [Export] public MeshInstance3D Terrain;

    private const string GradientDir = "res://addons/topographic/gradients/";
    private const string OutDir = "res://screenshots/";

    // The banner is rendered at BannerSupersample times its output size, then downscaled, so the
    // high-contrast ink lines get true supersampled anti-aliasing. The shader's 1px line edge
    // alone looks smooth when the lines are pale, but reads as jagged once they are dark.
    private const int BannerWidth = 2400;
    private const int BannerHeight = 960;
    private const float BannerSupersample = 1.5f;

    // One image per preset (file name preset-<name>.png).
    private static readonly string[] Presets =
    [
        "classic_ink", "sepia_vintage", "hypsometric_classic", "hypsometric_atlas",
        "matrix", "alpine", "nautical", "viridis", "heatmap", "magma", "blueprint", "grayscale"
    ];

    private struct Shot
    {
        public string Gradient;
        public string Path;
        public bool Banner; // wide supersampled window; keeps the scene's own styling
        public bool Dark; // banner variant: black field with white lines (vs the scene's light look)
    }

    private TopographicCompositorEffect _effect;
    private ShaderMaterial _material;
    private readonly List<Shot> _queue = [];
    private bool _viewMode;
    private bool _producerSeen;
    private int _shotIndex;
    private int _frames;

    // The scene's line widths, captured before any banner supersample scaling so the scaling can
    // be applied absolutely (idempotently) when more than one banner is in the queue.
    private float _baseMinorWidth;
    private float _baseMajorWidth;

    public override void _Ready()
    {
        _material = (ShaderMaterial)TopoRect.Material;
        _baseMinorWidth = _material.GetShaderParameter("minor_line_width_px").AsSingle();
        _baseMajorWidth = _material.GetShaderParameter("major_line_width_px").AsSingle();

        // The TopoRect material binds the runtime inputs (height buffer, segment texture,
        // elevation model) in the inspector. This script keeps the compositor reference
        // only to gate the consumer on the producer's first render.
        _effect = (TopographicCompositorEffect)TopDownCamera.Compositor.CompositorEffects[0];

        TopoRect.Visible = false;

        string[] args = OS.GetCmdlineUserArgs();
        string mode = args.Length > 0 && args[0].Length > 0 ? args[0] : "";

        if (mode == "banner")
        {
            UseBannerTerrain();
            DisplayServer.WindowSetSize(new(
                (int)(BannerWidth * BannerSupersample), (int)(BannerHeight * BannerSupersample)));
            _queue.Add(new() { Path = $"{OutDir}banner-light.png", Banner = true });
            _queue.Add(new() { Path = $"{OutDir}banner-dark.png", Banner = true, Dark = true });
        }
        else if (mode == "presets")
        {
            // The preset showcase shares the scene's compositor effect, so its contours carry the
            // same smoothness as the banner terrain.
            UseBannerTerrain();
            DisplayServer.WindowSetSize(new(1024, 1024));
            foreach (string preset in Presets)
            {
                _queue.Add(new() { Gradient = preset, Path = $"{OutDir}preset-{preset}.png" });
            }
        }
        else
        {
            _viewMode = true;
        }
    }

    // Release the compositor's GPU resources while the render thread and RenderingDevice are still
    // alive. The effect's own predelete runs too late at app shutdown and faults natively during
    // Godot's Texture2Drd/material teardown. See TopographicCompositorEffect.ReleaseGpuResources.
    public override void _ExitTree() => _effect?.ReleaseGpuResources();

    public override void _Process(double delta)
    {
        // Wait for the producer's first render before showing/capturing the consumer.
        if (!_producerSeen)
        {
            if (!_effect.HasProduced)
            {
                return;
            }

            _producerSeen = true;
            TopoRect.Visible = true;
            if (!_viewMode)
            {
                ApplyShot(_queue[0]);
            }

            _frames = 0;
            return;
        }

        if (_viewMode)
        {
            // Display only: leave all styling to the scene so it can be tweaked and run live.
            // Just keep the view aspect-correct, honoring whatever zoom the scene material sets.
            SetWindow(_material.GetShaderParameter("window_span").AsVector2().X, false);
            return;
        }

        _frames++;
        if (_frames < 5)
        {
            return;
        }

        var shot = _queue[_shotIndex];
        var image = GetViewport().GetTexture().GetImage();
        if (shot.Banner)
        {
            // Downscale the supersampled render to its committed size, smoothing the ink lines.
            image.Resize(BannerWidth, BannerHeight, Image.Interpolation.Lanczos);
        }

        var error = image.SavePng(ProjectSettings.GlobalizePath(shot.Path));
        GD.Print($"Saved {shot.Path}: {error}");

        _shotIndex++;
        if (_shotIndex >= _queue.Count)
        {
            GetTree().Quit();
            return;
        }

        ApplyShot(_queue[_shotIndex]);
        _frames = 0;
    }

    private void ApplyShot(Shot shot)
    {
        if (shot.Banner)
        {
            // The light banner keeps the scene's own styling (white field, black lines); the dark
            // variant inverts it to a black field with white lines. Only the line widths scale up
            // so they keep their intended thickness once the supersampled image is downscaled.
            if (shot.Dark)
            {
                _material.SetShaderParameter("elevation_gradient", SolidGradient(new(0f, 0f, 0f)));
                _material.SetShaderParameter("line_color", new Color(1f, 1f, 1f));
                _material.SetShaderParameter("line_color_from_gradient", 0.0f);
            }

            ScaleLineWidthsForSupersample();
            SetWindow(0.72f, true);
        }
        else
        {
            StyleTile(shot.Gradient);
            SetWindow(0.5f, false);
        }
    }

    // The scene defaults the terrain heightmap to the torture terrain for testing; the README
    // glamour modes render the smooth banner terrain instead.
    private void UseBannerTerrain()
    {
        var terrainMaterial = (ShaderMaterial)Terrain.MaterialOverride;
        terrainMaterial.SetShaderParameter(
            "heightmap", GD.Load<Texture2D>("res://TopoDemo/assets/banner_heightmap.exr"));
    }

    // Window over the terrain. The view is a centered square (or the banner's 2.5:1 strip),
    // kept aspect-correct so the square world is not stretched.
    private void SetWindow(float spanX, bool banner)
    {
        var size = TopoRect.Size;
        float spanY = banner ? spanX * size.Y / Mathf.Max(size.X, 1f) : spanX;
        _material.SetShaderParameter("window_center", new Vector2(0.5f, 0.5f));
        _material.SetShaderParameter("window_span", new Vector2(spanX, spanY));
    }

    // Solid single-color elevation gradient, used to flood the banner field (white for light,
    // black for dark) so the contour lines carry the image.
    private static GradientTexture1D SolidGradient(Color color)
    {
        var gradient = new Gradient();
        gradient.SetColor(0, color);
        gradient.SetColor(1, color);
        return new() { Gradient = gradient, Width = 64 };
    }

    private void ScaleLineWidthsForSupersample()
    {
        _material.SetShaderParameter("minor_line_width_px", _baseMinorWidth * BannerSupersample);
        _material.SetShaderParameter("major_line_width_px", _baseMajorWidth * BannerSupersample);
    }

    private void StyleTile(string gradient)
    {
        _material.SetShaderParameter("elevation_gradient", GD.Load<Texture2D>($"{GradientDir}{gradient}.tres"));
        _material.SetShaderParameter("lines_per_major", 5.0f);
        _material.SetShaderParameter("minor_line_width_px", 0.7f);
        _material.SetShaderParameter("major_line_width_px", 1.1f);

        if (gradient == "blueprint")
        {
            _material.SetShaderParameter("line_color", new Color(0.95f, 0.96f, 1.0f));
            _material.SetShaderParameter("line_color_from_gradient", 0.0f);
        }
        else if (gradient == "matrix")
        {
            _material.SetShaderParameter("line_color", new Color(0.35f, 1.0f, 0.45f));
            _material.SetShaderParameter("line_color_from_gradient", 0.0f);
        }
        else if (gradient == "heatmap")
        {
            // Soft lines: blend a gentle cream toward each band's own gradient color, with only
            // a slight darkening so the contours read as a subtle emboss instead of hard lines.
            _material.SetShaderParameter("line_color", new Color(0.92f, 0.90f, 0.85f));
            _material.SetShaderParameter("line_color_from_gradient", 0.6f);
            _material.SetShaderParameter("line_gradient_lightness", -0.12f);
            _material.SetShaderParameter("line_gradient_shift", 0.0f);
        }
        else
        {
            _material.SetShaderParameter("line_color_from_gradient", 1.0f);
            _material.SetShaderParameter("line_gradient_lightness", -0.6f);
            _material.SetShaderParameter("line_gradient_shift", 0.0f);
        }
    }
}
