using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// 任务表格：在进度列（索引 2）底部画一条细进度条，同时保留 cell.Value 字符串不变
/// （测试 Snapshot/Progress 依赖 "45%" 文本）。继承 DataGridView，测试强转 (DataGridView) 安全。
/// 绘制顺序必须是「背景 → 自绘条 → 前景文字 → Handled」，不得在 Handled 之后再触发 e.Paint，
/// 否则同一区域会被重复绘制，双缓冲下表现为整列噪点。
/// </summary>
internal sealed class BufferedGrid : DataGridView
{
    const int ProgressColumn = 2;

    public BufferedGrid()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        if (e.Handled) return;                                   // 外部订阅者已完全接管
        if (e.RowIndex < 0 || e.ColumnIndex != ProgressColumn) return;
        if (!TryParsePercent(e.FormattedValue, out var value)) return;

        // 1) 只画背景（含选中态底色）
        e.PaintBackground(e.ClipBounds, true);

        // 2) 画进度条：贴在单元格底部，与垂直居中的百分比文字错开，不再互相压字
        var g = e.Graphics!;
        float s = DeviceDpi / 96f;
        var r = e.CellBounds;
        int barH = Math.Max(2, (int)Math.Round(3 * s));
        int inset = (int)Math.Round(5 * s);
        int gap = (int)Math.Round(3 * s);
        int trackW = Math.Max(0, r.Width - 2 * inset);
        if (trackW > 0)
        {
            var track = new Rectangle(r.X + inset, r.Bottom - barH - gap, trackW, barH);
            using (var bg = new SolidBrush(Theme.Divider))
                g.FillRectangle(bg, track);
            int filled = (int)Math.Round(track.Width * value / 100.0);
            if (filled > 0)
                using (var fg = new SolidBrush(value >= 100 ? Theme.Success : Theme.Accent))
                    g.FillRectangle(fg, new Rectangle(track.X, track.Y, filled, barH));
        }

        // 3) 画文字与边框，然后声明已处理
        e.Paint(e.ClipBounds, DataGridViewPaintParts.ContentForeground | DataGridViewPaintParts.Border);
        e.Handled = true;
    }

    static bool TryParsePercent(object? formatted, out double value)
    {
        value = 0;
        if (formatted is not string txt) return false;
        txt = txt.Trim();
        if (!txt.EndsWith('%')) return false;
        if (!double.TryParse(txt.TrimEnd('%'), NumberStyles.Float,
                             CultureInfo.InvariantCulture, out value)) return false;
        value = Math.Clamp(value, 0, 100);
        return true;
    }
}
