using System.Drawing.Drawing2D;

namespace CodeXProxy.UI;

internal static class Theme
{
    public static readonly Color Canvas = Color.FromArgb(244, 241, 234);
    public static readonly Color Surface = Color.FromArgb(255, 253, 248);
    public static readonly Color SurfaceMuted = Color.FromArgb(238, 234, 224);
    public static readonly Color Ink = Color.FromArgb(28, 43, 43);
    public static readonly Color Muted = Color.FromArgb(98, 108, 103);
    public static readonly Color Border = Color.FromArgb(215, 209, 197);
    public static readonly Color Accent = Color.FromArgb(220, 91, 56);
    public static readonly Color AccentHover = Color.FromArgb(198, 77, 44);
    public static readonly Color Running = Color.FromArgb(39, 117, 96);
    public static readonly Color Warning = Color.FromArgb(181, 121, 32);
    public static readonly Color Error = Color.FromArgb(179, 56, 49);

    public const string DisplayFont = "Segoe UI Variable Display";
    public const string BodyFont = "Segoe UI Variable Text";
    public const string MonoFont = "Cascadia Mono";

    public static Font Display(float size, FontStyle style = FontStyle.Bold)
    {
        return new Font(DisplayFont, size, style, GraphicsUnit.Point);
    }

    public static Font Body(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(BodyFont, size, style, GraphicsUnit.Point);
    }

    public static Font Mono(float size, FontStyle style = FontStyle.Regular)
    {
        return new Font(MonoFont, size, style, GraphicsUnit.Point);
    }

    public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 1)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Label SectionLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Body(9.5f, FontStyle.Bold),
            ForeColor = Ink,
            Margin = new Padding(0, 0, 0, 4)
        };
    }

    public static Label HintLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = Body(8.5f),
            ForeColor = Muted,
            Margin = new Padding(0, 3, 0, 6)
        };
    }
}

