using Godot;

namespace TopographicMap.TopoDemo;

// Edit-time command-line tool. From the repo root:
//   godot --headless --path . --script res://TopoDemo/scripts/TerrainBaker.cs
// Bakes every committed terrain asset, then quits:
//   heightmap.exr + terrain_collision.res - the demo continent (used by the game)
//   banner_heightmap.exr                  - rich terrain for DemoTerrain.tscn / the README shots
// Never referenced by the game scene or any autoload, so the shipped game contains no
// generator; only the baked outputs load at runtime.
public partial class TerrainBaker : MainLoop
{
    private const float WorldSize = 1536f;
    private const float Half = WorldSize * 0.5f; // 768
    private const float MinHeight = -40f;
    private const float MaxHeight = 110f;

    private const int DemoRes = 512; // demo heightmap texels
    private const int BannerRes = 1024; // banner heightmap texels (finer relief)
    private const int CollisionGrid = 513; // verts; cell = WorldSize / (CollisionGrid - 1) = 3 units

    private const string HeightmapPath = "res://TopoDemo/assets/heightmap.exr";
    private const string CollisionPath = "res://TopoDemo/assets/terrain_collision.res";
    private const string BannerPath = "res://TopoDemo/assets/banner_heightmap.exr";

    public override void _Initialize()
    {
        BuildDemoNoise();
        BakeHeightmap(HeightmapPath, DemoRes, DemoHeight);
        BakeCollision();

        BuildBannerNoise();
        BakeHeightmap(BannerPath, BannerRes, BannerHeight);
    }

    // Quit after the first frame; all work is done in _Initialize.
    public override bool _Process(double delta) => true;

    // Shared heightmap bake: a single-channel EXR normalized 0..1 over [MinHeight, MaxHeight].
    private static void BakeHeightmap(string path, int res, System.Func<float, float, float> sample)
    {
        var image = Image.CreateEmpty(res, res, false, Image.Format.Rf);
        float minSeen = float.MaxValue, maxSeen = float.MinValue;
        for (int ty = 0; ty < res; ty++)
        for (int tx = 0; tx < res; tx++)
        {
            float wx = (tx + 0.5f) / res * WorldSize - Half;
            float wz = (ty + 0.5f) / res * WorldSize - Half;
            float height = sample(wx, wz);
            minSeen = Mathf.Min(minSeen, height);
            maxSeen = Mathf.Max(maxSeen, height);
            float normalized = Mathf.Clamp((height - MinHeight) / (MaxHeight - MinHeight), 0f, 1f);
            image.SetPixel(tx, ty, new(normalized, 0f, 0f));
        }

        var error = image.SaveExr(ProjectSettings.GlobalizePath(path), true);
        GD.Print($"Baked {path} ({res}x{res}): {error}  height {minSeen:0.0}..{maxSeen:0.0}");
    }

    private void BakeCollision()
    {
        // Collision HeightMapShape3D, heights in world units, same field as the demo heightmap.
        float[] data = new float[CollisionGrid * CollisionGrid];
        const float cell = WorldSize / (CollisionGrid - 1); // world units between grid points after the node scale
        int below = 0;
        for (int iz = 0; iz < CollisionGrid; iz++)
        for (int ix = 0; ix < CollisionGrid; ix++)
        {
            // Sample at the world position each grid point lands on after the uniform
            // (cell,cell,cell) node scale. Heights are stored divided by the cell size so the
            // uniform scale (needed to keep the CollisionShape3D happy) does not stretch them:
            // storedHeight * cell == true world height.
            float wx = (ix - (CollisionGrid - 1) * 0.5f) * cell;
            float wz = (iz - (CollisionGrid - 1) * 0.5f) * cell;
            float height = DemoHeight(wx, wz);
            data[iz * CollisionGrid + ix] = height / cell;
            if (height < 0f) below++;
        }

        var shape = new HeightMapShape3D { MapWidth = CollisionGrid, MapDepth = CollisionGrid, MapData = data };
        var error = ResourceSaver.Save(shape, CollisionPath);
        float waterPct = 100f * below / (CollisionGrid * CollisionGrid);
        GD.Print($"Collision: {error} -> {CollisionPath} ({CollisionGrid}x{CollisionGrid})  water {waterPct:0.0}%");
    }

    // ---- Demo continent: the playable terrain. Smooth, broadly rolling land (single-octave
    // simplex, no ridged noise or domain warp, so the map contours read as smooth ripples),
    // with a small ocean in the far SW corner, a smooth inland lake, and a flattened spawn.
    private FastNoiseLite _continent, _relief;

    private void BuildDemoNoise()
    {
        _continent = new()
        {
            Seed = 1337, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.0016f
        };
        _relief = new()
        {
            Seed = 1539, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.None, Frequency = 0.0032f
        };
    }

    private float DemoHeight(float wx, float wz)
    {
        float nx = wx / Half; // -1..1
        float nz = wz / Half;

        float coast = (nx - nz) * 0.5f; // -1 ocean corner .. +1 inland
        float cont = _continent.GetNoise2D(wx, wz);

        float land = coast * 0.85f + cont * 0.4f + 0.6f;
        float shore = Mathf.SmoothStep(-0.2f, 0.25f, land); // 0 open ocean, 1 inland

        float height = Mathf.Lerp(MinHeight, 16f, shore);

        // Smooth rolling relief on the land: two single-octave simplex scales blended, biased
        // upward so most of the land sits above sea level (a coastline, not a flooded map).
        float relief = cont * 0.6f + _relief.GetNoise2D(wx, wz) * 0.4f;
        height += (relief + 0.28f) * 52f * shore;

        // A smooth inland lake basin.
        float lake = 1f - Mathf.SmoothStep(45f, 95f, Distance(wx, wz, 170f, 30f));
        height = Mathf.Lerp(height, -10f, lake * shore * 0.85f);

        // Flatten the spawn area so the player starts on gentle ground.
        float spawn = 1f - Mathf.SmoothStep(0f, 22f, Distance(wx, wz, 120f, 130f));
        height = Mathf.Lerp(height, Mathf.Max(height, 12f), spawn);

        return Mathf.Clamp(height, MinHeight, MaxHeight);
    }

    // ---- Banner terrain (used only by DemoTerrain.tscn and the README screenshots) ----
    // Multi-scale smooth fbm (SimplexSmooth, no ridged noise, no domain warp): a few octaves
    // give large massifs with smaller hills nested inside, so the terrain naturally carries
    // lots of smooth concentric contours. Ridged noise and domain warp are what folded the
    // contours into jagged zigzags before, so neither is used.
    private FastNoiseLite _bSwell;

    private void BuildBannerNoise()
    {
        _bSwell = new()
        {
            Seed = 7001, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3, FractalGain = 0.46f,
            FractalLacunarity = 2.0f, Frequency = 0.0036f
        };
    }

    // Smooth rolling relief over a full-ish height span, so the frame carries many smooth
    // concentric contour rings. The gentle banner colors come from the soft elevation_gradient,
    // not from squashing the height range, so the contour count stays high.
    private float BannerHeight(float wx, float wz)
    {
        float h = _bSwell.GetNoise2D(wx, wz);
        float n = Mathf.Clamp(h * 0.92f + 0.5f, 0f, 1f);
        return Mathf.Lerp(MinHeight, MaxHeight, n);
    }

    private static float Distance(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx, dz = az - bz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
