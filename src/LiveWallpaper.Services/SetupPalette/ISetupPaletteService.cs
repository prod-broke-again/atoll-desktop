using System;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;

namespace LiveWallpaper.Services.SetupPalette;

public interface ISetupPaletteService
{
    RgbColor? CurrentAccent { get; }

    event EventHandler<RgbColor>? PaletteApplied;

    Task ApplyAccentAsync(RgbColor accent, CancellationToken ct = default);

    Task ApplyCurrentAsync(CancellationToken ct = default);
}
