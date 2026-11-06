using System;
using System.Runtime.InteropServices;
using LiveWallpaper.Core.Collage;
using LiveWallpaper.Core.SetupPalette;
using Microsoft.Win32;

namespace LiveWallpaper.Native;

public static class WindowsAccentHelper
{
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam);

    /// <summary>
    /// Writes accent color into DWM/Explorer registry and enables ColorPrevalence
    /// so Windows 11 actually paints Start / taskbar / title bars with it.
    /// </summary>
    public static bool ApplyAccent(RgbColor accent)
    {
        try
        {
            var abgr = (int)PaletteColorUtils.ToAbgr(accent);
            var argb = (int)PaletteColorUtils.ToArgb(accent);
            var paletteBytes = PaletteColorUtils.GenerateWindowsAccentPalette(accent);
            var startColor = (int)PaletteColorUtils.ToAbgr(PaletteColorUtils.Blend(accent, new RgbColor(0, 0, 0), 0.20));
            var inactive = (int)PaletteColorUtils.ToAbgr(PaletteColorUtils.Blend(accent, new RgbColor(0, 0, 0), 0.45));

            // 1. DWM — title bars / window borders
            using (var dwmKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM"))
            {
                if (dwmKey != null)
                {
                    dwmKey.SetValue("AccentColor", abgr, RegistryValueKind.DWord);
                    dwmKey.SetValue("AccentColorInactive", inactive, RegistryValueKind.DWord);
                    dwmKey.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
                    dwmKey.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
                    // Without this, Win11 keeps grey title bars even if AccentColor is set.
                    dwmKey.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
                    dwmKey.SetValue("EnableWindowColorization", 1, RegistryValueKind.DWord);
                }
            }

            // 2. Explorer — Start / taskbar accent swatches
            using (var accentKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                if (accentKey != null)
                {
                    accentKey.SetValue("AccentColorMenu", abgr, RegistryValueKind.DWord);
                    accentKey.SetValue("StartColorMenu", startColor, RegistryValueKind.DWord);
                    accentKey.SetValue("AccentPalette", paletteBytes, RegistryValueKind.Binary);
                }
            }

            // 3. Personalize — "Show accent color on Start and taskbar"
            using (var personalize = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                personalize?.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
            }

            // Prefer manual accent over wallpaper auto-pick.
            using (var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                desktop?.SetValue("AutoColorization", 0, RegistryValueKind.DWord);
            }

            BroadcastAccentChanged();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void BroadcastAccentChanged()
    {
        // Notify, don't wait. A synchronous broadcast walks every top-level window and
        // stalls the desktop; DWM reads the new accent from the registry on its own.
        _ = SendNotifyMessage(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "ImmersiveColorSet");
    }
}
