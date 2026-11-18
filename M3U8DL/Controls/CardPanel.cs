using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// 白色圆角卡片容器：GraphicsPath 圆角 Region + 1px 描边。
/// 踩坑：Region 会裁剪本控件一切绘制，阴影不能画在自身 OnPaint，必须由外层 ShadowHost 承担。
/// Region 替换前必须 Dispose，否则每次 Resize 泄漏 GDI 句柄。
/// </summary>
internal sealed class CardPanel : Panel
{
    int _cornerRadius = 14;

    /// <summary>圆角半径（逻辑像素，96 DPI）。WFO1000：公开可写属性需隐藏序列化。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius == value) return; _cornerRadius = Math.Max(0, value); ApplyRegion(); }
    }

    public CardPanel()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        // 尺寸变化时整块重绘：否则 WM_SIZE 只让新增区域失效，圆角描边与 Region 换新后
        // 旧边框像素会留在原位（拉高窗口时卡片边线出现「重影」）。
        ResizeRedraw = true;
        BackColor = Theme.Surface;
        Padding = new Padding(12, 10, 12, 10);
        Margin = new Padding(0);
    }

    protected override void OnLayout(LayoutEventArgs e) { ApplyRegion(); base.OnLayout(e); }
    protected override void OnSizeChanged(EventArgs e) { ApplyRegion(); base.OnSizeChanged(e); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { ApplyRegion(); base.OnDpiChangedAfterParent(e); }

    void ApplyRegion()
    {
        if (Width <= 0 || Height <= 0) return;
        float s = DeviceDpi / 96f;
        int r = Math.Min((int)Math.Round(_cornerRadius * s), Math.Min(Width, Height) / 2);
        using var path = RoundedRect(0, 0, Width, Height, r);
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = DeviceDpi / 96f;
        int r = Math.Min((int)Math.Round(_cornerRadius * s), Math.Min(Width, Height) / 2);
        using var path = RoundedRect(0, 0, Width - 1, Height - 1, r);
        using var pen = new Pen(Theme.Divider, Math.Max(1f, s));
        g.DrawPath(pen, path);
    }

    /// <summary>圆角矩形路径：AddArc 四段弧，r 已被夹到合法范围，不会出现月牙缺陷。</summary>
    internal static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        if (w <= 0 || h <= 0) return path;
        r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2));
        if (r == 0) { path.AddRectangle(new Rectangle(x, y, w, h)); return path; }
        path.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        path.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }
}
