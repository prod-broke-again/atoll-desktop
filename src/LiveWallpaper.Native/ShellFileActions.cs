using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace LiveWallpaper.Native;

/// <summary>
/// Desktop-shortcut verbs that Windows Explorer exposes via IContextMenu / ShellExecute.
/// Mapping (from shell verb research):
///   open            → ShellExecute "open"
///   runas           → ShellExecute "runas" (UAC)
///   properties      → ShellExecute "properties"
///   cut/copy        → CF_HDROP + Preferred DropEffect on clipboard
///   delete          → FO_DELETE via SHFileOperation (recycle bin when possible)
///   open location   → explorer /select,"path"
///   copy as path    → unicode text of full path
///   pin to start    → shell verb "pintostartscreen" when registered
///   show more       → TrackPopupMenu on IContextMenu for the item
/// </summary>
public static class ShellFileActions
{
    public static bool Open(string path)
    {
        return ShellExecute(path, "open");
    }

    public static bool RunAsAdmin(string path)
    {
        // For .lnk, resolve target first when possible; shell still accepts the link with runas.
        return ShellExecute(path, "runas");
    }

    public static bool Properties(string path)
    {
        return ShellExecute(path, "properties");
    }

    public static bool OpenFileLocation(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool CopyAsPath(string path)
    {
        try
        {
            Clipboard.SetText(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool Cut(string path) => SetClipboardDrop(path, preferredDropEffect: 2); // DROPEFFECT_MOVE

    public static bool Copy(string path) => SetClipboardDrop(path, preferredDropEffect: 1); // DROPEFFECT_COPY

    public static bool Delete(string path, bool allowUndo = true)
    {
        try
        {
            var flags = (ushort)(FOF_SILENT | FOF_NOCONFIRMATION | FOF_NOERRORUI);
            if (allowUndo) flags |= FOF_ALLOWUNDO;

            var from = path + "\0\0";
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = flags
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch
        {
            return false;
        }
    }

    public static bool Rename(string path, string newName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(newName)) return false;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return false;

            if (File.Exists(path))
            {
                var ext = Path.GetExtension(path);
                var dest = Path.Combine(dir, newName + ext);
                if (File.Exists(dest)) return false;
                File.Move(path, dest);
                return true;
            }

            if (Directory.Exists(path))
            {
                var dest = Path.Combine(dir, newName);
                if (Directory.Exists(dest)) return false;
                Directory.Move(path, dest);
                return true;
            }
        }
        catch
        {
            // ignored
        }
        return false;
    }

    public static bool PinToStart(string path)
    {
        // Registered by Shell for many shortcut types on Win10/11.
        return ShellExecute(path, "pintostartscreen") || ShellExecute(path, "startpin");
    }

    /// <summary>Shows the full classic shell context menu at screen coordinates.</summary>
    public static bool ShowExtendedContextMenu(string path, int screenX, int screenY)
    {
        try
        {
            var pidl = ILCreateFromPath(path);
            if (pidl == IntPtr.Zero) return false;

            try
            {
                var hr = SHBindToParent(pidl, typeof(IShellFolder).GUID, out var folderUnk, out var child);
                if (hr != 0 || folderUnk == IntPtr.Zero) return false;

                var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderUnk);
                Marshal.Release(folderUnk);

                var apidl = new[] { child };
                folder.GetUIObjectOf(IntPtr.Zero, 1, apidl, typeof(IContextMenu).GUID, IntPtr.Zero, out var menuUnk);
                if (menuUnk == IntPtr.Zero) return false;

                var menu = (IContextMenu)Marshal.GetObjectForIUnknown(menuUnk);
                Marshal.Release(menuUnk);

                var hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;

                try
                {
                    menu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXTENDEDVERBS);
                    var cmd = TrackPopupMenuEx(
                        hMenu,
                        TPM_RETURNCMD | TPM_LEFTALIGN | TPM_RIGHTBUTTON,
                        screenX,
                        screenY,
                        GetDesktopWindow(),
                        IntPtr.Zero);

                    if (cmd != 0)
                    {
                        var info = new CMINVOKECOMMANDINFOEX
                        {
                            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                            fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE,
                            hwnd = GetDesktopWindow(),
                            lpVerb = (IntPtr)(cmd - 1),
                            lpVerbW = (IntPtr)(cmd - 1),
                            nShow = SW_SHOWNORMAL,
                            ptInvoke = new POINT { x = screenX, y = screenY }
                        };
                        menu.InvokeCommand(ref info);
                    }
                    return true;
                }
                finally
                {
                    DestroyMenu(hMenu);
                }
            }
            finally
            {
                ILFree(pidl);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool ShellExecute(string path, string verb)
    {
        try
        {
            var sei = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
                fMask = SEE_MASK_INVOKEIDLIST | SEE_MASK_FLAG_NO_UI,
                lpVerb = verb,
                lpFile = path,
                nShow = SW_SHOWNORMAL
            };
            return ShellExecuteEx(ref sei);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Verb = verb,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool SetClipboardDrop(string path, int preferredDropEffect)
    {
        try
        {
            var files = new System.Collections.Specialized.StringCollection { path };
            var data = new DataObject();
            data.SetFileDropList(files);
            data.SetData("Preferred DropEffect", true, BitConverter.GetBytes(preferredDropEffect));
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOWNORMAL = 1;
    private const int FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const uint CMIC_MASK_PTINVOKE = 0x20000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public string lpVerb;
        public string lpFile;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public int wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public string? lpParameters;
        public string? lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public string? lpTitle;
        public IntPtr lpVerbW;
        public string? lpParametersW;
        public string? lpDirectoryW;
        public string? lpTitleW;
        public POINT ptInvoke;
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, string pszDisplayName, ref uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, string pszName, uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);
        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uFlags, IntPtr pReserved, StringBuilder pszName, uint cchMax);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPath(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
