using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TaskbarSpike;

internal sealed record AppButton(
    string Id,
    string Name,
    string? IconDataUrl,
    bool Pinned,
    bool Running,
    bool Active,
    long ActiveHwnd,
    IReadOnlyList<string> Titles,
    IReadOnlyList<long> Hwnds,
    string? LaunchPath,
    string? Aumid,
    string? TargetPath,
    int Badge);

internal static class WindowCatalog
{
    private static readonly Dictionary<string, string?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Last app the taskbar toggled to the foreground — second click minimizes even if the dock stole focus.</summary>
    private static string? _taskbarToggleId;
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Progman",
        "WorkerW",
    };

    private static readonly HashSet<string> HostExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe",
        "dllhost.exe",
        "svchost.exe",
        "RuntimeBroker.exe",
        "sihost.exe",
        "backgroundTaskHost.exe",
    };

    private static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<AppButton> _buttons = Array.Empty<AppButton>();
    private static readonly List<string> _order = new();
    private static bool _orderLoaded;

    public static IReadOnlyList<AppButton> Current => _buttons;
    private static bool _badgesLogged;

    public static IReadOnlyList<AppButton> Snapshot(IntPtr ownHwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = CollectWindows(ownHwnd, foreground);
        var groups = new List<LiveGroup>();
        foreach (var window in windows)
        {
            var key = GroupKey(window);
            var group = groups.Find(item => item.Key == key);
            if (group == null)
            {
                group = new LiveGroup(key, window);
                groups.Add(group);
            }

            group.Windows.Add(window);
        }

        var used = new bool[groups.Count];
        var buttons = new List<AppButton>(groups.Count);
        foreach (var pin in ShellProperties.Pins())
        {
            var match = -1;
            for (var i = 0; i < groups.Count; i++)
            {
                if (!used[i] && Matches(pin, groups[i]))
                {
                    match = i;
                    break;
                }
            }

            if (match >= 0)
            {
                used[match] = true;
                buttons.Add(ToButton(groups[match], pin));
            }
            else
            {
                buttons.Add(PinnedOnly(pin));
            }
        }

        for (var i = 0; i < groups.Count; i++)
        {
            if (!used[i])
            {
                buttons.Add(ToButton(groups[i], null));
            }
        }

        var ordered = Arrange(buttons);
        var visible = new List<AppButton>(ordered.Count);
        foreach (var button in ordered)
        {
            if (button.Pinned || button.Running)
            {
                visible.Add(button);
            }
        }

        _buttons = visible;
        if (!_badgesLogged)
        {
            var noted = buttons.Where(button => button.Badge > 0).Select(button => button.Name + "=" + button.Badge).ToArray();
            if (noted.Length > 0)
            {
                _badgesLogged = true;
                Console.WriteLine("[spike] notification badges: " + string.Join(", ", noted));
            }
        }

        return visible;
    }

    public static void SetVisibleOrder(IReadOnlyList<string> visibleIds)
    {
        LoadOrder();
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _order.Count; i++)
        {
            rank.TryAdd(_order[i], i);
        }

        var present = new List<string>();
        foreach (var id in visibleIds)
        {
            if (rank.ContainsKey(id))
            {
                present.Add(id);
            }
        }

        var slots = present.Select(id => rank[id]).OrderBy(slot => slot).ToArray();
        for (var i = 0; i < present.Count; i++)
        {
            rank[present[i]] = slots[i];
        }

        var next = rank.Count == 0 ? 0 : rank.Values.Max() + 1;
        foreach (var id in visibleIds)
        {
            if (rank.TryAdd(id, next))
            {
                next++;
            }
        }

        _order.Clear();
        _order.AddRange(rank.OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key));
        SaveOrder();
    }

    private static List<AppButton> Arrange(List<AppButton> buttons)
    {
        LoadOrder();
        var map = new Dictionary<string, AppButton>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in buttons)
        {
            map.TryAdd(button.Id, button);
        }

        var arranged = new List<AppButton>(buttons.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _order)
        {
            if (map.TryGetValue(id, out var button) && seen.Add(button.Id))
            {
                arranged.Add(button);
            }
        }

        foreach (var button in buttons)
        {
            if (seen.Add(button.Id))
            {
                arranged.Add(button);
            }
        }

        var changed = _order.Count != arranged.Count;
        if (!changed)
        {
            for (var i = 0; i < arranged.Count; i++)
            {
                if (!string.Equals(_order[i], arranged[i].Id, StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    break;
                }
            }
        }

        if (changed)
        {
            _order.Clear();
            _order.AddRange(arranged.Select(button => button.Id));
            SaveOrder();
        }

        return arranged;
    }

    private static void LoadOrder()
    {
        if (_orderLoaded)
        {
            return;
        }

        _orderLoaded = true;
        try
        {
            if (!File.Exists(OrderPath))
            {
                return;
            }

            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OrderPath));
            if (ids == null)
            {
                return;
            }

            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _order.Add(id);
                }
            }
        }
        catch
        {
            _order.Clear();
        }
    }

    private static void SaveOrder()
    {
        try
        {
            var directory = Path.GetDirectoryName(OrderPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(OrderPath, JsonSerializer.Serialize(_order));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] taskbar order: " + ex.Message);
        }
    }

    private static string OrderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarSpike",
        "order.json");

    public static bool TryGetApp(string id, out AppButton? button)
    {
        button = _buttons.FirstOrDefault(item => item.Id == id);
        return button != null;
    }

    public static void ActivateApp(string id)
    {
        var button = _buttons.FirstOrDefault(item => item.Id == id);
        if (button == null)
        {
            return;
        }

        if (button.Hwnds.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(button.LaunchPath))
            {
                Process.Start(new ProcessStartInfo(button.LaunchPath) { UseShellExecute = true });
            }

            _taskbarToggleId = id;
            return;
        }

        if (button.Hwnds.Count == 1)
        {
            var hwnd = new IntPtr(button.Hwnds[0]);
            // Snapshot Active lags while the dock holds foreground after the first
            // click. Remember the last taskbar-activated app and treat a second
            // click on it as minimize (Explorer does the same).
            var armed = string.Equals(_taskbarToggleId, id, StringComparison.Ordinal);
            if ((button.Active || armed) && IsShown(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwMinimize);
                _taskbarToggleId = null;
            }
            else
            {
                Activate(hwnd);
                _taskbarToggleId = id;
            }

            return;
        }

        var hwnds = button.Hwnds.ToList();
        var activeIndex = hwnds.FindIndex(hwnd => hwnd == button.ActiveHwnd);
        var next = hwnds[(activeIndex + 1) % hwnds.Count];
        Activate(new IntPtr(next));
        _taskbarToggleId = id;
    }

    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        }

        // Stay in the ordinary band. A hop through HWND_TOPMOST blanks a
        // window whose composition surface cannot survive that style change
        // (Chromium sets WS_EX_NOREDIRECTIONBITMAP): it paints for one frame,
        // then the page stays transparent and the desktop shows through.
        var place = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow;
        if ((NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle) & NativeMethods.WsExTopmost) != 0)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndNotopmost, 0, 0, 0, 0, place);
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0)
        {
            NativeMethods.AllowSetForegroundWindow(pid);
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var currentThread = NativeMethods.GetCurrentThreadId();
        var attachedForeground = false;
        var attachedTarget = false;

        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            }

            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        // After the thread detach. Doing this earlier, or calling
        // SwitchToThisWindow, sets the foreground thread and then leaves
        // the window under whichever app was already on top. The icon is
        // the right app, but the screen still shows the other one.
        RaiseToOrdinaryTop(hwnd, place);
        if (!IsInFront(hwnd))
        {
            NativeMethods.SetForegroundWindow(hwnd);
            RaiseToOrdinaryTop(hwnd, place);
        }
    }

    private static void RaiseToOrdinaryTop(IntPtr hwnd, uint place)
    {
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTop, 0, 0, 0, 0, place);
        NativeMethods.BringWindowToTop(hwnd);
    }

    private static bool IsShown(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out var cloakValue, sizeof(int)) == 0 &&
            cloakValue != 0;
        return !cloaked || !VirtualDesktops.IsCurrent(hwnd);
    }

    internal static bool IsInFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        var root = foreground == hwnd ? hwnd : NativeMethods.GetAncestor(foreground, NativeMethods.GaRoot);
        if (root != hwnd)
        {
            return false;
        }

        // Foreground can be this hwnd while it is still buried under another
        // ordinary window. That is the "clicked icon, saw a different app" case.
        var above = NativeMethods.GetWindow(hwnd, NativeMethods.GwHwndPrev);
        while (above != IntPtr.Zero)
        {
            if (Covers(hwnd, above))
            {
                return false;
            }

            above = NativeMethods.GetWindow(above, NativeMethods.GwHwndPrev);
        }

        return true;
    }

    private static bool Covers(IntPtr target, IntPtr above)
    {
        if (!NativeMethods.IsWindowVisible(above) || NativeMethods.IsIconic(above))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLong(above, NativeMethods.GwlExStyle);
        if ((exStyle & NativeMethods.WsExTopmost) != 0)
        {
            return false;
        }

        if ((exStyle & NativeMethods.WsExToolWindow) != 0 && (exStyle & NativeMethods.WsExAppWindow) == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(above, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId)
        {
            return false;
        }

        var owner = NativeMethods.GetWindow(above, NativeMethods.GwOwner);
        if (owner == target)
        {
            return false;
        }

        var cloaked = NativeMethods.DwmGetWindowAttribute(above, NativeMethods.DwmwaCloaked, out var cloakValue, sizeof(int)) == 0 &&
            cloakValue != 0;
        return !cloaked;
    }

    private static bool Include(IntPtr hwnd, IntPtr ownHwnd, uint ownPid, out uint pid)
    {
        pid = 0;
        if (hwnd == IntPtr.Zero || hwnd == ownHwnd || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        var className = NativeMethods.GetClass(hwnd);
        if (ShellClasses.Contains(className))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GwlExStyle);
        var toolWindow = (exStyle & NativeMethods.WsExToolWindow) != 0;
        var appWindow = (exStyle & NativeMethods.WsExAppWindow) != 0;
        if (toolWindow && !appWindow)
        {
            return false;
        }

        var cloaked = NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DwmwaCloaked, out var cloakValue, sizeof(int)) == 0 &&
            cloakValue != 0;
        var onCurrent = VirtualDesktops.IsCurrent(hwnd);
        // A minimized window stays on the taskbar. A window on another virtual
        // desktop is cloaked too, and is kept so its slot does not get reissued.
        if (cloaked && onCurrent && !NativeMethods.IsIconic(hwnd))
        {
            return false;
        }

        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner);
        if (owner != IntPtr.Zero && !appWindow)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        return pid != 0 && pid != ownPid;
    }

    private static bool TryDescribe(uint pid, out string processName, out string? path)
    {
        processName = string.Empty;
        path = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            processName = process.ProcessName;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                path = null;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<LiveWindow> CollectWindows(IntPtr ownHwnd, IntPtr foreground)
    {
        var ownPid = (uint)Environment.ProcessId;
        var result = new List<LiveWindow>();
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            try
            {
                if (!Include(hwnd, ownHwnd, ownPid, out var pid))
                {
                    return true;
                }

                var title = NativeMethods.GetText(hwnd).Trim();
                if (title.Length == 0)
                {
                    return true;
                }

                if (!TryDescribe(pid, out var processName, out var path))
                {
                    return true;
                }

                if (processName.Equals("TaskbarSpike", StringComparison.OrdinalIgnoreCase) ||
                    processName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                result.Add(new LiveWindow
                {
                    Hwnd = hwnd.ToInt64(),
                    Title = title,
                    ProcessName = processName,
                    Path = path,
                    Aumid = ShellProperties.WindowAumid(hwnd),
                    Active = hwnd == foreground,
                    OnCurrent = VirtualDesktops.IsCurrent(hwnd),
                });
            }
            catch
            {
                // One bad window must not abort the enumeration.
            }

            return true;
        };

        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    private static string GroupKey(LiveWindow window)
    {
        if (!string.IsNullOrWhiteSpace(window.Aumid))
        {
            return "aumid:" + window.Aumid;
        }

        if (HostExecutables.Contains(window.FileName))
        {
            return "hwnd:" + window.Hwnd;
        }

        if (!string.IsNullOrWhiteSpace(window.Path))
        {
            return "path:" + window.Path;
        }

        return "proc:" + window.ProcessName;
    }

    private static bool Matches(PinEntry pin, LiveGroup group)
    {
        if (!string.IsNullOrWhiteSpace(pin.Aumid) &&
            group.Windows.Any(window => string.Equals(window.Aumid, pin.Aumid, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(pin.TargetPath) &&
            group.Windows.Any(window => string.Equals(window.Path, pin.TargetPath, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var pinFile = string.IsNullOrWhiteSpace(pin.TargetPath) ? null : Path.GetFileName(pin.TargetPath);
        if (string.IsNullOrWhiteSpace(pinFile) || HostExecutables.Contains(pinFile))
        {
            return false;
        }

        return group.Windows.Any(window => string.Equals(window.FileName, pinFile, StringComparison.OrdinalIgnoreCase));
    }

    private static AppButton ToButton(LiveGroup group, PinEntry? pin)
    {
        var first = group.Windows[0];
        var live = group.Windows.Where(window => window.OnCurrent).ToList();
        var iconPath = first.Path;
        var iconIndex = 0;
        if (string.IsNullOrWhiteSpace(iconPath) && pin != null)
        {
            iconPath = pin.IconPath;
            iconIndex = pin.IconIndex;
        }

        var name = pin?.Name ?? DisplayName(first);
        var aumid = pin?.Aumid
            ?? group.Windows.Select(window => window.Aumid).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var targetPath = pin?.TargetPath
            ?? group.Windows.Select(window => window.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        var aumids = group.Windows.Select(window => window.Aumid).Append(pin?.Aumid);
        var names = group.Windows.Select(window => window.ProcessName).Append(pin?.Name);
        return new AppButton(
            StableId(pin == null ? group.Key : "pin:" + pin.LinkPath),
            name,
            IconFor(iconPath, iconIndex),
            pin != null,
            live.Count > 0,
            live.Any(window => window.Active),
            live.FirstOrDefault(window => window.Active)?.Hwnd ?? 0,
            live.Select(window => window.Title).ToArray(),
            live.Select(window => window.Hwnd).ToArray(),
            pin?.LinkPath,
            aumid,
            targetPath,
            NotificationCounts.For(aumids, names));
    }

    private static AppButton PinnedOnly(PinEntry pin)
    {
        return new AppButton(
            StableId("pin:" + pin.LinkPath),
            pin.Name,
            IconFor(pin.IconPath, pin.IconIndex),
            true,
            false,
            false,
            0,
            Array.Empty<string>(),
            Array.Empty<long>(),
            pin.LinkPath,
            pin.Aumid,
            pin.TargetPath,
            NotificationCounts.For(new[] { pin.Aumid }, new[] { pin.Name }));
    }

    private static string DisplayName(LiveWindow window)
    {
        if (!string.IsNullOrWhiteSpace(window.Path))
        {
            if (NameCache.TryGetValue(window.Path, out var cached))
            {
                return cached;
            }

            var description = string.Empty;
            try
            {
                description = FileVersionInfo.GetVersionInfo(window.Path).FileDescription ?? string.Empty;
            }
            catch
            {
                description = string.Empty;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                description = window.ProcessName;
            }

            NameCache[window.Path] = description;
            return description;
        }

        return window.ProcessName;
    }

    private static string StableId(string key)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key.ToUpperInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    /// <summary>PNG data URL of an executable's icon (cached, thread-safe) — mixer rows reuse it.</summary>
    internal static string? IconForExe(string? path) => IconFor(path, 0);

    /// <summary>PNG data URL for a file/exe icon path (Jump List rows).</summary>
    internal static string? IconForPath(string? path, int iconIndex = 0) => IconFor(path, iconIndex);

    internal static Image? TryDecodeIcon(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl) ||
            !dataUrl.StartsWith("data:image/png;base64,", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(dataUrl["data:image/png;base64,".Length..]);
            return Image.FromStream(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private static string? IconFor(string? path, int iconIndex)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (iconIndex < 0)
        {
            iconIndex = 0;
        }

        var cacheKey = path + "|" + iconIndex;
        lock (IconCache)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        var dataUrl = ExtractIconDataUrl(path, iconIndex);
        lock (IconCache)
        {
            IconCache[cacheKey] = dataUrl;
        }

        return dataUrl;
    }

    private sealed class LiveWindow
    {
        public long Hwnd;
        public string Title = string.Empty;
        public string ProcessName = string.Empty;
        public string? Path;
        public string? Aumid;
        public bool Active;
        public bool OnCurrent;
        public string FileName => string.IsNullOrWhiteSpace(Path) ? ProcessName + ".exe" : System.IO.Path.GetFileName(Path);
    }

    private sealed class LiveGroup
    {
        public LiveGroup(string key, LiveWindow first)
        {
            Key = key;
            Windows = new List<LiveWindow>();
            _ = first;
        }

        public string Key { get; }
        public List<LiveWindow> Windows { get; }
    }

    private static string? ExtractIconDataUrl(string path, int iconIndex)
    {
        var handles = new IntPtr[1];
        var ids = new uint[1];
        try
        {
            var count = NativeMethods.PrivateExtractIcons(path, iconIndex, 48, 48, handles, ids, 1, 0);
            if (count == 0 || handles[0] == IntPtr.Zero)
            {
                count = NativeMethods.PrivateExtractIcons(path, iconIndex, 32, 32, handles, ids, 1, 0);
            }

            if (count != 0 && handles[0] != IntPtr.Zero)
            {
                return IconHandleToDataUrl(handles[0]);
            }

            // Documents / folders: shell association icon (PrivateExtractIcons only works on icon resources).
            return ShellFileIconDataUrl(path);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (handles[0] != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(handles[0]);
            }
        }
    }

    private static string? ShellFileIconDataUrl(string path)
    {
        var info = new ShFileInfo();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiSmallIcon);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return IconHandleToDataUrl(info.IconHandle);
        }
        finally
        {
            NativeMethods.DestroyIcon(info.IconHandle);
        }
    }

    private static string? IconHandleToDataUrl(IntPtr handle)
    {
        using var icon = Icon.FromHandle(handle);
        using var bitmap = icon.ToBitmap();
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(stream.ToArray());
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr IconHandle;
        public int Icon;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint infoSize, uint flags);
}
