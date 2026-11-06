using System;
using System.Text.Json;

namespace LiveWallpaper.Core.Server;

public static class ServerMetricsParser
{
    public const string Prefix = "LW|";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryParse(string? line, out RawServerMetrics? metrics)
    {
        metrics = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (!span.StartsWith(Prefix.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> jsonSpan = span[Prefix.Length..].Trim();
        if (jsonSpan.IsEmpty || jsonSpan[0] != '{')
        {
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RawServerMetrics>(jsonSpan, Options);
            if (parsed != null && parsed.T > 0 && parsed.Cpu.Length >= 4)
            {
                metrics = parsed;
                return true;
            }

            metrics = null;
            return false;
        }
        catch
        {
            metrics = null;
            return false;
        }
    }
}
