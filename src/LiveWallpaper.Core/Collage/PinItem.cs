using System;

namespace LiveWallpaper.Core.Collage;

public sealed class PinItem
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required RgbColor DominantColor { get; init; }
    public required HueGroup HueGroup { get; init; }
    public float Hue { get; init; }
    public float Saturation { get; init; }
    public float Brightness { get; init; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Category { get; init; } = "Общее";
    public bool IsFavorite { get; init; }
    public bool IsHidden { get; init; }
}

public sealed class CollageSelectionResult
{
    public required IReadOnlyList<PinItem> SelectedPins { get; init; }
    public required HueGroup PrimaryHue { get; init; }
    public required RgbColor AverageBackdropColor { get; init; }
}
