using System;
using Godot;

namespace TopographicMap;

// Turns a height-buffer Image (R = normalized height, G = coverage mask) into a
// ContourField. Reads the raw image bytes once (converted to 32-bit float RGBA)
// rather than calling GetPixel per texel, so even a full-resolution readback is
// fast. Optionally box-downsamples to maxResolution; at full resolution the line
// crossings line up with the per-pixel tint so band edges do not bleed.
public static class ContourExtractor
{
    public static ContourField Build(Image image, float heightMin, float heightMax,
        float interval, int majorEvery, int maxResolution)
    {
        image.Convert(Image.Format.Rgbaf);
        byte[] data = image.GetData();
        int srcW = image.GetWidth();
        int srcH = image.GetHeight();
        const int stride = 16; // bytes per pixel in Rgbaf (4 channels x 4 bytes)

        int step = Mathf.Max(1, Mathf.CeilToInt((float)Mathf.Max(srcW, srcH) / maxResolution));
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
