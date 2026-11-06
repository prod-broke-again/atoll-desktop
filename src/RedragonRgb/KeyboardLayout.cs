namespace RedragonRgb;

/// <summary>
/// Slot map for Sinowealth 258A:0049 keyboards (126 LED slots).
/// Default layout matches Sinodragon / TKL-style boards; remap via
/// <see cref="FromJson"/> or <see cref="WithKey"/> if your board differs.
/// </summary>
public sealed class KeyboardLayout
{
    public const int SlotCount = 126;

    /// <summary>
    /// Column-major 16×6 grid used by Sinodragon (TKL / 75%-class boards).
    /// "NAN" means an unused physical slot that must still occupy 3 bytes.
    /// </summary>
    private static readonly string[][] DefaultGrid =
    [
        ["Esc", "`", "Tab", "Caps", "Shift", "Ctrl"],
        ["F1", "1", "Q", "A", "Z", "Win"],
        ["F2", "2", "W", "S", "X", "Alt"],
        ["F3", "3", "E", "D", "C", "NAN"],
        ["F4", "4", "R", "F", "V", "NAN"],
        ["F5", "5", "T", "G", "B", "Space"],
        ["F6", "6", "Y", "H", "N", "NAN"],
        ["F7", "7", "U", "J", "M", "NAN"],
        ["F8", "8", "I", "K", ",", "AltR"],
        ["F9", "9", "O", "L", ".", "Fn"],
        ["F10", "0", "P", ";", "/", "CtrlR"],
        ["F11", "-", "[", "'", "NAN", "NAN"],
        ["F12", "=", "]", "NAN", "NAN", "NAN"],
        ["PrtSc", "Bksp", "\\", "Enter", "ShiftR", "Left"],
        ["Pause", "NAN", "NAN", "NAN", "Up", "Down"],
        ["Del", "Home", "End", "PgUp", "PgDn", "Right"],
    ];

    private readonly Dictionary<string, int> _keyToSlot;
    private readonly string?[] _slotToKey;

    public KeyboardLayout(IReadOnlyDictionary<string, int> keyToSlot)
    {
        _keyToSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        _slotToKey = new string?[SlotCount];

        foreach (var (key, slot) in keyToSlot)
        {
            if (slot is < 0 or >= SlotCount)
                throw new ArgumentOutOfRangeException(nameof(keyToSlot), $"Slot {slot} for '{key}' is out of range.");
            if (string.Equals(key, "NAN", StringComparison.OrdinalIgnoreCase))
                continue;

            _keyToSlot[key] = slot;
            _slotToKey[slot] = key;
        }
    }

    public static KeyboardLayout Default { get; } = CreateDefault();

    public IReadOnlyDictionary<string, int> Keys => _keyToSlot;

    public bool TryGetSlot(string key, out int slot) => _keyToSlot.TryGetValue(key, out slot);

    public string? GetKey(int slot) =>
        slot is >= 0 and < SlotCount ? _slotToKey[slot] : null;

    public KeyboardLayout WithKey(string key, int slot)
    {
        var copy = new Dictionary<string, int>(_keyToSlot, StringComparer.OrdinalIgnoreCase)
        {
            [key] = slot
        };
        return new KeyboardLayout(copy);
    }

    /// <summary>
    /// Build a linear RGB buffer for all 126 slots from a key→color map.
    /// Unknown keys are ignored; unset slots stay black.
    /// </summary>
    public RgbColor[] BuildFrame(IReadOnlyDictionary<string, RgbColor> keyColors, float intensity = 1f)
    {
        var frame = new RgbColor[SlotCount];
        foreach (var (key, color) in keyColors)
        {
            if (_keyToSlot.TryGetValue(key, out var slot))
                frame[slot] = color.Scale(intensity);
        }
        return frame;
    }

    private static KeyboardLayout CreateDefault()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var slot = 0;
        foreach (var column in DefaultGrid)
        {
            foreach (var key in column)
            {
                if (!string.Equals(key, "NAN", StringComparison.OrdinalIgnoreCase) &&
                    !map.ContainsKey(key))
                {
                    map[key] = slot;
                }

                slot++;
            }
        }

        // Default grid covers the first 96 slots (16×6). Remaining slots up to 126
        // are unused padding on TKL boards / numpad region on full-size boards.
        if (slot > SlotCount)
            throw new InvalidOperationException($"Default grid produced {slot} slots, max is {SlotCount}.");

        return new KeyboardLayout(map);
    }
}
