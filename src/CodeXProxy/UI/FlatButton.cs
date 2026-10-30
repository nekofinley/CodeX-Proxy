using System.Drawing.Drawing2D;

namespace CodeXProxy.UI;

internal sealed class FlatButton : Button
{
    private bool _hovered;
    private bool _pressed;

    public FlatButton()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Font = Theme.Body(9f, FontStyle.Bold);
        Height = 34;
        CornerRadius = 8;
    }

    public int CornerRadius { get; set; }

    public Color NormalColor { get; set; } = Theme.Ink;

    public Color HoverColor { get; set; } = Color.FromArgb(50, 65, 64);

    public Color PressedColor { get; set; } = Color.FromArgb(16, 30, 30);

    public Color TextColor { get; set; } = Color.White;

    protected override void OnMouseEnter(EventArgs e)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Parent is not null)
        {
            pevent.Graphics.Clear(Parent.BackColor);
        }

        var color = !Enabled
            ? Color.FromArgb(185, 181, 171)
            : _pressed
                ? PressedColor
                : _hovered
                    ? HoverColor
                    : NormalColor;

        using var path = Theme.RoundedRectangle(
            new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1)),
            CornerRadius);
        using var brush = new SolidBrush(color);
        pevent.Graphics.FillPath(brush, path);

        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            ClientRectangle,
            Enabled ? TextColor : Color.FromArgb(245, 242, 235),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

