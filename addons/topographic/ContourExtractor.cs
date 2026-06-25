using System;

namespace TopographicMap;

// Turns the raw bytes of a height-buffer image (Rgbaf: R = normalized height,
// G = coverage mask, 16 bytes per pixel) into a ContourField. Pure C# with no
// Godot types, so the heavy Marching Squares pass can run on a background thread.
// Optionally box-downsamples to maxResolution; at full resolution the line
// crossings line up with the per-pixel tint so band edges do not bleed.
public static class ContourExtractor
{
    public static ContourField Build(byte[] data, int srcW, int srcH, float heightMin,
        float heightMax, float interval, int majorEvery, int maxResolution)
    {
        const int stride = 16; // bytes per pixel in Rgbaf (4 channels x 4 bytes)
        int step = Math.Max(1, (Math.Max(srcW, srcH) + maxResolution - 1) / maxResolution);
        int cols = (srcW + step - 1) / step;
        int rows = (srcH + step - 1) / step;

        float[] field = new float[cols * rows];
        float[] mask = new float[cols * rows];
        for (int ry = 0; ry < rows; ry++)
        {
            for (int rx = 0; rx < cols; rx++)
            {
                // Average a step x step block (a single texel when at full resolution).
                float sum = 0f;
                float maskSum = 0f;
                int count = 0;
                for (int dy = 0; dy < step; dy++)
                {
                    int sy = ry * step + dy;
                    if (sy >= srcH)
                    {
                        break;
                    }

                    for (int dx = 0; dx < step; dx++)
                    {
                        int sx = rx * step + dx;
                        if (sx >= srcW)
                        {
                            break;
                        }

                        int offset = (sy * srcW + sx) * stride;
                        sum += BitConverter.ToSingle(data, offset);
                        maskSum += BitConverter.ToSingle(data, offset + 4);
                        count++;
                    }
                }

                int index = ry * cols + rx;
                field[index] = count > 0 ? sum / count : 0f;
                mask[index] = count > 0 && maskSum / count >= 0.5f ? 1f : 0f;
            }
        }

        return ContourField.Build(field, mask, cols, rows, heightMin, heightMax, interval, majorEvery);
    }
}
