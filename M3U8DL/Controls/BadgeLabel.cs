using System.Drawing;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>①②③ 编号圆章 / 计数胶囊：PrimarySoft 底 + Primary 字，自绘避免不同字体下基线不一致。</summary>
internal sealed class BadgeLabel : Label
{
    int _diameter = 22;
    bool _pill;

    public BadgeLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        AutoSize = false;
        BackColor = Color.Transparent;
        ForeColor = Theme.Accent;
        Font = Theme.Fonts.Caption;
        TextAlign = ContentAlignment.MiddleCenter;
        Size = new Size(_diameter, _diameter);
    }

    /// <summary>圆直径（逻辑像素）。WFO1000：隐藏序列化。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public int Diameter
    {
        get => _diameter;
        set { _diameter = Math.Max(12, value); Size = new Size(_diameter, _diameter); Invalidate(); }
    }

    /// <summary>胶囊模式（宽度按文字自适应），用于 CountBadge「N 个」。隐藏序列化。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    [System.ComponentModel.Browsable(false)]
    public bool Pill
    {
        get => _pill;
        set { _pill = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Theme.AccentSoft);
        if (_pill)
        {
            var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            using var path = CardPanel.RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, rect.Height / 2);
            g.FillPath(brush, path);
        }
        else
        {
            int d = Math.Min(Width - 1, Height - 1);
            g.FillEllipse(brush, (Width - d) / 2f, (Height - d) / 2f, d, d);
        }
        TextRenderer.DrawText(g, Text ?? "", Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}
