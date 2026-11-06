using System.Windows.Forms;

namespace LiveWallpaper.App;

public sealed class WallpaperApplicationContext : ApplicationContext
{
    private readonly WallpaperForm _form;
    private bool _isShuttingDown;

    public WallpaperApplicationContext(WallpaperForm form)
    {
        _form = form;
        _form.FormClosed += (s, e) =>
        {
            if (!_isShuttingDown)
            {
                ExitThread();
            }
        };
        _form.Show();
    }

    public void Shutdown()
    {
        _isShuttingDown = true;
        _form.ForceClose();
        ExitThread();
    }
}
