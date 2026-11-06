using System;
using LiveWallpaper.Core.Atmosphere;

namespace LiveWallpaper.Services.Atmosphere;

public interface IAtmosphereService
{
    AtmosphereSignalsState CurrentState { get; }

    event EventHandler<AtmosphereSignalsState>? AtmosphereUpdated;

    void TriggerTestSignal(string signalType, bool active);
}
