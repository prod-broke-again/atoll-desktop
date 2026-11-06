using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSpike;

/// <summary>
/// Start menu account name and power actions.
/// The Windows Start window is never opened. Power runs only when that item is clicked.
/// </summary>
internal static class StartMenu
{
    public static string Describe()
    {
        return StartLayout.Describe() + Environment.NewLine + PhoneLink.Describe();
    }

    public static string AccountName()
    {
        var size = 256;
        var buffer = new StringBuilder(size);
        if (GetUserNameEx(3, buffer, ref size) != 0 && buffer.Length > 0)
        {
            return buffer.ToString();
        }

        return Environment.UserName;
    }

    public static void Power(string action)
    {
        TaskbarGuard.Restore();
        var args = action switch
        {
            "sleep" => null,
            "shutdown" => "/s /t 0",
            "restart" => "/r /t 0",
            "signout" => "/l",
            _ => null,
        };
        if (action == "sleep")
        {
            Application.SetSuspendState(PowerState.Suspend, true, false);
            return;
        }

        if (args == null)
        {
            return;
        }

        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern byte GetUserNameEx(int nameFormat, StringBuilder name, ref int size);
}
