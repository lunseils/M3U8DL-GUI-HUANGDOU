using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// 卡片阴影宿主：作为 CardPanel 的父容器，在自身背景上画偏移淡色圆角底，
/// 模拟 0 2px 8px rgba(17,24,39,.06) 的轻量投影（零 GDI Interop、DPI 安全）。
/// 阴影绝不能画进 CardPanel.OnPaint —— 会被其 Region 裁掉。
/// </summary>
internal sealed class ShadowHost : Panel
{
    int _cornerRadius = 14;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius == value) return; _cornerRadius = Math.Max(0, value); Invalidate(); }
    }

    public ShadowHost()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint, true);
        // 阴影是自绘的：尺寸变化必须整块重画，否则旧阴影轮廓会残留在原位
        ResizeRedraw = true;
        BackColor = Theme.Canvas;
        Margin = new Padding(0, 0, 8, 8);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 0 || Height <= 0) return;
        float s = DeviceDpi / 96f;
        int r = Math.Min((int)Math.Round(_cornerRadius * s), Math.Min(Width, Height) / 2);
        int off = Math.Max(1, (int)Math.Round(2 * s));
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // 下移 2px 的淡色圆角底作为阴影
        using var path = CardPanel.RoundedRect(0, off, Width - 1, Height - 1 - off, r);
        using var brush = new SolidBrush(Theme.Divider);
        g.FillPath(brush, path);
    }
}
