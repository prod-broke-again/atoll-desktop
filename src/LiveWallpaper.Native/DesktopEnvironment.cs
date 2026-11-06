using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

public static class DesktopEnvironment
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    public static bool IsInteractiveDesktop()
    {
        return FindWindow("Progman", null) != IntPtr.Zero;
    }

    public static bool SpawnOnDefaultDesktop(string[] args)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return false;

        string cmdLine;
        if (exePath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var appDll = Path.Combine(AppContext.BaseDirectory, "LiveWallpaper.App.dll");
            cmdLine = $"\"{exePath}\" \"{appDll}\" --interactive-desktop {string.Join(" ", args)}";
        }
        else
        {
            cmdLine = $"\"{exePath}\" --interactive-desktop {string.Join(" ", args)}";
        }

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        si.lpDesktop = @"winsta0\default";

        var ok = CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0x01000000 /* CREATE_BREAKAWAY_FROM_JOB */, IntPtr.Zero, null, ref si, out var pi);
        if (!ok)
        {
            // Fallback without breakaway flag if disallowed by parent job
            ok = CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi);
        }

        if (ok)
        {
            Console.WriteLine($"[DesktopEnvironment] Child process PID {pi.dwProcessId} running on winsta0\\default. Parent waiting...");
            _ = WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
            GetExitCodeProcess(pi.hProcess, out var exitCode);
            Console.WriteLine($"[DesktopEnvironment] Child process PID {pi.dwProcessId} exited with code {exitCode}");
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);
        }
        else
        {
            var err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[DesktopEnvironment] CreateProcess failed: error {err}");
        }
        return ok;
    }
}
