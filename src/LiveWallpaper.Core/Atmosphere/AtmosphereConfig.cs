namespace LiveWallpaper.Core.Atmosphere;

public sealed class AtmosphereConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Красноватые края экрана при падении прода (site down или critical alert).</summary>
    public bool EnableProdAlert { get; set; } = true;

    /// <summary>Тёплый фон перед созвоном.</summary>
    public bool EnableUpcomingCall { get; set; } = true;

    /// <summary>Янтарная точка / маяк при низком балансе Timeweb.</summary>
    public bool EnableLowBalanceBeacon { get; set; } = true;

    /// <summary>Сколько минут до начала созвона включать тёплый фон.</summary>
    public int UpcomingCallMinutesBefore { get; set; } = 15;

    // Test overrides (for UI verification and user testing)
    public bool TestProdAlert { get; set; }
    public bool TestUpcomingCall { get; set; }
    public bool TestLowBalance { get; set; }
}
