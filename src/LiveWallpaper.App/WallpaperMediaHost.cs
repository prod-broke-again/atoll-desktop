using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LiveWallpaper.Core.Configuration;
using Microsoft.Web.WebView2.Core;

namespace LiveWallpaper.App;

/// <summary>
/// Serves local wallpaper files through a single WebView2 virtual host.
/// Files are mirrored into a cache with safe ASCII names so Cyrillic / spaces
/// in the original path never break the URL mapping.
/// </summary>
public sealed class WallpaperMediaHost
{
    private const string HostName = "wallpaper.livewallpaper";

    private CoreWebView2? _core;
    private bool _mapped;
    private readonly string _cacheDir;
    private readonly Dictionary<string, string> _pathToUrl = new(StringComparer.OrdinalIgnoreCase);

    public WallpaperMediaHost()
    {
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiveWallpaper",
            "WallpaperMedia");
        Directory.CreateDirectory(_cacheDir);
    }

    public void Attach(CoreWebView2 core)
    {
        _core = core;
        EnsureMapped();
    }

    public void SyncFromConfig(WallpaperBackgroundConfig? wallpaper)
    {
        if (_core == null || wallpaper?.Sources == null) return;
        EnsureMapped();

        foreach (var src in wallpaper.Sources)
        {
            if (src.Type is not ("video" or "image")) continue;
            if (string.IsNullOrWhiteSpace(src.Value) || !Path.IsPathRooted(src.Value)) continue;
            _ = TryGetMediaUrl(src.Value);
        }
    }

    public string? TryGetMediaUrl(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathRooted(filePath)) return null;

        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full)) return null;

        if (_pathToUrl.TryGetValue(full, out var cached) && File.Exists(CachePathFromUrl(cached)))
            return cached;

        EnsureMapped();

        var ext = Path.GetExtension(full);
        if (string.IsNullOrEmpty(ext) || ext.Length > 8) ext = ".bin";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant()[..16];
        var safeName = hash + ext.ToLowerInvariant();
        var dest = Path.Combine(_cacheDir, safeName);

        try
        {
            var srcInfo = new FileInfo(full);
            var destInfo = new FileInfo(dest);
            if (!destInfo.Exists || destInfo.Length != srcInfo.Length || destInfo.LastWriteTimeUtc != srcInfo.LastWriteTimeUtc)
            {
                File.Copy(full, dest, overwrite: true);
                File.SetLastWriteTimeUtc(dest, srcInfo.LastWriteTimeUtc);
            }
        }
        catch
        {
            return null;
        }

        var url = $"https://{HostName}/{safeName}";
        _pathToUrl[full] = url;
        return url;
    }

    private void EnsureMapped()
    {
        if (_mapped || _core == null) return;
        Directory.CreateDirectory(_cacheDir);
        _core.SetVirtualHostNameToFolderMapping(
            HostName,
            _cacheDir,
            CoreWebView2HostResourceAccessKind.Allow);
        _mapped = true;
    }

    private string CachePathFromUrl(string url)
    {
        var name = url.Substring(url.LastIndexOf('/') + 1);
        return Path.Combine(_cacheDir, name);
    }
}
