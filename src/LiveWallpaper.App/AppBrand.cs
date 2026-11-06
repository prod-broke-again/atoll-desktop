using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace LiveWallpaper.App;

/// <summary>Shared app icon for the tray, settings window, and taskbar.</summary>
internal static class AppBrand
{
    private static Icon? _icon;

    public static Icon Icon
    {
        get
        {
            if (_icon != null) return _icon;

            try
            {
                var baseDir = AppContext.BaseDirectory;
                var icoPath = Path.Combine(baseDir, "Assets", "app.ico");
                if (File.Exists(icoPath))
                {
                    _icon = new Icon(icoPath);
                    return _icon;
                }

                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("LiveWallpaper.App.Assets.app.ico");
                if (stream != null)
                {
                    _icon = new Icon(stream);
                    return _icon;
                }
            }
            catch
            {
                // Fall through to system icon.
            }

            _icon = SystemIcons.Application;
            return _icon;
        }
    }
}
