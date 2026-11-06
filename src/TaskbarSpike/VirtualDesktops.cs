using System.Runtime.InteropServices;

namespace TaskbarSpike;

internal sealed record DesktopChip(int Index, string Name, bool Current);

/// <summary>
/// Windows virtual desktops. Listing and switching go through the shell's
/// private manager; whether a window is on the current desktop uses the
/// documented manager. A build that no longer speaks this interface just
/// hides the switcher.
/// </summary>
internal static class VirtualDesktops
{
    private static IVirtualDesktopManagerInternal? _internal;
    private static IVirtualDesktopManager? _public;
    private static bool _internalFailed;
    private static bool _publicFailed;
    private static bool _logged;

    public static bool IsCurrent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !EnsurePublic())
        {
            return true;
        }

        try
        {
            return _public!.IsWindowOnCurrentVirtualDesktop(hwnd);
        }
        catch
        {
            return true;
        }
    }

    public static IReadOnlyList<DesktopChip> List()
    {
        if (!EnsureInternal())
        {
            return Array.Empty<DesktopChip>();
        }

        IObjectArray? array = null;
        try
        {
            _internal!.GetDesktops(out array);
            array.GetCount(out var count);
            Guid currentId = default;
            IVirtualDesktop? current = null;
            try
            {
                current = _internal.GetCurrentDesktop();
                currentId = current.GetId();
            }
            catch
            {
                currentId = default;
            }
            finally
            {
                Release(current);
            }

            var chips = new List<DesktopChip>(count);
            var iid = typeof(IVirtualDesktop).GUID;
            for (var i = 0; i < count; i++)
            {
                array.GetAt(i, ref iid, out var obj);
                var desktop = (IVirtualDesktop)obj;
                try
                {
                    var id = desktop.GetId();
                    chips.Add(new DesktopChip(i, Label(desktop, i), id == currentId));
                }
                finally
                {
                    Release(desktop);
                }
            }

            return chips;
        }
        catch (Exception ex)
        {
            Note(ex.Message);
            return Array.Empty<DesktopChip>();
        }
        finally
        {
            Release(array);
        }
    }

    public static void Switch(int index)
    {
        WithDesktop(index, (manager, desktop) => manager.SwitchDesktop(desktop));
    }

    public static void Create()
    {
        if (!EnsureInternal())
        {
            return;
        }

        IVirtualDesktop? created = null;
        try
        {
            created = _internal!.CreateDesktop();
            _internal.SwitchDesktop(created);
        }
        catch (Exception ex)
        {
            Note(ex.Message);
        }
        finally
        {
            Release(created);
        }
    }

    public static void Close(int index)
    {
        if (!EnsureInternal())
        {
            return;
        }

        IObjectArray? array = null;
        try
        {
            _internal!.GetDesktops(out array);
            array.GetCount(out var count);
            if (index < 0 || index >= count || count < 2)
            {
                return;
            }

            var fallbackIndex = index == 0 ? 1 : index - 1;
            var iid = typeof(IVirtualDesktop).GUID;
            array.GetAt(index, ref iid, out var targetObj);
            array.GetAt(fallbackIndex, ref iid, out var fallbackObj);
            var target = (IVirtualDesktop)targetObj;
            var fallback = (IVirtualDesktop)fallbackObj;
            try
            {
                _internal.RemoveDesktop(target, fallback);
            }
            finally
            {
                Release(target);
                Release(fallback);
            }
        }
        catch (Exception ex)
        {
            Note(ex.Message);
        }
        finally
        {
            Release(array);
        }
    }

    public static string Describe()
    {
        var chips = List();
        if (chips.Count == 0)
        {
            return _logged ? "virtual desktops unavailable" : "virtual desktops: none";
        }

        return string.Join(", ", chips.Select(chip => (chip.Current ? "*" : "") + chip.Index + ":" + chip.Name));
    }

    private static void WithDesktop(int index, Action<IVirtualDesktopManagerInternal, IVirtualDesktop> action)
    {
        if (!EnsureInternal())
        {
            return;
        }

        IObjectArray? array = null;
        try
        {
            _internal!.GetDesktops(out array);
            array.GetCount(out var count);
            if (index < 0 || index >= count)
            {
                return;
            }

            var iid = typeof(IVirtualDesktop).GUID;
            array.GetAt(index, ref iid, out var obj);
            var desktop = (IVirtualDesktop)obj;
            try
            {
                action(_internal, desktop);
            }
            finally
            {
                Release(desktop);
            }
        }
        catch (Exception ex)
        {
            Note(ex.Message);
        }
        finally
        {
            Release(array);
        }
    }

    private static string Label(IVirtualDesktop desktop, int index)
    {
        try
        {
            var name = desktop.GetName();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }
        catch
        {
            // An unnamed desktop has no string yet.
        }

        return (index + 1).ToString();
    }

    private static bool EnsurePublic()
    {
        if (_public != null)
        {
            return true;
        }

        if (_publicFailed)
        {
            return false;
        }

        try
        {
            _public = (IVirtualDesktopManager)new VirtualDesktopManager();
            return true;
        }
        catch (Exception ex)
        {
            _publicFailed = true;
            Note(ex.Message);
            return false;
        }
    }

    private static bool EnsureInternal()
    {
        if (_internal != null)
        {
            return true;
        }

        if (_internalFailed)
        {
            return false;
        }

        try
        {
            var shell = (IServiceProvider10)new ImmersiveShell();
            var service = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
            var iid = typeof(IVirtualDesktopManagerInternal).GUID;
            _internal = (IVirtualDesktopManagerInternal)shell.QueryService(ref service, ref iid);
            return _internal != null;
        }
        catch (Exception ex)
        {
            _internalFailed = true;
            Note(ex.Message);
            return false;
        }
    }

    private static void Note(string message)
    {
        if (_logged)
        {
            return;
        }

        _logged = true;
        Console.WriteLine("[spike] virtual desktops: " + message);
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com))
        {
            Marshal.ReleaseComObject(com);
        }
    }

    [ComImport]
    [Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")]
    private class ImmersiveShell
    {
    }

    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManager
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    private interface IVirtualDesktopManager
    {
        bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
        Guid GetWindowDesktopId(IntPtr topLevelWindow);
        void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    private interface IVirtualDesktop
    {
        bool IsViewVisible(IntPtr view);
        Guid GetId();

        [return: MarshalAs(UnmanagedType.HString)]
        string GetName();
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal
    {
        int GetCount();
        void MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IVirtualDesktop GetCurrentDesktop();
        void GetDesktops(out IObjectArray desktops);

        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);

        void SwitchDesktop(IVirtualDesktop desktop);
        void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
        IVirtualDesktop CreateDesktop();
        void MoveDesktop(IVirtualDesktop desktop, int index);
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
        IVirtualDesktop FindDesktop(ref Guid desktopId);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    private interface IObjectArray
    {
        void GetCount(out int count);
        void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }
}
