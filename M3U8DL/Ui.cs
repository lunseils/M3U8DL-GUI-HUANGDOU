namespace M3U8DL;

/// <summary>配色与控件外观（对齐原 PyQt 主题）。</summary>
internal static class Theme
{
    public static readonly Color Bg = Html("#F4F6FA");
    public static readonly Color Border = Html("#E1E5EC");
    public static readonly Color Text = Html("#1F2937");
    public static readonly Color Muted = Html("#6B7280");
    public static readonly Color Primary = Html("#2D7FF9");
    public static readonly Color Danger = Html("#EF4444");
    public static readonly Color Done = Html("#10B981");
    public static readonly Color Cancel = Html("#9CA3AF");
    public static readonly Color Size = Html("#0F766E");
    public static readonly Color FieldBorder = Html("#D8DEE7");
    public static readonly Color Hover = Html("#E2E8F2");
    public static readonly Color Pressed = Html("#D4DCEA");

    // ---- S4 新设计 token（PRD §3.1）----
    public static readonly Color Canvas = Html("#F5F6FA");
    public static readonly Color Surface = Html("#FFFFFF");
    public static readonly Color Chrome = Html("#FAFBFD");
    public static readonly Color Accent = Html("#5B6EE8");
    public static readonly Color AccentHover = Html("#4F61D4");
    public static readonly Color AccentPressed = Html("#4556C4");
    public static readonly Color AccentDisabled = Html("#B9C0EE");
    public static readonly Color AccentSoft = Html("#EEF0FF");
    public static readonly Color TextPrimary = Html("#1F2937");
    public static readonly Color TextSecondary = Html("#6B7280");
    public static readonly Color TextMuted = Html("#9CA3AF");
    public static readonly Color Divider = Html("#ECEEF5");
    public static readonly Color Dash = Html("#CBD3E8");
    public static readonly Color FieldBgNew = Html("#F8F9FC");
    public static readonly Color FieldLine = Html("#E3E7F0");
    public static readonly Color Success = Html("#10B981");
    public static readonly Color DotOn = Html("#22C55E");
    public static readonly Color DotOff = Html("#CBD5E1");
    public static readonly Color Ghost = Html("#F3F4F8");
    public static readonly Color GhostHover = Html("#E9ECF4");
    // 危险动作的浅底（悬停用）：与 AccentSoft 同角色，但偏红，避免「清空」类按钮悬停时发蓝
    public static readonly Color DangerSoft = Html("#FEF2F2");
    /// <summary>运行日志的背景：比纯白略偏冷，长时间阅读不刺眼。</summary>
    public static readonly Color LogBg = Html("#FBFCFE");

    static Color Html(string hex) => ColorTranslator.FromHtml(hex);

    /// <summary>统一字体层级（PRD §3.3 / 架构 §2.7）。缓存实例避免每次 new Font 泄漏 GDI 句柄。</summary>
    public static class Fonts
    {
        public static readonly Font Caption = new("Microsoft YaHei UI", 8F);
        public static readonly Font Body = new("Microsoft YaHei UI", 9F);
        public static readonly Font Subtitle = new("Microsoft YaHei UI", 11F, FontStyle.Bold);
        public static readonly Font Title = new("Microsoft YaHei UI", 14F, FontStyle.Bold);
        public static readonly Font Mono = new("Consolas", 10F);
    }

    public static Button Button(string text, Color? fill = null, Color? fore = null, bool autoSize = true)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = autoSize,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand,
            BackColor = fill ?? Html("#EEF2F8"),
            ForeColor = fore ?? Text,
            Font = new Font("Microsoft YaHei UI", 9F),
            Padding = new Padding(6, 0, 6, 0),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = FieldBorder;
        b.FlatAppearance.MouseOverBackColor = Hover;
        b.FlatAppearance.MouseDownBackColor = Pressed;
        if (fill is not null)
        {
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(fill.Value, 0.12f);
            b.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(fill.Value, 0.12f);
        }
        return b;
    }

    /// <summary>文本框（只读态、子控件样式都由调用方另设，这里只统一外观）。</summary>
    public static TextBox Field()
    {
        var t = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = Text,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F),
        };
        return t;
    }

    /// <summary>下拉框（只读选择样式，与 Field 同风格）。</summary>
    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F),
            Margin = new Padding(0),
            FlatStyle = FlatStyle.Standard,
        };
        foreach (var it in items) c.Items.Add(it);
        if (c.Items.Count > 0) c.SelectedIndex = 0;
        return c;
    }
}

/// <summary>极简输入框（重命名任务用）。</summary>
internal static class PromptDialog
{
    public static string? Ask(IWin32Window? owner, string prompt, string title, string initial = "")
    {
        using var f = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(430, 130),
            Font = new Font("Microsoft YaHei UI", 9F),
            BackColor = Theme.Bg,
        };
        var lbl = new Label { Text = prompt, Location = new Point(14, 16), Size = new Size(400, 20), ForeColor = Theme.Text };
        var tb = new TextBox { Text = initial, Location = new Point(14, 42), Width = 400 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(230, 78), Width = 86 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(326, 78), Width = 86 };
        f.Controls.AddRange([lbl, tb, ok, cancel]);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}
