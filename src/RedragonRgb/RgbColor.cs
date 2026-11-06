namespace RedragonRgb;

/// <summary>8-bit RGB color for a single LED slot.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor Black => new(0, 0, 0);
    public static RgbColor White => new(255, 255, 255);
    public static RgbColor Red => new(255, 0, 0);
    public static RgbColor Green => new(0, 255, 0);
    public static RgbColor Blue => new(0, 0, 255);
    public static RgbColor Cyan => new(0, 255, 255);
    public static RgbColor Magenta => new(255, 0, 255);
    public static RgbColor Yellow => new(255, 255, 0);

    public static RgbColor FromRgb(byte r, byte g, byte b) => new(r, g, b);

    public static RgbColor FromHex(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        hex = hex.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8))
            throw new ArgumentException("Expected RRGGBB or AARRGGBB hex.", nameof(hex));

        var offset = hex.Length == 8 ? 2 : 0;
        return new RgbColor(
            Convert.ToByte(hex.Substring(offset, 2), 16),
            Convert.ToByte(hex.Substring(offset + 2, 2), 16),
            Convert.ToByte(hex.Substring(offset + 4, 2), 16));
    }

    public RgbColor Scale(float intensity)
    {
        intensity = Math.Clamp(intensity, 0f, 1f);
        return new RgbColor(
            (byte)Math.Clamp((int)(R * intensity), 0, 255),
            (byte)Math.Clamp((int)(G * intensity), 0, 255),
            (byte)Math.Clamp((int)(B * intensity), 0, 255));
    }

    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}
