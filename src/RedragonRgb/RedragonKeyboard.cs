using HidSharp;
using HidSharp.Reports;

namespace RedragonRgb;

/// <summary>
/// Direct HID RGB client for Redragon / Sinowealth keyboards with VID 258A / PID 0049
/// (Garen Pro, K616, KS82-B, and others sharing this ID).
/// <para>
/// Sends only the LED feature report — never firmware/flash commands —
/// so it cannot brick the board the way the disabled OpenRGB Sinowealth path could.
/// </para>
/// Protocol (Sinodragon / ks82rgb):
/// <code>
/// byte 0     : 0x08  report id
/// bytes 1..3 : 0x0A 0x7A 0x01
/// bytes 4..  : 126 × RGB triples (378 bytes)
/// total      : 382 bytes
/// </code>
/// </summary>
public sealed class RedragonKeyboard : IDisposable
{
    public const int VendorId = 0x258A;
    public const int ProductId = 0x0049;
    public const int UsagePage = 0xFF00;
    public const int Usage = 0x0001;
    public const int PacketLength = 382;
    public const int SlotCount = KeyboardLayout.SlotCount;

    private static readonly byte[] PacketHeader = [0x08, 0x0A, 0x7A, 0x01];

    private HidStream? _stream;
    private bool _disposed;

    public bool IsConnected => _stream is { CanWrite: true };

    public string? ProductName { get; private set; }
    public string? Manufacturer { get; private set; }
    public string? DevicePath { get; private set; }

    public KeyboardLayout Layout { get; set; } = KeyboardLayout.Default;

    /// <summary>Enumerate matching HID interfaces without opening the stream.</summary>
    public static IReadOnlyList<DeviceInfo> Enumerate()
    {
        var list = new List<DeviceInfo>();
        foreach (var device in DeviceList.Local.GetHidDevices(VendorId, ProductId))
        {
            var featureLen = TryGetFeatureLength(device);
            TryGetUsage(device, out var usagePage, out var usage);
            var product = TryGetString(() => device.GetProductName());
            var manufacturer = TryGetString(() => device.GetManufacturer());
            var serial = TryGetString(() => device.GetSerialNumber());

            list.Add(new DeviceInfo(
                device.DevicePath,
                manufacturer,
                product,
                serial,
                usagePage,
                usage,
                featureLen,
                IsLedInterface(usagePage, usage, featureLen)));
        }

        return list;
    }

    /// <summary>
    /// Open the vendor LED interface. Prefers Usage Page 0xFF00 / Usage 0x0001,
    /// then a feature-report length of 382, then any matching VID/PID path.
    /// </summary>
    public bool Connect()
    {
        ThrowIfDisposed();
        Disconnect();

        var devices = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
        if (devices.Count == 0)
            return false;

        // This PID exposes several 0xFF00 collections (6 / 382 / 1032 byte features).
        // Prefer the 382-byte LED report used by Sinodragon / ks82rgb.
        HidDevice? byFeatureLen = null;
        HidDevice? byUsage = null;
        HidDevice? fallback = null;

        foreach (var device in devices)
        {
            fallback ??= device;
            var featureLen = TryGetFeatureLength(device);
            if (featureLen == PacketLength)
                byFeatureLen ??= device;

            if (byUsage is null &&
                TryGetUsage(device, out var page, out var usage) &&
                page == UsagePage && usage == Usage)
            {
                byUsage = device;
            }
        }

        var target = byFeatureLen ?? byUsage ?? fallback;
        if (target is null)
            return false;

        if (!target.TryOpen(out var stream))
            return false;

        _stream = stream;
        _stream.WriteTimeout = 1000;
        DevicePath = target.DevicePath;
        Manufacturer = TryGetString(() => target.GetManufacturer());
        ProductName = TryGetString(() => target.GetProductName());
        return true;
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        DevicePath = null;
        ProductName = null;
        Manufacturer = null;
    }

    /// <summary>Paint every LED slot the same color.</summary>
    public bool SetAll(RgbColor color, float intensity = 1f)
    {
        var scaled = color.Scale(intensity);
        var frame = new RgbColor[SlotCount];
        Array.Fill(frame, scaled);
        return SendFrame(frame);
    }

    /// <summary>Set colors by key name using the current <see cref="Layout"/>.</summary>
    public bool SetKeys(IReadOnlyDictionary<string, RgbColor> keyColors, float intensity = 1f) =>
        SendFrame(Layout.BuildFrame(keyColors, intensity));

    /// <summary>Set a single key by name; other keys stay black.</summary>
    public bool SetKey(string key, RgbColor color, float intensity = 1f) =>
        SetKeys(new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase) { [key] = color }, intensity);

    /// <summary>
    /// Send a full 126-slot frame. Index = LED slot (0..125), value = RGB.
    /// Short arrays are padded with black.
    /// </summary>
    public bool SendFrame(IReadOnlyList<RgbColor> slots)
    {
        ThrowIfDisposed();
        if (!EnsureConnected())
            return false;

        var packet = BuildPacket(slots);
        try
        {
            _stream!.SetFeature(packet);
            return true;
        }
        catch (IOException)
        {
            Disconnect();
            return false;
        }
        catch (ObjectDisposedException)
        {
            Disconnect();
            return false;
        }
    }

    public static byte[] BuildPacket(IReadOnlyList<RgbColor> slots)
    {
        var packet = new byte[PacketLength];
        PacketHeader.CopyTo(packet, 0);

        var offset = PacketHeader.Length;
        var count = Math.Min(slots.Count, SlotCount);
        for (var i = 0; i < count; i++)
        {
            var c = slots[i];
            packet[offset++] = c.R;
            packet[offset++] = c.G;
            packet[offset++] = c.B;
        }

        return packet;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private bool EnsureConnected() => IsConnected || Connect();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static bool IsLedInterface(int usagePage, int usage, int featureLen) =>
        featureLen == PacketLength && usagePage == UsagePage;

    private static int TryGetFeatureLength(HidDevice device)
    {
        try { return device.GetMaxFeatureReportLength(); }
        catch { return 0; }
    }

    private static bool TryGetUsage(HidDevice device, out int usagePage, out int usage)
    {
        usagePage = 0;
        usage = 0;
        try
        {
            ReportDescriptor? report = device.GetReportDescriptor();
            if (report is null)
                return false;

            foreach (var item in report.DeviceItems)
            {
                foreach (var raw in item.Usages.GetAllValues())
                {
                    usagePage = (int)((raw >> 16) & 0xFFFF);
                    usage = (int)(raw & 0xFFFF);
                    if (usagePage == UsagePage && usage == Usage)
                        return true;
                }
            }

            return usagePage != 0 || usage != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryGetString(Func<string> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    public readonly record struct DeviceInfo(
        string Path,
        string? Manufacturer,
        string? Product,
        string? Serial,
        int UsagePage,
        int Usage,
        int FeatureReportLength,
        bool LikelyLedControl);
}
