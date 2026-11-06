using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveWallpaper.Core.Collage;

public sealed class CollageSelector
{
    private readonly Queue<string> _recentPaths = new();
    private readonly int _maxHistory;
    private readonly Random _random;

    public CollageSelector(int maxHistory = 25, Random? random = null)
    {
        _maxHistory = Math.Max(5, maxHistory);
        _random = random ?? new Random();
    }

    public IReadOnlyCollection<string> RecentPaths => _recentPaths;

    public CollageSelectionResult? SelectHarmoniousCollage(
        IReadOnlyList<PinItem> allPins,
        int count = 9,
        bool enableHarmony = true,
        string? targetCategory = null,
        string? targetHue = null)
    {
        if (allPins == null || allPins.Count == 0) return null;

        // 1. Filter out hidden pins
        var visiblePins = allPins.Where(p => !p.IsHidden).ToList();
        if (visiblePins.Count == 0) visiblePins = allPins.ToList();

        // 2. Filter by category if specified and not "All"
        List<PinItem> pool = visiblePins;
        if (!string.IsNullOrWhiteSpace(targetCategory) &&
            !string.Equals(targetCategory, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetCategory, "Все", StringComparison.OrdinalIgnoreCase))
        {
            var byCat = visiblePins.Where(p => string.Equals(p.Category, targetCategory, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCat.Count > 0)
            {
                pool = byCat;
            }
        }

        count = Math.Clamp(count, 1, pool.Count);

        // Filter out pins from recent history if possible
        var recentSet = new HashSet<string>(_recentPaths, StringComparer.OrdinalIgnoreCase);
        var availablePins = pool.Where(p => !recentSet.Contains(p.FilePath)).ToList();

        // If filtering leaves too few pins, use the whole category pool
        if (availablePins.Count < count)
        {
            availablePins = pool.ToList();
        }

        List<PinItem> selected;
        HueGroup primaryHue;

        // Parse explicit hue target if not "Auto"
        HueGroup? explicitHue = null;
        if (!string.IsNullOrWhiteSpace(targetHue) &&
            !string.Equals(targetHue, "Auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(targetHue, "Авто", StringComparison.OrdinalIgnoreCase))
        {
            if (Enum.TryParse<HueGroup>(targetHue, true, out var parsed))
            {
                explicitHue = parsed;
            }
        }

        if (explicitHue.HasValue)
        {
            primaryHue = explicitHue.Value;
            var allowedGroups = new HashSet<HueGroup>(HueClassifier.GetHarmoniousNeighbors(primaryHue));
            var candidatePool = availablePins.Where(p => allowedGroups.Contains(p.HueGroup)).ToList();

            if (candidatePool.Count < count)
            {
                var remaining = availablePins.Except(candidatePool).OrderBy(_ => _random.Next());
                candidatePool.AddRange(remaining.Take(count - candidatePool.Count));
            }

            selected = candidatePool.OrderBy(_ => _random.Next()).Take(count).ToList();
            selected = selected.OrderBy(p => p.Hue).ThenBy(p => p.Brightness).ToList();
        }
        else if (enableHarmony)
        {
            // Pick a random primary hue group from available pins
            var groups = availablePins
                .GroupBy(p => p.HueGroup)
                .OrderByDescending(g => g.Count())
                .ToList();

            // Choose among the top populated groups
            var topGroups = groups.Take(Math.Min(4, groups.Count)).ToList();
            primaryHue = topGroups[_random.Next(topGroups.Count)].Key;

            var allowedGroups = new HashSet<HueGroup>(HueClassifier.GetHarmoniousNeighbors(primaryHue));
            var candidatePool = availablePins.Where(p => allowedGroups.Contains(p.HueGroup)).ToList();

            if (candidatePool.Count < count)
            {
                // Fallback: fill remaining from all available
                var remaining = availablePins.Except(candidatePool).OrderBy(_ => _random.Next());
                candidatePool.AddRange(remaining.Take(count - candidatePool.Count));
            }

            // Shuffle and take required count
            selected = candidatePool.OrderBy(_ => _random.Next()).Take(count).ToList();

            // Sort by Hue and Brightness for gradient aesthetic
            selected = selected.OrderBy(p => p.Hue).ThenBy(p => p.Brightness).ToList();
        }
        else
        {
            primaryHue = HueGroup.NeutralDark;
            selected = availablePins.OrderBy(_ => _random.Next()).Take(count).ToList();
        }

        // Update history ring buffer
        foreach (var pin in selected)
        {
            _recentPaths.Enqueue(pin.FilePath);
            while (_recentPaths.Count > _maxHistory)
            {
                _recentPaths.Dequeue();
            }
        }

        // Calculate dark average backdrop color
        long sumR = 0, sumG = 0, sumB = 0;
        foreach (var pin in selected)
        {
            sumR += pin.DominantColor.R;
            sumG += pin.DominantColor.G;
            sumB += pin.DominantColor.B;
        }

        byte avgR = (byte)Math.Clamp((sumR / selected.Count) / 3, 10, 40);
        byte avgG = (byte)Math.Clamp((sumG / selected.Count) / 3, 12, 45);
        byte avgB = (byte)Math.Clamp((sumB / selected.Count) / 3, 15, 55);

        return new CollageSelectionResult
        {
            SelectedPins = selected,
            PrimaryHue = primaryHue,
            AverageBackdropColor = new RgbColor(avgR, avgG, avgB)
        };
    }
}
