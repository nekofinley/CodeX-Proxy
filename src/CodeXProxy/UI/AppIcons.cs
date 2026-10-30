using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodeXProxy.UI;

internal static class AppIcons
{
    public static Icon Create(bool running)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var backgroundPath = Theme.RoundedRectangle(new Rectangle(2, 2, 28, 28), 8);
            using var background = new SolidBrush(running ? Theme.Running : Color.FromArgb(92, 101, 98));
            graphics.FillPath(background, backgroundPath);

            using var pen = new Pen(Color.FromArgb(255, 249, 239), 2.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            graphics.DrawLine(pen, 7f, 11f, 24f, 11f);
            graphics.DrawLines(pen, [new PointF(19.5f, 6.5f), new PointF(24f, 11f), new PointF(19.5f, 15.5f)]);
            graphics.DrawLine(pen, 25f, 21f, 8f, 21f);
            graphics.DrawLines(pen, [new PointF(12.5f, 16.5f), new PointF(8f, 21f), new PointF(12.5f, 25.5f)]);
        }

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

