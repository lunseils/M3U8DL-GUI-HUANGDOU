using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// iOS 风格开关。必须继承 CheckBox（不是 Control）：
/// Tests.cs:792 把 chk 字典强转为 Dictionary&lt;string, CheckBox&gt; 并读写 .Checked。
/// 复用基类 Checked/Space 键/无障碍，本类只覆盖 OnPaint 画胶囊，不新增公开可写属性（规避 WFO1000）。
/// </summary>
internal sealed class ToggleSwitch : CheckBox
{
    // 逻辑尺寸（96 DPI 基准）。40×24 → 宽高比 1.67，与 iOS UISwitch 的 51×31（1.65）一致；
    // 高度 24 对比 9pt 正文行高 17px 约为 1.41，也正好是 iOS「开关高 / 正文行高」的比例。
    const int LogicalW = 40;
    const int LogicalH = 24;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        // 控件矩形只容纳开关本体，绝不承载文字（文字由外部独立 Label 绘制）。
        // 显式设定 DPI 缩放后的固定尺寸，避免继承 CheckBox 的默认宽度吞噬右侧 Label。
        Size = ScaledSize();
        BackColor = Color.Transparent;
        FlatStyle = FlatStyle.Flat;
        Appearance = Appearance.Normal;
        Text = "";                       // 永不绘制基类文字：轨道/滑块由 OnPaint 全权负责
        Margin = Padding.Empty;
        Cursor = Cursors.Hand;
    }

    Size ScaledSize()
    {
        float s = DeviceDpi / 96f;
        return new Size(
            Math.Max(1, (int)Math.Round(LogicalW * s)),
            Math.Max(1, (int)Math.Round(LogicalH * s)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        // 不调用 base.OnPaint —— 否则会画出原生复选框方框
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // 关键：先用父容器底色铺满整块区域。
        // 本控件是透明背景 + 自绘抗锯齿图形，若不铺底，胶囊边缘那些半透明像素
        // 会和双缓冲里的残留内容混合，屏幕上就是一整圈黑/灰脏边（"背景不干净"的根因）。
        g.Clear(EffectiveBackColor());

        float s = DeviceDpi / 96f;
        int w = Math.Max(1, (int)Math.Round(LogicalW * s));
        int h = Math.Max(1, (int)Math.Round(LogicalH * s));
        int trackW = Math.Min(w, ClientRectangle.Width);
        int trackH = Math.Min(h, ClientRectangle.Height);
        if (trackW <= 0 || trackH <= 0) return;

        var trackColor = !Enabled ? Theme.AccentDisabled : (Checked ? Theme.Accent : Theme.Dash);

        // 胶囊 = 中间全高矩形 ∪ 左右两个整圆。
        // 三条子路径塞进同一个 GraphicsPath 后一次性 FillPath：并集只做一遍抗锯齿，
        // 不会在「矩形与圆的交界」处留下色差接缝（分开画三次就会有三条缝）。
        // FillMode 必须是 Winding：默认的 Alternate 会把矩形与圆的重叠区域挖成洞。
        using (var capsule = new GraphicsPath(FillMode.Winding))
        using (var track = new SolidBrush(trackColor))
        {
            int midW = Math.Max(0, trackW - h);
            if (midW > 0) capsule.AddRectangle(new Rectangle(h / 2, 0, midW, trackH));
            capsule.AddEllipse(0, 0, h, h);
            capsule.AddEllipse(trackW - h, 0, h, h);
            g.FillPath(track, capsule);
        }

        // 滑块：纯白圆，不画阴影。
        // 原来那层 FromArgb(60,0,0,0) 阴影在 18px 直径上只会沿轨道糊出一圈发暗的脏边。
        int pad = Math.Max(1, (int)Math.Round(2 * s));
        int d = Math.Max(1, trackH - 2 * pad);
        int x = Checked ? trackW - d - pad : pad;
        int y = Math.Max(0, (trackH - d) / 2);
        using (var thumb = new SolidBrush(Color.White))
            g.FillEllipse(thumb, x, y, d, d);

        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, ClientRectangle);
    }

    /// <summary>向上找第一个不透明祖先的底色，作为抗锯齿边缘的混合底色。</summary>
    Color EffectiveBackColor()
    {
        for (var c = Parent; c is not null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return Theme.Surface;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        // 保持逻辑尺寸比例，DPI 变化时重算
        var desired = ScaledSize();
        if (desired.Width > 0 && desired.Height > 0 && (Width != desired.Width || Height != desired.Height))
            Size = desired;
        base.OnSizeChanged(e);
    }
}
