using System.Runtime.InteropServices;

namespace TaskbarSpike;

internal sealed record AudioIcon(string Device, int Volume, bool Muted)
{
    public static AudioIcon Empty { get; } = new("", 0, false);
}

// FormFactor: PKEY_AudioEndpoint_FormFactor (3/5 = headphones/headset, 9 = HDMI/DP display, 10 = unknown).
internal sealed record AudioDeviceItem(string Id, string Name, bool Active, int FormFactor = 10);

internal sealed record AudioSessionItem(string Key, string Name, int Volume, bool Muted, string? ExePath = null);

internal sealed record AudioDetails(
    string DeviceId,
    string DeviceName,
    int Volume,
    bool Muted,
    IReadOnlyList<AudioDeviceItem> Devices,
    IReadOnlyList<AudioSessionItem> Sessions)
{
    public static AudioDetails Empty { get; } = new("", "", 0, false, Array.Empty<AudioDeviceItem>(), Array.Empty<AudioSessionItem>());

    public AudioIcon Icon => new(DeviceName, Volume, Muted);
}

/// <summary>
/// Master volume, output devices, and the per-app mixer via Core Audio.
/// The Windows quick-settings window is never opened.
/// </summary>
internal static class AudioDevices
{
    private const int Render = 0;
    private const int ConsoleRole = 0;
    private const int ActiveState = 1;
    private const int SessionExpired = 2;
    private static readonly Guid FriendlyNameId = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private static readonly Guid EndpointVolumeId = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private static readonly Guid SessionManagerId = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Dictionary<uint, string> ProcessNames = new();
    private static string _logged = "";
    private static bool _faultLogged;

    public static AudioIcon Icon()
    {
        var details = Read(false);
        Note(details);
        return details.Icon;
    }

    public static AudioDetails Details()
    {
        var details = Read(true);
        Note(details);
        return details;
    }

    public static string Describe()
    {
        var details = Read(true);
        var devices = string.Join("; ", details.Devices.Select(device => (device.Active ? "*" : "") + device.Name));
        var sessions = string.Join("; ", details.Sessions.Select(session => session.Name + " " + session.Volume));
        return $"audio device={details.DeviceName} volume={details.Volume} muted={details.Muted}{Environment.NewLine}devices: {devices}{Environment.NewLine}sessions: {sessions}";
    }

    public static void SetVolume(int percent)
    {
        UseEndpoint(volume => volume.SetMasterVolumeLevelScalar(Math.Clamp(percent, 0, 100) / 100f, IntPtr.Zero));
    }

    public static void SetMuted(bool muted)
    {
        UseEndpoint(volume => volume.SetMute(muted ? 1 : 0, IntPtr.Zero));
    }

    public static void SetDefault(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        var policy = (IPolicyConfigVista)new PolicyConfigClient();
        try
        {
            for (var role = 0; role < 3; role++)
            {
                Check(policy.SetDefaultEndpoint(deviceId, role), "SetDefaultEndpoint");
            }
        }
        finally
        {
            Release(policy);
        }
    }

    public static void SetSession(string key, int percent)
    {
        var level = Math.Clamp(percent, 0, 100) / 100f;
        WalkSessions((session, simple, sessionKey) =>
        {
            if (sessionKey == key)
            {
                Check(simple.SetMasterVolume(level, IntPtr.Zero), "session volume");
            }
        });
    }

    private static void Note(AudioDetails details)
    {
        var line = string.IsNullOrWhiteSpace(details.DeviceName)
            ? "none"
            : details.DeviceName + (details.Muted ? " muted" : "");
        if (line == _logged)
        {
            return;
        }

        _logged = line;
        Console.WriteLine("[spike] audio: " + line);
    }

    private static AudioDetails Read(bool includeSessions)
    {
        try
        {
            return ReadCore(includeSessions);
        }
        catch (Exception ex)
        {
            if (!_faultLogged)
            {
                _faultLogged = true;
                Console.WriteLine("[spike] audio: " + ex.Message);
            }

            return AudioDetails.Empty;
        }
    }

    private static AudioDetails ReadCore(bool includeSessions)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? endpoint = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out endpoint), "default endpoint");
            var id = DeviceId(endpoint!);
            var name = DeviceName(endpoint!);
            volume = Activate<IAudioEndpointVolume>(endpoint!, EndpointVolumeId);
            Check(volume.GetMasterVolumeLevelScalar(out var level), "volume");
            Check(volume.GetMute(out var mute), "mute");
            var devices = ListDevices(enumerator, id);
            IReadOnlyList<AudioSessionItem> sessions = includeSessions
                ? ListSessions(endpoint!)
                : Array.Empty<AudioSessionItem>();
            return new AudioDetails(id, name, Percent(level), mute != 0, devices, sessions);
        }
        finally
        {
            Release(volume);
            Release(endpoint);
            Release(enumerator);
        }
    }

    private static List<AudioDeviceItem> ListDevices(IMMDeviceEnumerator enumerator, string activeId)
    {
        IMMDeviceCollection? collection = null;
        var devices = new List<AudioDeviceItem>();
        try
        {
            Check(enumerator.EnumAudioEndpoints(Render, ActiveState, out collection), "endpoints");
            Check(collection!.GetCount(out var count), "endpoint count");
            for (var i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) < 0 || device == null)
                    {
                        continue;
                    }

                    var id = DeviceId(device);
                    devices.Add(new AudioDeviceItem(id, DeviceName(device), id == activeId, FormFactor(device)));
                }
                finally
                {
                    Release(device);
                }
            }
        }
        finally
        {
            Release(collection);
        }

        devices.Sort((left, right) =>
        {
            var byActive = right.Active.CompareTo(left.Active);
            return byActive != 0 ? byActive : string.Compare(left.Name, right.Name, StringComparison.CurrentCulture);
        });
        return devices;
    }

    private static List<AudioSessionItem> ListSessions(IMMDevice endpoint)
    {
        var manager = Activate<IAudioSessionManager2>(endpoint, SessionManagerId);
        IAudioSessionEnumerator? enumerator = null;
        var grouped = new Dictionary<string, AudioSessionItem>(StringComparer.Ordinal);
        try
        {
            Check(manager.GetSessionEnumerator(out enumerator), "sessions");
            Check(enumerator!.GetCount(out var count), "session count");
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl2? session = null;
                ISimpleAudioVolume? simple = null;
                try
                {
                    if (enumerator.GetSession(i, out session) < 0 || session == null)
                    {
                        continue;
                    }

                    if (session.GetState(out var state) < 0 || state == SessionExpired)
                    {
                        continue;
                    }

                    var system = session.IsSystemSoundsSession() == 0;
                    uint pid = 0;
                    if (!system && (session.GetProcessId(out pid) < 0 || pid == 0 || pid == (uint)Environment.ProcessId))
                    {
                        continue;
                    }

                    simple = (ISimpleAudioVolume)session;
                    if (simple.GetMasterVolume(out var level) < 0)
                    {
                        continue;
                    }

                    simple.GetMute(out var mute);
                    var key = system ? "system" : "pid:" + pid;
                    var item = new AudioSessionItem(key, system ? "Системные звуки" : ProcessLabel(pid), Percent(level), mute != 0, system ? null : ProcessPath(pid));
                    if (!grouped.ContainsKey(key) || state == 1)
                    {
                        grouped[key] = item;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[spike] audio session: " + ex.Message);
                }
                finally
                {
                    Release(simple);
                    Release(session);
                }
            }
        }
        finally
        {
            Release(enumerator);
            Release(manager);
        }

        return grouped.Values
            .OrderBy(item => item.Key == "system" ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static void WalkSessions(Action<IAudioSessionControl2, ISimpleAudioVolume, string> visit)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? endpoint = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out endpoint), "default endpoint");
            manager = Activate<IAudioSessionManager2>(endpoint!, SessionManagerId);
            Check(manager.GetSessionEnumerator(out sessions), "sessions");
            Check(sessions!.GetCount(out var count), "session count");
            for (var i = 0; i < count; i++)
            {
                IAudioSessionControl2? session = null;
                ISimpleAudioVolume? simple = null;
                try
                {
                    if (sessions.GetSession(i, out session) < 0 || session == null)
                    {
                        continue;
                    }

                    var system = session.IsSystemSoundsSession() == 0;
                    uint pid = 0;
                    if (!system && session.GetProcessId(out pid) < 0)
                    {
                        continue;
                    }

                    simple = (ISimpleAudioVolume)session;
                    visit(session, simple, system ? "system" : "pid:" + pid);
                }
                finally
                {
                    Release(simple);
                    Release(session);
                }
            }
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(endpoint);
            Release(enumerator);
        }
    }

    private static void UseEndpoint(Func<IAudioEndpointVolume, int> action)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? endpoint = null;
        IAudioEndpointVolume? volume = null;
        try
        {
            Check(enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out endpoint), "default endpoint");
            volume = Activate<IAudioEndpointVolume>(endpoint!, EndpointVolumeId);
            Check(action(volume), "endpoint");
        }
        finally
        {
            Release(volume);
            Release(endpoint);
            Release(enumerator);
        }
    }

    private static T Activate<T>(IMMDevice device, Guid iid) where T : class
    {
        Check(device.Activate(ref iid, 23, IntPtr.Zero, out var obj), iid.ToString());
        return (T)obj;
    }

    private static string DeviceId(IMMDevice device)
    {
        Check(device.GetId(out var id), "device id");
        return id ?? "";
    }

    private static string DeviceName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null)
            {
                return "Устройство";
            }

            var key = new PropertyKey { FormatId = FriendlyNameId, PropertyId = 14 };
            if (store.GetValue(ref key, out var value) < 0)
            {
                return "Устройство";
            }

            try
            {
                if (value.VarType is 31 or 8 && value.Pointer != IntPtr.Zero)
                {
                    var text = Marshal.PtrToStringUni(value.Pointer);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }

        return "Устройство";
    }

    private static readonly Guid FormFactorId = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e");
    private static readonly Dictionary<uint, string?> ProcessPaths = new();

    private static int FormFactor(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null)
            {
                return 10;
            }

            var key = new PropertyKey { FormatId = FormFactorId, PropertyId = 0 };
            if (store.GetValue(ref key, out var value) < 0)
            {
                return 10;
            }

            // VT_UI4: the number sits in the first bytes of the value union.
            return value.VarType == 19 ? (int)(value.Pointer.ToInt64() & 0xFFFFFFFF) : 10;
        }
        catch
        {
            return 10;
        }
        finally
        {
            Release(store);
        }
    }

    private static string? ProcessPath(uint pid)
    {
        if (ProcessPaths.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        string? path = null;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            path = process.MainModule?.FileName;
        }
        catch
        {
            // Protected or exited process: the mixer row falls back to a letter.
        }

        ProcessPaths[pid] = path;
        return path;
    }

    private static string ProcessLabel(uint pid)
    {
        if (ProcessNames.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        var label = "Приложение";
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                label = string.IsNullOrWhiteSpace(description) ? process.ProcessName : description;
            }
            catch
            {
                label = string.IsNullOrWhiteSpace(process.ProcessName) ? label : process.ProcessName;
            }
        }
        catch
        {
            label = "Приложение";
        }

        ProcessNames[pid] = label;
        return label;
    }

    private static int Percent(float level)
    {
        return (int)Math.Round(Math.Clamp(level, 0f, 1f) * 100f);
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0)
        {
            throw new InvalidOperationException(what + " 0x" + hr.ToString("X8"));
        }
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)]
    public ushort VarType;

    [FieldOffset(8)]
    public IntPtr Pointer;
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int Item(int index, out IMMDevice? device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(int access, out IPropertyStore store);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out int state);
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr callback);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr callback);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, IntPtr context);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, IntPtr context);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, IntPtr context);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute(int mute, IntPtr context);

    [PreserveSig]
    int GetMute(out int mute);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr session);

    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int RegisterDuckNotification(IntPtr name, IntPtr notification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr notification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl2? session);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out int state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);

    [PreserveSig]
    int GetGroupingParam(out Guid grouping);

    [PreserveSig]
    int SetGroupingParam(ref Guid grouping, IntPtr context);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetProcessId(out uint pid);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, IntPtr context);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute(int mute, IntPtr context);

    [PreserveSig]
    int GetMute(out int mute);
}

[ComImport]
[Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClient
{
}

[ComImport]
[Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfigVista
{
    // The ten slots before SetDefaultEndpoint are part of the COM vtable.
    [PreserveSig] int Slot01();
    [PreserveSig] int Slot02();
    [PreserveSig] int Slot03();
    [PreserveSig] int Slot04();
    [PreserveSig] int Slot05();
    [PreserveSig] int Slot06();
    [PreserveSig] int Slot07();
    [PreserveSig] int Slot08();
    [PreserveSig] int Slot09();
    [PreserveSig] int Slot10();

    [PreserveSig]
    int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
}
