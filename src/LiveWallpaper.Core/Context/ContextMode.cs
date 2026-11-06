namespace LiveWallpaper.Core.Context;

public static class ContextMode
{
    public const string Auto = "auto";
    public const string Work = "work";
    public const string Game = "game";
    public const string Night = "night";

    public static readonly string[] All = { Auto, Work, Game, Night };

    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return Auto;
        var m = mode.Trim().ToLowerInvariant();
        return m switch
        {
            Work => Work,
            Game => Game,
            Night => Night,
            _ => Auto
        };
    }

    public static string GetDisplayName(string mode)
    {
        return Normalize(mode) switch
        {
            Work => "Рабочий",
            Game => "Игровой",
            Night => "Ночной",
            _ => "Авто"
        };
    }
}
