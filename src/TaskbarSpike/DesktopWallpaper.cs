using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarSpike;

/// <summary>Current desktop wallpaper, scaled down for virtual-desktop thumbnails.</summary>
internal static class DesktopWallpaper
{
    private const int SpiGetDeskWallpaper = 0x0073;
    private static string? _url;
    private static string? _path;
    private static DateTime _stamp;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfo(int action, int param, StringBuilder value, int winIni);

    public static string? DataUrl()
    {
        var path = ReadPath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return _url;
        }

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return _url;
        }

        if (_url != null && path == _path && stamp == _stamp)
        {
            return _url;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            using var frame = new Bitmap(320, 192);
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, new Rectangle(0, 0, frame.Width, frame.Height));
            }

            using var output = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            frame.Save(output, jpeg, quality);
            _url = "data:image/jpeg;base64," + Convert.ToBase64String(output.ToArray());
            _path = path;
            _stamp = stamp;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[spike] wallpaper: " + ex.Message);
        }

        return _url;
    }

    private static string? ReadPath()
    {
        var buffer = new StringBuilder(512);
        return SystemParametersInfo(SpiGetDeskWallpaper, buffer.Capacity, buffer, 0) ? buffer.ToString() : null;
    }
}
