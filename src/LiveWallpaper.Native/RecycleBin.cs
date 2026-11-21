using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiveWallpaper.Native;

/// <summary>
/// Shell recycle bin. It is a virtual desktop icon, not a file, so the launcher scan never sees it.
/// </summary>
public static class RecycleBin
{
    private const uint SHERB_NOCONFIRMATION = 0x00000001;
    private const int E_UNEXPECTED = unchecked((int)0x8000FFFF);

    public readonly record struct Info(long Count, long Bytes);

    public static bool TryQuery(out Info info)
    {
        info = default;
        var query = new SHQUERYRBINFO
        {
            cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>(),
        };

        var hr = SHQueryRecycleBin(null, ref query);
        if (hr != 0) return false;

        info = new Info(Math.Max(0, query.i64NumItems), Math.Max(0, query.i64Size));
        return true;
    }

    /// <summary>Empties every drive. Returns the shell HRESULT (0 when it succeeds).</summary>
    public static int Empty()
    {
        // Progress UI and the empty sound stay; the wallpaper already asked for confirmation.
        return SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION);
    }

    public static bool IsEmptyResult(int hr) => hr == 0 || hr == E_UNEXPECTED;

    public static void Open()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = "shell:RecycleBinFolder",
            UseShellExecute = true,
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);
}
