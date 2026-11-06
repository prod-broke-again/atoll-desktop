using System.Reflection;
using System.Runtime.InteropServices;

namespace TaskbarSpike;

internal static class DesktopToggle
{
    public static void Toggle()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type == null)
        {
            Console.WriteLine("[spike] desktop: Shell.Application is missing");
            return;
        }

        var shell = Activator.CreateInstance(type);
        if (shell == null)
        {
            return;
        }

        try
        {
            type.InvokeMember("ToggleDesktop", BindingFlags.InvokeMethod, null, shell, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] desktop: " + ex.Message);
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }
}
