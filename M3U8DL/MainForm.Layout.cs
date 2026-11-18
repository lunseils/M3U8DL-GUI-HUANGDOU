using System.Drawing;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// MainForm 的纯 UI 构建部分（PRD §2 控件树 + §3 视觉规格）。
/// 关键：compatHost 常驻且构造期完成布局，内部严格满足 Tests.LayoutTests 的父子树断言：
///   txtCookieFile.Parent.Parent == optGroup(GroupBox)；
///   optGroup.DisplayRectangle 完整包含两个 Cookie 控件；
///   perfGroup(② 性能设置) 位于左栏且紧跟在 ① 下载选项之后。
/// 反射契约字段仍声明在主文件 MainForm.cs（partial 编译后同属一个类型）。
/// </summary>
internal sealed partial class MainForm : Form
{
    // ---- S4 新布局字段（跨方法访问）----
    readonly Panel settingsOverlay = new()
    {
        Dock = DockStyle.Fill,
        Visible = false,
        BackColor = Color.FromArgb(140, 31, 41, 55),
    };
    readonly CardPanel settingsCard = new() { CornerRadius = 14 };
    readonly Panel compatHost = new() { BackColor = Color.Transparent };

    // 这两个 GroupBox 必须是 compatHost 的直接子控件（布局断言 C）。
    // CardGroupBox 继承 GroupBox：as GroupBox / OfType<GroupBox> / Text 断言全部成立，外观为现代圆角卡片。
    readonly CardGroupBox optGroup = new() { Text = "基础选项" };
    // 序号圈与左栏「① 下载选项」用同一套字符（①、② 本身就是圈号字形），两张卡片并排时视觉一致
    readonly CardGroupBox perfGroup = new() { Text = "② 性能设置" };

    readonly Label lblStatus = new()
    {
        Text = "就绪",
        AutoSize = true,
        ForeColor = Theme.TextSecondary,
        Font = Theme.Fonts.Caption,
    };
    readonly Label dotYtDlp = MakeDot();
    readonly Label dotRe = MakeDot();
    readonly Label dotFfmpeg = MakeDot();

    readonly BadgeLabel badgeRight = new() { Pill = true, Diameter = 22 };
    readonly Panel emptyGridState = MakeEmptyState("还没有任务", "点工具栏「粘贴」，或把 .txt / .m3u8 拖进队列");
    // 「① 添加任务」卡片已整体取消（需求变更）：粘贴进右侧工具栏，选文件/手动输入进队列右键菜单。
    readonly CardGroupBox optsGroup = new() { Text = "① 下载选项" };

    // ============================================================
    // 界面搭建
    // ============================================================
    void BuildUi()
    {
        Text = "M3U8 批量下载器";
        BackColor = Theme.Canvas;

        // ---------- TopBar ----------
        var topBar = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Surface };
        var pbIcon = new PictureBox { Size = new Size(32, 32), Location = new Point(20, 16), SizeMode = PictureBoxSizeMode.StretchImage };
        try
        {
            // 单文件发布时同目录没有 app.ico，退回从 exe 内嵌资源提取，标题栏/左上角才不会是空白
            var icon = AppIcon();
            if (icon is not null) pbIcon.Image = icon.ToBitmap();
        }
        catch { }
        var titleBlock = new Panel { Location = new Point(60, 12), Size = new Size(300, 44) };
        titleBlock.Controls.Add(new Label
        {
            Text = "M3U8 批量下载器", AutoSize = true, Location = new Point(0, 0),
            Font = Theme.Fonts.Title, ForeColor = Theme.TextPrimary,
        });
        titleBlock.Controls.Add(new Label
        {
            Text = "粘贴链接，批量下载", AutoSize = true, Location = new Point(0, 26),
            Font = Theme.Fonts.Caption, ForeColor = Theme.TextMuted,
        });
        var shortcut = new Label
        {
            Text = "Ctrl+V 粘贴 · Ctrl+Shift+C 复制", AutoSize = true,
            Font = Theme.Fonts.Caption, ForeColor = Theme.TextMuted, Margin = Padding.Empty,
        };
        // 右上角唯一的设置入口：齿轮图标。原来左栏卡片底部还有个「更多设置 ▸」链接，
        // 与这里重复且藏在滚动区域里不易发现，已移除。
        // 用 Segoe MDL2 Assets 的 U+E713（Settings）而非「⚙」：后者是 emoji 变体，
        // 实测渲染成细线小圈且偏上不居中。
        var btnSettings = new Button
        {
            Text = "\uE713", AutoSize = false, Size = new Size(36, 32), FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface, ForeColor = Theme.TextSecondary,
            Font = new Font("Segoe MDL2 Assets", 15F), Cursor = Cursors.Hand,
        };
        btnSettings.FlatAppearance.BorderSize = 0;
        btnSettings.FlatAppearance.MouseOverBackColor = Theme.GhostHover;
        btnSettings.FlatAppearance.MouseDownBackColor = Theme.Ghost;
        btnSettings.Click += (_, _) => OpenSettings();
        var settingsTip = new ToolTip();
        settingsTip.SetToolTip(btnSettings, "设置：性能 / 合并 / 命名 / 网络");
        // 右对齐的工具区（从右往左依次放置）
        var rightActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8, 16, 14, 0), WrapContents = false,
        };
        rightActions.Controls.Add(btnSettings);
        // 快捷键提示：FlowLayoutPanel + AutoSize 才能让 Dock=Right 的宽度跟着文字走
        //（普通 Panel 的 AutoSize 在 Dock 模式下不会收缩宽度）
        var shortcutHost = new FlowLayoutPanel
        {
            Dock = DockStyle.Right, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
            Padding = new Padding(0, 24, 6, 0),
        };
        shortcut.Dock = DockStyle.Fill;
        shortcutHost.Controls.Add(shortcut);
        topBar.Controls.Add(pbIcon);
        topBar.Controls.Add(titleBlock);
        topBar.Controls.Add(shortcutHost);
        topBar.Controls.Add(rightActions); // 后加 → 最先停靠 → 齿轮独占最右，快捷键提示落在它左边

        // ---------- Body ----------
        var bodyHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas, Padding = new Padding(20, 16, 20, 12) };

        // ---- 左栏（固定 300）：① 下载选项 + ② 性能设置，两张卡片都按内容定高 ----
        // 需求变更：原「① 添加任务」卡片整体取消 —— 粘贴按钮移入右栏工具栏，
        // 选文件 / 手动输入收进队列右键菜单，拖放改为直接拖到队列表格上；
        // 「性能设置」从设置浮层移到这里常驻，最常改的参数不再需要「齿轮 → 滚动 → 找」。
        // 高度策略（本轮）：两张卡片都不再吃满剩余高度，卡片高度 = 行数 × 固定行高 + 标题区，
        // 剩余高度全部交给第 3 行空白。此前的 Percent 行会把 6 行开关均分到整张卡片上，
        // 窗口一拉高行距就被拉开到 60px+，开关之间空得能塞下一行字。行高定死后才紧凑。
        // 注意：leftRows 必须 Dock=Fill 且 AutoSize=false —— AutoSize 的 TLP 会把 Percent 行压成内容高度。
        var leftCol = new Panel { Dock = DockStyle.Left, Width = 300, BackColor = Theme.Canvas, Padding = new Padding(0, 0, 8, 0) };
        var leftRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = false,
            Padding = new Padding(0), Margin = new Padding(0),
        };
        // 行顺序即视觉顺序（TLP 按 row 索引从上到下，与 z-order 无关）。
        // 前两行高度在卡片构造完、量出内容高度后回填（见下方 RowStyles[n] = ...），
        // 这样 DPI 变化时不必再维护一组魔法常量。
        leftRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));  // ① 下载选项
        leftRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 0));  // ② 性能设置
        leftRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // 剩余空间：卡片永远贴顶

        float dpi = DeviceDpi / 96f;
        // 开关行高 34（逻辑像素）：开关本体高 22，上下各留 6 的呼吸，比 36 更紧凑，
        // 又不会让「仅解析不下载」这种两行文字的描述贴到相邻条目上。
        var toggleRowH = (int)Math.Round(34 * dpi);
        var perfRowH = (int)Math.Round(30 * dpi);

        // ① 下载选项（6 个高频开关）：固定行高 + 行间 1px 分隔线 = 系统设置式的条目列表。
        optsGroup.Dock = DockStyle.Fill;
        optsGroup.Margin = new Padding(0, 0, 0, 10);
        optsGroup.Padding = new Padding(12, 6, 12, 8);
        // 两列布局：第 0 列只放 ToggleSwitch（控件矩形=开关本体），第 1 列放独立 Label。
        // 文字位置由列宽决定而非硬编码坐标，任何 DPI 缩放下开关都不会遮挡文字。
        var toggleHost = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2, RowCount = 11,
            BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        toggleHost.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)Math.Round(48 * dpi)));
        toggleHost.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (var r = 0; r < 11; r++)
        {
            if (r % 2 == 1)
            {
                toggleHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));
                var line = new Panel { Dock = DockStyle.Fill, BackColor = Theme.FieldLine, Margin = Padding.Empty };
                toggleHost.Controls.Add(line, 0, r);
                toggleHost.SetColumnSpan(line, 2);
            }
            else toggleHost.RowStyles.Add(new RowStyle(SizeType.Absolute, toggleRowH));
        }
        AddToggle(toggleHost, "useYtDlp", "使用 yt-dlp 引擎", true, 0);
        AddToggle(toggleHost, "skipExisting", "跳过已下载成品", true, 2);
        AddToggle(toggleHost, "concurrentDownload", "并发下载音视频", true, 4);
        AddToggle(toggleHost, "noSystemProxy", "不使用系统代理", true, 6);
        AddToggle(toggleHost, "onlyParseM3u8", "仅解析不下载", false, 8);
        // 批次结束后自动把「失败」任务重置并按轮数重跑；轮数就在下方「② 性能设置」最后一格（0 = 关闭）
        AddToggle(toggleHost, "autoRetryFailed", "失败自动重试", true, 10);
        // Dock=Top 的控件高度不会自己收缩到内容高度，显式给出行高之和，卡片高度才能算准
        toggleHost.Height = 6 * toggleRowH + 5;
        optsGroup.Controls.Add(toggleHost);
        // 卡片高度 = 标题占位（DisplayRectangle.Top，由字体高度决定）+ 内容高度 + 底部内边距
        optsGroup.Height = optsGroup.DisplayRectangle.Top + toggleHost.Height + optsGroup.Padding.Bottom;
        leftRows.RowStyles[0] = new RowStyle(SizeType.Absolute, optsGroup.Height + optsGroup.Margin.Bottom);
        leftRows.Controls.Add(optsGroup, 0, 0);

        // ② 性能设置：单列 6 行（标签 + 数字框）。
        // 标签列定宽 + AutoSize=false：原来标签是 AutoSize，最后一格「自动重试轮数：」比列宽大出几像素，
        // 表格为了容纳它把这一行整体往下挤，结果 6 个输入框有一段错位、标签跑到输入框下方。
        perfGroup.Dock = DockStyle.Fill;
        perfGroup.Margin = new Padding(0);
        perfGroup.Padding = new Padding(12, 6, 12, 8);
        var perfLayout = new TableLayoutPanel
        {
            ColumnCount = 2, RowCount = 6, Dock = DockStyle.Top,
            Height = 6 * perfRowH, Margin = Padding.Empty, Padding = Padding.Empty,
        };
        perfLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (int)Math.Round(108 * dpi)));
        perfLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        for (var i = 0; i < 6; i++) perfLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, perfRowH));
        void AddPerfRow(int row, string label, NumericUpDown nud)
        {
            var lbl = MakeLabel(label);
            // 标签撑满自己的单元格并左对齐：文字宽度不再影响列宽，6 行的输入框左边缘严格成一条竖线
            lbl.AutoSize = false;
            lbl.Dock = DockStyle.Fill;
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            lbl.Margin = new Padding(2, 0, 6, 0);
            perfLayout.Controls.Add(lbl, 0, row);
            // Anchor 只留 Left|Right：宽度跟随单元格，垂直方向由 TLP 居中。
            // 用 Dock=Fill 不行 —— UpDownBase 会把自己的高度强行改回 PreferredHeight(22)，
            // 高度一变它就贴到行顶部，与居中后的标签差 4px（截图上看着就是"标签偏上、框偏下"）。
            nud.Dock = DockStyle.None;
            nud.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            nud.Margin = new Padding(0);
            perfLayout.Controls.Add(nud, 1, row);
        }
        AddPerfRow(0, "同时下载数：", nudConc);
        AddPerfRow(1, "下载线程数：", nudThreads);
        AddPerfRow(2, "重试次数：", nudRetry);
        AddPerfRow(3, "HTTP超时(s)：", nudTimeout);
        AddPerfRow(4, "限速(kb/s)：", nudSpeed);
        AddPerfRow(5, "自动重试轮数：", nudAutoRetry);
        perfGroup.Controls.Add(perfLayout);
        perfGroup.Height = perfGroup.DisplayRectangle.Top + perfLayout.Height + perfGroup.Padding.Bottom;
        leftRows.RowStyles[1] = new RowStyle(SizeType.Absolute, perfGroup.Height);
        leftRows.Controls.Add(perfGroup, 0, 1);

        // 主色双态按钮（开始 / 停止）—— 已移到「③ 下载队列」工具栏最右（见下方 toolBar）：
        // 主操作跟着它作用的对象（队列）走，不再孤立地压在左栏底部。这里只做外观初始化。
        btnStart.Dock = DockStyle.Fill;
        btnStart.Text = "开始下载";
        btnStart.BackColor = Theme.Accent;
        btnStart.FlatAppearance.BorderSize = 0;
        btnStart.FlatAppearance.MouseOverBackColor = Theme.AccentHover;
        btnStart.FlatAppearance.MouseDownBackColor = Theme.AccentPressed;
        btnStart.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
        btnStart.Click += (_, _) => { if (batchRunning) StopBatch(); else StartBatch(); };

        // 「清空全部任务」已并入右侧工具栏按钮流（见 toolBar），左栏不再挂文字链接。

        leftCol.Controls.Add(leftRows);
        // 注意：leftCol 的挂载必须放在 rightCol(Fill) 之后，见 bodyHost.Controls.Add 处注释。

        // ---- 右栏（弹性）：用 TableLayoutPanel 分三行（头+工具栏 / 队列flex / 日志）----
        var rightCol = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Canvas };
        var rightRows = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(0), Margin = new Padding(0),
        };
        rightRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));  // sectionHeader + toolBar
        rightRows.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 队列
        rightRows.RowStyles.Add(new RowStyle(SizeType.Absolute, 176)); // 日志 + about

        // 顶部：标题 + 工具栏装在一个 Panel 里用 Dock 堆叠（后加 Top 在上）
        var headerHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var toolBar = new Panel { Dock = DockStyle.Top, Height = 44 };
        var toolFlow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        // 粘贴排在第一位（需求：「粘贴」移到「复制选中」之前，原「① 添加任务」卡片取消后
        // 粘贴升格为工具栏最左的主入口）；后面依次是复制 / 重试 / 删除 / 定位 / 打开目录 / 清空。
        toolFlow.Controls.Add(MakeGhostButton("粘贴", () => PasteTasks()));
        toolFlow.Controls.Add(MakeGhostButton("复制选中", () => CopySelection()));
        toolFlow.Controls.Add(MakeGhostButton("重试失败", () => RetryFailed()));
        toolFlow.Controls.Add(MakeGhostButton("删除选中", () => DeleteSelected()));
        toolFlow.Controls.Add(MakeGhostButton("定位文件", () => LocateTaskFile()));
        // 「打开目录」紧跟在「定位文件」后面，且与前面四个同为 Ghost 样式：
        // 五个按钮用同一套 AutoSize 尺寸才整齐（原来是 96×32 的实心 Accent 孤块，比旁边大一截）。
        // 主色强调位让给右侧的「开始下载」——一个工具栏里只留一个主按钮。
        toolFlow.Controls.Add(MakeGhostButton("打开目录", () => OpenSaveDir()));
        // 「清空全部任务」并入同一行：原本是左栏底部一个红色文字链接，位置离队列很远、
        // 又是纯文字（不可点区域只有几个像素高），既难找也容易误触。
        // 现在做成与前五个同尺寸的描边按钮，靠 12px 的额外左间距与浏览类动作隔开，
        // 文字保持危险色，避免它在视觉上和「删除选中」混为一谈。
        var btnClearAll = MakeGhostButton("清空全部任务", () => ClearAll());
        btnClearAll.ForeColor = Theme.Danger;
        btnClearAll.Margin = new Padding(12, 0, 6, 0);
        btnClearAll.FlatAppearance.MouseOverBackColor = Theme.DangerSoft;
        var clearAllTip = new ToolTip();
        clearAllTip.SetToolTip(btnClearAll, "清空队列中的所有任务（下载进行中会先确认，并中止正在下载的任务）");
        toolFlow.Controls.Add(btnClearAll);
        // 六个按钮宽度统一到最大者：「清空全部任务」6 个字，AutoSize 会比其余宽 17px，
        // 一行里长短不齐看起来像没对齐。统一成栅格后间距均匀（右侧还留有 ~60px 余量，
        // 最小窗口 1136 宽下也不会挤到「开始下载」）。
        var cellW = toolFlow.Controls.Cast<Button>().Max(b => b.PreferredSize.Width);
        var cellH = toolFlow.Controls.Cast<Button>().Max(b => b.PreferredSize.Height);
        foreach (Button b in toolFlow.Controls)
        {
            // AutoSize=false 后 Height 会掉回 Button 默认的 23，必须显式补回 29，
            // 否则整行比「开始下载」矮 6px、中心线也差 3px。
            b.AutoSize = false;
            b.Width = cellW;
            b.Height = cellH;
        }
        toolBar.Controls.Add(toolFlow);

        // 开始/停止：停在工具栏最右，主色实心，与队列区同屏
        var startHost = new Panel
        {
            Dock = DockStyle.Right, Width = 140,
            Padding = new Padding(8, 3, 0, 7), BackColor = Color.Transparent,
            Margin = new Padding(0, 6, 0, 0),
        };
        startHost.Controls.Add(btnStart);
        toolBar.Controls.Add(startHost); // 后加 → 先停靠，独占最右
        var sectionHeader = new Panel { Dock = DockStyle.Top, Height = 40 };
        var badge3 = new BadgeLabel { Diameter = 26, Text = "③", Location = new Point(0, 6) };
        sectionHeader.Controls.Add(badge3);
        sectionHeader.Controls.Add(new Label
        {
            Text = "下载队列", AutoSize = true, Location = new Point(34, 8),
            Font = Theme.Fonts.Subtitle, ForeColor = Theme.TextPrimary,
        });
        badgeRight.Location = new Point(110, 9);
        sectionHeader.Controls.Add(badgeRight);
        headerHost.Controls.Add(toolBar);       // 先加：Displayed 下方
        headerHost.Controls.Add(sectionHeader); // 后加 Dock=Top：排最上
        rightRows.Controls.Add(headerHost, 0, 0);

        // 队列卡片（flex）：Dock=Fill 的 ShadowHost 撑满整行
        var queueCard = NewCard("", 0, out var queueBody, showHeader: false);
        queueCard.Margin = new Padding(0, 4, 0, 4);
        SetupGrid();
        queueBody.Controls.Add(emptyGridState); // 先加在底层
        queueBody.Controls.Add(grid);           // 后加 Dock=Fill 覆盖其上（grid 有透明背景？否 → 需空态在上）
        // 让空态覆盖在 grid 上方
        emptyGridState.BringToFront();
        rightRows.Controls.Add(queueCard, 0, 1);

        // 日志卡片：占满底部整块高度。
        // 原来卡片下方还挂着一个「N_m3u8DL-RE」文字链接，和状态栏右下角的同名依赖标签重复，
        // 且它挤掉了日志可视高度。现在链接语义合并到底边那个标签上（点它开 RE 发布页），
        // 这里只留卡片本身，日志区多出一行可读空间。
        var bottomHost = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
        var logCard = NewCard("运行日志", 136, out var logBody);
        logCard.Dock = DockStyle.Fill;
        logCard.Margin = new Padding(0);
        txtLog.BorderStyle = BorderStyle.None;
        txtLog.BackColor = Theme.Surface;
        logBody.Controls.Add(txtLog);
        bottomHost.Controls.Add(logCard);
        rightRows.Controls.Add(bottomHost, 0, 2);

        rightCol.Controls.Add(rightRows);
        // WinForms 按「后添加先停靠」的顺序处理 Dock：Fill 面板必须最先加入，
        // 否则它会独占整个客户区，紧随其后的 Dock=Left 左栏将叠在它上面，
        // 把右侧表格左侧约 280px（任务名称列与列头）整块盖住。
        bodyHost.Controls.Add(rightCol);   // 先加：Fill 停靠时占据左栏之外的全部宽度
        bodyHost.Controls.Add(leftCol);    // 后加：Left 停靠先定位，宽度 280
        Controls.Add(bodyHost);
        // 顶栏必须挂在 bodyHost(Fill) 之后、statusBar 之前：
        // 后添加的先停靠，这样 Top/Bottom 先各占一条，Fill 只吃中间剩余区域。
        // （此前 topBar 构造完却从未 Controls.Add，导致标题区、快捷键提示与右上角设置按钮全都不显示，
        // 用户只能看到左栏那个「更多设置 ▸」链接。）
        Controls.Add(topBar);

        // ---------- StatusBar ----------
        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Chrome };
        statusBar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Divider });
        // 左侧：状态文字 + 并发数。并发放在这里是为了「下载中随时可调」——
        // 藏进设置浮层里要三步才能改，而这是最常临时调整的参数。
        var statusLeft = new FlowLayoutPanel
        {
            Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Padding = new Padding(20, 10, 0, 0), BackColor = Theme.Chrome,
        };
        lblStatus.Margin = new Padding(0, 2, 0, 0);
        statusLeft.Controls.Add(lblStatus);
        var concLabel = new Label
        {
            Text = "并发", AutoSize = true, Font = Theme.Fonts.Caption,
            ForeColor = Theme.TextMuted, Margin = new Padding(16, 3, 4, 0),
            Cursor = Cursors.Hand,
        };
        // 点标签即可聚焦输入框，鼠标不用瞄准那个 26px 宽的小方块
        concLabel.Click += (_, _) => nudConcInline.Focus();
        statusLeft.Controls.Add(concLabel);
        // 状态栏里只留数字：把 NumericUpDown 套进 28px 宽的灰色小胶囊，宽度设 44 让
        // 内部编辑框占满 28（44-16=28），右侧的上下微调按钮正好落在胶囊外面被裁掉。
        // 不能直接 Controls[0].Visible=false —— 隐藏后那块 16px 区域没人重绘，
        // 会残留窗口初始化时的蓝色按钮像素，状态栏上就是一个突兀的蓝方块。
        var concBox = new Panel
        {
            Size = new Size(28, nudConcInline.Height), BackColor = Theme.Ghost,
            Margin = new Padding(0, 1, 0, 0),
        };
        foreach (Control c in nudConcInline.Controls)
            if (c is TextBox tb)
            {
                tb.BackColor = Theme.Ghost;
                tb.ForeColor = Theme.TextPrimary;
                tb.BorderStyle = BorderStyle.None;
                tb.TextAlign = HorizontalAlignment.Center;
            }
        nudConcInline.Width = 44;
        nudConcInline.Location = Point.Empty;
        concBox.Controls.Add(nudConcInline);
        statusLeft.Controls.Add(concBox);
        var concTip = new ToolTip();
        concTip.SetToolTip(concBox, "同时下载的任务数（1–20），点数字可输入，下载中修改立即生效");
        statusBar.Controls.Add(statusLeft);
        var depFlow = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 20, 0), WrapContents = false };
        // FlowDirection.RightToLeft 下「先添加的排在最右」，所以这里必须倒序添加，
        // 否则视觉顺序会变成 yt-dlp → RE → FFmpeg，与声明顺序相反。
        depFlow.Controls.Add(DependencyChip("yt-dlp", dotYtDlp));
        // RE 的版本页链接就挂在底边这个标签上：日志区下方原来那个同名 LinkLabel 已删除，
        // 状态栏本来就是「依赖是否就绪」的信息位，点名字看发布页比再单开一行更自然。
        depFlow.Controls.Add(DependencyChip("N_m3u8DL-RE", dotRe,
            () => OpenUrl("https://github.com/nilaoda/N_m3u8DL-RE/releases")));
        depFlow.Controls.Add(DependencyChip("FFmpeg", dotFfmpeg));
        statusBar.Controls.Add(depFlow);

        Controls.Add(statusBar);

        // ---------- SettingsOverlay（覆盖式浮层）----------
        BuildSettingsOverlay();
        Controls.Add(settingsOverlay);

        // ---------- 事件接线 ----------
        // 并发两处入口双向同步：同步过程中不再互相触发，避免来回弹跳。
        void SyncConc(decimal v)
        {
            if (syncingConc) return;
            syncingConc = true;
            try
            {
                if (nudConcInline.Value != v) nudConcInline.Value = v;
                if (nudConc.Value != v) nudConc.Value = v;
                concurrentLimit = Math.Max(1, (int)v);
            }
            finally { syncingConc = false; }
        }
        nudConc.ValueChanged += (_, _) => SyncConc(nudConc.Value);
        nudConcInline.ValueChanged += (_, _) => SyncConc(nudConcInline.Value);
        grid.RowsAdded += (_, _) => RefreshEmptyState();
        grid.RowsRemoved += (_, _) => RefreshEmptyState();
        txtLog.ContextMenuStrip = TextMenu(txtLog, canClear: true);
        txtExe.ContextMenuStrip = TextMenu(txtExe);
        // 状态点跟着「RE 可执行」文本框走：构造期这里是空的，LoadSettings 才会填上探测结果，
        // 若不重新刷新，右下角会一直停在灰色「不可用」（明明已经探测到了）。
        txtExe.TextChanged += (_, _) => RefreshDependencyDots();
        txtWorkDir.ContextMenuStrip = TextMenu(txtWorkDir);
        txtMux.ContextMenuStrip = TextMenu(txtMux);
        grid.ContextMenuStrip = GridMenu();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape && settingsOverlay.Visible) { CloseSettings(); return; }
            if (!e.Control) return;
            if (e.KeyCode == Keys.V) { e.Handled = true; e.SuppressKeyPress = true; PasteTasks(); }
            else if (e.KeyCode == Keys.C && e.Shift) { e.Handled = true; e.SuppressKeyPress = true; CopySelection(); }
        };
        FormClosing += (_, e) =>
        {
            // 「关闭时最小化到托盘」：默认关闭，勾选后点 X 不退出，只收到托盘（下载继续跑）。
            if (!exiting && C("closeToTray") && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            exiting = true;
            batchRunning = false;
            engine.TerminateAll();
            FlushPendingSave(); // BUG-8：关闭前强制落盘，避免未提交的状态丢失
            SaveSettings();     // S6：退出时持久化选项
            DisposeTray();
        };
        // 整窗都是拖放入口（卡片取消后仍然能「拖文件进窗口」导入任务）
        EnableTaskDrop(this);

        // 构造期强制完成 compatHost 布局，保证布局断言读到的 Bounds 正确
        compatHost.CreateControl();
        compatHost.PerformLayout();
        RefreshEmptyState();
        RefreshDependencyDots();
    }

    /// <summary>
    /// Step 卡片：投影宿主 + 白色圆角 CardPanel + 标题 + 内容容器。
    /// height&gt;0 → Dock=Top 固定高度；height<=0 → Dock=Fill（队列区）。
    /// 注意：card 内先加 body(Fill) 再加 title(Top)，后加的 Dock=Top 才排在最上方。
    /// </summary>
    ShadowHost NewCard(string title, int height, out Panel body, bool showHeader = true)
    {
        var host = new ShadowHost { CornerRadius = 14 };
        if (height > 0) { host.Dock = DockStyle.Top; host.Height = height; }
        else host.Dock = DockStyle.Fill;

        var card = new CardPanel { CornerRadius = 14, Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };
        body = new Panel { Dock = DockStyle.Fill };
        card.Controls.Add(body);
        if (showHeader)
            card.Controls.Add(new Label
            {
                Text = title, AutoSize = true, Dock = DockStyle.Top,
                Font = Theme.Fonts.Subtitle, ForeColor = Theme.TextPrimary,
                Margin = new Padding(0, 0, 0, 6),
            });
        host.Controls.Add(card);
        return host;
    }

    /// <summary>
    /// 让控件接受 .txt / .m3u8 文件拖放。原「① 添加任务」卡片（含虚线拖放区）已取消，
    /// 拖放入口迁移到队列表格与窗体本身 —— 拖到窗口任何位置都能导入。
    /// </summary>
    void EnableTaskDrop(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        target.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files) return;
            ImportFiles(files);
        };
    }

    /// <summary>导入任务文件：.txt 逐行解析，.m3u8 直接作为一条任务。返回新增条数。</summary>
    int ImportFiles(IEnumerable<string> files)
    {
        var added = 0;
        foreach (var f in files)
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            try
            {
                if (ext == ".txt")
                {
                    var (n, _) = AddTaskLines(File.ReadAllText(f));
                    added += n;
                }
                else if (ext == ".m3u8")
                {
                    if (AddTask(Path.GetFileNameWithoutExtension(f), new Uri(f).AbsoluteUri, "待下载", [])) added++;
                }
            }
            catch (Exception e) { AppendLog($"⚠ 导入 {Path.GetFileName(f)} 失败：{e.Message}"); }
        }
        if (added > 0) AppendLog($"📥 已从文件导入 {added} 条任务");
        return added;
    }

    /// <summary>手动输入任务：原左栏「手动输入」按钮取消后改由队列右键菜单打开（多行输入框）。</summary>
    void ShowManualInput()
    {
        using var dlg = new Form
        {
            Text = "手动输入任务", StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(640, 400), Font = new Font("Microsoft YaHei UI", 9F),
            BackColor = Theme.Surface,
            FormBorderStyle = FormBorderStyle.Sizable,
        };
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(14, 10, 14, 0),
            ForeColor = Theme.TextSecondary, Font = Theme.Fonts.Body,
            Text = "每行一条：name|url|referer|ua|cookie|origin|x-cdn-auth（后面的字段可省略）",
        };
        var box = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle, Font = Theme.Fonts.Mono,
            BackColor = Theme.FieldBgNew, AcceptsReturn = true,
        };
        var boxHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 14, 4) };
        boxHost.Controls.Add(box);
        var buttonBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 12, 14, 10), WrapContents = false,
        };
        var ok = Theme.Button("添加任务", Theme.Accent, Color.White, autoSize: false);
        ok.Size = new Size(96, 30);
        var cancel = Theme.Button("取消", autoSize: false);
        cancel.Size = new Size(76, 30);
        cancel.Margin = new Padding(8, 0, 0, 0);
        cancel.DialogResult = DialogResult.Cancel;
        ok.Click += (_, _) => { dlg.DialogResult = DialogResult.OK; };
        buttonBar.Controls.Add(ok);
        buttonBar.Controls.Add(cancel);
        dlg.Controls.Add(boxHost);
        dlg.Controls.Add(buttonBar);
        dlg.Controls.Add(hint);
        dlg.CancelButton = cancel;   // 不设 AcceptButton：多行输入里 Enter 必须换行
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AddTasksFromText(box.Text);
    }

    /// <summary>
    /// 把多行任务文本加入队列（手动输入 / 拖放 .txt 共用）。
    /// 解析规则与粘贴完全一致（<see cref="AddTaskLines"/>），这里只补一条汇总日志。
    /// </summary>
    void AddTasksFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var (added, skipped) = AddTaskLines(text);
        if (added == 0) return;
        AppendLog($"✅ 已添加 {added} 条任务" + (skipped.Count > 0 ? $"（跳过 {skipped.Count} 行无效数据）" : ""));
    }

    void BrowseTaskFile()
    {
        using var dlg = new OpenFileDialog { Title = "选择任务清单", Filter = "文本文件 (*.txt;*.m3u8)|*.txt;*.m3u8|所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try { ImportFiles([dlg.FileName]); }
        catch (Exception e) { MessageBox.Show(this, $"读取文件失败：{e.Message}", "错误"); }
    }

    /// <summary>
    /// 覆盖式设置浮层：遮罩 + 居中圆角面板；compatHost 从构造起永久是浮层的子控件，
    /// 打开/关闭只切 SettingsOverlay.Visible，绝不 reparent（Controls.Add/Remove、Parent= 均不在开关路径）。
    /// 4 个分组绝对定位、纵向排列、组间距 6px、统一宽 700，总高 480：
    ///   1 基础选项 138 / 2 路径与依赖 112 / 3 通用 88 / 4 RE 引擎专用 124（常显，不再折叠）。
    /// 「性能设置」已移到主界面左栏常驻（需求：性能设置栏移到原下载选项的位置），此处不再重复。
    /// 所有分组 Padding 上边距 6：GroupBox 的 DisplayRectangle 已自动扣除标题高度。
    /// 隐藏容器里 Dock 不自动拉伸，故所有 GroupBox 显式 Size/Location。
    /// </summary>
    void BuildSettingsOverlay()
    {
        // 564 = 页头 48 + 内容 (8+480+8) + 20 余量：AutoScroll 只要差 1px 就会弹出竖向滚动条，
        // 那条滚动条会挤掉 17px 宽度，把「先关浏览器」之类的第三列文字顶出分组边框。
        settingsCard.Size = new Size(760, 564);
        settingsOverlay.Resize += (_, _) =>
            settingsCard.Location = new Point(
                Math.Max(0, (settingsOverlay.Width - settingsCard.Width) / 2),
                Math.Max(0, (settingsOverlay.Height - settingsCard.Height) / 2));
        settingsOverlay.Controls.Add(settingsCard);

        var header = new Panel { Dock = DockStyle.Top, Height = 48 };
        header.Controls.Add(new Label
        {
            Text = "设置", AutoSize = true, Location = new Point(20, 12),
            Font = Theme.Fonts.Title, ForeColor = Theme.TextPrimary,
        });
        var btnClose = new Button
        {
            Text = "关闭", AutoSize = false, Size = new Size(72, 30),
            FlatStyle = FlatStyle.Flat, BackColor = Theme.Ghost, ForeColor = Theme.TextPrimary,
            Location = new Point(760 - 92, 9), Cursor = Cursors.Hand,
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.Click += (_, _) => CloseSettings();
        header.Controls.Add(btnClose);

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface };
        // 与 bodyHost 同样的 Dock 顺序约束：Fill 面板必须最先挂载，否则它会独占卡片整个客户区，
        // 随后挂载的 Dock=Top 页头只能叠在它上面 —— 结果是 compatHost 的内容整体上移 48px，
        // 第一组标题被页头压住、首行文字被裁掉上半截。
        settingsCard.Controls.Add(scroll);
        settingsCard.Controls.Add(header);

        // compatHost 绝对定位其内 GroupBox，保证层级与相对位置稳定；显式尺寸避免隐藏态 Dock 停在 200。
        // 总高 480（138+112+88+124 + 3×6）；RE 组常显后不再有折叠/展开两套高度。
        const int ContentTotal = 480;
        compatHost.Size = new Size(700, ContentTotal);
        compatHost.Location = new Point(10, 8);
        scroll.Controls.Add(compatHost);

        // ---- 1) optGroup "基础选项"  Height=138 : Table(3,3,34) → Cookie 文件 @(0,1) / 浏览器 @(0,2) ----
        // 断言 A：txtCookie.Parent.Parent as GroupBox == optGroup（CardGroupBox 继承 GroupBox）。
        optGroup.Location = new Point(0, 0);
        optGroup.Size = new Size(700, 138);
        optGroup.Padding = new Padding(12, 6, 12, 6); // GroupBox 的 DisplayRectangle 已扣除标题高度，6px 足够
        var cookieLayout = Table(3, 3, 34);
        Cols(cookieLayout, (SizeType.Absolute, 110), (SizeType.Percent, 100), (SizeType.Absolute, 84));
        cookieLayout.Controls.Add(MakeLabel("Cookie 来源配置：文件与浏览器二选一"), 1, 0);
        cookieLayout.Controls.Add(MakeLabel("Cookie 文件："), 0, 1);
        txtCookieFile.PlaceholderText = "可选：cookies.txt（Netscape 格式）";
        cookieLayout.Controls.Add(txtCookieFile, 1, 1);
        var cookieBtn = Theme.Button("浏览…", autoSize: false);
        cookieBtn.Dock = DockStyle.Fill;
        cookieBtn.Click += (_, _) => BrowseCookieFile();
        cookieLayout.Controls.Add(cookieBtn, 2, 1);
        cookieLayout.Controls.Add(MakeLabel("浏览器 Cookie："), 0, 2);
        cookieLayout.Controls.Add(cmbCookieBrowser, 1, 2);
        // 第三列只有 84px 宽，长句会折行并溢出分组边框；Caption 8pt 下 5 个汉字 ≈ 55px
        var cookieTip = MakeLabel("先关浏览器");
        cookieTip.ForeColor = Theme.TextMuted;
        cookieLayout.Controls.Add(cookieTip, 2, 2);
        optGroup.Controls.Add(cookieLayout);
        compatHost.Controls.Add(optGroup);

        // ---- 2) pathGroup "路径与依赖"  Height=112 : 2 行（保存目录 / RE 可执行）----
        // 性能设置已经不在这里（移到主界面左栏），本组上移到第 2 位。
        var pathGroup = new GroupBox
        {
            Text = "路径与依赖", Location = new Point(0, 144), Size = new Size(700, 112),
            Padding = new Padding(12, 6, 12, 6), Font = Theme.Fonts.Body,
        };
        var pathLayout = Table(3, 2, 34);
        Cols(pathLayout, (SizeType.Absolute, 110), (SizeType.Percent, 100), (SizeType.Absolute, 84));
        AddPathRow(pathLayout, 0, "保存目录：", txtWorkDir, BrowseDir);
        AddPathRow(pathLayout, 1, "RE 可执行：", txtExe, BrowseExe);
        pathGroup.Controls.Add(pathLayout);
        compatHost.Controls.Add(pathGroup);

        // ---- 3) generalGroup "通用"  Height=88 : 勾选 5 个（含托盘最小化 2 项，可折行）----
        var generalGroup = new GroupBox
        {
            Text = "通用", Location = new Point(0, 262), Size = new Size(700, 88),
            Padding = new Padding(12, 6, 12, 6), Font = Theme.Fonts.Body,
        };
        var generalFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
        };
        AddChk(generalFlow, "cleanStaleParts", "跳过时清理 .part 残留", true);
        AddChk(generalFlow, "noMerge", "下载后不合并", false);
        AddChk(generalFlow, "alwaysOnTop", "置顶本窗口", false);
        // 托盘行为（默认只在最小化时收进托盘，关闭仍是退出，避免"点 X 程序还在跑"的意外）
        AddChk(generalFlow, "minimizeToTray", "最小化到托盘", true);
        AddChk(generalFlow, "closeToTray", "关闭时最小化到托盘", false);
        generalGroup.Controls.Add(generalFlow);
        compatHost.Controls.Add(generalGroup);

        // ---- 4) advGroup "RE 引擎专用"  Height=124 : 混流行 34 + 7 个勾选折成两行 ----
        // 仅关闭 yt-dlp 引擎（走 ReAsync）时被读取的选项。原先默认折叠、点「展开 ▾」才显示，
        // 但那 7 个勾选实际经常要改（合并后删除分片、检测分片数量…），折叠只是多点一步；
        // 现在去掉折叠键常显，浮层仍够放（内容总高 480，卡片 564）。
        var advGroup = new GroupBox
        {
            Text = "RE 引擎专用（关闭 yt-dlp 引擎时生效）",
            Location = new Point(0, 356), Size = new Size(700, 124),
            Padding = new Padding(12, 6, 12, 6), Font = Theme.Fonts.Body,
        };
        var muxLayout = Table(3, 1, 34);
        muxLayout.Dock = DockStyle.Top;
        muxLayout.Height = 34;
        Cols(muxLayout, (SizeType.Absolute, 110), (SizeType.Percent, 100), (SizeType.Absolute, 84));
        AddPathRow(muxLayout, 0, "混流文件：", txtMux, BrowseMux);
        var advFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true,
        };
        AddChk(advFlow, "delAfterMerge", "合并后删除分片", true);
        AddChk(advFlow, "noDateInName", "合并时不写入日期", true);
        AddChk(advFlow, "muxWhileDownload", "边下边看", false);
        AddChk(advFlow, "binaryMerge", "二进制合并", false);
        AddChk(advFlow, "noLog", "关闭日志输出", false);
        AddChk(advFlow, "checkSegmentsCount", "检测分片数量", true);
        AddChk(advFlow, "appendUrlParams", "URL 参数附加到分片", false);
        advGroup.Controls.Add(advFlow);
        advGroup.Controls.Add(muxLayout); // 后加的 Dock=Top 排最上
        compatHost.Controls.Add(advGroup);
    }

    void AddPathRow(TableLayoutPanel layout, int row, string label, TextBox box, Action browse)
    {
        layout.Controls.Add(MakeLabel(label), 0, row);
        layout.Controls.Add(box, 1, row);
        var btn = Theme.Button("浏览…", autoSize: false);
        btn.Dock = DockStyle.Fill;
        btn.Click += (_, _) => browse();
        layout.Controls.Add(btn, 2, row);
    }

    void OpenSettings()
    {
        // 打开设置时重扫一次 PATH：用户很可能刚装好 yt-dlp / FFmpeg 就进来核对状态点
        depsProbed = false;
        RefreshDependencyDots();
        settingsOverlay.Visible = true;
        settingsOverlay.BringToFront();
    }

    void CloseSettings()
    {
        settingsOverlay.Visible = false;
        SaveSettings(); // S6：关闭设置浮层即持久化当前选项
    }

    /// <summary>刷新表格空态与队列计数徽标。</summary>
    void RefreshEmptyState()
    {
        emptyGridState.Visible = grid.Rows.Count == 0;
        // 以表格行数为准：它就是队列的镜像，且只在 UI 线程读，不需要抢 queueLock
        badgeRight.Text = grid.Rows.Count + " 条";
    }

    /// <summary>
    /// 刷新底部依赖状态点。
    /// PATH 级探测（yt-dlp / ffmpeg）要读环境变量 + 两级注册表，只做一次并缓存，
    /// 否则 txtExe 每次按键都会连带重扫一遍；RE 路径只做 File.Exists，实时跟随输入。
    /// </summary>
    void RefreshDependencyDots()
    {
        if (!depsProbed)
        {
            ytDlpOnPath = OnPath("yt-dlp.exe") || OnPath("yt-dlp");
            ffmpegOnPath = OnPath("ffmpeg.exe");
            depsProbed = true;
        }
        dotYtDlp.ForeColor = ytDlpOnPath ? Theme.DotOn : Theme.DotOff;
        dotRe.ForeColor = File.Exists(txtExe.Text.Trim()) ? Theme.DotOn : Theme.DotOff;
        dotFfmpeg.ForeColor = ffmpegOnPath ? Theme.DotOn : Theme.DotOff;
    }

    /// <summary>
    /// 状态栏依赖标签：色点 + 名称。<paramref name="onClick"/> 非空时名称变成文字链接
    /// （下划线 + 手形光标），用于「N_m3u8DL-RE → 发布页」这类外链。
    /// </summary>
    Control DependencyChip(string text, Label dot, Action? onClick = null)
    {
        var chip = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(0, 0, 12, 0) };
        dot.Text = "●";
        // 圆点字号必须跟旁边 8pt 的 Caption 文字接近：11pt 的「●」比文字高一截，
        // 视觉上像个突出的色块；Margin.Top=2 让两者中心线重合（实测 ΔcenterY=0）。
        dot.Font = new Font("Microsoft YaHei UI", 9F);
        dot.AutoSize = true;
        dot.Margin = new Padding(3, 2, 0, 0);
        chip.Controls.Add(dot);
        var lbl = new Label
        {
            Text = text, AutoSize = true,
            // 下划线字体只能新建实例（Theme.Fonts.Caption 是共享缓存，不能改它的样式）
            Font = onClick is null ? Theme.Fonts.Caption : new Font(Theme.Fonts.Caption, FontStyle.Underline),
            ForeColor = Theme.TextSecondary, Margin = new Padding(4, 2, 0, 0),
        };
        if (onClick is not null)
        {
            // 链接的点击热区要比 8pt 文字大一圈：圆点、标签、容器都挂同一个动作
            lbl.Cursor = Cursors.Hand;
            lbl.Click += (_, _) => onClick();
            dot.Cursor = Cursors.Hand;
            dot.Click += (_, _) => onClick();
            chip.Cursor = Cursors.Hand;
            // ToolTip 必须挂在长寿对象上：局部变量会被 GC 回收，提示随之中止显示
            tips.SetToolTip(lbl, $"打开 {text} 发布页（GitHub）");
        }
        chip.Controls.Add(lbl);
        return chip;
    }

    static Label MakeDot() => new() { Text = "●", AutoSize = true, ForeColor = Theme.DotOff };

    static Panel MakeEmptyState(string title, string subtitle)
    {
        var p = new RedrawPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
        p.Paint += (_, e) => DrawDashedBorderStatic(e.Graphics!, p.ClientRectangle,
            ColorTranslator.FromHtml("#DCE2EF"), 1);
        var box = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };
        var lbl = new Label
        {
            Text = title + "\n" + subtitle, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = Theme.Fonts.Body, ForeColor = Theme.TextMuted,
        };
        box.Controls.Add(lbl);
        p.Controls.Add(box);
        return p;
    }

    static void DrawDashedBorderStatic(Graphics g, Rectangle r, Color color, float width)
    {
        using var pen = new Pen(color, width) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        g.DrawRectangle(pen, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
    }

    static Button MakeGhostButton(string text, Action onClick)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.Transparent, ForeColor = Theme.TextSecondary,
            Font = Theme.Fonts.Body, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 6, 0),
        };
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Theme.Divider;
        b.FlatAppearance.MouseOverBackColor = Theme.AccentSoft;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>固定行高、固定列宽的表格布局（保留供兼容）。</summary>
    static TableLayoutPanel Table(int cols, int rows, int rowHeight = 28)
    {
        var t = new TableLayoutPanel { ColumnCount = cols, RowCount = rows, Dock = DockStyle.Fill };
        for (var i = 0; i < rows; i++) t.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
        return t;
    }

    static void Cols(TableLayoutPanel t, params (SizeType Type, float Value)[] specs)
    {
        foreach (var (type, value) in specs) t.ColumnStyles.Add(new ColumnStyle(type, value));
    }

    void SetupGrid()
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.MultiSelect = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // 不要用 EditOnEnter：那样任何单元格一获得焦点就立刻进入编辑态，
        // 表格里会常驻一个带边框的输入框（截图里第一眼像渲染错位）。
        // EditOnKeystrokeOrF2 仍保留「双击改名 / 选中后直接打字改名 / F2 改名」三种入口。
        grid.EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        grid.RowTemplate.Height = 30;
        grid.BackgroundColor = Color.White;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Theme.Divider;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 30;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Theme.Chrome,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Fonts.Caption,
            SelectionBackColor = Theme.Chrome,
            SelectionForeColor = Theme.TextSecondary,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Theme.TextPrimary,
            SelectionBackColor = Theme.AccentSoft,
            SelectionForeColor = Theme.TextPrimary,
            Font = Theme.Fonts.Body,
        };
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "任务名称（可编辑）",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 100,
            MinimumWidth = 220,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "进度", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, MinimumWidth = 64, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "速度", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, MinimumWidth = 76, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "文件大小", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells, ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.CellValueChanged += OnCellValueChanged;
        // 双击：名称列进入改名，其它列定位文件（与下载软件一致）
        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == 0) { grid.CurrentCell = grid.Rows[e.RowIndex].Cells[0]; grid.BeginEdit(true); }
            else LocateTaskFile(e.RowIndex);
        };
        // 右键：先把光标所在行选中，再弹菜单 —— 否则菜单动作会落到上一次选中的别的行上
        // （WinForms 默认不因右键改变选区，这是「右键菜单删错任务」的根因）。
        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            var row = grid.Rows[e.RowIndex];
            if (!row.Selected)
            {
                grid.ClearSelection();
                row.Selected = true;
            }
            if (e.ColumnIndex >= 0) grid.CurrentCell = row.Cells[e.ColumnIndex];
        };
        grid.KeyDown += (_, e) =>
        {
            if (e.Control)
            {
                if (e.KeyCode == Keys.V) { e.Handled = true; e.SuppressKeyPress = true; PasteTasks(); }
                else if (e.KeyCode == Keys.C) { e.Handled = true; e.SuppressKeyPress = true; CopySelection(); }
                else if (e.KeyCode == Keys.A) { e.Handled = true; e.SuppressKeyPress = true; grid.SelectAll(); }
                return;
            }
            if (e.KeyCode == Keys.Delete) { e.Handled = true; e.SuppressKeyPress = true; DeleteSelected(); }
            else if (e.KeyCode == Keys.F2 && grid.CurrentRow is not null) { grid.BeginEdit(true); }
        };
        // 空态上点一下 = 粘贴（粘贴是最高频入口，取消「添加任务」卡片后要有顺手的位置）。
        // 用静默版：剪贴板里没有任务文本时什么都不做，避免手滑点一下就弹「剪贴板为空」。
        emptyGridState.Click += (_, _) => PasteFromEmptyState();
        foreach (Control c in emptyGridState.Controls) c.Click += (_, _) => PasteFromEmptyState();
        // 文件拖放：卡片取消后，直接拖到队列上导入
        EnableTaskDrop(grid);
    }

    /// <summary>
    /// 高频开关：第 0 列 ToggleSwitch（只画开关本体），第 1 列独立 Label（显示文字）。
    /// Label 点击转发为 toggle.PerformClick()，保证整行可点；chk 字典契约保持不变。
    /// </summary>
    void AddToggle(TableLayoutPanel host, string key, string text, bool check, int row)
    {
        var toggle = new ToggleSwitch
        {
            Checked = check,
            // Anchor 不含 Top/Bottom → TableLayoutPanel 会把开关在行内垂直居中，
            // 与右侧 Dock=Fill + MiddleLeft 的文字基线对齐。
            // 原来 Anchor=Left|Top + Margin(2,6,0,0)：22px 开关在 42px 行里落在 y=6..28，
            // 中心 17 而文字中心 21，整体偏高 4px，看着"开关和文字不在一条线上"。
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
        };
        if (key == "alwaysOnTop") toggle.CheckedChanged += (_, _) => TopMost = toggle.Checked;
        chk[key] = toggle;
        host.Controls.Add(toggle, 0, row);

        var label = new Label
        {
            Text = text, AutoSize = false, Dock = DockStyle.Fill,
            ForeColor = Theme.TextPrimary, Font = Theme.Fonts.Body, BackColor = Theme.Surface,
            UseCompatibleTextRendering = true, TextAlign = ContentAlignment.MiddleLeft,
        };
        // CheckBox 无 PerformClick（那是 Button 的成员），直接切换 Checked 同样触发 CheckedChanged，行为等价。
        label.Click += (_, _) => toggle.Checked = !toggle.Checked;
        host.Controls.Add(label, 1, row);

        // 轮数不在这一层，显式告知去哪里调，避免用户以为开关就是全部
        if (key == "autoRetryFailed")
        {
            const string hint = "整批任务跑完后，失败的任务会自动重跑。\n轮数在左栏「② 性能设置 → 自动重试轮数」调整（0 = 关闭）。";
            tips.SetToolTip(label, hint);
            tips.SetToolTip(toggle, hint);
        }
    }

    /// <summary>低频开关用普通 CheckBox，仍以原 key 存入 chk。</summary>
    void AddChk(FlowLayoutPanel host, string key, string text, bool check = false)
    {
        var c = new CheckBox { Text = text, AutoSize = true, Margin = new Padding(4, 2, 12, 2), ForeColor = Theme.TextPrimary, Font = Theme.Fonts.Body };
        c.Checked = check;
        if (key == "alwaysOnTop") c.CheckedChanged += (_, _) => TopMost = c.Checked;
        chk[key] = c;
        host.Controls.Add(c);
    }

    // ============================================================
    // 右键菜单
    // ============================================================
    ContextMenuStrip TextMenu(TextBoxBase box, bool canClear = false)
    {
        var m = new ContextMenuStrip();
        m.Items.Add("撤销", null, (_, _) => box.Undo());
        m.Items.Add("重做", null, (_, _) => { if (box is RichTextBox rt) rt.Redo(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("剪切", null, (_, _) => box.Cut());
        m.Items.Add("复制", null, (_, _) => box.Copy());
        m.Items.Add("粘贴", null, (_, _) => box.Paste());
        if (canClear) m.Items.Add("清空", null, (_, _) => box.Clear());
        m.Items.Add("删除", null, (_, _) => { if (box is TextBox tb) tb.Text = ""; });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("全选", null, (_, _) => box.SelectAll());
        return m;
    }

    /// <summary>
    /// 队列右键菜单：对齐常见下载软件（IDM / 迅雷 / FDM）的鼠标操作习惯 ——
    /// 打开文件 / 打开所在文件夹 / 复制链接 / 重命名 / 重试 / 排序 / 删除 / 粘贴 / 导入 / 手动输入。
    /// 菜单在 Opening 时按当前选中状态与下载状态动态启用，避免点了没反应。
    /// </summary>
    ContextMenuStrip GridMenu()
    {
        var m = new ContextMenuStrip();
        var miOpen = m.Items.Add("打开文件", null, (_, _) => OpenTaskFile());
        var miFolder = m.Items.Add("打开所在文件夹", null, (_, _) => LocateTaskFile());
        m.Items.Add(new ToolStripSeparator());
        var miRename = m.Items.Add("重命名…", null, (_, _) => RenameSelected());
        var miCopyRow = m.Items.Add("复制任务（下载数据格式）", null, (_, _) => CopySelection());
        var miCopyUrl = m.Items.Add("复制链接", null, (_, _) => CopySelectedUrls());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("粘贴任务", null, (_, _) => PasteTasks());
        m.Items.Add("导入任务文件…", null, (_, _) => BrowseTaskFile());
        m.Items.Add("手动输入任务…", null, (_, _) => ShowManualInput());
        m.Items.Add(new ToolStripSeparator());
        var miRetry = m.Items.Add("重试选中任务", null, (_, _) => RetrySelected());
        m.Items.Add("重试全部失败任务", null, (_, _) => RetryFailed());
        var miTop = m.Items.Add("移到队首", null, (_, _) => MoveSelected(int.MinValue));
        var miUp = m.Items.Add("上移", null, (_, _) => MoveSelected(-1));
        var miDown = m.Items.Add("下移", null, (_, _) => MoveSelected(+1));
        var miBottom = m.Items.Add("移到队尾", null, (_, _) => MoveSelected(int.MaxValue));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("全选", null, (_, _) => grid.SelectAll());
        m.Items.Add("反选", null, (_, _) => InvertSelection());
        m.Items.Add(new ToolStripSeparator());
        var miDelete = m.Items.Add("删除选中任务", null, (_, _) => DeleteSelected());
        var miClear = m.Items.Add("清空全部任务", null, (_, _) => ClearAll());

        m.Opening += (_, _) =>
        {
            var rows = SelectedRows();
            var hasSel = rows.Count > 0;
            var single = rows.Count == 1;
            // 单选的「打开文件 / 重命名」只在一条任务上有意义；
            // 「打开所在文件夹」实际要定位成品文件，未完成的任务没有文件可定位，同样只在完成后启用。
            miOpen.Enabled = single && IsFinishedRow(rows[0]);
            miFolder.Enabled = single && IsFinishedRow(rows[0]);
            miRename.Enabled = single && !batchRunning;
            miCopyRow.Enabled = hasSel;
            miCopyUrl.Enabled = hasSel;
            miRetry.Enabled = hasSel && !batchRunning;
            miDelete.Enabled = hasSel;
            // 排序对多选也允许（按选中块整体移动），但下载中上移/下移只支持单选，避免顺序歧义
            var canMove = single && !batchRunning;
            miTop.Enabled = miUp.Enabled = miDown.Enabled = miBottom.Enabled = canMove;
            miClear.Enabled = QueueCount > 0;
        };
        return m;
    }

    static Label MakeLabel(string text) =>
        new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, ForeColor = Theme.TextPrimary, Font = Theme.Fonts.Body, Margin = new Padding(4, 7, 4, 0) };

    /// <summary>
    /// 双态按钮文案/配色的唯一来源（BUG-3）。StopBatch / OnBatchFinished / StartBatch
    /// 只调用 SetBatchButtons，不再各自改写 btnStart.Text，避免状态打架。
    /// </summary>
    void SyncRunButton(bool idle)
    {
        btnStart.Enabled = true;
        btnStart.Text = idle ? "开始下载" : "停止下载";
        btnStart.BackColor = idle ? Theme.Accent : Theme.Danger;
        btnStart.FlatAppearance.MouseOverBackColor = idle ? Theme.AccentHover : ColorTranslator.FromHtml("#DC2626");
        btnStart.FlatAppearance.MouseDownBackColor = idle ? Theme.AccentPressed : ColorTranslator.FromHtml("#B91C1C");
        btnStart.ForeColor = Color.White;
    }

    /// <summary>
    /// 批次开始/结束时只切「开始下载 / 停止下载」这一个主按钮。
    /// 删除、清空、粘贴在下载中一律可用（需求：下载中可随意增删任务），所以不再统一禁用。
    /// </summary>
    void SetBatchButtons(bool enabled) => SyncRunButton(enabled);
}
