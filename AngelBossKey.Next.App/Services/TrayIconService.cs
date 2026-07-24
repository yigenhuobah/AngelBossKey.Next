using AngelBossKey.Next.App.ViewModels;
using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace AngelBossKey.Next.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Icon _readyIcon;
    private readonly Icon _hiddenIcon;
    private readonly Forms.ToolStripMenuItem _toggleItem;

    public TrayIconService(Action showWindow, Action toggleVisibility, Action exit)
    {
        _readyIcon = CreateIcon(Color.FromArgb(20, 125, 100));
        _hiddenIcon = CreateIcon(Color.FromArgb(182, 106, 24));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => showWindow());
        _toggleItem = new Forms.ToolStripMenuItem("隐藏目标", null, (_, _) => toggleVisibility());
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "天使老板键 Next - 保护就绪",
            Icon = _readyIcon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
    }

    public void Update(bool isHidden)
    {
        _notifyIcon.Icon = isHidden ? _hiddenIcon : _readyIcon;
        _notifyIcon.Text = isHidden
            ? "天使老板键 Next - 目标已隐藏"
            : "天使老板键 Next - 保护就绪";
        _toggleItem.Text = isHidden ? "恢复目标" : "隐藏目标";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _readyIcon.Dispose();
        _hiddenIcon.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Icon CreateIcon(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var darkBrush = new SolidBrush(Color.FromArgb(36, 51, 59));
        using var accentBrush = new SolidBrush(accent);
        graphics.FillRoundedRectangle(darkBrush, new RectangleF(2, 2, 28, 28), 7);
        graphics.FillEllipse(accentBrush, 9, 9, 14, 14);
        using var highlight = new Pen(Color.FromArgb(235, 255, 255, 255), 2.2f);
        graphics.DrawArc(highlight, 11, 11, 10, 10, 205, 230);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint icon);
}

internal static class GraphicsExtensions
{
    internal static void FillRoundedRectangle(
        this Graphics graphics,
        Brush brush,
        RectangleF rectangle,
        float radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
