using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace TopographicMap;

// Pure-C# container for extracted contour geometry. No Godot dependencies.
public sealed class ContourPolyline
{
    public List<ContourPoint> Points { get; init; } = [];
    public float Level { get; init; } // normalized [0,1]
    public bool IsMajor { get; init; }
    public float MinX { get; init; }
    public float MinY { get; init; }
    public float MaxX { get; init; }
    public float MaxY { get; init; }
}

public sealed class ContourField
{
    public List<ContourPolyline> Polylines { get; } = [];

    // Builds polylines for every interior contour level. The field is normalized
    // height (0..1); levels are derived from world heights so callers can keep
    // thinking in world units. A level is major when its index round(H/interval)
    // is a multiple of majorEvery. Levels are independent, so they are extracted
    // in parallel across CPU cores.
    public static ContourField Build(float[] field, float[] mask, int cols, int rows,
        float heightMin, float heightMax, float interval, int majorEvery, float simplifyEpsilon = 0.00015f)
    {
        var result = new ContourField();
        float span = Math.Max(0.0001f, heightMax - heightMin);

        int firstIndex = (int)Math.Floor(heightMin / interval) + 1;
        int lastIndex = (int)Math.Ceiling(heightMax / interval) - 1;
        int levelCount = lastIndex - firstIndex + 1;
        if (levelCount <= 0)
        {
            return result;
        }

        var perLevel = new List<ContourPolyline>[levelCount];
        Parallel.For(0, levelCount, li =>
        {
            var polylines = new List<ContourPolyline>();
            int index = firstIndex + li;
            float worldHeight = index * interval;
            if (worldHeight > heightMin && worldHeight < heightMax)
            {
                float level = (worldHeight - heightMin) / span;
                bool isMajor = majorEvery > 0 && index % majorEvery == 0;

                var segments = MarchingSquares.ExtractSegments(field, mask, cols, rows, level);
                var chains = MarchingSquares.ChainSegments(segments);
                foreach (var rawChain in chains)
                {
                    var chain = MarchingSquares.Simplify(rawChain, simplifyEpsilon);
                    float minX = float.MaxValue, minY = float.MaxValue;
                    float maxX = float.MinValue, maxY = float.MinValue;
                    foreach (var point in chain)
                    {
                        minX = Math.Min(minX, point.X);
                        minY = Math.Min(minY, point.Y);
                        maxX = Math.Max(maxX, point.X);
                        maxY = Math.Max(maxY, point.Y);
                    }

                    polylines.Add(new()
                    {
                        Points = chain,
                        Level = level,
                        IsMajor = isMajor,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY
                    });
                }
            }

            perLevel[li] = polylines;
        });

        foreach (var polylines in perLevel)
        {
            result.Polylines.AddRange(polylines);
        }

        return result;
    }
}
