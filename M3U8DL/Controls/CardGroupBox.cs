using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace M3U8DL;

/// <summary>
/// 圆角卡片式 GroupBox：继承 GroupBox 以保持 as GroupBox / Text 反射与布局断言契约
/// （LayoutTests 用 txtCookie.Parent.Parent as GroupBox 且按 Text 查找）。
/// 用 UserPaint 自绘现代圆角卡片外观，替代系统默认的方框标题样式。
/// DisplayRectangle 由 GroupBox 基类按 Font/Padding 计算，无需自绘参与。
/// <para>
/// 圆角必须靠 <see cref="Control.Region"/> 裁出来，只画圆角路径是不够的：
/// UserPaint 模式下 WM_ERASEBKGND 仍会先用 BackColor 填满整个客户矩形，
/// 于是四角那 4 小块「圆角之外」的区域留成白色 —— 落在 #F5F6FA 画布上就是四个刺眼的白色直角凸起。
/// 这里与 CardPanel 用同一套 Region 方案（同样必须在尺寸 / DPI 变化时重算并 Dispose 旧 Region）。
/// </para>
/// </summary>
internal sealed class CardGroupBox : GroupBox
{
    // WFO1000：自定义 Control 的公开可写属性必须隐藏序列化，避免设计器序列化告警。
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius { get; set; } = 12;

    public CardGroupBox()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        ResizeRedraw = true; // 自绘圆角边框，尺寸变化需整块重画，否则旧边线残留
        BackColor = Theme.Surface;
        ForeColor = Theme.TextPrimary;
        Font = Theme.Fonts.Body;
        Padding = new Padding(10, 2, 10, 6);
    }

    protected override void OnLayout(LayoutEventArgs e) { ApplyRegion(); base.OnLayout(e); }
    protected override void OnSizeChanged(EventArgs e) { ApplyRegion(); base.OnSizeChanged(e); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { ApplyRegion(); base.OnDpiChangedAfterParent(e); }

    int ScaledRadius()
    {
        float s = DeviceDpi / 96f;
        return Math.Max(0, (int)Math.Round(CornerRadius * s));
    }

    void ApplyRegion()
    {
        // 首次 OnLayout 时 ClientSize 可能仍是 0：此时设 Region 会把控件整个裁没
        if (Width <= 0 || Height <= 0) return;
        int r = Math.Min(ScaledRadius(), Math.Min(Width, Height) / 2);
        using var path = CardPanel.RoundedRect(0, 0, Width, Height, r);
        var old = Region;
        Region = new Region(path);
        old?.Dispose(); // 不 Dispose 会随每次 Resize 泄漏 GDI 句柄
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics!;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int r = Math.Min(ScaledRadius(), Math.Min(Width, Height) / 2);
        using (var path = CardPanel.RoundedRect(0, 0, Width - 1, Height - 1, r))
        using (var brush = new SolidBrush(BackColor))
        using (var pen = new Pen(Theme.Border))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(
                g, Text, Font, new Point(10, 2), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }
}
