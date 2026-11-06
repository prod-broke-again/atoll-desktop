using System;
using System.Collections.Generic;
using SkiaSharp;

namespace LiveWallpaper.Core.Collage;

public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public (float H, float S, float V) ToHsv()
    {
        float r = R / 255f;
        float g = G / 255f;
        float b = B / 255f;

        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;

        float h = 0f;
        if (delta > 0.0001f)
        {
            if (Math.Abs(max - r) < 0.0001f)
            {
                h = 60f * (((g - b) / delta) % 6f);
            }
            else if (Math.Abs(max - g) < 0.0001f)
            {
                h = 60f * (((b - r) / delta) + 2f);
            }
            else
            {
                h = 60f * (((r - g) / delta) + 4f);
            }
        }

        if (h < 0f) h += 360f;
        float s = max <= 0.0001f ? 0f : delta / max;
        float v = max;

        return (h, s, v);
    }

    public static RgbColor FromHsv(float h, float s, float v)
    {
        h = (h % 360f + 360f) % 360f;
        s = Math.Clamp(s, 0f, 1f);
        v = Math.Clamp(v, 0f, 1f);

        float c = v * s;
        float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
        float m = v - c;

        float r1 = 0, g1 = 0, b1 = 0;
        if (h < 60f) { r1 = c; g1 = x; b1 = 0; }
        else if (h < 120f) { r1 = x; g1 = c; b1 = 0; }
        else if (h < 180f) { r1 = 0; g1 = c; b1 = x; }
        else if (h < 240f) { r1 = 0; g1 = x; b1 = c; }
        else if (h < 300f) { r1 = x; g1 = 0; b1 = c; }
        else { r1 = c; g1 = 0; b1 = x; }

        byte r = (byte)Math.Clamp(Math.Round((r1 + m) * 255f), 0, 255);
        byte g = (byte)Math.Clamp(Math.Round((g1 + m) * 255f), 0, 255);
        byte b = (byte)Math.Clamp(Math.Round((b1 + m) * 255f), 0, 255);

        return new RgbColor(r, g, b);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

public static class ColorQuantizer
{
    public static List<RgbColor> ExtractDominantColors(SKBitmap sourceBitmap, int k = 3, int sampleSize = 80)
    {
        ArgumentNullException.ThrowIfNull(sourceBitmap);

        using var resized = new SKBitmap(sampleSize, sampleSize, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(resized);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(sourceBitmap, new SKRect(0, 0, sampleSize, sampleSize), paint);

        var pixels = new List<RgbColor>(sampleSize * sampleSize);
        var span = resized.GetPixelSpan();

        for (int i = 0; i < span.Length; i += 4)
        {
            byte r = span[i];
            byte g = span[i + 1];
            byte b = span[i + 2];
            byte a = span[i + 3];

            if (a > 128)
            {
                pixels.Add(new RgbColor(r, g, b));
            }
        }

        if (pixels.Count == 0)
        {
            return new List<RgbColor> { new(30, 30, 35) };
        }

        return RunKMeans(pixels, k, maxIterations: 10);
    }

    private static List<RgbColor> RunKMeans(List<RgbColor> pixels, int k, int maxIterations)
    {
        k = Math.Min(k, pixels.Count);
        var centroids = new List<RgbColor>(k);

        // Deterministic initial centroid selection evenly spaced across pixels
        int step = pixels.Count / k;
        for (int i = 0; i < k; i++)
        {
            centroids.Add(pixels[i * step]);
        }

        int[] assignments = new int[pixels.Count];

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;

            // Assignment step
            for (int i = 0; i < pixels.Count; i++)
            {
                var p = pixels[i];
                int bestCluster = 0;
                int bestDist = int.MaxValue;

                for (int c = 0; c < k; c++)
                {
                    var cent = centroids[c];
                    int dr = p.R - cent.R;
                    int dg = p.G - cent.G;
                    int db = p.B - cent.B;
                    int dist = dr * dr + dg * dg + db * db;

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestCluster = c;
                    }
                }

                if (assignments[i] != bestCluster)
                {
                    assignments[i] = bestCluster;
                    changed = true;
                }
            }

            if (!changed && iter > 0) break;

            // Update step
            long[] sumR = new long[k];
            long[] sumG = new long[k];
            long[] sumB = new long[k];
            int[] counts = new int[k];

            for (int i = 0; i < pixels.Count; i++)
            {
                int cluster = assignments[i];
                sumR[cluster] += pixels[i].R;
                sumG[cluster] += pixels[i].G;
                sumB[cluster] += pixels[i].B;
                counts[cluster]++;
            }

            for (int c = 0; c < k; c++)
            {
                if (counts[c] > 0)
                {
                    centroids[c] = new RgbColor(
                        (byte)(sumR[c] / counts[c]),
                        (byte)(sumG[c] / counts[c]),
                        (byte)(sumB[c] / counts[c]));
                }
            }
        }

        return centroids;
    }
}
