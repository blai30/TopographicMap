using System;
using System.Collections.Generic;

namespace TopographicMap;

// Pure-C# Marching Squares. No Godot dependencies so it can be unit-tested.
// Points are in normalized [0,1] space: grid column c maps to x = c/(cols-1),
// row r maps to y = r/(rows-1). The field is the normalized height (0..1); mask
// is 1 for real terrain and 0 for background.
public readonly record struct ContourPoint(float X, float Y);

public static class MarchingSquares
{
    // Returns a flat list of segment endpoints: points [2*i] and [2*i+1] are the
    // endpoints of segment i. Crossings are computed in a canonical corner order
    // so a shared edge yields identical points from both adjacent cells (needed
    // for exact chaining later).
    public static List<ContourPoint> ExtractSegments(float[] field, float[] mask, int cols, int rows, float level)
    {
        var segments = new List<ContourPoint>();
        for (int cy = 0; cy < rows - 1; cy++)
        {
            for (int cx = 0; cx < cols - 1; cx++)
            {
                int iTl = cy * cols + cx;
                int iTr = cy * cols + cx + 1;
                int iBr = (cy + 1) * cols + cx + 1;
                int iBl = (cy + 1) * cols + cx;

                if (mask[iTl] < 0.5f || mask[iTr] < 0.5f || mask[iBr] < 0.5f || mask[iBl] < 0.5f)
                {
                    continue;
                }

                float hTl = field[iTl], hTr = field[iTr], hBr = field[iBr], hBl = field[iBl];
                int code = (hTl > level ? 1 : 0) | (hTr > level ? 2 : 0)
                                                 | (hBr > level ? 4 : 0) | (hBl > level ? 8 : 0);
                if (code is 0 or 15)
                {
                    continue;
                }

                switch (code)
                {
                    case 1: Seg(Left(), Top()); break;
                    case 2: Seg(Top(), Right()); break;
                    case 3: Seg(Left(), Right()); break;
                    case 4: Seg(Right(), Bottom()); break;
                    case 5:
                        Seg(Left(), Top());
                        Seg(Right(), Bottom());
                        break;
                    case 6: Seg(Top(), Bottom()); break;
                    case 7: Seg(Left(), Bottom()); break;
                    case 8: Seg(Bottom(), Left()); break;
                    case 9: Seg(Top(), Bottom()); break;
                    case 10:
                        Seg(Left(), Bottom());
                        Seg(Top(), Right());
                        break;
                    case 11: Seg(Right(), Bottom()); break;
                    case 12: Seg(Left(), Right()); break;
                    case 13: Seg(Top(), Right()); break;
                    case 14: Seg(Left(), Top()); break;
                }

                continue;

                // Canonical edge crossings (a is always the lower row-major corner).
                ContourPoint Cross(int ax, int ay, int bx, int by)
                {
                    float va = field[ay * cols + ax];
                    float vb = field[by * cols + bx];
                    float t = (level - va) / (vb - va);
                    float x = (ax + (bx - ax) * t) / (cols - 1);
                    float y = (ay + (by - ay) * t) / (rows - 1);
                    return new(x, y);
                }

                ContourPoint Top() => Cross(cx, cy, cx + 1, cy);
                ContourPoint Right() => Cross(cx + 1, cy, cx + 1, cy + 1);
                ContourPoint Bottom() => Cross(cx, cy + 1, cx + 1, cy + 1);
                ContourPoint Left() => Cross(cx, cy, cx, cy + 1);

                void Seg(ContourPoint a, ContourPoint b)
                {
                    segments.Add(a);
                    segments.Add(b);
                }
            }
        }

        return segments;
    }

    // Links loose segments into polylines by matching shared endpoints. Endpoints
    // are quantized to a grid so floating-point near-equal points join cleanly.
    public static List<List<ContourPoint>> ChainSegments(List<ContourPoint> segments)
    {
        int n = segments.Count / 2;
        bool[] used = new bool[n];

        // endpoint key -> list of segment indices touching it
        var touching = new Dictionary<long, List<int>>();

        for (int i = 0; i < n; i++)
        {
            Register(segments[2 * i], i);
            Register(segments[2 * i + 1], i);
        }

        var result = new List<List<ContourPoint>>();
        for (int i = 0; i < n; i++)
        {
            if (used[i])
            {
                continue;
            }

            used[i] = true;
            var chain = new LinkedList<ContourPoint>();
            chain.AddLast(segments[2 * i]);
            chain.AddLast(segments[2 * i + 1]);

            // Extend from the tail.
            while (true)
            {
                int s = NextSegment(chain.Last!.Value, out var far);
                if (s < 0)
                {
                    break;
                }

                used[s] = true;
                chain.AddLast(far);
            }

            // Extend from the head.
            while (true)
            {
                int s = NextSegment(chain.First!.Value, out var far);
                if (s < 0)
                {
                    break;
                }

                used[s] = true;
                chain.AddFirst(far);
            }

            result.Add([.. chain]);
        }

        return result;

        static long Key(ContourPoint p)
        {
            long qx = (long)MathF.Round(p.X * 1_000_000f);
            long qy = (long)MathF.Round(p.Y * 1_000_000f);
            return qx * 2_000_003L + qy;
        }

        void Register(ContourPoint p, int seg)
        {
            long k = Key(p);
            if (!touching.TryGetValue(k, out var list))
            {
                list = [];
                touching[k] = list;
            }

            list.Add(seg);
        }

        // Returns the far endpoint of an unused segment touching point p, or -1.
        int NextSegment(ContourPoint p, out ContourPoint far)
        {
            far = default;
            if (!touching.TryGetValue(Key(p), out var list)) return -1;
            foreach (int s in list)
            {
                if (used[s])
                {
                    continue;
                }

                var e0 = segments[2 * s];
                var e1 = segments[2 * s + 1];
                far = Key(e0) == Key(p) ? e1 : e0;
                return s;
            }

            return -1;
        }
    }

    // Ramer-Douglas-Peucker simplification. Drops points that lie within epsilon
    // (normalized units) of the straight line between kept neighbors, cutting the
    // dense per-cell point count with no visible change. Iterative to avoid deep
    // recursion on long polylines.
    public static List<ContourPoint> Simplify(List<ContourPoint> points, float epsilon)
    {
        int n = points.Count;
        if (n < 3)
        {
            return new(points);
        }

        bool[] keep = new bool[n];
        keep[0] = true;
        keep[n - 1] = true;
        float eps2 = epsilon * epsilon;
        var stack = new Stack<(int First, int Last)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            (int first, int last) = stack.Pop();
            float maxDist2 = 0f;
            int index = -1;
            for (int i = first + 1; i < last; i++)
            {
                float d2 = PointSegmentDistanceSq(points[i], points[first], points[last]);
                if (d2 > maxDist2)
                {
                    maxDist2 = d2;
                    index = i;
                }
            }

            if (index != -1 && maxDist2 > eps2)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        var result = new List<ContourPoint>();
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }

        return result;
    }

    private static float PointSegmentDistanceSq(ContourPoint p, ContourPoint a, ContourPoint b)
    {
        float abx = b.X - a.X;
        float aby = b.Y - a.Y;
        float apx = p.X - a.X;
        float apy = p.Y - a.Y;
        float ab2 = abx * abx + aby * aby;
        float t = ab2 > 1e-12f ? (apx * abx + apy * aby) / ab2 : 0f;
        t = Math.Clamp(t, 0f, 1f);
        float cx = a.X + abx * t;
        float cy = a.Y + aby * t;
        float dx = p.X - cx;
        float dy = p.Y - cy;
        return dx * dx + dy * dy;
    }
}
