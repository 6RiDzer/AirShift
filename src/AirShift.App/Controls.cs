using System.Drawing.Drawing2D;

namespace AirShift.App;

internal static class Theme
{
    public static readonly Color Back = Color.FromArgb(15, 17, 20);
    public static readonly Color Panel = Color.FromArgb(23, 26, 31);
    public static readonly Color Line = Color.FromArgb(38, 43, 51);
    public static readonly Color Text = Color.FromArgb(232, 234, 237);
    public static readonly Color Muted = Color.FromArgb(138, 145, 156);
    public static readonly Color Accent = Color.FromArgb(53, 224, 176);
    public static readonly Color AccentDark = Color.FromArgb(14, 64, 52);
    public static readonly Color Warn = Color.FromArgb(255, 176, 72);
    public static readonly Color Bad = Color.FromArgb(255, 107, 91);

    public static Font Ui(float size, FontStyle style = FontStyle.Regular) => new("Segoe UI", size, style, GraphicsUnit.Point);
}

/// <summary>A flat button with a hover state and an optional "selected" accent.</summary>
internal sealed class FlatButton : Control
{
    private bool _hover;
    private bool _selected;
    private bool _primary;

    public FlatButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Ui(10f, FontStyle.Bold);
        ForeColor = Theme.Text;
        Cursor = Cursors.Hand;
        Height = 40;
    }

    public string Hint { get; set; } = "";

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public bool Primary
    {
        get => _primary;
        set { _primary = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        Color fill, border, text;
        if (!Enabled)
        {
            fill = Theme.Panel; border = Theme.Line; text = Theme.Muted;
        }
        else if (_primary)
        {
            fill = _hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent; border = fill; text = Theme.Back;
        }
        else if (_selected)
        {
            fill = Theme.AccentDark; border = Theme.Accent; text = Theme.Accent;
        }
        else
        {
            fill = _hover ? Color.FromArgb(32, 36, 43) : Theme.Panel; border = Theme.Line; text = Theme.Text;
        }
        using (var path = Rounded(rect, 4))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
        if (Hint.Length > 0)
        {
            var main = new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - 12);
            TextRenderer.DrawText(g, Text, Font, main, text, flags);
            using var small = Theme.Ui(7.5f);
            var hint = new Rectangle(rect.X, rect.Bottom - 22, rect.Width, 18);
            TextRenderer.DrawText(g, Hint, small, hint, _primary ? Color.FromArgb(150, Theme.Back) : Theme.Muted, flags);
        }
        else
        {
            TextRenderer.DrawText(g, Text, Font, rect, text, flags);
        }
    }

    internal static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>A labelled slider. Reports its value while dragging and commits on release.</summary>
internal sealed class Slider : Control
{
    private double _value;
    private bool _dragging;

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 44;
        Font = Theme.Ui(9f);
        Cursor = Cursors.Hand;
    }

    public string Label { get; set; } = "";
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100;
    public double Step { get; set; } = 1;
    public Func<double, string> Format { get; set; } = v => v.ToString("0");

    /// <summary>Value at which a small tick is drawn (stock, for instance).</summary>
    public double? Mark { get; set; }

    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return;
            _value = Math.Clamp(value, Minimum, Maximum);
            Invalidate();
        }
    }

    public event Action<double>? Committed;

    private Rectangle Track => new(8, Height - 14, Width - 16, 4);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!Enabled) return;
        _dragging = true;
        Capture = true;
        SetFromX(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) SetFromX(e.X);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!_dragging) return;
        SetFromX(e.X);
        _dragging = false;
        Capture = false;
        Committed?.Invoke(_value);
    }

    private void SetFromX(int x)
    {
        var t = Math.Clamp((x - Track.X) / (double)Track.Width, 0, 1);
        var raw = Minimum + t * (Maximum - Minimum);
        _value = Math.Clamp(Math.Round(raw / Step) * Step, Minimum, Maximum);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Back);
        var labelHeight = Math.Max(20, Height - 24);
        TextRenderer.DrawText(g, Label, Font, new Rectangle(4, 2, Width - 90, labelHeight), Enabled ? Theme.Text : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        using var bold = Theme.Ui(9f, FontStyle.Bold);
        TextRenderer.DrawText(g, Format(_value), bold, new Rectangle(Width - 94, 2, 90, labelHeight), Enabled ? Theme.Accent : Theme.Muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        var track = Track;
        using (var back = new SolidBrush(Theme.Line)) g.FillRectangle(back, track);
        var t = (Maximum - Minimum) <= 0 ? 0 : (_value - Minimum) / (Maximum - Minimum);
        var zero = Minimum < 0 && Maximum > 0 ? (0 - Minimum) / (Maximum - Minimum) : 0;
        var x0 = track.X + (int)(track.Width * Math.Min(zero, t));
        var x1 = track.X + (int)(track.Width * Math.Max(zero, t));
        using (var fill = new SolidBrush(Enabled ? Theme.Accent : Theme.Muted)) g.FillRectangle(fill, x0, track.Y, Math.Max(1, x1 - x0), track.Height);
        if (Mark is { } mark)
        {
            var mx = track.X + (int)(track.Width * (mark - Minimum) / (Maximum - Minimum));
            using var pen = new Pen(Theme.Muted, 1);
            g.DrawLine(pen, mx, track.Y - 5, mx, track.Bottom + 5);
        }
        var thumbX = track.X + (int)(track.Width * t);
        using var thumb = new SolidBrush(Enabled ? Theme.Text : Theme.Muted);
        g.FillEllipse(thumb, thumbX - 7, track.Y - 5, 14, 14);
    }
}

/// <summary>A side view of the car that follows the live height of each axle.</summary>
internal sealed class RideView : Control
{
    private double _front;
    private double _rear;
    private bool _active;

    public RideView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Height = 150;
    }

    public string Caption { get; set; } = "";

    public void Show(double frontPercent, double rearPercent, bool active, string caption)
    {
        if (Math.Abs(_front - frontPercent) < 0.5 && Math.Abs(_rear - rearPercent) < 0.5 && _active == active && Caption == caption) return;
        _front = frontPercent;
        _rear = rearPercent;
        _active = active;
        Caption = caption;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Panel);
        using (var border = new Pen(Theme.Line)) g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var ground = Height - 34;
        var wheelR = 22;
        var rearX = Width / 2 - 120;
        var frontX = Width / 2 + 120;
        // 100 % of clearance maps to 26 px of travel.
        double Lift(double percent) => Math.Clamp(percent, -100, 30) / 100.0 * 26;
        var rearBody = ground - wheelR - 18 - Lift(_rear);
        var frontBody = ground - wheelR - 18 - Lift(_front);

        using (var groundPen = new Pen(Theme.Line, 2)) g.DrawLine(groundPen, 16, ground, Width - 16, ground);

        float Y(float x, float offset) => (float)(rearBody + (frontBody - rearBody) * (x - (rearX - 60)) / ((frontX + 70) - (rearX - 60))) - offset;
        var left = rearX - 60f;
        var right = frontX + 70f;
        var body = new PointF[]
        {
            new(left, Y(left, 0)),
            new(left, Y(left, 22)),
            new(rearX - 10, Y(rearX - 10, 26)),
            new(rearX + 40, Y(rearX + 40, 52)),
            new(frontX - 40, Y(frontX - 40, 54)),
            new(frontX + 10, Y(frontX + 10, 30)),
            new(right, Y(right, 22)),
            new(right, Y(right, 0)),
        };
        using (var fill = new SolidBrush(Color.FromArgb(34, 39, 47))) g.FillPolygon(fill, body);
        using (var outline = new Pen(_active ? Theme.Accent : Theme.Muted, 2)) g.DrawPolygon(outline, body);

        foreach (var x in new[] { rearX, frontX })
        {
            using var tyre = new SolidBrush(Color.FromArgb(10, 11, 13));
            using var rim = new Pen(Theme.Muted, 2);
            g.FillEllipse(tyre, x - wheelR, ground - 2 * wheelR, 2 * wheelR, 2 * wheelR);
            g.DrawEllipse(rim, x - wheelR + 7, ground - 2 * wheelR + 7, 2 * wheelR - 14, 2 * wheelR - 14);
        }

        using var small = Theme.Ui(8.5f);
        TextRenderer.DrawText(g, $"Rear {Signed(_rear)}", small, new Point(14, Height - 24), Theme.Muted);
        var frontText = $"Front {Signed(_front)}";
        var size = TextRenderer.MeasureText(frontText, small);
        TextRenderer.DrawText(g, frontText, small, new Point(Width - 14 - size.Width, Height - 24), Theme.Muted);
        var captionSize = TextRenderer.MeasureText(Caption, small);
        TextRenderer.DrawText(g, Caption, small, new Point((Width - captionSize.Width) / 2, 10), _active ? Theme.Accent : Theme.Muted);
    }

    private static string Signed(double percent) => Math.Abs(percent) < 0.5 ? "stock" : $"{percent:+0;-0} %";
}
