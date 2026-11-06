using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace TaskbarSpike;

internal sealed record PinEntry(string Name, string? Aumid, string? TargetPath, string? IconPath, int IconIndex, string LinkPath);

/// <summary>
/// Reads AppUserModelIDs and the current user's pinned taskbar shortcuts.
/// Pins come from the shell's own TaskBar folder, ordered by the Taskband registry blob.
/// </summary>
internal static class ShellProperties
{
    private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPid = 5;
    private static readonly Guid PropertyStoreIid = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    private static IReadOnlyList<PinEntry> _pins = Array.Empty<PinEntry>();
    private static DateTime _loadedAt = DateTime.MinValue;
    private static bool _logged;

    public static string? WindowAumid(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var iid = PropertyStoreIid;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store == null)
            {
                return null;
            }

            try
            {
                return ReadAumid(store);
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    public static void Invalidate()
    {
        _loadedAt = DateTime.MinValue;
    }

    public static IReadOnlyList<PinEntry> Pins()
    {
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromSeconds(5))
        {
            return _pins;
        }

        try
        {
            _pins = ReadPins();
            if (!_logged)
            {
                Console.WriteLine($"[spike] Windows taskbar pins: {_pins.Count}");
                _logged = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[spike] Could not read pinned taskbar apps: {ex.Message}");
        }

        _loadedAt = DateTime.UtcNow;
        return _pins;
    }

    private static IReadOnlyList<PinEntry> ReadPins()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (!Directory.Exists(folder))
        {
            return Array.Empty<PinEntry>();
        }

        var files = Directory.GetFiles(folder, "*.lnk");
        var order = PinOrder(files);
        var pins = new List<PinEntry>(order.Count);
        foreach (var file in order)
        {
            if (TryReadLink(file, out var pin))
            {
                pins.Add(pin);
            }
        }

        return pins;
    }

    private static List<string> PinOrder(string[] files)
    {
        byte[]? blob = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            blob = key?.GetValue("Favorites") as byte[];
        }
        catch
        {
            blob = null;
        }

        return files
            .Select(file => (file, index: blob == null ? -1 : IndexOfName(blob, Path.GetFileName(file))))
            .OrderBy(item => item.index < 0 ? int.MaxValue : item.index)
            .ThenBy(item => item.file, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.file)
            .ToList();
    }

    private static int IndexOfName(byte[] blob, string name)
    {
        var utf16 = IndexOf(blob, Encoding.Unicode.GetBytes(name));
        var utf8 = IndexOf(blob, Encoding.UTF8.GetBytes(name));
        if (utf16 < 0)
        {
            return utf8;
        }

        if (utf8 < 0)
        {
            return utf16;
        }

        return Math.Min(utf16, utf8);
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return -1;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryReadLink(string linkPath, out PinEntry pin)
    {
        pin = new PinEntry(Path.GetFileNameWithoutExtension(linkPath), null, null, null, 0, linkPath);
        try
        {
            var link = (IShellLinkW)new ShellLinkClass();
            ((IPersistFile)link).Load(linkPath, 0);

            var targetBuffer = new StringBuilder(1024);
            link.GetPath(targetBuffer, targetBuffer.Capacity, IntPtr.Zero, 0);
            var target = targetBuffer.ToString();
            if (!File.Exists(target))
            {
                target = string.Empty;
            }

            var iconBuffer = new StringBuilder(1024);
            link.GetIconLocation(iconBuffer, iconBuffer.Capacity, out var iconIndex);
            var iconPath = Environment.ExpandEnvironmentVariables(iconBuffer.ToString());
            if (!File.Exists(iconPath))
            {
                iconPath = target;
                iconIndex = 0;
            }

            // Shell pins such as File Explorer have no exe target. The icon path is explorer.exe.
            if (string.IsNullOrWhiteSpace(target) &&
                iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(iconPath))
            {
                target = iconPath;
            }

            string? aumid = null;
            if (link is IPropertyStore store)
            {
                aumid = ReadAumid(store);
            }

            pin = new PinEntry(
                Path.GetFileNameWithoutExtension(linkPath),
                string.IsNullOrWhiteSpace(aumid) ? null : aumid,
                string.IsNullOrWhiteSpace(target) ? null : target,
                string.IsNullOrWhiteSpace(iconPath) ? null : iconPath,
                iconIndex,
                linkPath);
            return true;
        }
        catch
        {
            return true;
        }
    }

    private static string? ReadAumid(IPropertyStore store)
    {
        var key = new PropertyKey { FormatId = AppUserModelFormat, PropertyId = AppUserModelIdPid };
        if (store.GetValue(ref key, out var value) != 0)
        {
            return null;
        }

        try
        {
            return value.ReadString();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
        [PreserveSig] int GetIDList(out IntPtr ppidl);
        [PreserveSig] int SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        [PreserveSig] int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        [PreserveSig] int GetHotkey(out ushort pwHotkey);
        [PreserveSig] int SetHotkey(ushort wHotkey);
        [PreserveSig] int GetShowCmd(out int piShowCmd);
        [PreserveSig] int SetShowCmd(int iShowCmd);
        [PreserveSig] int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        [PreserveSig] int Resolve(IntPtr hwnd, uint fFlags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;

        public string? ReadString()
        {
            if (Pointer == IntPtr.Zero)
            {
                return null;
            }

            return VariantType switch
            {
                31 => Marshal.PtrToStringUni(Pointer),
                8 => Marshal.PtrToStringBSTR(Pointer),
                _ => null,
            };
        }
    }
}
