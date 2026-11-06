using RedragonRgb;

static void PrintHelp()
{
    Console.WriteLine("""
        RedragonRgb.Test — HID RGB smoke test for VID 258A / PID 0049

        Usage:
          RedragonRgb.Test                 interactive demo (enumerate + color cycle)
          RedragonRgb.Test enum            list HID interfaces
          RedragonRgb.Test all <hex>       set all keys (e.g. FF0000)
          RedragonRgb.Test key <name> <hex> light one key, rest black
          RedragonRgb.Test keys <n=hex>...  several keys, e.g. W=00FFFF Enter=00FF00
          RedragonRgb.Test wasd            cyan WASD demo
          RedragonRgb.Test pulse           brightness pulse on current accent
          RedragonRgb.Test off             all black
          RedragonRgb.Test packet          print first 16 bytes of a built packet
        """);
}

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "demo";

try
{
    switch (cmd)
    {
        case "help":
        case "-h":
        case "--help":
            PrintHelp();
            return 0;

        case "enum":
            return CmdEnum();

        case "all":
            return CmdAll(args);

        case "key":
            return CmdKey(args);

        case "keys":
            return CmdKeys(args);

        case "wasd":
            return CmdWasd();

        case "pulse":
            return CmdPulse();

        case "off":
            return CmdOff();

        case "packet":
            return CmdPacket();

        case "demo":
            return CmdDemo();

        default:
            Console.Error.WriteLine($"Unknown command: {cmd}");
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}

static int CmdEnum()
{
    var devices = RedragonKeyboard.Enumerate();
    if (devices.Count == 0)
    {
        Console.WriteLine("No devices with VID 258A / PID 0049 found.");
        Console.WriteLine("Plug the keyboard in wired (or 2.4G dongle) mode and retry.");
        return 1;
    }

    Console.WriteLine($"Found {devices.Count} HID interface(s):\n");
    foreach (var d in devices)
    {
        Console.WriteLine($"  Product   : {d.Product ?? "?"}");
        Console.WriteLine($"  Maker     : {d.Manufacturer ?? "?"}");
        Console.WriteLine($"  Usage     : page=0x{d.UsagePage:X4} usage=0x{d.Usage:X4}");
        Console.WriteLine($"  FeatureLen: {d.FeatureReportLength}");
        Console.WriteLine($"  LED?      : {(d.LikelyLedControl ? "yes" : "no")}");
        Console.WriteLine($"  Path      : {d.Path}");
        Console.WriteLine();
    }

    return 0;
}

static int CmdAll(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: all <RRGGBB>");
        return 1;
    }

    var color = RgbColor.FromHex(args[1]);
    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    Console.WriteLine($"SetAll {color}");
    return kb.SetAll(color) ? 0 : FailSend();
}

static int CmdKey(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: key <name> <RRGGBB>");
        return 1;
    }

    var key = args[1];
    var color = RgbColor.FromHex(args[2]);
    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    if (!kb.Layout.TryGetSlot(key, out var slot))
    {
        Console.Error.WriteLine($"Unknown key '{key}'. Known examples: Esc, W, A, S, D, Enter, Space");
        return 1;
    }

    Console.WriteLine($"SetKey {key} (slot {slot}) -> {color}");
    return kb.SetKey(key, color) ? 0 : FailSend();
}

static int CmdKeys(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: keys W=00FFFF A=00FFFF Enter=00FF00");
        return 1;
    }

    var map = new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase);
    foreach (var part in args.Skip(1))
    {
        var eq = part.IndexOf('=');
        if (eq <= 0)
        {
            Console.Error.WriteLine($"Bad pair '{part}', expected Name=RRGGBB");
            return 1;
        }

        var key = part[..eq];
        var color = RgbColor.FromHex(part[(eq + 1)..]);
        map[key] = color;
    }

    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    foreach (var key in map.Keys)
    {
        if (!kb.Layout.TryGetSlot(key, out _))
            Console.WriteLine($"Warning: unknown key '{key}' (ignored by layout)");
    }

    Console.WriteLine("SetKeys " + string.Join(", ", map.Select(kv => $"{kv.Key}={kv.Value}")));
    return kb.SetKeys(map) ? 0 : FailSend();
}

static int CmdWasd()
{
    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    var map = new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["Esc"] = RgbColor.Red,
        ["W"] = RgbColor.Cyan,
        ["A"] = RgbColor.Cyan,
        ["S"] = RgbColor.Cyan,
        ["D"] = RgbColor.Cyan,
        ["Enter"] = RgbColor.Green,
        ["Space"] = RgbColor.Magenta,
    };

    Console.WriteLine("Esc=red, WASD=cyan, Enter=green, Space=magenta (rest black)");
    return kb.SetKeys(map) ? 0 : FailSend();
}

static int CmdPulse()
{
    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    Console.WriteLine("Pulsing cyan brightness 5 times (Ctrl+C to abort)...");
    for (var i = 0; i < 5; i++)
    {
        for (var step = 0; step <= 20; step++)
        {
            var t = step / 20f;
            if (!kb.SetAll(RgbColor.Cyan, t))
                return FailSend();
            Thread.Sleep(30);
        }

        for (var step = 20; step >= 0; step--)
        {
            var t = step / 20f;
            if (!kb.SetAll(RgbColor.Cyan, Math.Max(t, 0.05f)))
                return FailSend();
            Thread.Sleep(30);
        }
    }

    kb.SetAll(RgbColor.Cyan);
    Console.WriteLine("Done.");
    return 0;
}

static int CmdOff()
{
    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    Console.WriteLine("All off.");
    return kb.SetAll(RgbColor.Black) ? 0 : FailSend();
}

static int CmdPacket()
{
    var frame = new RgbColor[KeyboardLayout.SlotCount];
    Array.Fill(frame, RgbColor.Red);
    var packet = RedragonKeyboard.BuildPacket(frame);
    Console.WriteLine($"Packet length: {packet.Length}");
    Console.WriteLine("Header + first colors: " + BitConverter.ToString(packet, 0, 16));
    Console.WriteLine($"Expected header: 08-0A-7A-01, then RGB...");
    return packet.Length == RedragonKeyboard.PacketLength &&
           packet[0] == 0x08 && packet[1] == 0x0A && packet[2] == 0x7A && packet[3] == 0x01
        ? 0 : 1;
}

static int CmdDemo()
{
    Console.WriteLine("=== RedragonRgb demo ===\n");
    CmdEnum();

    using var kb = OpenOrFail(out var code);
    if (kb is null) return code;

    Console.WriteLine($"Connected: {kb.Manufacturer} / {kb.ProductName}");
    Console.WriteLine($"Path: {kb.DevicePath}\n");

    var colors = new (string Name, RgbColor Color)[]
    {
        ("Red", RgbColor.Red),
        ("Green", RgbColor.Green),
        ("Blue", RgbColor.Blue),
        ("Cyan", RgbColor.Cyan),
        ("Magenta", RgbColor.Magenta),
        ("Yellow", RgbColor.Yellow),
        ("White", RgbColor.White),
    };

    foreach (var (name, color) in colors)
    {
        Console.WriteLine($"→ {name} ({color})");
        if (!kb.SetAll(color))
            return FailSend();
        Thread.Sleep(700);
    }

    Console.WriteLine("→ WASD highlight (rest black)");
    var map = new Dictionary<string, RgbColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["W"] = RgbColor.Cyan,
        ["A"] = RgbColor.Cyan,
        ["S"] = RgbColor.Cyan,
        ["D"] = RgbColor.Cyan,
    };
    if (!kb.SetKeys(map))
        return FailSend();
    Thread.Sleep(1200);

    Console.WriteLine("→ Restore soft white");
    kb.SetAll(RgbColor.White, 0.35f);
    Console.WriteLine("\nDemo finished. If colors changed on the board — protocol works.");
    return 0;
}

static RedragonKeyboard? OpenOrFail(out int exitCode)
{
    var kb = new RedragonKeyboard();
    if (!kb.Connect())
    {
        Console.Error.WriteLine("Failed to open VID 258A / PID 0049.");
        Console.Error.WriteLine("Check cable/dongle (not Bluetooth) and that vendor software is closed.");
        kb.Dispose();
        exitCode = 1;
        return null;
    }

    exitCode = 0;
    return kb;
}

static int FailSend()
{
    Console.Error.WriteLine("Failed to send feature report (device busy or wrong interface).");
    return 1;
}
