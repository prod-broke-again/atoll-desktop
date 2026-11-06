using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace LiveWallpaper.Services.Launcher;

public sealed class IconExtractor
{
    private readonly string _cacheDirectory;
    private readonly ILogger<IconExtractor> _logger;

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkClass { }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string szFileName,
        int nIconIndex,
        int cxIcon,
        int cyIcon,
        IntPtr[] phicon,
        uint[] piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public string CacheDirectory => _cacheDirectory;

    public IconExtractor(string cacheDirectory, ILogger<IconExtractor> logger)
    {
        _cacheDirectory = cacheDirectory;
        _logger = logger;

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create IconCache directory '{Dir}'", _cacheDirectory);
        }
    }

    public string GetOrExtractIconUrl(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return string.Empty;
        }

        try
        {
            var hash = ComputeFileHash(filePath);
            var cacheFile = Path.Combine(_cacheDirectory, $"{hash}.png");

            if (File.Exists(cacheFile) && new FileInfo(cacheFile).Length > 0)
            {
                return $"https://icons.livewallpaper/{hash}.png";
            }

            var success = ExtractIconToPng(filePath, cacheFile);
            if (!success)
            {
                GenerateFallbackPng(filePath, cacheFile);
            }

            return $"https://icons.livewallpaper/{hash}.png";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract icon for '{File}'", filePath);
            return string.Empty;
        }
    }

    private bool ExtractIconToPng(string filePath, string targetPng)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".url")
        {
            return ExtractFromUrl(filePath, targetPng);
        }

        if (ext == ".lnk")
        {
            return ExtractFromLnk(filePath, targetPng);
        }

        if (ext == ".exe")
        {
            return ExtractWithPrivateExtractIcons(filePath, 0, targetPng);
        }

        return ExtractAssociated(filePath, targetPng);
    }

    private bool ExtractFromUrl(string urlFilePath, string targetPng)
    {
        try
        {
            string? iconFile = null;
            int iconIndex = 0;
            string? url = null;

            foreach (var line in File.ReadAllLines(urlFilePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                {
                    iconFile = trimmed["IconFile=".Length..].Trim('\"', ' ');
                }
                else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(trimmed["IconIndex=".Length..].Trim(), out var idx))
                    {
                        iconIndex = idx;
                    }
                }
                else if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    url = trimmed["URL=".Length..].Trim();
                }
            }

            if (!string.IsNullOrEmpty(iconFile))
            {
                var expanded = Environment.ExpandEnvironmentVariables(iconFile);
                if (File.Exists(expanded))
                {
                    if (ExtractWithPrivateExtractIcons(expanded, iconIndex, targetPng))
                    {
                        return true;
                    }
                }
            }

            // Fallback for Steam shortcuts
            if (url != null && url.StartsWith("steam://", StringComparison.OrdinalIgnoreCase))
            {
                var steamExe = @"C:\Program Files (x86)\Steam\steam.exe";
                if (File.Exists(steamExe) && ExtractWithPrivateExtractIcons(steamExe, 0, targetPng))
                {
                    return true;
                }
            }

            // Fallback for Epic Games shortcuts
            if (url != null && url.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase))
            {
                var epicLauncher = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
                if (File.Exists(epicLauncher) && ExtractWithPrivateExtractIcons(epicLauncher, 0, targetPng))
                {
                    return true;
                }
            }

            return ExtractAssociated(urlFilePath, targetPng);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse .url file '{Path}'", urlFilePath);
            return ExtractAssociated(urlFilePath, targetPng);
        }
    }

    private bool ExtractFromLnk(string lnkFilePath, string targetPng)
    {
        try
        {
            var link = (IShellLinkW)new ShellLinkClass();
            var persist = (IPersistFile)link;
            persist.Load(lnkFilePath, 0);

            var iconPathSb = new StringBuilder(1024);
            link.GetIconLocation(iconPathSb, iconPathSb.Capacity, out int iconIndex);
            var iconPath = Environment.ExpandEnvironmentVariables(iconPathSb.ToString());

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                if (ExtractWithPrivateExtractIcons(iconPath, iconIndex, targetPng))
                {
                    return true;
                }
            }

            var targetSb = new StringBuilder(1024);
            link.GetPath(targetSb, targetSb.Capacity, IntPtr.Zero, 0);
            var targetPath = Environment.ExpandEnvironmentVariables(targetSb.ToString());

            if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
            {
                if (ExtractWithPrivateExtractIcons(targetPath, 0, targetPng))
                {
                    return true;
                }
            }

            return ExtractAssociated(lnkFilePath, targetPng);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve .lnk icon for '{Path}'", lnkFilePath);
            return ExtractAssociated(lnkFilePath, targetPng);
        }
    }

    private static bool ExtractWithPrivateExtractIcons(string sourcePath, int iconIndex, string targetPng)
    {
        var phicon = new IntPtr[1];
        var piconid = new uint[1];

        // Request 128x128 for high-DPI crystal clear display
        uint count = PrivateExtractIcons(sourcePath, iconIndex, 128, 128, phicon, piconid, 1, 0);
        if (count == 0 || phicon[0] == IntPtr.Zero)
        {
            // Try 96x96
            count = PrivateExtractIcons(sourcePath, iconIndex, 96, 96, phicon, piconid, 1, 0);
        }

        if (count > 0 && phicon[0] != IntPtr.Zero)
        {
            try
            {
                using var icon = Icon.FromHandle(phicon[0]);
                using var bmp = icon.ToBitmap();
                bmp.Save(targetPng, ImageFormat.Png);
                return true;
            }
            finally
            {
                DestroyIcon(phicon[0]);
            }
        }

        return false;
    }

    private static bool ExtractAssociated(string filePath, string targetPng)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(filePath);
            if (icon != null)
            {
                using var bmp = icon.ToBitmap();
                bmp.Save(targetPng, ImageFormat.Png);
                return true;
            }
        }
        catch { }

        return false;
    }

    private static void GenerateFallbackPng(string filePath, string targetPng)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        var letter = !string.IsNullOrEmpty(name) ? name[..1].ToUpperInvariant() : "?";

        using var bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, 128, 128),
            Color.FromArgb(200, 32, 38, 52),
            Color.FromArgb(220, 20, 24, 34),
            45f);

        g.FillRoundedRectangle(brush, new Rectangle(8, 8, 112, 112), 24);

        using var pen = new Pen(Color.FromArgb(80, 255, 255, 255), 2);
        g.DrawRoundedRectangle(pen, new Rectangle(8, 8, 112, 112), 24);

        using var font = new Font("Segoe UI Variable Display", 48, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.FromArgb(235, 240, 255));
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        g.DrawString(letter, font, textBrush, new RectangleF(0, 0, 128, 128), format);

        bmp.Save(targetPng, ImageFormat.Png);
    }

    private static string ComputeFileHash(string filePath)
    {
        var mtime = File.GetLastWriteTimeUtc(filePath).Ticks;
        var key = $"{filePath}|{mtime}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash).ToLowerInvariant()[..24];
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle bounds, int cornerRadius)
    {
        using var path = CreateRoundedRectanglePath(bounds, cornerRadius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle bounds, int cornerRadius)
    {
        using var path = CreateRoundedRectanglePath(bounds, cornerRadius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        // Top left
        path.AddArc(arc, 180, 90);

        // Top right
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);

        // Bottom right
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);

        // Bottom left
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);

        path.CloseFigure();
        return path;
    }
}
