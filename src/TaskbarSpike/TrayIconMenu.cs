namespace TaskbarSpike;

/// <summary>
/// Opens the application's own tray context menu by replaying the right-click
/// message sequence to the icon's owner window (see TrayHost).
/// </summary>
internal static class TrayIconMenu
{
    public static bool TryShow(TrayButton button)
    {
        var icon = TrayHost.FindIcon(button);
        if (icon == null)
        {
            Console.WriteLine($"[spike] tray menu: no hosted icon for '{button.Name}' (icons={TrayHost.Snapshot().Count})");
            return false;
        }

        return icon.RightClick();
    }
}
