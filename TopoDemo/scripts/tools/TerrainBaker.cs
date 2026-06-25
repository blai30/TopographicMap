using Godot;

namespace TopographicMap.TopoDemo.Tools;

// Edit-time command-line tool. From the repo root:
//   godot --headless --path . --script res://TopoDemo/scripts/tools/TerrainBaker.cs
// Bakes the showcase continent into two committed static assets (a normalized
// heightmap EXR and a HeightMapShape3D collision .res), then quits. Never
// referenced by the Demo scene or any autoload, so the shipped game contains no
// generator; only the baked outputs load at runtime.
public partial class TerrainBaker : MainLoop
{
    private const float WorldSize = 1536f;
    private const float Half = WorldSize * 0.5f; // 768
    private const int TextureRes = 512;          // heightmap texels
    private const int CollisionGrid = 513;       // verts; cells span the world after the matching node scale (WorldSize / (CollisionGrid - 1) = 3 units, scale (3,1,3))
    private const float MinHeight = -40f;
    private const float MaxHeight = 110f;

    private const string HeightmapPath = "res://TopoDemo/assets/heightmap.exr";
    private const string CollisionPath = "res://TopoDemo/assets/terrain_collision.res";

    private FastNoiseLite _continent;
    private FastNoiseLite _ridge;
    private FastNoiseLite _mountainMask;
    private FastNoiseLite _warp;

    public override void _Initialize()
    {
        BuildNoises();
        BakeHeightmap();
        BakeCollision();
    }

    // Quit after the first frame; all work is done in _Initialize.
    public override bool _Process(double delta) => true;

    private void BakeHeightmap()
    {
        // Heightmap EXR, normalized 0..1 over [MinHeight, MaxHeight].
        var image = Image.CreateEmpty(TextureRes, TextureRes, false, Image.Format.Rf);
        for (int ty = 0; ty < TextureRes; ty++)
        for (int tx = 0; tx < TextureRes; tx++)
        {
            float wx = (tx + 0.5f) / TextureRes * WorldSize - Half;
            float wz = (ty + 0.5f) / TextureRes * WorldSize - Half;
            float normalized = Mathf.Clamp((SampleHeight(wx, wz) - MinHeight) / (MaxHeight - MinHeight), 0f, 1f);
            image.SetPixel(tx, ty, new Color(normalized, 0f, 0f));
        }
        Error error = image.SaveExr(ProjectSettings.GlobalizePath(HeightmapPath), grayscale: true);
        GD.Print($"Heightmap EXR: {error} -> {HeightmapPath} ({TextureRes}x{TextureRes})");
    }

    private void BakeCollision()
    {
        // Collision HeightMapShape3D, heights in world units, same field as the heightmap.
        var data = new float[CollisionGrid * CollisionGrid];
        float cell = WorldSize / (CollisionGrid - 1); // world units between grid points after the node scale
        float minSeen = float.MaxValue;
        float maxSeen = float.MinValue;
        int below = 0;
        for (int iz = 0; iz < CollisionGrid; iz++)
        for (int ix = 0; ix < CollisionGrid; ix++)
        {
            // Sample at the world position each grid point lands on after the (cell,1,cell) node scale.
            float wx = (ix - (CollisionGrid - 1) * 0.5f) * cell;
            float wz = (iz - (CollisionGrid - 1) * 0.5f) * cell;
            float height = SampleHeight(wx, wz);
            data[iz * CollisionGrid + ix] = height;
            minSeen = Mathf.Min(minSeen, height);
            maxSeen = Mathf.Max(maxSeen, height);
            if (height < 0f) below++;
        }
        var shape = new HeightMapShape3D
        {
            MapWidth = CollisionGrid,
            MapDepth = CollisionGrid,
            MapData = data
        };
        Error error = ResourceSaver.Save(shape, CollisionPath);
        float waterPct = 100f * below / (CollisionGrid * CollisionGrid);
        GD.Print($"Collision: {error} -> {CollisionPath} ({CollisionGrid}x{CollisionGrid})");
        GD.Print($"Height range: {minSeen:0.0}..{maxSeen:0.0}  water coverage: {waterPct:0.0}%");
    }

    private void BuildNoises()
    {
        _continent = new FastNoiseLite
        {
            Seed = 1337,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 4,
            Frequency = 0.0016f,
            DomainWarpEnabled = true,
            DomainWarpAmplitude = 40f,
            DomainWarpFrequency = 0.005f
        };
        // Ridged fractal for the massifs. Few octaves and a low frequency keep the
        // ridges big and smooth, so the contours form broad flowing ripples rather
        // than many spotty little peaks.
        _ridge = new FastNoiseLite
        {
            Seed = 1539,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            FractalOctaves = 3,
            FractalGain = 0.4f,
            FractalLacunarity = 1.9f,
            Frequency = 0.0019f
        };
        // Low-frequency, domain-warped mask that selects a few large regions to become
        // mountain massifs, leaving broad smooth lowlands between them.
        _mountainMask = new FastNoiseLite
        {
            Seed = 1640,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3,
            Frequency = 0.001f,
            DomainWarpEnabled = true,
            DomainWarpAmplitude = 80f,
            DomainWarpFrequency = 0.004f
        };
        // Higher-frequency warp used to break up the lake shoreline and river path
        // so they read as organic rather than a perfect circle or a clean sine.
        _warp = new FastNoiseLite
        {
            Seed = 1741,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3,
            Frequency = 0.02f
        };
    }

    // World height in units for a world XZ position. The land is mostly broad, smooth
    // lowland (few, widely-spaced contours) with a handful of big rippling mountain
    // massifs; a small ocean sits in the far SW corner, plus an inland lake and river.
    private float SampleHeight(float wx, float wz)
    {
        float nx = wx / Half; // -1..1
        float nz = wz / Half; // -1..1

        float coast = (nx - nz) * 0.5f;             // -1 ocean corner .. +1 inland
        float cont = _continent.GetNoise2D(wx, wz); // smooth, domain-warped large relief

        // Small ocean only in the far SW corner; strong land bias keeps water minimal.
        float land = coast * 0.85f + cont * 0.4f + 0.6f;
        float shore = Mathf.SmoothStep(-0.2f, 0.25f, land); // 0 open ocean, 1 inland

        // Broad, smooth base relief: large gentle swells so the lowlands carry only a
        // few widely-spaced contours instead of many little islands.
        float height = Mathf.Lerp(MinHeight, 16f, shore);
        height += cont * 28f * shore;

        // Big rippling mountain massifs: a low-frequency mask picks a few large regions
        // across the land; smooth ridged noise stacks broad concentric ripples inside them.
        float massif = Mathf.SmoothStep(0.38f, 0.66f, _mountainMask.GetNoise2D(wx, wz) * 0.5f + 0.5f);
        float ridged = Mathf.Pow(_ridge.GetNoise2D(wx, wz) * 0.5f + 0.5f, 1.1f);
        height += massif * ridged * 85f * shore;

        // Inland lake: a noisy radius so the shoreline wobbles instead of being a circle.
        float lakeDist = Distance(wx, wz, 180f, 40f) + _warp.GetNoise2D(wx, wz) * 32f;
        float lake = 1f - Mathf.SmoothStep(45f, 80f, lakeDist);
        height = Mathf.Lerp(height, -12f, lake * (1f - massif));

        // River valley: a smooth, rounded depression along a meandering path rather
        // than a flat-bottomed trench. The bed eases down to the centre as a bowl and
        // dips below sea level in the lowlands to read as water; its width and depth
        // vary along its length, and it fades near the massifs so the river weaves
        // between the mountains instead of slotting straight through one.
        float meander = _warp.GetNoise2D(800f, wz * 0.22f) * 70f
                      + _warp.GetNoise2D(1200f, wz * 0.07f) * 50f
                      + Mathf.Sin(wz * 0.006f) * 35f;
        float riverX = -10f + meander;
        float valleyWidth = 78f + _warp.GetNoise2D(wz, 300f) * 22f;
        float across = Mathf.Clamp(Mathf.Abs(wx - riverX) / valleyWidth, 0f, 1f);
        float valley = (1f - across) * (1f - across); // smooth rounded bowl, no flat floor
        float depth = 44f + _warp.GetNoise2D(1500f, wz * 0.3f) * 10f;
        height -= valley * depth * shore * (1f - massif * 0.5f);

        float spawn = 1f - Mathf.SmoothStep(0f, 22f, Distance(wx, wz, 120f, 130f));
        height = Mathf.Lerp(height, Mathf.Max(height, 12f), spawn);

        return Mathf.Clamp(height, MinHeight, MaxHeight);
    }

    private static float Distance(float ax, float az, float bx, float bz)
    {
        float dx = ax - bx;
        float dz = az - bz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
