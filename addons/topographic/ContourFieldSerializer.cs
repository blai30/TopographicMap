using System;
using System.Collections.Generic;

namespace TopographicMap;

// Pure-C# flatten/inflate between a ContourField and packed primitive arrays, so
// the field can be serialized into a Godot resource without the core depending on
// Godot. Inflate recomputes each polyline's bounding box and major flag, so those
// are never stored. No Godot types, so this is unit-tested by the console project.
public static class ContourFieldSerializer
{
    // Concatenates every polyline's points (interleaved x,y) into pointsXy, with a
    // matching per-polyline point count and normalized level.
    public static void Flatten(ContourField field, out float[] pointsXy, out int[] pointCounts, out float[] levels)
    {
        var polylines = field.Polylines;
        pointCounts = new int[polylines.Count];
        levels = new float[polylines.Count];

        int total = 0;
        foreach (var t in polylines)
        {
            total += t.Points.Count;
        }

        pointsXy = new float[total * 2];
        int offset = 0;
        for (int i = 0; i < polylines.Count; i++)
        {
            var polyline = polylines[i];
            pointCounts[i] = polyline.Points.Count;
            levels[i] = polyline.Level;
            foreach (var point in polyline.Points)
            {
                pointsXy[offset++] = point.X;
                pointsXy[offset++] = point.Y;
            }
        }
    }

    // Rebuilds a ContourField from packed arrays, recomputing each polyline's
    // bounding box and major flag from the level and the level params.
    public static ContourField Inflate(float[] pointsXy, int[] pointCounts, float[] levels,
        float heightMin, float heightMax, float interval, int majorEvery)
    {
        var result = new ContourField();
        float span = Math.Max(0.0001f, heightMax - heightMin);

        int offset = 0;
        for (int i = 0; i < pointCounts.Length; i++)
        {
            int count = pointCounts[i];
            var points = new List<ContourPoint>(count);
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int j = 0; j < count; j++)
            {
                float x = pointsXy[offset++];
                float y = pointsXy[offset++];
                points.Add(new(x, y));
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }

            float level = levels[i];
            float worldHeight = heightMin + level * span;
            int index = (int)MathF.Round(worldHeight / interval);
            bool isMajor = majorEvery > 0 && index % majorEvery == 0;

            result.Polylines.Add(new()
            {
                Points = points,
                Level = level,
                IsMajor = isMajor,
                MinX = minX,
                MinY = minY,
                MaxX = maxX,
                MaxY = maxY
            });
        }

        return result;
    }
}
