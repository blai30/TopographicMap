using Godot;

namespace TopographicCameraShader.Demo;

// Editor-only utility. Flip "Bake" in the inspector to regenerate the procedural
// island, save its mesh and baked collider under res://Demo/assets, and fit the
// topo effect's elevation ramp to the new height range.
[Tool]
public partial class TerrainBaker : Node
{
    private const float WorldSize = 400f;
    private const int Resolution = 384;
    private const int Seed = 20260622;

    private const string MeshPath = "res://Demo/assets/terrain.res";
    private const string CollisionPath = "res://Demo/assets/terrain_collision.res";
    private const string CompositorPath = "res://addons/topographic/topographic_compositor.tres";

    [Export]
    public bool Bake
    {
        get;
        set
        {
            // Only act on a rising edge inside the editor.
            if (value && Engine.IsEditorHint())
            {
                BakeToFiles();
            }

            field = false;
        }
    }

    // Regenerates the island and runs the three bake steps in order. Static so it
    // can run from the editor inspector tick or from a headless bake run.
    public static void BakeToFiles()
    {
        var bake = TerrainGenerator.CreateTerrain(WorldSize, Resolution, Seed);
        DirAccess.MakeDirRecursiveAbsolute("res://Demo/assets");

        if (!SaveMesh(bake) || !SaveCollider(bake))
        {
            return;
        }

        FitEffectRamp(bake.MaxHeight);
    }

    private static bool SaveMesh(TerrainBake bake)
    {
        var err = ResourceSaver.Save(bake.Mesh, MeshPath);
        if (err != Error.Ok)
        {
            GD.PrintErr($"Failed to save terrain mesh: {err}");
            return false;
        }

        GD.Print($"Baked terrain to {MeshPath}. Height range: {bake.MinHeight:F1} .. {bake.MaxHeight:F1} (world Y)");
        return true;
    }

    // Bakes the heightmap collider as a resource so the scene can reference it
    // directly, with no runtime collider construction. A HeightMapShape3D's samples
    // are always 1 unit apart, but the grid spans WorldSize across (gridSize - 1)
    // cells, so the node needs a step scale. Pre-dividing the stored heights by that
    // step lets the CollisionShape3D use a UNIFORM scale (step on every axis), which
    // restores true world heights on Y and the correct spacing on X/Z -- a
    // non-uniform collider scale is flagged by Godot as unreliable.
    private static bool SaveCollider(TerrainBake bake)
    {
        float step = WorldSize / (bake.GridSize - 1);
        float[] colliderHeights = new float[bake.HeightField.Length];
        for (int i = 0; i < bake.HeightField.Length; i++)
        {
            colliderHeights[i] = bake.HeightField[i] / step;
        }

        var collisionShape = new HeightMapShape3D
        {
            MapWidth = bake.GridSize,
            MapDepth = bake.GridSize,
            MapData = colliderHeights
        };

        var err = ResourceSaver.Save(collisionShape, CollisionPath);
        if (err != Error.Ok)
        {
            GD.PrintErr($"Failed to save terrain collider: {err}");
            return false;
        }

        GD.Print($"Baked terrain collider to {CollisionPath}");
        return true;
    }

    // Fits the topo ramp from sea level (y = 0) up to the highest peak. The island
    // is mostly low coastal plains with an inland mountain range, so anchoring the
    // dark end at sea level spreads the contour bands across all the low land
    // instead of crushing it into one shade. Anything below sea level (the
    // submerged rim) falls into the darkest band, reading like water on the map.
    //
    // This is the one place the demo writes into the shipped addon resource. It is
    // intentional: re-baking refits the demo's own copy, while a consumer of the
    // addon sets their own elevation range. See CLAUDE.md.
    private static void FitEffectRamp(float maxHeight)
    {
        const float rampMin = 0f;
        var compositor = GD.Load<Compositor>(CompositorPath);
        if (compositor == null)
        {
            return;
        }

        if (TopographicEffect.FindIn(compositor) is { } topo)
        {
            topo.MinElevation = rampMin;
            topo.MaxElevation = maxHeight;
        }

        ResourceSaver.Save(compositor, CompositorPath);
        GD.Print($"Updated {CompositorPath} elevation ramp to {rampMin:F1} .. {maxHeight:F1}");
    }
}
