using System;

namespace LiveWallpaper.Services.Weather;

public sealed class WeatherData
{
    public bool Enabled { get; set; }
    public string City { get; set; } = string.Empty;
    public double Temperature { get; set; }
    public double ApparentTemperature { get; set; }
    public int WeatherCode { get; set; }
    public string WeatherDescription { get; set; } = "Ясно";
    public string Icon { get; set; } = "sun";
    public double WindSpeed { get; set; }
    public int RelativeHumidity { get; set; }
    public bool IsDay { get; set; } = true;
    public string Units { get; set; } = "metric";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class WmoWeatherCodeHelper
{
    public static (string Description, string Icon) Describe(int code, bool isDay)
    {
        return code switch
        {
            0 => isDay ? ("Ясно", "sun") : ("Ясно", "moon"),
            1 => isDay ? ("В основном ясно", "sun-cloud") : ("В основном ясно", "moon-cloud"),
            2 => ("Переменная облачность", "cloud-sun"),
            3 => ("Пасмурно", "cloud"),
            45 or 48 => ("Туман", "fog"),
            51 or 53 or 55 => ("Морось", "drizzle"),
            56 or 57 => ("Ледяная морось", "sleet"),
            61 or 63 or 65 => ("Дождь", "rain"),
            66 or 67 => ("Ледяной дождь", "sleet"),
            71 or 73 or 75 => ("Снегопад", "snow"),
            77 => ("Снежные зёрна", "snow"),
            80 or 81 or 82 => ("Ливень", "heavy-rain"),
            85 or 86 => ("Метель", "heavy-snow"),
            95 => ("Гроза", "thunder"),
            96 or 99 => ("Гроза с градом", "thunder-hail"),
            _ => ("Ясно", isDay ? "sun" : "moon")
        };
    }
}
