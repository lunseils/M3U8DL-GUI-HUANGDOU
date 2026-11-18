using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace M3U8DL;

/// <summary>
/// 主窗体。反射契约要求字段与关键方法定义在本类型本身 —— 采用 partial class 拆分到
/// MainForm.Layout.cs / .Queue.cs / .Download.cs / .Persistence.cs，编译后同属一个类型。
/// 本文件保留：字段声明、常量、构造、事件接线、UI 状态刷新、日志、工具方法。
/// </summary>
internal sealed partial class MainForm : Form
{
    // 硬编码路径常量已移除（team-lead 裁定）：默认值改由 PATH/常见目录探测 + UserProfile/Downloads，
    // 持久化在独立的 settings.json（见 MainForm.Settings.cs）。
    const int MaxLogLines = 5000;
    const int QueuePollMs = 200;

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---------- 控件 ----------
    readonly TextBox txtExe = Theme.Field();
    readonly TextBox txtWorkDir = Theme.Field();
    readonly TextBox txtMux = Theme.Field();
    readonly TextBox txtCookieFile = Theme.Field();
    readonly ComboBox cmbCookieBrowser = Theme.Combo(
        "(不使用)", "chrome", "chromium", "edge", "firefox",
        "brave", "opera", "vivaldi", "safari", "whale");
    readonly DataGridView grid = new BufferedGrid();
    readonly NumericUpDown nudConc = new() { Minimum = 1, Maximum = 20, Value = 1, Width = 80 };
    /// <summary>
    /// 状态栏里的「并发」控件：与设置浮层里的 <see cref="nudConc"/> 是同一个值的两个入口，
    /// 双向同步。批次调度每轮都重读 concurrentLimit，所以下载中改动下一轮即生效。
    /// </summary>
    readonly NumericUpDown nudConcInline = new()
    {
        Minimum = 1, Maximum = 20, Value = 1, Width = 26,
        BorderStyle = BorderStyle.None, BackColor = Theme.Ghost, ForeColor = Theme.TextPrimary,
        Font = Theme.Fonts.Caption, TextAlign = HorizontalAlignment.Center,
        // 不参与 Tab 链：否则窗体启动时焦点会落进来，UpDownBase 自动全选 → 状态栏上
        // 出现一块难看的蓝色高亮。鼠标点击后仍可正常输入。
        TabStop = false,
    };
    bool syncingConc;
    readonly NumericUpDown nudThreads = new() { Minimum = 1, Maximum = 100, Value = 32, Width = 90 };
    readonly NumericUpDown nudRetry = new() { Minimum = 1, Maximum = 100, Value = 3, Width = 90 };
    readonly NumericUpDown nudTimeout = new() { Minimum = 1, Maximum = 300, Value = 100, Width = 90 };
    readonly NumericUpDown nudSpeed = new() { Minimum = 0, Maximum = 10000, Value = 0, Width = 100 };
    // 「失败自动重试」的轮数（0 = 关闭）。与 nudRetry（引擎内部单任务重试次数）是两个概念。
    readonly NumericUpDown nudAutoRetry = new() { Minimum = 0, Maximum = 5, Value = 1, Width = 90 };
    readonly Button btnStart = Theme.Button("开始队列下载", Theme.Primary, Color.White);
    readonly RichTextBox txtLog = new()
    {
        ReadOnly = true,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.LogBg,
        ForeColor = Theme.Text,
        Font = new Font("Consolas", 10F),
        Dock = DockStyle.Fill,
    };
    readonly Dictionary<string, CheckBox> chk = [];
    /// <summary>控件提示气泡。必须作为字段持有，局部变量会被 GC 回收导致提示失效。</summary>
    readonly ToolTip tips = new();

    // ---------- 状态 ----------
    readonly List<DownloadTask> queue = [];
    readonly object queueLock = new();
    readonly object rowLock = new();
    /// <summary>任务 id → 令牌。下载中删除/排序会让行号漂移，所以身份一律用任务 id。</summary>
    readonly Dictionary<int, int> rowToken = [];
    readonly Engine engine;
    volatile bool batchRunning;
    int batchEpoch;
    int concurrentLimit = 1;
    /// <summary>本批次参与调度的任务 id 集合（添加/删除任务时同步增删，不随行号漂移）。</summary>
    HashSet<int> batchRows = [];
    bool updating;
    bool loading;
    int logLines;
    /// <summary>任务 id 发号器。任务一旦入队 id 不再变化，删除也不会复用。</summary>
    int taskSeq;
    /// <summary>
    /// PATH 级依赖（yt-dlp / ffmpeg）的就绪状态缓存。
    /// 探测一次要走 PATH + 两级注册表，不能挂在 txtExe.TextChanged 上每次按键都跑一遍
    /// （用户敲 20 个字符就是 100 多次注册表读取，输入框会肉眼可见地卡）。
    /// RE 路径只做 File.Exists，随输入实时更新，无需缓存。
    /// </summary>
    bool ytDlpOnPath;
    bool ffmpegOnPath;
    bool depsProbed;
    /// <summary>托盘图标：首次最小化时才创建（测试/纯命令行场景不留托盘残影）。</summary>
    NotifyIcon? tray;
    /// <summary>真正退出中（区别于「收进托盘」）。</summary>
    bool exiting;

    public MainForm()
    {
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        // 按 team-lead 裁定以 PRD 为准：ClientSize 1280×800、MinimumSize 1120×720
        MinimumSize = new Size(1120, 720);
        ClientSize = new Size(1280, 800);
        AutoScaleMode = AutoScaleMode.Dpi;

        engine = new Engine(
            msg => Ui(() => AppendLog(msg)),
            (row, st) => Ui(() => SetStatus(row, st)),
            (row, pct) => Ui(() => SetProgress(row, pct)),
            () => batchRunning);

        BuildUi();
        LoadSettings(); // S6：先套用独立 settings.json（含默认路径探测），再载入任务
        LoadTasks();
        // 载入完任务后必须刷新一次状态栏：否则启动时永远停在初始文案「就绪」，
        // 连「共 N 个任务」都不显示（AddTask 只在 batchRunning 时才刷新）。
        UpdateQueueStatusLabel();

        // BUG-9：后台静默预热 yt-dlp 探测（最坏 64 秒的 PATH/Python 枚举不阻塞 UI 构造与首屏）。
        // FindOnPath / FindYtDlp 内部 _cmdLock 保证只探测一次，StartBatch 再读同一缓存。
        _ = Task.Run(() =>
        {
            try { engine.PrewarmYtDlp(); } catch { }
        });
    }

    bool C(string key) => chk.TryGetValue(key, out var c) && c.Checked;
    // ============================================================
    // 状态刷新
    // ============================================================
    static Color StatusColor(string status) => status switch
    {
        "完成" => Theme.Done,
        "失败" => Theme.Danger,
        "下载中" => Theme.Primary,
        "已取消" => Theme.Cancel,
        _ => Theme.Muted,
    };

    /// <summary>
    /// 任务 id → 当前行号；任务已被删除返回 -1。
    /// 下载中允许随时删除/排序任务，行号会变，所以所有异步回调（引擎状态、进度、大小）
    /// 都先按 id 反查行号，查不到就静默丢弃 —— 绝不把结果写到别人的行上。
    /// </summary>
    int RowOf(int id)
    {
        lock (queueLock)
        {
            for (var i = 0; i < queue.Count; i++)
                if (queue[i].Id == id) return i;
        }
        return -1;
    }

    bool TaskExists(int id) => RowOf(id) >= 0;

    /// <summary>队列长度快照（后台线程读 Count 也要走锁：List 扩容瞬间读 Count 并不保证看到一致值）。</summary>
    int QueueCount
    {
        get { lock (queueLock) return queue.Count; }
    }

    DownloadTask? TaskById(int id)
    {
        lock (queueLock)
            foreach (var t in queue)
                if (t.Id == id) return t;
        return null;
    }

    void SetStatus(int id, string text)
    {
        var row = RowOf(id);
        if (row < 0) return;   // 任务已被删掉：结果直接丢弃
        lock (queueLock) queue[row].Status = text;
        if (row < grid.Rows.Count)
        {
            updating = true;
            try
            {
                grid.Rows[row].Cells[1].Value = text;
                grid.Rows[row].Cells[1].Style.ForeColor = StatusColor(text);
                if (text == "完成")
                {
                    grid.Rows[row].Cells[2].Value = "100%";
                    grid.Rows[row].Cells[2].Style.ForeColor = Theme.Done;
                }
                else if (text is "失败" or "已取消")
                {
                    var cur = Convert.ToString(grid.Rows[row].Cells[2].Value) ?? "";
                    if (!cur.EndsWith('%')) grid.Rows[row].Cells[2].Value = "—";
                    grid.Rows[row].Cells[2].Style.ForeColor = Theme.Cancel;
                }
            }
            finally { updating = false; }
        }
        UpdateQueueStatusLabel();
        if (text is "完成" or "失败" or "已取消") SaveTasks();
    }

    void SetProgress(int id, double pct)
    {
        var row = RowOf(id);
        if (row >= 0 && row < grid.Rows.Count)
        {
            updating = true;
            try
            {
                var text = Math.Abs(pct - Math.Round(pct)) < 0.05
                    ? FormattableString.Invariant($"{Math.Round(pct):0}%")
                    : FormattableString.Invariant($"{pct:0.0}%");
                grid.Rows[row].Cells[2].Value = text;
                grid.Rows[row].Cells[2].Style.ForeColor = pct >= 100 ? Theme.Done : Theme.Primary;
            }
            finally { updating = false; }
        }
    }

    void SetSize(int id, long size, int count)
    {
        var row = RowOf(id);
        if (row < 0 || row >= grid.Rows.Count) return;
        updating = true;
        try
        {
            var text = Naming.FmtSize(size);
            if (count > 1) text += $" · {count} 项";
            grid.Rows[row].Cells[3].Value = text;
            grid.Rows[row].Cells[3].Style.ForeColor = Theme.Size;
        }
        finally { updating = false; }
    }

    void UpdateQueueStatusLabel()
    {
        // S4 双态按钮：btnStart 文案由 SetBatchButtons 统一维护（开始下载 / 停止下载），
        // 这里只更新底部状态栏文字与进度，不再改写按钮 Text。
        if (!batchRunning)
        {
            lblStatus.Text = $"就绪 · 共 {QueueCount} 个任务";
            return;
        }
        // 按「当前队列顺序」取本批次的任务：下载中删过任务、排过序也不会算错序号（内部用任务 id 匹配）
        List<(int Row, string Status)> items;
        int totalCount;
        lock (queueLock)
        {
            totalCount = queue.Count;
            items = queue.Select((t, i) => (t, i)).Where(x => batchRows.Contains(x.t.Id))
                         .Select(x => (x.i, x.t.Status)).OrderBy(x => x.i).ToList();
        }
        if (items.Count == 0) { lblStatus.Text = $"运行中 · 共 {totalCount} 个任务"; return; }
        int? downloadingPos = null;
        var doneCount = 0;
        for (var pos = 1; pos <= items.Count; pos++)
        {
            var st = items[pos - 1].Status;
            if (st == "下载中" && downloadingPos is null) downloadingPos = pos;
            else if (st is "完成" or "失败" or "已取消") doneCount++;
        }
        var total = items.Count;
        lblStatus.Text = downloadingPos is not null
            ? $"运行中 · 正在下载 {downloadingPos}/{total}"
            : doneCount >= total
                ? $"完成 · {total}/{total}"
                : $"运行中 · {Math.Min(doneCount + 1, total)}/{total}";
    }

    // ============================================================
    // 日志
    // ============================================================
    void AppendLog(string text)
    {
        if (logLines > MaxLogLines) TrimLog();
        txtLog.AppendText(text + Environment.NewLine);
        // BUG-12：text 常含换行，按实际行数累加，TrimLog 才能按行正确裁剪
        logLines += text.Count(ch => ch == '\n') + 1;
        txtLog.SelectionStart = txtLog.TextLength;
        txtLog.ScrollToCaret();
    }

    void TrimLog()
    {
        var excess = logLines - MaxLogLines;
        if (excess <= 0) return;
        try
        {
            var idx = txtLog.GetFirstCharIndexFromLine(excess);
            if (idx > 0)
            {
                txtLog.Select(0, idx);
                txtLog.SelectedText = "";
            }
        }
        catch { }
        logLines -= excess;
    }

    // ============================================================
    // 杂项
    // ============================================================
#if DEBUG
    /// <summary>调试用：导出控件树的几何信息（仅 DEBUG 构建；默认写入临时目录，不再硬编码 C:\tmp）。</summary>
    public void DumpLayout(string? path = null)
    {
        path ??= Path.Combine(Path.GetTempPath(), "m3u8dl-layout.txt");
        using var w = new StreamWriter(path);
        void Walk(Control c, int depth)
        {
            w.WriteLine($"{new string(' ', depth * 2)}{c.GetType().Name} [{(c as TextBox)?.Text}] bounds={c.Bounds} dock={c.Dock} auto={c.AutoSize} parent={(c.Parent?.GetType().Name ?? "-")}");
            foreach (Control ch in c.Controls) Walk(ch, depth + 1);
        }
        Walk(this, 0);
    }
#endif

    void Ui(Action a)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch { }
    }

    /// <summary>
    /// BUG-7：同步版 UI 封送。用于必须在同一帧内完成"改终态 + 落盘"的关键路径，
    /// 避免 Ui()/BeginInvoke 的异步队列让 OnBatchFinished 抢先 SaveTasks 造成终态丢失竞态。
    /// Invoke 在句柄尚未创建/窗口销毁时会抛 InvalidOperationException，需吞掉。
    /// </summary>
    void UiSync(Action a)
    {
        if (IsDisposed || Disposing) return;
        try
        {
            if (InvokeRequired) Invoke(a);
            else a();
        }
        catch (InvalidOperationException) { }
    }

    // ============================================================
    // 系统托盘（「桌面状态栏」最小化）
    // ============================================================
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 最小化 → 收进系统托盘并继续下载（可在「设置 → 通用 → 最小化到托盘」关掉）。
        if (WindowState == FormWindowState.Minimized && !exiting && !IsDisposed && C("minimizeToTray"))
            HideToTray();
    }

    /// <summary>
    /// 取程序图标：优先同目录 app.ico（便携版放了图标文件时），
    /// 否则从 exe 内嵌资源提取（csproj 的 ApplicationIcon 会嵌进去，单文件发布也能拿到）。
    /// 结果缓存：标题栏与托盘各要一份，重复 new Icon 会白占 GDI 句柄。
    /// </summary>
    internal static Icon? AppIcon()
    {
        if (appIconLoaded) return appIcon;
        appIconLoaded = true;
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (File.Exists(p)) return appIcon = new Icon(p);
        }
        catch { }
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                appIcon = Icon.ExtractAssociatedIcon(exe);
        }
        catch { }
        return appIcon;
    }

    static Icon? appIcon;
    static bool appIconLoaded;

    /// <summary>创建托盘图标（懒创建：没最小化过就不占系统托盘）。</summary>
    void EnsureTray()
    {
        if (tray is not null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new ToolStripSeparator());
        var miStart = menu.Items.Add("开始下载", null, (_, _) => { RestoreFromTray(); if (!batchRunning) StartBatch(); });
        var miStop = menu.Items.Add("停止下载", null, (_, _) => { if (batchRunning) StopBatch(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        menu.Opening += (_, _) =>
        {
            miStart.Enabled = !batchRunning;
            miStop.Enabled = batchRunning;
        };
        tray = new NotifyIcon
        {
            Icon = AppIcon() ?? SystemIcons.Application,
            Text = "M3U8 批量下载器",
            Visible = true,
            ContextMenuStrip = menu,
        };
        tray.DoubleClick += (_, _) => RestoreFromTray();
    }

    void HideToTray()
    {
        EnsureTray();
        ShowInTaskbar = false;
        Hide();
        try { tray?.ShowBalloonTip(1200, "已最小化到托盘", "下载仍在后台继续，双击托盘图标可恢复窗口。", ToolTipIcon.Info); }
        catch { }
    }

    void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    void DisposeTray()
    {
        if (tray is null) return;
        try { tray.Visible = false; tray.Dispose(); } catch { }
        tray = null;
    }

    void ExitApp()
    {
        exiting = true;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeTray();   // 托盘图标不释放会在任务栏留下"幽灵图标"
        base.Dispose(disposing);
    }

    /// <summary>
    /// PATH 里是否存在该可执行文件（进程 PATH + 注册表 PATH，自动补 .exe）。
    /// 注意扩展名：Windows 上 File.Exists("D:\...\ffmpeg") 恒为 false（File.Exists 不做 PATHEXT 解析），
    /// 所以 "ffmpeg" 这种没写扩展名的名字必须由 PathProbe 补 .exe —— 否则 PATH 里
    /// 明明有 ffmpeg.exe，也会在「开始下载」时误报"未在系统 PATH 中找到 FFmpeg"。
    /// 同时走注册表 PATH 兜底，避免进程 PATH 是登录快照导致的漏判。
    /// </summary>
    static bool OnPath(string name) => PathProbe.Exists(name);
}
