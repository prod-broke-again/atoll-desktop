using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TaskbarSpike;

/// <summary>
/// Pin / unpin apps in the shell TaskBar shortcuts folder that <see cref="ShellProperties.Pins"/> already reads.
/// Prefer folder mutations over Explorer's taskband so the hidden Win11 bar stays untouched.
/// </summary>
internal static class TaskbarPin
{
    public static string PinsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    public static bool Unpin(AppButton app)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(app.LaunchPath) &&
                File.Exists(app.LaunchPath) &&
                app.LaunchPath.StartsWith(PinsFolder, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(app.LaunchPath);
                ShellProperties.Invalidate();
                return true;
            }

            foreach (var pin in ShellProperties.Pins())
            {
                if (!Matches(app, pin))
                {
                    continue;
                }

                if (File.Exists(pin.LinkPath))
                {
                    File.Delete(pin.LinkPath);
                    ShellProperties.Invalidate();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] unpin: " + ex.Message);
        }

        return false;
    }

    public static bool Pin(AppButton app)
    {
        try
        {
            Directory.CreateDirectory(PinsFolder);
            if (ShellProperties.Pins().Any(pin => Matches(app, pin)))
            {
                return true;
            }

            var fileName = Sanitize(app.Name) + ".lnk";
            var linkPath = UniquePath(Path.Combine(PinsFolder, fileName));
            if (!CreateShortcut(linkPath, app))
            {
                return false;
            }

            ShellProperties.Invalidate();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] pin: " + ex.Message);
            return false;
        }
    }

    private static bool Matches(AppButton app, PinEntry pin)
    {
        if (!string.IsNullOrWhiteSpace(app.Aumid) &&
            string.Equals(app.Aumid, pin.Aumid, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(app.TargetPath) &&
            string.Equals(app.TargetPath, pin.TargetPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(app.LaunchPath) &&
            string.Equals(app.LaunchPath, pin.LinkPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(app.Name, pin.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool CreateShortcut(string linkPath, AppButton app)
    {
        var link = (IShellLinkW)new ShellLinkClass();
        try
        {
            if (!string.IsNullOrWhiteSpace(app.TargetPath) && File.Exists(app.TargetPath))
            {
                link.SetPath(app.TargetPath);
                try
                {
                    link.SetWorkingDirectory(Path.GetDirectoryName(app.TargetPath) ?? string.Empty);
                }
                catch
                {
                    // Optional.
                }
            }
            else if (!string.IsNullOrWhiteSpace(app.LaunchPath) && File.Exists(app.LaunchPath))
            {
                // Already a shortcut — copy bytes.
                File.Copy(app.LaunchPath, linkPath, overwrite: true);
                return true;
            }
            else
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(app.Aumid) && link is IPropertyStore store)
            {
                WriteAumid(store, app.Aumid);
            }

            ((IPersistFile)link).Save(linkPath, true);
            return File.Exists(linkPath);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private static void WriteAumid(IPropertyStore store, string aumid)
    {
        var key = new PropertyKey
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
        var value = new PropVariant();
        value.SetString(aumid);
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    private static string Sanitize(string name)
    {
        var clean = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "App" : clean;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? PinsFolder;
        var stem = Path.GetFileNameWithoutExtension(path);
        for (var i = 2; i < 50; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}).lnk");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(dir, stem + "-" + Guid.NewGuid().ToString("N")[..8] + ".lnk");
    }

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

        public void SetString(string value)
        {
            VariantType = 31; // VT_LPWSTR
            Pointer = Marshal.StringToCoTaskMemUni(value);
        }
    }
}
