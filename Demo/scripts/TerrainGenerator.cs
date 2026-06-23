using Godot;

namespace TopographicCameraShader.Demo;

public readonly record struct TerrainBake(
    ArrayMesh Mesh,
    float[] HeightField,
    int GridSize,
    float MinHeight,
    float MaxHeight);

public static class TerrainGenerator
{
    private record struct NoiseSet(
        FastNoiseLite Continent,
        FastNoiseLite Detail,
        FastNoiseLite Mountain,
        FastNoiseLite MountainMask);

    public static TerrainBake CreateTerrain(float worldSize, int resolution, int seed)
    {
        float freqScale = 1200f / worldSize;
        var noises = BuildNoises(seed, freqScale);

        float half = worldSize * 0.5f;
        float step = worldSize / resolution;
        float[,] heights = new float[resolution + 1, resolution + 1];

        for (int z = 0; z <= resolution; z++)
        for (int x = 0; x <= resolution; x++)
            heights[x, z] = SampleHeight(-half + x * step, -half + z * step, worldSize, in noises);

        Smooth(heights, resolution);

        (float[] heightField, int gridSize, float minHeight, float maxHeight) = BakeHeightField(heights, resolution);
        var mesh = BuildMesh(heights, resolution, worldSize);
        return new(mesh, heightField, gridSize, minHeight, maxHeight);
    }

    // DomainWarpAmplitude is a world-space distance, so it scales DOWN as the world
    // shrinks -- opposite of Frequency, which scales up.
    private static NoiseSet BuildNoises(int seed, float freqScale) => new(
        new()
        {
            Seed = seed,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3,
            FractalLacunarity = 2.0f,
            FractalGain = 0.5f,
            Frequency = 0.0011f * freqScale,
            DomainWarpEnabled = true,
            DomainWarpType = FastNoiseLite.DomainWarpTypeEnum.SimplexReduced,
            DomainWarpAmplitude = 60f / freqScale,
            DomainWarpFrequency = 0.004f * freqScale,
            DomainWarpFractalType = FastNoiseLite.DomainWarpFractalTypeEnum.Progressive,
            DomainWarpFractalOctaves = 3
        },
        new()
        {
            Seed = seed + 101,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 3,
            FractalLacunarity = 2.1f,
            FractalGain = 0.5f,
            Frequency = 0.0030f * freqScale
        },
        new()
        {
            Seed = seed + 202,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Ridged,
            FractalOctaves = 5,
            FractalLacunarity = 2.0f,
            FractalGain = 0.5f,
            Frequency = 0.0019f * freqScale
        },
        new()
        {
            Seed = seed + 303,
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 2,
            Frequency = 0.0012f * freqScale
        }
    );

    private static float SampleHeight(float wx, float wz, float worldSize, in NoiseSet n)
    {
        float maxRadius = worldSize * 0.5f;
        float d = Mathf.Sqrt(wx * wx + wz * wz) / maxRadius;
        float falloff = 1f - Mathf.SmoothStep(0.6f, 1.05f, d);

        float cont = n.Continent.GetNoise2D(wx, wz) * 0.5f + 0.5f;
        float det = n.Detail.GetNoise2D(wx, wz);
        float t = Mathf.Clamp((cont + det * 0.008f) * falloff, 0f, 1f);

        float baseHeight = Mathf.Lerp(-10f, 26f, Mathf.Pow(t, 1.4f));

        // Keep spawn center as dry land without disturbing beaches elsewhere.
        float center = Mathf.Clamp(1f - d / 0.14f, 0f, 1f);
        baseHeight = Mathf.Lerp(baseHeight, Mathf.Max(baseHeight, 7f), center);

        // River only cuts through lowland; excluded from spawn center and mountains.
        float riverX = worldSize * 0.18f
                       + Mathf.Sin(wz * (Mathf.Tau / (worldSize * 0.7f))) * worldSize * 0.10f
                       + Mathf.Sin(wz * (Mathf.Tau / (worldSize * 0.27f))) * worldSize * 0.04f;
        float river = (1f - Mathf.SmoothStep(worldSize * 0.018f, worldSize * 0.05f, Mathf.Abs(wx - riverX)))
                      * (1f - Mathf.SmoothStep(6f, 20f, baseHeight))
                      * (1f - center);
        baseHeight = Mathf.Lerp(baseHeight, -2.5f, river);

        float ridge = n.Mountain.GetNoise2D(wx, wz) * 0.5f + 0.5f;
        float mask = Mathf.SmoothStep(0.5f, 0.72f, n.MountainMask.GetNoise2D(wx, wz) * 0.5f + 0.5f);
        float mountainHeight = Mathf.Pow(ridge, 2.2f) * mask * falloff * (1f - river) * (1f - center) * 62f;

        return baseHeight + mountainHeight;
    }

    private static void Smooth(float[,] heights, int resolution, int passes = 3)
    {
        for (int pass = 0; pass < passes; pass++)
        {
            float[,] src = (float[,])heights.Clone();
            for (int z = 1; z < resolution; z++)
            for (int x = 1; x < resolution; x++)
            {
                float sum = 0f;
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    sum += src[x + dx, z + dz];
                heights[x, z] = sum / 9f;
            }
        }
    }

    private static (float[] field, int gridSize, float minHeight, float maxHeight) BakeHeightField(
        float[,] heights, int resolution)
    {
        int gridSize = resolution + 1;
        float[] field = new float[gridSize * gridSize];
        float minH = float.MaxValue, maxH = float.MinValue;
        for (int z = 0; z <= resolution; z++)
        for (int x = 0; x <= resolution; x++)
        {
            float h = heights[x, z];
            field[z * gridSize + x] = h;
            minH = Mathf.Min(minH, h);
            maxH = Mathf.Max(maxH, h);
        }

        return (field, gridSize, minH, maxH);
    }

    private static ArrayMesh BuildMesh(float[,] heights, int resolution, float worldSize)
    {
        float half = worldSize * 0.5f;
        float step = worldSize / resolution;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        for (int z = 0; z < resolution; z++)
        for (int x = 0; x < resolution; x++)
        {
            var v00 = Vert(x, z, half, step, heights);
            var v10 = Vert(x + 1, z, half, step, heights);
            var v01 = Vert(x, z + 1, half, step, heights);
            var v11 = Vert(x + 1, z + 1, half, step, heights);

            st.AddVertex(v00);
            st.AddVertex(v01);
            st.AddVertex(v11);
            st.AddVertex(v00);
            st.AddVertex(v11);
            st.AddVertex(v10);
        }

        st.Index();
        st.GenerateNormals();
        return st.Commit();
    }

    private static Vector3 Vert(int x, int z, float half, float step, float[,] h) =>
        new(-half + x * step, h[x, z], -half + z * step);
}
