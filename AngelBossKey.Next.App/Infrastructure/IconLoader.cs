using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AngelBossKey.Next.App.Infrastructure;

public static class IconLoader
{
    public static ImageSource LoadFromExecutable(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                return source;
            }
        }
        catch
        {
        }

        return CreateFallback();
    }

    private static DrawingImage CreateFallback()
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            context.DrawRoundedRectangle(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 51, 59)),
                null,
                new Rect(2, 2, 28, 28),
                6,
                6);
            context.DrawEllipse(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 196, 158)),
                null,
                new System.Windows.Point(16, 16),
                6,
                6);
        }

        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
