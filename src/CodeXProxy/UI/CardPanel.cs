namespace CodeXProxy.UI;

internal sealed class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Theme.Surface;
        Padding = new Padding(18);
    }

    public int CornerRadius { get; set; } = 12;

    public Color BorderColor { get; set; } = Theme.Border;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (Parent is not null)
        {
            e.Graphics.Clear(Parent.BackColor);
        }

        using var path = Theme.RoundedRectangle(
            new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
            CornerRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = Theme.RoundedRectangle(
            new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
            CornerRadius);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }
}

