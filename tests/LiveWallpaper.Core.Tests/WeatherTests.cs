using System;
using System.Text.Json;
using FluentAssertions;
using LiveWallpaper.Services.Weather;
using Xunit;

namespace LiveWallpaper.Core.Tests;

public class WeatherTests
{
    [Theory]
    [InlineData(0, true, "Ясно", "sun")]
    [InlineData(0, false, "Ясно", "moon")]
    [InlineData(3, true, "Пасмурно", "cloud")]
    [InlineData(61, true, "Дождь", "rain")]
    [InlineData(71, true, "Снегопад", "snow")]
    [InlineData(95, true, "Гроза", "thunder")]
    public void WmoCodeHelper_DescribesCodesCorrectly(int code, bool isDay, string expectedDesc, string expectedIcon)
    {
        var (desc, icon) = WmoWeatherCodeHelper.Describe(code, isDay);
        desc.Should().Be(expectedDesc);
        icon.Should().Be(expectedIcon);
    }

    [Fact]
    public void WeatherData_SerializationRoundTrip_PreservesProperties()
    {
        var weather = new WeatherData
        {
            Enabled = true,
            City = "Москва",
            Temperature = 21.4,
            ApparentTemperature = 20.8,
            WeatherCode = 1,
            WeatherDescription = "В основном ясно",
            Icon = "sun-cloud",
            WindSpeed = 4.2,
            RelativeHumidity = 55,
            IsDay = true,
            Units = "metric",
            UpdatedAt = DateTime.UtcNow
        };

        var json = JsonSerializer.Serialize(weather);
        var deserialized = JsonSerializer.Deserialize<WeatherData>(json);

        deserialized.Should().NotBeNull();
        deserialized!.City.Should().Be("Москва");
        deserialized.Temperature.Should().Be(21.4);
        deserialized.ApparentTemperature.Should().Be(20.8);
        deserialized.WeatherDescription.Should().Be("В основном ясно");
        deserialized.Icon.Should().Be("sun-cloud");
        deserialized.RelativeHumidity.Should().Be(55);
    }
}
