using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AuraChat.Windows;

internal static class ImageUtil
{
    // 冻结后才能跨线程用：位图在后台线程解码。
    public static BitmapImage? ToImage(byte[] bytes)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static BitmapImage? FromBase64(string b64)
    {
        try { return ToImage(Convert.FromBase64String(b64)); }
        catch { return null; }
    }

    // 长边压到 1600 够了；png 保透明，其余转 jpeg q85。
    public static (string Base64, string Mime) Prepare(string path, int maxSide = 1600)
    {
        using var fs = File.OpenRead(path);
        var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource src = dec.Frames[0];
        var longSide = Math.Max(src.PixelWidth, src.PixelHeight);
        if (longSide > maxSide)
        {
            var k = (double)maxSide / longSide;
            src = new TransformedBitmap(src, new ScaleTransform(k, k));
        }

        var png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        BitmapEncoder enc = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return (Convert.ToBase64String(ms.ToArray()), png ? "image/png" : "image/jpeg");
    }
}
