using System.Drawing.Drawing2D;

namespace ScreenSwitcher;

/// <summary>Drag-and-drop arrangement of the enabled monitors of a profile, like Windows' own display settings.</summary>
public sealed class LayoutCanvas : Control
{
    public Profile? Profile { get; set; }
    public Func<string, string> NameOf { get; set; } = _ => "";
    public event EventHandler? LayoutChanged;

    private MonitorSetting? _drag;
    private Point _mouseStart, _posStart;
    private float _scale = 0.1f;
    private PointF _origin;
    private int? _guideX, _guideY; // desktop coordinates of the alignment lines currently snapped to

    public LayoutCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(32, 36, 44);
    }

    private List<MonitorSetting> Shown =>
        Profile?.Monitors.Where(m => m.Enabled && m.Width > 0 && m.Height > 0).ToList() ?? new();

    private void ComputeTransform(List<MonitorSetting> shown)
    {
        if (shown.Count == 0) return;
        var b = shown[0].Bounds;
        foreach (var m in shown) b = Rectangle.Union(b, m.Bounds);
        float pad = LogicalToDeviceUnits(28);
        _scale = Math.Min((Width - 2 * pad) / Math.Max(b.Width, 1), (Height - 2 * pad) / Math.Max(b.Height, 1));
        if (_scale <= 0) _scale = 0.01f;
        _origin = new PointF((Width - b.Width * _scale) / 2 - b.X * _scale, (Height - b.Height * _scale) / 2 - b.Y * _scale);
    }

    private RectangleF ToScreen(Rectangle r) =>
        new(_origin.X + r.X * _scale, _origin.Y + r.Y * _scale, r.Width * _scale, r.Height * _scale);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var shown = Shown;
        if (shown.Count == 0)
        {
            TextRenderer.DrawText(g, Profile == null ? "Create or select a profile" : "No monitors are switched on in this profile",
                Font, ClientRectangle, Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }
        if (_drag == null) ComputeTransform(shown);

        using var small = new Font(Font.FontFamily, Font.Size * 0.9f);
        using var bold = new Font(Font, FontStyle.Bold);
        foreach (var m in shown.OrderBy(m => ReferenceEquals(m, _drag)))
        {
            var r = ToScreen(m.Bounds);
            r.Inflate(-1.5f, -1.5f);
            bool dragging = ReferenceEquals(m, _drag);
            using var fill = new SolidBrush(dragging ? Color.FromArgb(230, 70, 140, 220) : Color.FromArgb(52, 110, 180));
            using var pen = new Pen(m.Primary ? Color.FromArgb(255, 210, 90) : Color.FromArgb(150, 190, 235), m.Primary ? 2.5f : 1.5f);
            g.FillRectangle(fill, r);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);

            string text = NameOf(m.Id) + "\n" + $"{m.Width} x {m.Height} @ {m.RefreshRate} Hz"
                          + (m.Rotation != 0 ? $"  ({m.Rotation}°)" : "") + (m.Primary ? "\nPrimary" : "");
            var rect = Rectangle.Round(r);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            var font = TextRenderer.MeasureText(g, text, bold, rect.Size, flags).Height <= rect.Height ? bold : small;
            TextRenderer.DrawText(g, text, font, rect, Color.White, flags);
        }

        if (_drag != null)
        {
            using var guidePen = new Pen(Color.FromArgb(255, 210, 90), 1.5f) { DashStyle = DashStyle.Dash };
            if (_guideX is int gx) { float sx = _origin.X + gx * _scale; g.DrawLine(guidePen, sx, 0, sx, Height); }
            if (_guideY is int gy) { float sy = _origin.Y + gy * _scale; g.DrawLine(guidePen, 0, sy, Width, sy); }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _drag = Shown.LastOrDefault(m => ToScreen(m.Bounds).Contains(e.Location));
        if (_drag == null) return;
        _mouseStart = e.Location;
        _posStart = new Point(_drag.X, _drag.Y);
        Cursor = Cursors.SizeAll;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == null) return;
        int x = _posStart.X + (int)((e.X - _mouseStart.X) / _scale);
        int y = _posStart.Y + (int)((e.Y - _mouseStart.Y) / _scale);
        int w = _drag.EffW, h = _drag.EffH;
        // Magnetic zone: within this many screen pixels the monitor jumps to the aligned position
        // (edge to edge, flush corners, or centred on its neighbour) and stays there until dragged clear.
        int threshold = (int)(LogicalToDeviceUnits(22) / _scale);

        int bestX = x, bestDx = threshold + 1, bestY = y, bestDy = threshold + 1;
        _guideX = _guideY = null;
        foreach (var o in Shown.Where(o => !ReferenceEquals(o, _drag)).Select(o => o.Bounds))
        {
            int cx = o.Left + o.Width / 2, cy = o.Top + o.Height / 2;
            foreach (var (c, guide) in new[] { (o.Left - w, o.Left), (o.Right, o.Right), (o.Left, o.Left), (o.Right - w, o.Right), (cx - w / 2, cx) })
                if (Math.Abs(c - x) < bestDx) { bestDx = Math.Abs(c - x); bestX = c; _guideX = guide; }
            foreach (var (c, guide) in new[] { (o.Top - h, o.Top), (o.Bottom, o.Bottom), (o.Top, o.Top), (o.Bottom - h, o.Bottom), (cy - h / 2, cy) })
                if (Math.Abs(c - y) < bestDy) { bestDy = Math.Abs(c - y); bestY = c; _guideY = guide; }
        }
        _drag.X = bestX;
        _drag.Y = bestY;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_drag == null) return;
        Settle(_drag);
        _drag = null;
        _guideX = _guideY = null;
        Cursor = Cursors.Default;
        Invalidate();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Windows wants monitors to touch without overlapping: push the moved one out of / up against its neighbours.</summary>
    private void Settle(MonitorSetting m)
    {
        var others = Shown.Where(o => !ReferenceEquals(o, m)).Select(o => o.Bounds).ToList();
        if (others.Count == 0) return;

        for (int pass = 0; pass < 8; pass++)
        {
            var r = m.Bounds;
            var hit = others.FirstOrDefault(o => o.IntersectsWith(r));
            if (hit.IsEmpty) break;
            int left = r.Right - hit.Left, right = hit.Right - r.Left, up = r.Bottom - hit.Top, down = hit.Bottom - r.Top;
            int min = Math.Min(Math.Min(left, right), Math.Min(up, down));
            if (min == left) m.X -= left;
            else if (min == right) m.X += right;
            else if (min == up) m.Y -= up;
            else m.Y += down;
        }

        var b = m.Bounds;
        static (int sx, int sy) Sep(Rectangle a, Rectangle o) =>
            (Math.Max(o.Left - a.Right, a.Left - o.Right), Math.Max(o.Top - a.Bottom, a.Top - o.Bottom));
        if (others.Any(o => { var (sx, sy) = Sep(b, o); return sx <= 0 && sy <= 0; })) return;

        var nearest = others.OrderBy(o => { var (sx, sy) = Sep(b, o); return Math.Max(sx, 0) + Math.Max(sy, 0); }).First();
        if (nearest.Left > b.Right) m.X += nearest.Left - b.Right;
        else if (b.Left > nearest.Right) m.X -= b.Left - nearest.Right;
        if (nearest.Top > b.Bottom) m.Y += nearest.Top - b.Bottom;
        else if (b.Top > nearest.Bottom) m.Y -= b.Top - nearest.Bottom;
    }
}
