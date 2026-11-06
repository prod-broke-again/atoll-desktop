using System;
using System.Collections.Generic;

namespace LiveWallpaper.Core.Collage;

public enum HueGroup
{
    NeutralDark = 0,
    NeutralLight = 1,
    NeutralMuted = 2,
    Red = 3,
    Orange = 4,
    Yellow = 5,
    Green = 6,
    Cyan = 7,
    Blue = 8,
    Purple = 9,
    Magenta = 10
}

public static class HueClassifier
{
    public static HueGroup Classify(RgbColor color)
    {
        var (h, s, v) = color.ToHsv();

        if (v < 0.22f) return HueGroup.NeutralDark;
        if (s < 0.16f && v >= 0.75f) return HueGroup.NeutralLight;
        if (s < 0.18f) return HueGroup.NeutralMuted;

        return h switch
        {
            >= 345f or < 15f => HueGroup.Red,
            >= 15f and < 45f => HueGroup.Orange,
            >= 45f and < 70f => HueGroup.Yellow,
            >= 70f and < 165f => HueGroup.Green,
            >= 165f and < 200f => HueGroup.Cyan,
            >= 200f and < 260f => HueGroup.Blue,
            >= 260f and < 305f => HueGroup.Purple,
            _ => HueGroup.Magenta
        };
    }

    public static IReadOnlyList<HueGroup> GetHarmoniousNeighbors(HueGroup group)
    {
        return group switch
        {
            HueGroup.Red => new[] { HueGroup.Red, HueGroup.Orange, HueGroup.Magenta },
            HueGroup.Orange => new[] { HueGroup.Orange, HueGroup.Red, HueGroup.Yellow },
            HueGroup.Yellow => new[] { HueGroup.Yellow, HueGroup.Orange, HueGroup.Green },
            HueGroup.Green => new[] { HueGroup.Green, HueGroup.Yellow, HueGroup.Cyan },
            HueGroup.Cyan => new[] { HueGroup.Cyan, HueGroup.Green, HueGroup.Blue },
            HueGroup.Blue => new[] { HueGroup.Blue, HueGroup.Cyan, HueGroup.Purple },
            HueGroup.Purple => new[] { HueGroup.Purple, HueGroup.Blue, HueGroup.Magenta },
            HueGroup.Magenta => new[] { HueGroup.Magenta, HueGroup.Purple, HueGroup.Red },
            HueGroup.NeutralDark => new[] { HueGroup.NeutralDark, HueGroup.NeutralMuted, HueGroup.Blue },
            HueGroup.NeutralLight => new[] { HueGroup.NeutralLight, HueGroup.NeutralMuted, HueGroup.Yellow },
            _ => new[] { HueGroup.NeutralMuted, HueGroup.NeutralDark, HueGroup.NeutralLight }
        };
    }
}
