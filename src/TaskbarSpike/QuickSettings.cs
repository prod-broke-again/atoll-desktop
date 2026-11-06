using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Devices.Radios;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace TaskbarSpike;

/// <summary>State of the quick tiles. A tile that cannot be switched reliably is not "available":
/// it opens the matching Settings page and is never drawn as on.</summary>
internal sealed record QuickState(
    bool Bluetooth,
    bool Hotspot,
    bool Night,
    bool Dnd,
    bool BluetoothAvailable,
    bool HotspotAvailable,
    bool NightAvailable,
    bool DndAvailable)
{
    public static QuickState Empty { get; } = new(false, false, false, false, false, false, false, false);
}

/// <summary>
/// Bluetooth (Windows.Devices.Radios), mobile hotspot (NetworkOperatorTetheringManager),
/// Night Light (CloudStore bluelightreductionstate blob), Do Not Disturb (WNF quiet-hours profile).
/// WinRT and WNF calls never run on the UI thread; the last result is cached for the snapshot.
/// </summary>
internal static class QuickSettings
{
    private const string NightStatePath =
        @"Software\Microsoft\Windows\CurrentVersion\CloudStore\Store\DefaultAccount\Current\" +
        @"default$windows.data.bluelightreduction.bluelightreductionstate\" +
        @"windows.data.bluelightreduction.bluelightreductionstate";

    /// <summary>WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED — 0 off, 1 priority, 2 alarms.</summary>
    private const ulong WnfQuietHoursActive = 0x0D83063EA3BF1C75UL;

    private static volatile QuickState _state = QuickState.Empty;
    private static int _refreshing;
    private static bool _faultLogged;

    public static QuickState State => _state;

    /// <summary>Raised on a thread-pool thread after the cache changed.</summary>
    public static event Action? Changed;

    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var next = await ReadAsync().ConfigureAwait(false);
                if (next != _state)
                {
                    _state = next;
                    Changed?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Log("refresh", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Cheap sync refresh of Night Light / DND for the dock bell without touching WinRT.</summary>
    public static void RefreshLocal()
    {
        try
        {
            var (nightAvailable, nightOn) = ReadNight();
            var (dndAvailable, dndOn) = ReadDnd();
            var current = _state;
            var next = current with
            {
                Night = nightOn,
                Dnd = dndOn,
                NightAvailable = nightAvailable,
                DndAvailable = dndAvailable,
            };
            if (next != current)
            {
                _state = next;
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log("local", ex);
        }
    }

    /// <summary>Switch a tile. Returns false when the caller must open Settings instead.</summary>
    public static bool Toggle(string key)
    {
        var state = _state;
        switch (key)
        {
            case "bluetooth" when state.BluetoothAvailable:
                _ = Task.Run(() => SwitchAsync(() => SetBluetoothAsync(!state.Bluetooth), "bluetooth"));
                return true;
            case "hotspot" when state.HotspotAvailable:
                _ = Task.Run(() => SwitchAsync(() => SetHotspotAsync(!state.Hotspot), "hotspot"));
                return true;
            case "night" when state.NightAvailable:
                _ = Task.Run(() => SwitchSync(() => SetNight(!state.Night), "night"));
                return true;
            case "dnd" when state.DndAvailable:
                _ = Task.Run(() => SwitchSync(() => SetDnd(!state.Dnd), "dnd"));
                return true;
            default:
                return false;
        }
    }

    public static string SettingsUri(string key) => key switch
    {
        "bluetooth" => "ms-settings:bluetooth",
        "night" => "ms-settings:nightlight",
        "dnd" => "ms-settings:quiethours",
        "hotspot" => "ms-settings:network-mobilehotspot",
        "a11y" => "ms-settings:easeofaccess",
        "cast" => "ms-settings:connecteddevices",
        "sound-settings" => "ms-settings:sound",
        "network-settings" => "ms-settings:network",
        "vpn" => "ms-settings:network-vpn",
        _ => "ms-settings:",
    };

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log("open " + uri, ex);
        }
    }

    private static async Task SwitchAsync(Func<Task<bool>> action, string key)
    {
        bool ok;
        try
        {
            ok = await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(key, ex);
            ok = false;
        }

        if (!ok)
        {
            OpenUri(SettingsUri(key));
        }

        Refresh();
    }

    private static void SwitchSync(Func<bool> action, string key)
    {
        bool ok;
        try
        {
            ok = action();
        }
        catch (Exception ex)
        {
            Log(key, ex);
            ok = false;
        }

        if (!ok)
        {
            OpenUri(SettingsUri(key));
        }

        Refresh();
    }

    private static async Task<QuickState> ReadAsync()
    {
        var (btAvailable, btOn) = await ReadBluetoothAsync().ConfigureAwait(false);
        var (hsAvailable, hsOn) = ReadHotspot();
        var (nightAvailable, nightOn) = ReadNight();
        var (dndAvailable, dndOn) = ReadDnd();
        return new QuickState(btOn, hsOn, nightOn, dndOn, btAvailable, hsAvailable, nightAvailable, dndAvailable);
    }

    private static async Task<(bool Available, bool On)> ReadBluetoothAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
            {
                return (false, false);
            }

            var radio = await FindBluetoothAsync();
            return radio == null ? (false, false) : (true, radio.State == RadioState.On);
        }
        catch (Exception ex)
        {
            Log("bluetooth read", ex);
            return (false, false);
        }
    }

    private static async Task<Radio?> FindBluetoothAsync()
    {
        var radios = await Radio.GetRadiosAsync();
        return radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
    }

    private static async Task<bool> SetBluetoothAsync(bool on)
    {
        var radio = await FindBluetoothAsync();
        if (radio == null)
        {
            return false;
        }

        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed;
    }

    private static NetworkOperatorTetheringManager? HotspotManager()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        if (profile == null)
        {
            return null;
        }

        if (NetworkOperatorTetheringManager.GetTetheringCapabilityFromConnectionProfile(profile) != TetheringCapability.Enabled)
        {
            return null;
        }

        return NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
    }

    private static (bool Available, bool On) ReadHotspot()
    {
        try
        {
            var manager = HotspotManager();
            return manager == null
                ? (false, false)
                : (true, manager.TetheringOperationalState == TetheringOperationalState.On);
        }
        catch (Exception ex)
        {
            Log("hotspot read", ex);
            return (false, false);
        }
    }

    private static async Task<bool> SetHotspotAsync(bool on)
    {
        var manager = HotspotManager();
        if (manager == null)
        {
            return false;
        }

        var result = on
            ? await manager.StartTetheringAsync()
            : await manager.StopTetheringAsync();
        return result.Status == TetheringOperationStatus.Success;
    }

    private static (bool Available, bool On) ReadNight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(NightStatePath);
            if (key?.GetValue("Data") is not byte[] data || data.Length <= 18)
            {
                return (false, false);
            }

            return (true, data[18] == 0x15);
        }
        catch (Exception ex)
        {
            Log("night read", ex);
            return (false, false);
        }
    }

    private static bool SetNight(bool on)
    {
        using var key = Registry.CurrentUser.OpenSubKey(NightStatePath, writable: true);
        if (key?.GetValue("Data") is not byte[] raw || raw.Length <= 18)
        {
            return false;
        }

        var data = new byte[raw.Length];
        Buffer.BlockCopy(raw, 0, data, 0, raw.Length);
        var currentlyOn = data[18] == 0x15;
        if (currentlyOn == on)
        {
            return true;
        }

        // Undocumented CloudStore blob: size at [18] is 0x15 when on (with 10 00 inserted at [23]),
        // 0x13 when off. Bump the VLQ timestamp at [10..14] so Windows accepts the write.
        var list = new List<byte>(data);
        if (currentlyOn)
        {
            list[18] = 0x13;
            if (list.Count > 24)
            {
                list.RemoveRange(23, 2);
            }
        }
        else
        {
            list[18] = 0x15;
            list.InsertRange(23, new byte[] { 0x10, 0x00 });
        }

        for (var i = 10; i < 15 && i < list.Count; i++)
        {
            if (list[i] != 0xFF)
            {
                list[i]++;
                break;
            }
        }

        key.SetValue("Data", list.ToArray(), RegistryValueKind.Binary);
        return true;
    }

    private static (bool Available, bool On) ReadDnd()
    {
        try
        {
            var buffer = new byte[4];
            var size = buffer.Length;
            var stateName = WnfQuietHoursActive;
            var status = NtQueryWnfStateData(
                ref stateName,
                IntPtr.Zero,
                IntPtr.Zero,
                out _,
                buffer,
                ref size);
            if (status != 0 || size < 4)
            {
                return (false, false);
            }

            var mode = BitConverter.ToUInt32(buffer, 0);
            return (true, mode != 0);
        }
        catch (Exception ex)
        {
            Log("dnd read", ex);
            return (false, false);
        }
    }

    private static bool SetDnd(bool on)
    {
        var buffer = BitConverter.GetBytes(on ? 1u : 0u); // 1 = Priority only (Do Not Disturb)
        var stateName = WnfQuietHoursActive;
        var status = NtUpdateWnfStateData(
            ref stateName,
            buffer,
            buffer.Length,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            0);
        return status == 0;
    }

    private static void Log(string what, Exception ex)
    {
        if (_faultLogged)
        {
            return;
        }

        _faultLogged = true;
        Console.WriteLine($"[spike] quick settings {what}: {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(
        ref ulong stateName,
        IntPtr typeId,
        IntPtr explicitScope,
        out uint changeStamp,
        byte[] buffer,
        ref int bufferSize);

    [DllImport("ntdll.dll")]
    private static extern int NtUpdateWnfStateData(
        ref ulong stateName,
        byte[] buffer,
        int length,
        IntPtr typeId,
        IntPtr explicitScope,
        uint matchingChangeStamp,
        int checkStamp);
}
