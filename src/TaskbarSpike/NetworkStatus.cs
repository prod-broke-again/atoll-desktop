using System.Collections;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TaskbarSpike;

internal sealed record NetworkInfo(string Kind, string Name, string Status, bool Connected)
{
    public static NetworkInfo Offline { get; } = new("none", "Нет подключения", "Не подключено", false);
}

/// <summary>
/// The Windows taskbar network icon follows the internet profile, not every Up adapter.
/// Hyper-V and tunnel interfaces stay up on this machine and must not replace Ethernet.
/// </summary>
internal static class NetworkStatus
{
    private static string _logged = "";
    private static bool _profileFaultLogged;

    public static NetworkInfo Read()
    {
        var info = ReadCore();
        var line = info.Connected ? info.Kind + " " + info.Name : "offline";
        if (line != _logged)
        {
            _logged = line;
            Console.WriteLine("[spike] network: " + line);
        }

        return info;
    }

    public static string Describe()
    {
        var info = ReadCore();
        return $"network kind={info.Kind} name={info.Name} status={info.Status} connected={info.Connected}";
    }

    private static NetworkInfo ReadCore()
    {
        try
        {
            var fromProfile = ReadProfile();
            if (fromProfile != null)
            {
                return fromProfile;
            }
        }
        catch (Exception ex)
        {
            if (!_profileFaultLogged)
            {
                _profileFaultLogged = true;
                Console.WriteLine("[spike] network profile: " + ex.Message);
            }
        }

        var nic = PhysicalUp().FirstOrDefault();
        if (nic == null)
        {
            return NetworkInfo.Offline;
        }

        return new NetworkInfo(KindOf(nic), nic.Name, "Подключено", true);
    }

    private static NetworkInfo? ReadProfile()
    {
        var nic = PhysicalUp().FirstOrDefault();
        if (nic == null)
        {
            return null;
        }

        var name = ProfileName() ?? nic.Name;
        return new NetworkInfo(KindOf(nic), name, "Подключено", true);
    }

    private static string? ProfileName()
    {
        var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
        if (type == null)
        {
            return null;
        }

        var manager = Activator.CreateInstance(type);
        if (manager == null)
        {
            return null;
        }

        object? networks = null;
        object? raw = null;
        try
        {
            networks = type.InvokeMember("GetNetworks", BindingFlags.InvokeMethod, null, manager, new object[] { 1 });
            if (networks == null)
            {
                return null;
            }

            raw = networks.GetType().InvokeMember("_NewEnum", BindingFlags.GetProperty, null, networks, null);
            var virtualNames = NetworkInterface.GetAllNetworkInterfaces()
                .Where(IsVirtual)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? chosen = null;
            foreach (var item in AsList(raw))
            {
                var name = item.GetType().InvokeMember("GetName", BindingFlags.InvokeMethod, null, item, null) as string;
                Release(item);
                if (string.IsNullOrWhiteSpace(name) || virtualNames.Contains(name))
                {
                    continue;
                }

                chosen ??= name;
            }

            return chosen;
        }
        finally
        {
            Release(raw);
            Release(networks);
            Release(manager);
        }
    }

    private static List<object> AsList(object? raw)
    {
        var items = new List<object>();
        if (raw is IEnumerator enumerator)
        {
            while (enumerator.MoveNext())
            {
                if (enumerator.Current != null)
                {
                    items.Add(enumerator.Current);
                }
            }

            return items;
        }

        if (raw is IEnumerable enumerable and not string)
        {
            foreach (var item in enumerable)
            {
                if (item != null)
                {
                    items.Add(item);
                }
            }

            return items;
        }

        if (raw != null)
        {
            items.Add(raw);
        }

        return items;
    }

    private static IEnumerable<NetworkInterface> PhysicalUp()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && !IsVirtual(nic) && HasGateway(nic))
            .OrderByDescending(Rank);
    }

    private static bool HasGateway(NetworkInterface nic)
    {
        try
        {
            return nic.GetIPProperties().GatewayAddresses.Any(address => address.Address != null);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsVirtual(NetworkInterface nic)
    {
        if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
        {
            return true;
        }

        var text = nic.Name + " " + nic.Description;
        return text.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
            || text.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }

    private static int Rank(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => 3,
            NetworkInterfaceType.Ethernet => 2,
            _ => 1,
        };
    }

    private static string KindOf(NetworkInterface nic)
    {
        return nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "wifi" : "ethernet";
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }
}
