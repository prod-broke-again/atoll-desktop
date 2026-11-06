using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveWallpaper.Core.Collage;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.SetupPalette;

public sealed class OpenRgbClient
{
    private const uint Magic = 0x4247524F; // "ORGB" little-endian
    private const uint CmdRequestControllerCount = 0;
    private const uint CmdRequestControllerData = 1;
    private const uint CmdUpdateLeds = 1050;
    private const uint ProtocolVersion = 4;

    private readonly ILogger? _logger;

    public OpenRgbClient(ILogger? logger = null)
    {
        _logger = logger;
    }

    public async Task<bool> ApplyAccentAsync(RgbColor accent, string host = "127.0.0.1", int port = 6742, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(600));

            await client.ConnectAsync(host, port, cts.Token);
            var stream = client.GetStream();

            // 1. Handshake: set client name
            var clientNameBytes = Encoding.ASCII.GetBytes("LiveWallpaper\0");
            await SendPacketAsync(stream, deviceId: 0, command: CmdRequestControllerCount, clientNameBytes, cts.Token);

            // Read controller count
            var header = await ReadHeaderAsync(stream, cts.Token);
            if (header == null) return false;

            var body = await ReadExactAsync(stream, (int)header.Value.DataLength, cts.Token);
            if (body.Length < 4) return false;

            uint count = BitConverter.ToUInt32(body, 0);
            if (count == 0)
            {
                _logger?.LogDebug("OpenRGB connected: 0 devices reported.");
                return true;
            }

            _logger?.LogInformation("OpenRGB connected: updating {Count} devices (Redragon/Razer/etc) to accent #{R:X2}{G:X2}{B:X2}.",
                count, accent.R, accent.G, accent.B);

            // 2. Update LEDs on all reported controllers
            for (uint i = 0; i < count; i++)
            {
                // Request controller data to get LED count
                var verPayload = BitConverter.GetBytes(ProtocolVersion);
                await SendPacketAsync(stream, deviceId: i, command: CmdRequestControllerData, verPayload, cts.Token);

                var ctrlHeader = await ReadHeaderAsync(stream, cts.Token);
                if (ctrlHeader == null) continue;

                var ctrlData = await ReadExactAsync(stream, (int)ctrlHeader.Value.DataLength, cts.Token);
                var ledCount = ParseLedCount(ctrlData);
                if (ledCount <= 0) ledCount = 64; // Safe fallback

                await SendUpdateLedsAsync(stream, deviceId: i, ledCount, accent, cts.Token);
            }

            return true;
        }
        catch (SocketException)
        {
            _logger?.LogDebug("OpenRGB server not reachable at {Host}:{Port}.", host, port);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug("OpenRGB connection timed out.");
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "OpenRGB update encountered an error.");
            return false;
        }
    }

    private static async Task SendPacketAsync(NetworkStream stream, uint deviceId, uint command, byte[] data, CancellationToken ct)
    {
        var packet = new byte[16 + data.Length];
        BitConverter.GetBytes(Magic).CopyTo(packet, 0);
        BitConverter.GetBytes(deviceId).CopyTo(packet, 4);
        BitConverter.GetBytes(command).CopyTo(packet, 8);
        BitConverter.GetBytes((uint)data.Length).CopyTo(packet, 12);
        data.CopyTo(packet, 16);

        await stream.WriteAsync(packet, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<(uint DeviceId, uint Command, uint DataLength)?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = await ReadExactAsync(stream, 16, ct);
        if (buf.Length < 16) return null;

        uint magic = BitConverter.ToUInt32(buf, 0);
        if (magic != Magic) return null;

        uint deviceId = BitConverter.ToUInt32(buf, 4);
        uint command = BitConverter.ToUInt32(buf, 8);
        uint length = BitConverter.ToUInt32(buf, 12);

        return (deviceId, command, length);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (chunk == 0) break;
            read += chunk;
        }
        return read == count ? buffer : buffer.AsSpan(0, read).ToArray();
    }

    private static int ParseLedCount(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            // Skip data_size (4) and type (4)
            if (ms.Length < 8) return 0;
            ms.Seek(8, SeekOrigin.Begin);

            // Read 7 null-terminated string buffers (name, vendor, description, version, serial, location, ...)
            for (int i = 0; i < 6; i++)
            {
                if (ms.Position + 2 > ms.Length) return 0;
                ushort len = reader.ReadUInt16();
                ms.Seek(len, SeekOrigin.Current);
            }

            // Modes count
            if (ms.Position + 2 > ms.Length) return 0;
            ushort modesCount = reader.ReadUInt16();
            for (int m = 0; m < modesCount; m++)
            {
                // Mode struct: name (len + str) + value(4) + flags(4) + speed_min(4) + speed_max(4) + colors_min(4) + colors_max(4) + speed(4) + direction(4) + color_mode(4) + num_colors(2)
                ushort nameLen = reader.ReadUInt16();
                ms.Seek(nameLen, SeekOrigin.Current);
                ms.Seek(36, SeekOrigin.Current); // fields up to num_colors
                ushort numColors = reader.ReadUInt16();
                ms.Seek(numColors * 4, SeekOrigin.Current);
            }

            // Zones count
            ushort zonesCount = reader.ReadUInt16();
            int totalLeds = 0;
            for (int z = 0; z < zonesCount; z++)
            {
                ushort zoneNameLen = reader.ReadUInt16();
                ms.Seek(zoneNameLen, SeekOrigin.Current);
                ms.Seek(4, SeekOrigin.Current); // type
                uint ledsMin = reader.ReadUInt32();
                uint ledsMax = reader.ReadUInt32();
                uint ledsCount = reader.ReadUInt32();
                totalLeds += (int)ledsCount;

                ushort matLen = reader.ReadUInt16();
                ms.Seek(matLen, SeekOrigin.Current);
            }

            if (totalLeds > 0) return totalLeds;

            // LEDs count
            if (ms.Position + 2 <= ms.Length)
            {
                return reader.ReadUInt16();
            }
        }
        catch
        {
            // Fallback
        }
        return 0;
    }

    private static async Task SendUpdateLedsAsync(NetworkStream stream, uint deviceId, int ledCount, RgbColor color, CancellationToken ct)
    {
        ledCount = Math.Clamp(ledCount, 1, 512);
        int payloadSize = 4 + 2 + ledCount * 4;
        var data = new byte[payloadSize];

        BitConverter.GetBytes((uint)payloadSize).CopyTo(data, 0);
        BitConverter.GetBytes((ushort)ledCount).CopyTo(data, 4);

        int offset = 6;
        for (int j = 0; j < ledCount; j++)
        {
            data[offset] = color.R;
            data[offset + 1] = color.G;
            data[offset + 2] = color.B;
            data[offset + 3] = 0;
            offset += 4;
        }

        await SendPacketAsync(stream, deviceId, CmdUpdateLeds, data, ct);
    }
}
