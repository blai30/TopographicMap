using Godot;
using System.Collections.Generic;

namespace TopographicMap.TopoDemo;

// A single baked terrain rendered through the topographic effect. Open this scene and press
// Play (F5) to view the effect live. The same scene also produces the README screenshots when
// run with a command-line argument after "--":
//   godot --path . res://TopoDemo/scenes/DemoTerrain.tscn -- banner    (one wide banner.png)
//   godot --path . res://TopoDemo/scenes/DemoTerrain.tscn -- presets   (one PNG per gradient)
// With no argument it just displays the topographic map and stays open. Only the gradient (and
// a little line styling) changes between shots; the terrain itself is the one baked heightmap.
// Needs a real GPU (the compositor is compute), so do not run with --headless.
public partial class DemoTerrain : Node3D
{
    [Export] public SubViewport TerrainView;
    [Export] public Camera3D TopDownCamera;
    [Export] public ColorRect TopoRect;

    private const float HeightMin = -40f;
    private const float HeightMax = 110f;
    private const float Interval = 10f; // must match the compositor effect's ContourInterval

    private const string GradientDir = "res://addons/topographic/gradients/";
    private const string OutDir = "res://screenshots/";

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
        public bool Banner; // wide window + fixed ink lines
    }

    private TopographicCompositorEffect _effect;
    private ShaderMaterial _material;
    private readonly List<Shot> _queue = [];
    private bool _viewMode;
    private bool _producerSeen;
    private int _shotIndex;
    private int _frames;

    public override void _Ready()
    {
        // Give this scene its own compositor effect so it never shares the demo's segment
        // texture. ContourInterval must match the material interval, or lines and bands drift.
        _effect = new() { ContourInterval = Interval };
        TopDownCamera.Compositor = new();
        TopDownCamera.Compositor.CompositorEffects = [_effect];

        _material = (ShaderMaterial)TopoRect.Material;
        _material.SetShaderParameter("height_buffer", TerrainView.GetTexture());
        _material.SetShaderParameter("segments", _effect.SegmentTexture);

        // Keep the consumer hidden until the producer's first render, or it samples the
        // segment texture before its RID is live and trips a "set (1)" draw error.
        TopoRect.Visible = false;

        string[] args = OS.GetCmdlineUserArgs();
        string mode = args.Length > 0 && args[0].Length > 0 ? args[0] : "";

        if (mode == "banner")
        {
            DisplayServer.WindowSetSize(new(2400, 960));
            _queue.Add(new() { Gradient = "classic_ink", Path = $"{OutDir}banner.png", Banner = true });
        }
        else if (mode == "presets")
        {
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

    public override void _Process(double delta)
    {
        // Wait for the producer's first render before styling/capturing.
        if (!_producerSeen)
        {
            if (_effect is { HasProduced: false })
            {
                return;
            }

            _producerSeen = true;
            TopoRect.Visible = true;
            if (_viewMode)
            {
                StyleTile("hypsometric_classic", false);
            }
            else
            {
                ApplyShot(_queue[0]);
            }

            _frames = 0;
            return;
        }

        if (_viewMode)
        {
            // Keep the window aspect-correct as the user resizes.
            SetWindow(0.7f, false);
            return;
        }

        _frames++;
        if (_frames < 5)
        {
            return;
        }

        var shot = _queue[_shotIndex];
        var error = GetViewport().GetTexture().GetImage().SavePng(ProjectSettings.GlobalizePath(shot.Path));
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
        StyleTile(shot.Gradient, shot.Banner);
        SetWindow(shot.Banner ? 0.72f : 0.5f, shot.Banner);
    }

    // Window over the terrain. The view is a centered square (or the banner's 2.5:1 strip),
    // kept aspect-correct so the square world is not stretched.
    private void SetWindow(float spanX, bool banner)
    {
        var size = TopoRect.Size;
        float spanY = banner ? spanX * size.Y / Mathf.Max(size.X, 1f) : spanX;
        _material.SetShaderParameter("window_center", new Vector2(0.5f, 0.5f));
        _material.SetShaderParameter("window_span", new Vector2(spanX, spanY));
        _material.SetShaderParameter("px_per_uv", size.X / spanX);
    }

    private void StyleTile(string gradient, bool banner)
    {
        _material.SetShaderParameter("elevation_gradient", GD.Load<Texture2D>($"{GradientDir}{gradient}.tres"));
        _material.SetShaderParameter("height_min", HeightMin);
        _material.SetShaderParameter("height_max", HeightMax);
        _material.SetShaderParameter("contour_interval", Interval);
        _material.SetShaderParameter("lines_per_major", 5.0f);
        _material.SetShaderParameter("minor_line_width_px", 0.7f);
        _material.SetShaderParameter("major_line_width_px", 1.1f);

        if (banner)
        {
            // Soft, light brown ink lines (the cartographic banner look).
            _material.SetShaderParameter("line_color", new Color(0.46f, 0.40f, 0.33f));
            _material.SetShaderParameter("line_color_from_gradient", 0.0f);
        }
        else if (gradient == "blueprint")
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
            _material.SetShaderParameter("line_gradient_lightness", -0.4f);
            _material.SetShaderParameter("line_gradient_shift", 0.0f);
        }
    }
}
