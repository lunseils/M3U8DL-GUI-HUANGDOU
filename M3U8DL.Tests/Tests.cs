using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace M3U8DL.Tests;

/// <summary>
/// C# 版回归测试：
///   1) 文件名规则与成品识别（对应 Python 版 test_audit_fixes.py 的用例）
///   2) 引擎命令构造
///   3) 用假下载器真跑一遍队列（yt-dlp / RE / 跳过成品 / 合流失败 / 降级重试）
/// </summary>
internal static class Tests
{
    static int _fail;
    static int _pass;

    /// <summary>看门狗线程的停止开关。</summary>
    static volatile bool _watchdogStop;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine($"  ✅ {name}"); }
        else { _fail++; Console.WriteLine($"  ❌ {name}  {detail}"); }
    }

    static string Root => Path.GetDirectoryName(Environment.GetCommandLineArgs()[0])!;
    static string FakeExe { get; set; } = "";

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (Environment.GetCommandLineArgs().Contains("--shot"))
        {
            ShotMode();
            return 0;
        }
        StartDialogWatchdog();
        PrepareFakeOnPath();
        Console.WriteLine("=== 1) 文件名与成品识别 ===");
        NamingTests();
        Console.WriteLine("=== 2) 引擎命令构造 ===");
        EngineTests();
        Console.WriteLine("=== 3) 界面布局 ===");
        LayoutTests();
        Console.WriteLine("=== 3.2) 队列鼠标操作与增删改 ===");
        QueueOpsTests();
        Console.WriteLine("=== 3.5) 可执行文件探测（RE / FFmpeg）===");
        PathDetectTests();
        Console.WriteLine("=== 4) 端到端（真跑队列）===");
        E2ETests();
        QaRegression.Run();
        RegressionAdditions.Run();
        Console.WriteLine($"\n==== 通过 {_pass} / 失败 {_fail} ====");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 截图模式（QA 用）：M3U8DL.Tests.exe --shot，输出到 SHOT_DIR（默认 %TEMP%\m3u8dl_shots）。
    /// 只读地构造主界面、塞几条示例任务、开一次设置浮层，用于人工核对视觉与排版。
    /// </summary>
    static void ShotMode()
    {
        var dir = Environment.GetEnvironmentVariable("SHOT_DIR")
                  ?? Path.Combine(Path.GetTempPath(), "m3u8dl_shots");
        Directory.CreateDirectory(dir);
        var appdata = Path.Combine(dir, "appdata");
        Directory.CreateDirectory(Path.Combine(appdata, "NM3U8DL_GUI"));
        Environment.SetEnvironmentVariable("APPDATA", appdata);
        // 每次截图都从空队列开始，避免把上一轮的示例任务叠加进来
        try { File.Delete(Path.Combine(appdata, "NM3U8DL_GUI", "tasks.json")); } catch { }

        using var f = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        f.Show();
        Application.DoEvents();
        f.Text = "M3U8 批量下载器";
        Call(f, "AddTask", "示例·第01集 4K", "https://example.com/a.m3u8", "完成", null, false);
        Call(f, "AddTask", "示例·第02集 4K", "https://example.com/b.m3u8", "下载中", null, false);
        Call(f, "AddTask", "示例·第03集 4K", "https://example.com/c.m3u8", "待下载", null, false);
        Call(f, "AddTask", "示例·第04集 4K", "https://example.com/d.m3u8", "失败", null, false);
        Application.DoEvents();
        var grid = (DataGridView)GetField(f, "grid");
        grid.Rows[0].Cells[2].Value = "100%";
        grid.Rows[1].Cells[2].Value = "43.5%";
        grid.Rows[1].Cells[3].Value = "1.28 MB/s";
        grid.Rows[1].Cells[3].Style.ForeColor = Theme.Primary;
        grid.Rows[0].Cells[4].Value = "1.28 GB";
        grid.Rows[1].Cells[4].Value = "612 MB";
        grid.Rows[1].Selected = true;

        Shot(f, Path.Combine(dir, "01_main.png"));
        ShotScreen(f, Path.Combine(dir, "01_main_screen.png"));
        Console.WriteLine($"  screen={Screen.PrimaryScreen?.Bounds}");
        // 几何自检：改布局时先读这些数字，比盯截图快（DPI、卡片高度、Region 是否真的设了）
        Console.WriteLine($"  dpi={f.DeviceDpi} scale={f.DeviceDpi / 96f:0.##} client={f.ClientSize}");
        foreach (var name in new[] { "optsGroup", "perfGroup" })
            if (GetField(f, name) is Control c)
                Console.WriteLine($"  {name}: h={c.Height} w={c.Width} top={c.Top} region={(c.Region is null ? "null" : "ok")}");
        // 性能行：标签与数字框的实测几何（垂直居中差了 4px 时靠这两行定位）
        foreach (var name in new[] { "nudConc", "nudAutoRetry" })
            if (GetField(f, name) is NumericUpDown nud && nud.Parent is TableLayoutPanel tlp0)
            {
                var lbl0 = tlp0.Controls.Cast<Control>().OfType<Label>()
                                .FirstOrDefault(l => tlp0.GetRow(l) == tlp0.GetRow(nud));
                Console.WriteLine($"  {name}: nud={nud.Bounds} anchor={nud.Anchor} row={tlp0.GetRow(nud)} " +
                                  $"rowH={tlp0.GetRowHeights()[tlp0.GetRow(nud)]} lbl={lbl0?.Bounds}");
            }
        Call(f, "OpenSettings");
        Application.DoEvents();
        // 设置浮层是半透明遮罩面板：DrawToBitmap 对 Alpha 背景无效，连子控件一起丢掉，
        // 于是拍整窗只能得到主界面。这里单独把 settingsCard 拍下来 —— QA 要核对的就是它。
        var settingsCard = (Control)GetField(f, "settingsCard")!;
        using (var bmp = new Bitmap(settingsCard.Width, settingsCard.Height))
        {
            settingsCard.DrawToBitmap(bmp, new Rectangle(0, 0, settingsCard.Width, settingsCard.Height));
            bmp.Save(Path.Combine(dir, "02_settings.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var compat = (Control)GetField(f, "compatHost")!;
        foreach (var g in compat.Controls.OfType<GroupBox>())
            Console.WriteLine($"  [settings] {g.Text}: top={g.Top} h={g.Height}");
        Call(f, "CloseSettings");
        // 右键菜单：直接弹出菜单截图
        // 右键菜单是独立顶层窗口，DrawToBitmap 拍不到，只能屏幕截图
        var menu = grid.ContextMenuStrip;
        if (menu is not null)
        {
            menu.Show(f, new Point(grid.Left + 140, grid.Top + 96));
            Application.DoEvents();
            Thread.Sleep(250);
            Application.DoEvents();
            var r = menu.Bounds;
            using var bmp = new Bitmap(r.Width + 8, r.Height + 8);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Left - 4, r.Top - 4, 0, 0, new Size(r.Width + 8, r.Height + 8));
            bmp.Save(Path.Combine(dir, "03_menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            menu.Close();
        }
        Console.WriteLine("shots -> " + dir);
    }

    static void Shot(Form f, string path)
    {
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();
        // DrawToBitmap 不依赖屏幕分辨率（屏幕比窗口小时 CopyFromScreen 会把底部裁掉）
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// 真实屏幕像素截图。
    /// 为什么还要这一份：<see cref="Control.DrawToBitmap"/> 走 WM_PRINT，打印时不套用窗口 Region，
    /// 圆角控件在图上会露出直角白块 —— 那是截图伪影，屏幕上并不存在。核对圆角/抗锯齿必须看真屏。
    /// </summary>
    static void ShotScreen(Form f, string path)
    {
        Application.DoEvents();
        Thread.Sleep(250);
        Application.DoEvents();
        using var bmp = new Bitmap(f.Width, f.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(f.Left, f.Top, 0, 0, new Size(f.Width, f.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>把假下载器放到 PATH 上，引擎才能找到 yt-dlp。</summary>
    static void PrepareFakeOnPath()
    {
        var fakeSrc = Environment.GetEnvironmentVariable("FAKE_EXE");
        if (string.IsNullOrEmpty(fakeSrc) || !File.Exists(fakeSrc)) return;
        var bin = Path.Combine(Path.GetTempPath(), "m3u8dl_fakebin");
        Directory.CreateDirectory(bin);
        File.Copy(fakeSrc, Path.Combine(bin, "yt-dlp.exe"), true);
        File.Copy(fakeSrc, Path.Combine(bin, "N_m3u8DL-RE.exe"), true);
        Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
    }

    // ============================================================
    // 模态对话框看门狗
    // ============================================================
    // MainForm 在剪贴板被独占时会弹模态 MessageBox（标题「提示」），
    // 自动化测试里没人去点确定，UI 线程会被永久卡住，整个测试进程挂死。
    // 这里起一个后台线程，自动关掉本进程弹出的这类对话框，保证测试不会挂死。
    const int WM_CLOSE = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>启动看门狗：自动关闭本进程弹出的模态对话框。</summary>
    static void StartDialogWatchdog()
    {
        var th = new Thread(() =>
        {
            var myPid = (uint)Environment.ProcessId;
            while (!_watchdogStop)
            {
                try
                {
                    // WinForms 的 MessageBox 是类名 "#32770" 的对话框窗口
                    var h = FindWindow("#32770", "提示");
                    if (h != IntPtr.Zero)
                    {
                        GetWindowThreadProcessId(h, out var pid);
                        // 只关自己进程的弹窗，绝不碰别的程序
                        if (pid == myPid) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
        })
        { IsBackground = true, Name = "DialogWatchdog" };
        th.Start();
    }

    // ============================================================
    // 1) 命名规则
    // ============================================================
    static void NamingTests()
    {
        Check("同名文件匹配", Naming.Matches("剧集.mp4", "剧集"));
        Check("带后缀匹配", Naming.Matches("剧集.f137.mp4", "剧集"));
        Check("前缀相同但非分隔符不匹配", !Naming.Matches("剧集X.mp4", "剧集"));
        Check("不同名不匹配", !Naming.Matches("别的.mp4", "剧集"));

        Check(".part 是中间产物", Naming.IsTempOutput("剧集.mp4.part"));
        Check(".ytdl 是中间产物", Naming.IsTempOutput("剧集.ytdl"));
        Check("-Frag12 是中间产物", Naming.IsTempOutput("剧集-Frag12.mp4"));
        Check("含 frag 的正常成品不算中间产物", !Naming.IsTempOutput("my-fragments.mp4"));
        Check("正常成品不是中间产物", !Naming.IsTempOutput("剧集.mp4"));

        Check("分离流 .f137.mp4 被识别", Naming.IsPartialStream("剧集.f137.mp4", "剧集"));
        Check("分离流 .f251.webm 被识别", Naming.IsPartialStream("剧集.f251.webm", "剧集"));
        Check("合流成品不是分离流", !Naming.IsPartialStream("剧集.mp4", "剧集"));
        Check("任务名本身带 .f137 不误伤", !Naming.IsPartialStream("A.f137.mp4", "A.f137"));

        Check("非法字符被替换", Naming.Sanitize("a/b:c*d") == "a_b_c_d", Naming.Sanitize("a/b:c*d"));
        Check("保留设备名加前缀", Naming.Sanitize("CON") == "_CON", Naming.Sanitize("CON"));
        Check("空名用兜底", Naming.Sanitize("", "task") == "task");
        var longName = Naming.Sanitize(new string('x', 200));
        Check("超长名截断到 150", longName.Length == 150, longName.Length.ToString());
        var trailing = Naming.Sanitize(new string('y', 149) + "  ");
        Check("截断后不留尾随空格", !trailing.EndsWith(' '), $"'{trailing[^3..]}'");
        Check("大小写保留", Naming.Sanitize("AbC") == "AbC");

        Check("大小格式化 B", Naming.FmtSize(512) == "512 B");
        Check("大小格式化 KB", Naming.FmtSize(2048) == "2.0 KB", $"[{Naming.FmtSize(2048)}]");
        Check("大小格式化 MB", Naming.FmtSize(5 * 1024 * 1024) == "5.00 MB", $"[{Naming.FmtSize(5 * 1024 * 1024)}]");
        Check("0 显示为破折号", Naming.FmtSize(0) == "—");
    }

    // ============================================================
    // 2) 成品检测
    // ============================================================
    static void OutputsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_out_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "剧集.mp4"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "剧集.mp4.part"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "剧集.f137.mp4"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dir, "剧集2.mp4"), new byte[100]);

            Check("找到成品", Outputs.FindFinished(dir, "剧集", []) == Path.Combine(dir, "剧集.mp4"));
            Check("中间产物不算成品", Outputs.FindFinished(dir, "剧集.part", []) is null);
            Check("分离流不算成品", Outputs.FindFinished(dir, "剧集.f", []) is null);
            Check("兄弟任务的文件不被抢占",
                  Outputs.FindFinished(dir, "剧集", ["剧集2"]) is null,
                  Outputs.FindFinished(dir, "剧集", ["剧集2"]) ?? "null");
            Check("没有任务时返回 null", Outputs.FindFinished(dir, "不存在", []) is null);

            Check("Exists 识别成品", Outputs.Exists(dir, "剧集"));
            Check("Exists 排除分离流", !Outputs.Exists(dir, "剧集.f"));

            var streams = Outputs.FindPartialStreams(dir, "剧集");
            Check("列出分离流", streams.Contains("剧集.f137.mp4"), string.Join(",", streams));

            var (size, count) = Outputs.OwnFilesSize(dir, "剧集");
            Check("统计文件大小", size == 100, size.ToString());
            Check("统计文件数量", count >= 1, count.ToString());

            // 过期残留清理（mtime 回拨 60 秒）
            var stale = Path.Combine(dir, "剧集-Frag1.mp4.part");
            File.WriteAllBytes(stale, new byte[10]);
            File.SetLastWriteTime(stale, DateTime.Now.AddSeconds(-60));
            var removed = Outputs.CleanupStaleParts(dir, "剧集");
            Check("清理过期 .part", removed == 1 && !File.Exists(stale), removed.ToString());

            Check("定位任务文件", Outputs.FindTaskFile(dir, "剧集") == Path.Combine(dir, "剧集.mp4"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ============================================================
    // 3) 引擎命令构造
    // ============================================================
    static void EngineTests()
    {
        var logs = new List<string>();
        var eng = new Engine(logs.Add, (_, _) => { }, (_, _) => { }, () => true);

        var opt = new Opt
        {
            WorkDir = @"C:\work",
            UseYtDlp = true,
            ConcurrentDownload = true,
            MaxThreads = 32,
            RetryCount = 3,
            Timeout = 100,
            LimitSpeed = 500,
            NoSystemProxy = true,
            NoMerge = true,
        };
        var findM = typeof(Engine).GetMethod("FindOnPath", BindingFlags.Static | BindingFlags.NonPublic);
        var found = (string?)findM!.Invoke(null, new object?[] { "yt-dlp.exe" });
        Console.WriteLine($"    [诊断] FindOnPath -> {found ?? "(null)"}");
        var cmd = BuildYtDlp(eng, "http://x/a.m3u8", "剧集 100%特别版", opt, ["Referer: http://r"])
                  ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("yt-dlp 命令含 -o", cmd.Contains("-o"));
        Check("yt-dlp 模板转义 %", cmd.Any(c => c.Contains("剧集 100%%特别版")));
        Check("yt-dlp 含 impersonate", cmd.Contains("--impersonate"));
        Check("yt-dlp 含并发分片", cmd.Contains("--concurrent-fragments") && cmd.Contains("32"));
        Check("yt-dlp 含重试次数", cmd.Contains("--retries") && cmd.Contains("3"));
        Check("yt-dlp 含超时", cmd.Contains("--socket-timeout") && cmd.Contains("100"));
        Check("yt-dlp 含限速", cmd.Contains("--limit-rate") && cmd.Contains("500K"));
        Check("yt-dlp 含请求头", cmd.Contains("--add-header"));
        Check("yt-dlp 含禁代理", cmd.Contains("--proxy"));
        Check("yt-dlp 含 --keep-video", cmd.Contains("--keep-video"));
        Check("yt-dlp 参数顺序正确（URL 在模板前）",
              cmd.IndexOf("http://x/a.m3u8") < cmd.IndexOf("-o"));

        var cmd2 = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", opt, ["Referer: http://r"], impersonate: false)
                   ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("降级模式不带 impersonate", !cmd2.Contains("--impersonate"));

        // ---- Cookie 来源 ----
        var cookieFile = @"C:\tmp\cookies.txt";
        var optFile = opt with { CookieFile = cookieFile };
        var cmdFile = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", optFile, ["Cookie: a=b", "Referer: http://r"])
                      ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("Cookie 文件参数进入 yt-dlp 命令",
              HasArg(cmdFile, "--cookies", cookieFile), string.Join(" ", cmdFile));
        Check("指定 Cookie 文件后不再传 header Cookie", !HasHeader(cmdFile, "Cookie:"));
        Check("指定 Cookie 文件后其它请求头照旧传入", HasHeader(cmdFile, "Referer: http://r"));

        var optBrowser = opt with { CookieBrowser = "chrome" };
        var cmdBrowser = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", optBrowser, ["Cookie: a=b"])
                         ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("浏览器 Cookie 参数进入 yt-dlp 命令",
              HasArg(cmdBrowser, "--cookies-from-browser", "chrome"), string.Join(" ", cmdBrowser));
        Check("指定浏览器 Cookie 后不再传 header Cookie", !HasHeader(cmdBrowser, "Cookie:"));

        var cmdPlain = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", opt, ["Cookie: a=b"])
                       ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("未指定 Cookie 来源时仍传 header Cookie", HasHeader(cmdPlain, "Cookie: a=b"));
        Check("未指定 Cookie 来源时不带 --cookies", !cmdPlain.Contains("--cookies"));
        Check("未指定 Cookie 来源时不带 --cookies-from-browser", !cmdPlain.Contains("--cookies-from-browser"));

        // ---- 中文诊断建议 ----
        var tips = Engine.DiagnoseYtDlpError("ERROR: Data must be padded to 16 byte boundary in CBC mode");
        Check("CBC 错误产出诊断建议", tips.Count > 0);
        Check("CBC 诊断提到 Cookie 文件", tips.Any(t => t.Contains("Cookie 文件")));
        Check("CBC 诊断提到 N_m3u8DL-RE", tips.Any(t => t.Contains("N_m3u8DL-RE")));
        // 归因必须落在「播放地址的时效签名过期」上，而不是 Cookie 作用域
        Check("CBC 诊断指出签名过期", tips.Any(t => t.Contains("签名过期")));
        Check("CBC 诊断给出重新获取播放地址的建议", tips.Any(t => t.Contains("重新获取播放地址")));
        Check("CBC 诊断不含 Cookie 域名作用域的错误归因",
              !tips.Any(t => t.Contains("限定在") || t.Contains("CDN 侧拿不到")));
        var tips2 = Engine.DiagnoseYtDlpError("Please consider loading cookies from a file or browser instead.");
        Check("cookie 警告产出诊断建议", tips2.Any(t => t.Contains("Cookie 文件") || t.Contains("--cookies")));
        Check("cookie 警告被标注为通用弃用警告",
              tips2.Any(t => t.Contains("通用弃用警告") || t.Contains("未必相关")));
        var tips3 = Engine.DiagnoseYtDlpError("ERROR: HTTP Error 403: Forbidden");
        Check("403 产出诊断建议", tips3.Any(t => t.Contains("403")));
        var tipsExp = Engine.DiagnoseYtDlpError("ERROR: HTTP Error 403: Forbidden: expired");
        Check("403 expired 产出签名过期诊断", tipsExp.Any(t => t.Contains("签名过期")));
        Check("403 expired 产出重新获取播放地址建议", tipsExp.Any(t => t.Contains("重新获取播放地址")));
        var tips4 = Engine.DiagnoseYtDlpError("ERROR: Unable to download webpage");
        Check("网页解析失败产出诊断建议", tips4.Any(t => t.Contains("Referer")));
        var tips5 = Engine.DiagnoseYtDlpError("ERROR: failed to load cookies");
        Check("Cookie 读取失败产出诊断建议", tips5.Any(t => t.Contains("cookies.txt")));
        Check("空错误不产出建议", Engine.DiagnoseYtDlpError("").Count == 0);
        // 证书过期与播放地址签名无关，不能被说成"签名过期"
        var tipsCert = Engine.DiagnoseYtDlpError("ERROR: SSL: certificate verify failed: certificate has expired");
        Check("证书过期不被误判为签名过期", !tipsCert.Any(t => t.Contains("签名过期")));

        // ---- 签名过期判定：只有播放地址签名过期才该重拉播放列表 ----
        Check("CBC 报错判定为签名过期",
              Engine.IsPlaylistSignatureExpired("ERROR: Data must be padded to 16 byte boundary in CBC mode"));
        Check("403 Forbidden: expired 判定为签名过期",
              Engine.IsPlaylistSignatureExpired("ERROR: 403 Forbidden: expired"));
        Check("HTTP 403 + expired 判定为签名过期",
              Engine.IsPlaylistSignatureExpired("ERROR: HTTP Error 403: expired"));
        Check("普通 403 不算签名过期",
              !Engine.IsPlaylistSignatureExpired("ERROR: HTTP Error 403: Forbidden"));
        Check("无关错误不算签名过期",
              !Engine.IsPlaylistSignatureExpired("ERROR: unable to download video data: HTTP Error 404: Not Found"));
        Check("证书过期不算播放地址签名过期",
              !Engine.IsPlaylistSignatureExpired("ERROR: SSL: certificate verify failed: certificate has expired"));
        Check("空错误不算签名过期", !Engine.IsPlaylistSignatureExpired(""));

        // ---- 降级判定：只对 Cookie / 解密 / 403 类失败降级 ----
        Check("CBC 错误触发降级", Engine.IsCookieOrDecryptFailure("ERROR: Data must be padded to 16 byte boundary in CBC mode"));
        Check("403 触发降级", Engine.IsCookieOrDecryptFailure("ERROR: HTTP Error 403: Forbidden"));
        Check("Forbidden 触发降级", Engine.IsCookieOrDecryptFailure("ERROR: Forbidden"));
        Check("无关错误不触发降级", !Engine.IsCookieOrDecryptFailure("ERROR: unable to download video data: HTTP Error 404: Not Found"));
        Check("普通错误不触发降级", !Engine.IsCookieOrDecryptFailure("ERROR: ffmpeg not found"));
        Check("空错误不触发降级", !Engine.IsCookieOrDecryptFailure(""));
        // 只有 cookie 作用域提示、没有硬证据时不该降级（该提示每次用 header Cookie 都会出现）
        Check("cookie 作用域提示 + 无关错误不触发降级",
              !Engine.IsCookieOrDecryptFailure("ERROR: ffmpeg not found", cookieScopeWarn: true));
        Check("cookie 作用域提示 + 网页失败触发降级",
              Engine.IsCookieOrDecryptFailure("ERROR: unable to download webpage", cookieScopeWarn: true));
    }

    /// <summary>命令行里是否存在 --add-header 后跟指定前缀的请求头。</summary>
    static bool HasHeader(List<string> cmd, string prefix)
    {
        for (var i = 0; i < cmd.Count - 1; i++)
            if (cmd[i] == "--add-header" &&
                cmd[i + 1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>命令行里是否存在 --flag value 这一对。</summary>
    static bool HasArg(List<string> cmd, string flag, string value)
    {
        for (var i = 0; i < cmd.Count - 1; i++)
            if (cmd[i] == flag && cmd[i + 1] == value) return true;
        return false;
    }

    static List<string>? BuildYtDlp(Engine eng, string url, string name, Opt o,
                                   List<string> headers, bool impersonate = true)
    {
        var m = typeof(Engine).GetMethod("BuildYtDlpCmd", BindingFlags.Instance | BindingFlags.NonPublic);
        return (List<string>?)m!.Invoke(eng, new object?[] { url, name, o, headers, impersonate });
    }

    // ============================================================
    // 3) 界面布局（新增的 Cookie 控件不能被裁切）
    // ============================================================
    static void LayoutTests()
    {
        var logs = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_layout_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var appdata = Path.Combine(dir, "appdata");
        Directory.CreateDirectory(Path.Combine(appdata, "NM3U8DL_GUI"));
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", appdata);
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            ForceDesignSize(f);

            var txtCookie = (TextBox)GetField(f, "txtCookieFile");
            var cmbBrowser = (ComboBox)GetField(f, "cmbCookieBrowser");

            Check("Cookie 文件输入框已创建", txtCookie is not null);
            Check("浏览器 Cookie 下拉框已创建", cmbBrowser is not null);
            Check("下拉框含 (不使用) 选项", cmbBrowser.Items.Contains("(不使用)"));
            foreach (var b in new[] { "chrome", "chromium", "edge", "firefox", "brave", "opera", "vivaldi", "safari", "whale" })
                Check($"下拉框含 {b}", cmbBrowser.Items.Contains(b));

            // 控件必须完整落在所属 GroupBox 的可视范围内，否则会被裁切
            var optGroup = txtCookie.Parent?.Parent as GroupBox;
            Check("Cookie 控件位于基础选项分组内", optGroup is not null);
            if (optGroup is not null)
            {
                // ctl.Bounds 相对直接父 TableLayoutPanel，而 DisplayRectangle 相对 GroupBox，坐标系错配。
                // 改用屏幕坐标比较，避免布局微调后误报（断言名保持原样）。
                var areaScreen = optGroup.RectangleToScreen(optGroup.DisplayRectangle);
                foreach (var (name, ctl) in new (string, Control)[]
                {
                    ("Cookie 文件输入框", txtCookie),
                    ("浏览器 Cookie 下拉框", cmbBrowser),
                })
                {
                    var ctlScreen = ctl.Parent!.RectangleToScreen(ctl.Bounds);
                    Check($"{name} 在分组可视范围内", areaScreen.Contains(ctlScreen),
                          $"ctl={ctlScreen} area={areaScreen}");
                }

                // 分组本身不能被下一个分组压住：否则最后一行会被裁掉
                // （「性能设置」已移到主界面左栏，这里改成校验紧随其后的「路径与依赖」）
                var root = optGroup.Parent;
                var next = root?.Controls.OfType<GroupBox>()
                               .FirstOrDefault(g => g.Text == "路径与依赖");
                Check("路径与依赖分组存在", next is not null);
                if (next is not null)
                {
                    var gap = next.Top - optGroup.Bottom;
                    Check("基础选项分组未与下一个分组重叠", gap >= 0,
                          $"optBottom={optGroup.Bottom} nextTop={next.Top}");
                }

                // ---- 需求：RE 引擎专用栏取消「展开 ▾」，内容直接显示 ----
                // 浮层平时是隐藏的（Visible=false 会让所有子控件的 Visible 都是 false），
                // 所以先打开浮层再校验「内容常显」，否则这条断言永远读不到真实可见性。
                Call(f, "OpenSettings");
                Application.DoEvents();
                var advGroup = root?.Controls.OfType<GroupBox>()
                                    .FirstOrDefault(g => g.Text.StartsWith("RE 引擎专用"));
                Check("RE 引擎专用分组存在", advGroup is not null);
                if (advGroup is not null && root is not null)
                {
                    Check("RE 引擎专用不再有展开/收起键",
                          !AllControls(advGroup).OfType<LinkLabel>().Any(),
                          string.Join(",", AllControls(advGroup).OfType<LinkLabel>().Select(l => l.Text)));
                    Check("RE 引擎专用分组常显且高度容得下内容", advGroup.Height >= 120, advGroup.Height.ToString());
                    var advFlow = AllControls(advGroup).OfType<FlowLayoutPanel>().FirstOrDefault();
                    Check("RE 引擎专用 7 个勾选项可见",
                          advFlow is not null && advFlow.Visible && advFlow.Controls.Count == 7,
                          advFlow is null ? "(null)" : $"{advFlow.Visible}/{advFlow.Controls.Count}");
                    // 分组底边不能越出浮层内容区，否则最后一行会被裁掉
                    Check("RE 引擎专用分组未越出内容区",
                          advGroup.Top + advGroup.Height <= root.Height,
                          $"groupBottom={advGroup.Top + advGroup.Height} host={root.Height}");
                }
                Call(f, "CloseSettings");
                Application.DoEvents();
            }

            // 快照能正确读到两个新值
            SetText(f, "txtCookieFile", @"C:\tmp\c.txt");
            SetBrowser(f, "edge");
            var opt = (Opt)Call(f, "Snapshot");
            Check("快照读到 Cookie 文件", opt.CookieFile == @"C:\tmp\c.txt", opt.CookieFile);
            Check("快照读到浏览器 Cookie", opt.CookieBrowser == "edge", opt.CookieBrowser);
            SetBrowser(f, "(不使用)");
            opt = (Opt)Call(f, "Snapshot");
            Check("快照把 (不使用) 视为空", opt.CookieBrowser == "", opt.CookieBrowser);

            // ---- 「失败自动重试」：轮数框落在性能设置分组内 + 0 能被持久化 ----
            var nudAuto = (NumericUpDown)GetField(f, "nudAutoRetry");
            var perfGroup = nudAuto.Parent?.Parent as GroupBox;
            Check("自动重试轮数框位于性能设置分组内",
                  perfGroup is not null && perfGroup.Text == "② 性能设置", perfGroup?.Text ?? "(null)");
            if (perfGroup is not null)
            {
                var area = perfGroup.RectangleToScreen(perfGroup.DisplayRectangle);
                var ctl = nudAuto.Parent!.RectangleToScreen(nudAuto.Bounds);
                Check("自动重试轮数框在分组可视范围内", area.Contains(ctl), $"ctl={ctl} area={area}");
            }
            Check("自动重试轮数范围为 0..5", nudAuto.Minimum == 0 && nudAuto.Maximum == 5,
                  $"{nudAuto.Minimum}..{nudAuto.Maximum}");
            Check("失败自动重试开关已创建",
                  ((Dictionary<string, CheckBox>)GetField(f, "chk")).ContainsKey("autoRetryFailed"));

            // 0 = 关闭是有效值：SetNud 把 ≤0 一律当缺省值，所以载入路径不能复用它，这里锁死行为
            foreach (var v in new decimal[] { 0, 4 })
            {
                SetNud(f, "nudAutoRetry", v);
                Call(f, "SaveSettings");
                using var reloaded = new MainForm();
                reloaded.Show();
                Application.DoEvents();
                var back = ((NumericUpDown)GetField(reloaded, "nudAutoRetry")).Value;
                Check($"自动重试轮数 {v} 能持久化并原样读回", back == v, back.ToString());
            }

            // ---- 右下角依赖状态点：刷新时机 / 视觉顺序 / 基线对齐 ----
            var txtExeBox = (TextBox)GetField(f, "txtExe");
            var dotRe = (Label)GetField(f, "dotRe");

            // 回归：BuildUi 尾部的 RefreshDependencyDots() 跑在 LoadSettings 之前，
            // 那时 txtExe 还是空串 → 右下角会一直显示灰色「RE 不可用」，即使路径已探测到。
            var exePath = txtExeBox.Text.Trim();
            Check("RE 路径已填充", exePath.Length > 0, exePath);
            Check("RE 状态点与路径实际存在性一致",
                  (dotRe.ForeColor == Theme.DotOn) == File.Exists(exePath),
                  $"dot={dotRe.ForeColor} exists={File.Exists(exePath)}");

            // 用户手选/清空 RE 路径后，状态点必须立刻跟随
            var exeBackup = txtExeBox.Text;
            txtExeBox.Text = Path.Combine(dir, "definitely-missing.exe");
            Application.DoEvents();
            Check("RE 路径失效后状态点变灰", dotRe.ForeColor == Theme.DotOff, dotRe.ForeColor.ToString());
            txtExeBox.Text = exeBackup;
            Application.DoEvents();
            Check("RE 路径恢复后状态点重新变绿",
                  (dotRe.ForeColor == Theme.DotOn) == File.Exists(exeBackup), dotRe.ForeColor.ToString());

            // 视觉顺序：流布局是 RightToLeft，先添加的排最右，写反了就成了 yt-dlp → RE → FFmpeg
            var depFlow = dotRe.Parent!.Parent!;
            string[] visualOrder = depFlow.Controls.Cast<Control>()
                .OrderBy(c => c.Left)
                .Select(c => c.Controls.Cast<Control>().OfType<Label>().Last(l => l.Text != "●").Text)
                .ToArray();
            Check("依赖状态点视觉顺序为 FFmpeg → N_m3u8DL-RE → yt-dlp",
                  visualOrder.SequenceEqual(["FFmpeg", "N_m3u8DL-RE", "yt-dlp"]),
                  string.Join(" | ", visualOrder));

            // 11pt 的「●」曾比 8pt 文字高出 2px，肉眼可见不齐
            foreach (var chip in depFlow.Controls.Cast<Control>())
            {
                var dot = chip.Controls.Cast<Control>().OfType<Label>().First(l => l.Text == "●");
                var lbl = chip.Controls.Cast<Control>().OfType<Label>().Last(l => l.Text != "●");
                var dr = dot.RectangleToScreen(dot.Bounds);
                var lr = lbl.RectangleToScreen(lbl.Bounds);
                var delta = dr.Top + dr.Height / 2 - (lr.Top + lr.Height / 2);
                Check($"圆点与文字「{lbl.Text}」垂直居中对齐", delta == 0, $"Δ={delta}");
            }

            // ---- 状态栏左侧文案：启动时也必须反映已载入的任务数 ----
            // 回归：初始 Text 写死「就绪」，而唯一会更新的 AddTask 只在 batchRunning 时刷新，
            // 于是正常启动永远只显示「就绪」两个字。
            var lblStatus = (Label)GetField(f, "lblStatus");
            Check("启动后状态栏显示任务数", lblStatus.Text == "就绪 · 共 0 个任务", lblStatus.Text);

            Call(f, "AddTask", "状态栏计数任务", "http://example.com/a.m3u8", "待下载", null, false);
            Call(f, "SaveTasks");
            using (var f2 = new MainForm())
            {
                f2.Show();
                Application.DoEvents();
                var lbl2 = (Label)GetField(f2, "lblStatus");
                Check("重启后状态栏反映已载入的任务数", lbl2.Text.Contains("共 1 个任务"), lbl2.Text);
            }

            // ---- 顶栏：设置入口必须在右上角，且顶栏本身真的挂到了窗体上 ----
            // 回归：topBar 构造完从未 Controls.Add，导致标题区、快捷键提示、
            // 右上角设置按钮全都不显示，用户只能在左栏找「更多设置 ▸」。
            var topBar = f.Controls.Cast<Control>().FirstOrDefault(c => c.Dock == DockStyle.Top);
            Check("顶栏已挂载到窗体", topBar is not null);
            if (topBar is not null)
            {
                Check("顶栏高度为 64", topBar.Height == 64, topBar.Height.ToString());
                var gear = topBar.Controls.Cast<Control>()
                    .SelectMany(c => c.Controls.Cast<Control>().Prepend(c))
                    .OfType<Button>()
                    .FirstOrDefault(b => b.Text == "\uE713");
                Check("右上角存在齿轮设置按钮", gear is not null);
                if (gear is not null)
                {
                    var gs = gear.RectangleToScreen(gear.Bounds);
                    Check("齿轮按钮位于顶栏右侧",
                          gs.Left > topBar.Width / 2, $"gearLeft={gs.Left} barWidth={topBar.Width}");
                    Check("齿轮按钮可见且尺寸合理",
                          gear.Visible && gear.Width >= 32 && gear.Height >= 28,
                          $"{gear.Width}x{gear.Height} visible={gear.Visible}");

                    // 入口要真的能打开浮层，而不只是摆在那里
                    gear.PerformClick();
                    Application.DoEvents();
                    var overlay = (Control)GetField(f, "settingsOverlay");
                    Check("点击齿轮能打开设置浮层", overlay.Visible);
                    Call(f, "CloseSettings");
                    Application.DoEvents();
                    Check("关闭后设置浮层隐藏", !overlay.Visible);
                }
            }

            // 左栏的「更多设置 ▸」次级入口应已删除（入口唯一化）
            var staleLink = AllControls(f).OfType<LinkLabel>()
                .FirstOrDefault(l => l.Text.Contains("更多设置"));
            Check("左栏「更多设置」链接已移除", staleLink is null, staleLink?.Text ?? "(none)");

            // 「清空全部任务」曾经是左栏底部一个红色文字链接，已并入工具栏按钮流
            var staleClearLink = AllControls(f).OfType<LinkLabel>()
                .FirstOrDefault(l => l.Text.Contains("清空全部任务"));
            Check("左栏「清空全部任务」链接已移除", staleClearLink is null, staleClearLink?.Text ?? "(none)");

            // ---- 并发：状态栏与设置浮层双向同步，并立即作用于调度 ----
            var nudInline = (NumericUpDown)GetField(f, "nudConcInline");
            var nudConcBox = (NumericUpDown)GetField(f, "nudConc");
            Check("状态栏并发框已创建", nudInline is not null);
            Check("状态栏并发框在状态栏内可见",
                  nudInline!.Visible && nudInline.Parent is not null, nudInline.Parent?.GetType().Name ?? "");

            nudInline.Value = 6;
            Application.DoEvents();
            Check("状态栏改并发 → 设置里同步", nudConcBox.Value == 6, nudConcBox.Value.ToString());
            Check("状态栏改并发 → 调度上限同步",
                  (int)GetField(f, "concurrentLimit")! == 6,
                  GetField(f, "concurrentLimit")!.ToString()!);
            nudConcBox.Value = 2;
            Application.DoEvents();
            Check("设置改并发 → 状态栏同步", nudInline.Value == 2, nudInline.Value.ToString());
            Check("设置改并发 → 调度上限同步",
                  (int)GetField(f, "concurrentLimit")! == 2,
                  GetField(f, "concurrentLimit")!.ToString()!);

            // ---- 拖放入口：原「① 添加任务」卡片取消后，拖放改挂到队列表格与窗体本身 ----
            var gridBox = (DataGridView)GetField(f, "grid")!;
            Check("队列表格接受文件拖放", gridBox.AllowDrop);
            Check("窗体本身接受文件拖放", f.AllowDrop);
            var staleFields = typeof(MainForm).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Select(x => x.Name).ToList();
            Check("「① 添加任务」卡片已取消",
                  !staleFields.Contains("dropZone") && !staleFields.Contains("manualInput"),
                  string.Join(",", staleFields.Where(n => n is "dropZone" or "manualInput")));

            // ---- 左栏：下载选项占原添加任务的位置，性能设置接在它下面 ----
            var optsGroup = GetField(f, "optsGroup") as GroupBox;
            var perfGroupBox = GetField(f, "perfGroup") as GroupBox;
            Check("左栏①号卡片是下载选项", optsGroup?.Text == "① 下载选项", optsGroup?.Text ?? "(null)");
            Check("下载选项卡片在左栏顶部",
                  optsGroup is not null && optsGroup.Parent is not null &&
                  optsGroup.RectangleToScreen(optsGroup.Bounds).Top < f.ClientSize.Height / 2,
                  optsGroup is null ? "(null)" : optsGroup.RectangleToScreen(optsGroup.Bounds).ToString());
            Check("性能设置卡片紧跟在下载选项下方",
                  optsGroup is not null && perfGroupBox is not null &&
                  perfGroupBox.RectangleToScreen(perfGroupBox.Bounds).Top >=
                  optsGroup.RectangleToScreen(optsGroup.Bounds).Bottom,
                  $"{optsGroup?.RectangleToScreen(optsGroup.Bounds)} / {perfGroupBox?.RectangleToScreen(perfGroupBox.Bounds)}");
            Check("性能设置已从设置浮层移出（不在 compatHost 里）",
                  perfGroupBox?.Parent?.GetType().Name == "TableLayoutPanel",
                  perfGroupBox?.Parent?.GetType().Name ?? "(null)");
            Check("性能设置标题带小圆标序号②", perfGroupBox?.Text == "② 性能设置",
                  perfGroupBox?.Text ?? "(null)");

            // ---- 需求：开关行距紧凑（此前 Percent 行会把 6 行均分到整张卡片，窗口一高就散开）----
            float sc = f.DeviceDpi / 96f;
            var toggleHost = optsGroup?.Controls.Cast<Control>().OfType<TableLayoutPanel>().FirstOrDefault();
            Check("下载选项用表格承载 6 个开关", toggleHost is not null,
                  optsGroup?.Controls.Count.ToString() ?? "(null)");
            if (toggleHost is not null && optsGroup is not null)
            {
                var rowHeights = toggleHost.GetRowHeights().Where(x => x > 1).Distinct().ToArray();
                Check("6 个开关行高一致且紧凑（34 逻辑像素 ± 4）",
                      rowHeights.Length == 1 && Math.Abs(rowHeights[0] - 34 * sc) <= 4 * sc,
                      string.Join(",", rowHeights));
                var expected = optsGroup.DisplayRectangle.Top + toggleHost.Height + optsGroup.Padding.Bottom;
                Check("下载选项卡片高度贴合内容（底部无多余空白）",
                      Math.Abs(optsGroup.Height - expected) <= 2,
                      $"h={optsGroup.Height} expected={expected}");
                // 拉高 240px：卡片高度必须纹丝不动，否则就是行高又被 Percent 抻开了
                var beforeOpts = optsGroup.Height;
                var beforePerf = perfGroupBox?.Height ?? 0;
                f.Height += 240;
                Application.DoEvents();
                Check("拉高窗口后下载选项卡片高度不变", optsGroup.Height == beforeOpts,
                      $"{beforeOpts} → {optsGroup.Height}");
                Check("拉高窗口后性能设置卡片高度不变", perfGroupBox?.Height == beforePerf,
                      $"{beforePerf} → {perfGroupBox?.Height}");
                f.Height -= 240;
                Application.DoEvents();
            }

            // ---- 需求：性能设置 6 行标签与输入框严格对齐 ----
            var perfNames = new[] { "nudConc", "nudThreads", "nudRetry", "nudTimeout", "nudSpeed", "nudAutoRetry" };
            var perfNuds = perfNames.Select(n => (NumericUpDown)GetField(f, n)!).ToArray();
            var nudLefts = perfNuds.Select(n => n.Parent!.RectangleToScreen(n.Bounds).Left).Distinct().ToArray();
            Check("6 个性能输入框左边缘在同一条竖线上", nudLefts.Length == 1, string.Join(",", nudLefts));
            var nudWidths = perfNuds.Select(n => n.Width).Distinct().ToArray();
            Check("6 个性能输入框宽度一致", nudWidths.Length == 1, string.Join(",", nudWidths));
            var badRows = new List<string>();
            foreach (var n in perfNuds)
            {
                var tlp = (TableLayoutPanel)n.Parent!;
                var lbl = tlp.Controls.Cast<Control>().OfType<Label>()
                             .FirstOrDefault(l => tlp.GetRow(l) == tlp.GetRow(n));
                if (lbl is null) { badRows.Add($"{n.Name}:缺少标签"); continue; }
                // Bounds 是父容器坐标：必须用父容器的 RectangleToScreen 换算，
                // 直接用控件自己的 RectangleToScreen(Bounds) 会把控件自身偏移再加一遍（差值正好翻倍）。
                var lr = tlp.RectangleToScreen(lbl.Bounds);
                var nr = tlp.RectangleToScreen(n.Bounds);
                var delta = lr.Top + lr.Height / 2 - (nr.Top + nr.Height / 2);
                if (Math.Abs(delta) > 2) badRows.Add($"{lbl.Text}Δ={delta} lbl={lr} nud={nr}");
            }
            Check("性能设置 6 行标签与输入框垂直居中且不错行", badRows.Count == 0, string.Join(";", badRows));

            // ---- 需求：卡片圆角必须真裁出来（只画圆角路径时四角会留白色直角块）----
            foreach (var (name, g) in new (string, GroupBox?)[] { ("下载选项", optsGroup), ("性能设置", perfGroupBox) })
            {
                if (g is null) { Check($"{name}卡片已应用圆角 Region", false, "(null)"); continue; }
                var reg = g.Region;
                var cornerCut = reg is not null && !reg.IsVisible(new Point(1, 1));
                var edgeKept = reg is not null && reg.IsVisible(new Point(g.Width / 2, 2));
                Check($"{name}卡片已应用圆角 Region（四角被裁掉、边线保留）", cornerCut && edgeKept,
                      reg is null ? "(null)" : $"corner={!cornerCut} edgeKept={edgeKept}");
            }

            // ---- 需求：运行日志下方的 N_m3u8DL-RE 链接移除，链接语义并入底边依赖标签 ----
            var strayLinks = AllControls(f).OfType<LinkLabel>()
                                    .Where(l => l.Text.Contains("N_m3u8DL-RE")).ToList();
            Check("运行日志下方不再有 N_m3u8DL-RE 独立链接", strayLinks.Count == 0,
                  string.Join(",", strayLinks.Select(l => l.Text)));
            var reChipLabel = AllControls(f).OfType<Label>()
                                     .FirstOrDefault(l => l.Text == "N_m3u8DL-RE");
            Check("底边 N_m3u8DL-RE 是可点击的文字链接",
                  reChipLabel is not null && reChipLabel.Cursor == Cursors.Hand && reChipLabel.Font.Underline,
                  reChipLabel is null ? "(null)" : $"{reChipLabel.Cursor}/{reChipLabel.Font.Underline}");

            // ---- 工具栏：六个动作按钮同尺寸同顺序；主按钮「开始下载」停在工具栏最右 ----
            // 回归：「打开目录」原本是 96×32 的实心 Accent 孤块，比旁边 AutoSize 的
            // Ghost 按钮大一圈，还挡在主按钮该在的位置；「开始下载」则孤立地压左栏底部。
            var startBtn = (Button)GetField(f, "btnStart")!;
            var bar = startBtn.Parent?.Parent; // btnStart → startHost → toolBar
            Check("开始下载按钮已移入工具栏", bar is not null && bar.Height == 44,
                  $"{startBtn.Parent?.GetType().Name} h={bar?.Height}");
            if (bar is not null)
            {
                var flow = bar.Controls.Cast<Control>().OfType<FlowLayoutPanel>().FirstOrDefault();
                var acts = flow is null ? new List<Button>() : flow.Controls.Cast<Button>().ToList();
                Check("工具栏动作按钮共 7 个", acts.Count == 7, acts.Count.ToString());
                Check("按钮顺序：粘贴/复制/重试/删除/定位文件/打开目录/清空",
                      string.Join("/", acts.Select(b => b.Text)) ==
                      "粘贴/复制选中/重试失败/删除选中/定位文件/打开目录/清空全部任务",
                      string.Join("/", acts.Select(b => b.Text)));
                // 需求：粘贴按钮必须排在「复制选中」之前
                if (acts.Count >= 2)
                    Check("「粘贴」排在「复制选中」之前",
                          acts[0].Text == "粘贴" && acts[1].Text == "复制选中",
                          $"{acts[0].Text}/{acts[1].Text}");

                var sizes = acts.Select(b => $"{b.Width}x{b.Height}").Distinct().ToList();
                Check("七个动作按钮尺寸完全一致", sizes.Count == 1, string.Join(",", sizes));
                if (acts.Count == 7)
                {
                    Check("「打开目录」不再比前面按钮大",
                          acts[4].Width == acts[3].Width && acts[4].Height == acts[3].Height,
                          $"{acts[4].Width}x{acts[4].Height} vs {acts[3].Width}x{acts[3].Height}");
                    Check("「打开目录」与前面按钮同为浅色描边样式",
                          acts[4].BackColor == acts[3].BackColor &&
                          acts[4].ForeColor == acts[3].ForeColor && acts[4].Font.Size == acts[3].Font.Size,
                          $"{acts[4].BackColor}/{acts[4].ForeColor} vs {acts[3].BackColor}/{acts[3].ForeColor}");
                    // 「清空全部任务」已从左栏文字链接并入按钮流：同尺寸、危险色、与浏览类动作拉开间距
                    var clearAct = acts[6];
                    var normalGap = acts[4].Left - acts[3].Right;
                    Check("「清空全部任务」已并入按钮流",
                          clearAct.Text == "清空全部任务" && clearAct.AutoSize == false,
                          clearAct.Text);
                    Check("「清空全部任务」保留危险色",
                          clearAct.ForeColor == Theme.Danger, clearAct.ForeColor.ToString());
                    Check("「清空全部任务」与前一个按钮拉开间距",
                          clearAct.Left - acts[4].Right >= normalGap * 2,
                          $"{clearAct.Left - acts[4].Right}px (普通间距 {normalGap}px)");
                }

                var centers = acts
                    .Select(b => b.Parent!.RectangleToScreen(b.Bounds).Top + b.Height / 2)
                    .ToList();
                centers.Add(startBtn.Parent!.RectangleToScreen(startBtn.Bounds).Top + startBtn.Height / 2);
                Check("开始下载与动作按钮中心线一致", centers.Distinct().Count() == 1, string.Join(",", centers));

                // 最小窗口宽度下七个按钮不能挤到「开始下载」身上
                flow!.PerformLayout();
                Check("按钮流未侵入主按钮区域",
                      flow.Right <= startBtn.Parent!.Left,
                      $"flowRight={flow.Right} startLeft={startBtn.Parent.Left} " +
                      $"toolBar={bar.Width} client={f.ClientSize.Width} dpi={f.DeviceDpi} " +
                      $"screen={Screen.PrimaryScreen!.Bounds.Width}");

                var host = startBtn.Parent!;
                Check("开始下载贴在宿主面板右侧且不越界",
                      startBtn.Right <= host.ClientSize.Width && host.Right == bar.ClientSize.Width,
                      $"btn={startBtn.Bounds} host={host.Bounds} hostClient={host.ClientSize} bar={bar.ClientSize}");
                Check("开始下载不再位于左栏",
                      startBtn.RectangleToScreen(startBtn.Bounds).Left > f.ClientSize.Width / 2,
                      startBtn.RectangleToScreen(startBtn.Bounds).Left.ToString());
                Check("开始下载保留主色实心样式",
                      startBtn.BackColor == Theme.Accent && startBtn.ForeColor == Color.White,
                      $"{startBtn.BackColor}/{startBtn.ForeColor}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ============================================================
    // 3.2) 队列鼠标操作 / 增删改 / 托盘（新需求回归）
    // ============================================================
    static void QueueOpsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_qops_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var appdata = Path.Combine(dir, "appdata");
        Directory.CreateDirectory(Path.Combine(appdata, "NM3U8DL_GUI"));
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", appdata);
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            var grid = (DataGridView)GetField(f, "grid");
            for (var i = 1; i <= 3; i++)
                Call(f, "AddTask", $"任务{i}", $"http://x/{i}.m3u8", "待下载", null, false);
            Application.DoEvents();

            // ---- 右键：先选中光标所在行，再弹菜单（否则菜单动作会落到上一次选中的行）----
            grid.ClearSelection();
            grid.Rows[0].Selected = true;
            Application.DoEvents();
            // 必须按参数类型取重载：DataGridView 有两个 OnCellMouseDown，只按名字取会 AmbiguousMatch
            var onCellMouseDown = typeof(DataGridView).GetMethod(
                "OnCellMouseDown", BindingFlags.Instance | BindingFlags.NonPublic,
                null, [typeof(DataGridViewCellMouseEventArgs)], null)!;
            onCellMouseDown.Invoke(grid, [new DataGridViewCellMouseEventArgs(
                0, 2, 10, 10, new MouseEventArgs(MouseButtons.Right, 1, 0, 0, 0))]);
            Application.DoEvents();
            Check("右键点第 3 行 → 该行被选中", grid.Rows[2].Selected, string.Join(",", grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)));
            Check("右键点未选中行 → 只保留一行选中", grid.SelectedRows.Count == 1, grid.SelectedRows.Count.ToString());

            // ---- 右键菜单包含下载软件该有的动作 ----
            var menu = grid.ContextMenuStrip;
            var items = menu is null ? [] : menu.Items.Cast<ToolStripItem>().Select(i => i.Text).ToList();
            foreach (var want in new[] { "打开文件", "打开所在文件夹", "复制链接", "重命名…", "粘贴任务",
                                         "导入任务文件…", "手动输入任务…", "上移", "下移", "删除选中任务", "清空全部任务" })
                Check($"右键菜单含「{want}」", items.Contains(want), string.Join("|", items));

            // ---- 上移 / 下移：模型与表格必须同步 ----
            Call(f, "MoveSelected", -1);
            Application.DoEvents();
            Check("上移后模型顺序正确",
                  string.Join(",", Queue(f).Select(t => t.Name)) == "任务1,任务3,任务2",
                  string.Join(",", Queue(f).Select(t => t.Name)));
            Check("上移后表格名称同步",
                  Convert.ToString(grid.Rows[1].Cells[0].Value) == "任务3",
                  Convert.ToString(grid.Rows[1].Cells[0].Value));
            Check("上移后选中行跟随任务",
                  grid.SelectedRows.Count == 1 && grid.SelectedRows[0].Index == 1,
                  string.Join(",", grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)));

            // ---- 删除中间一条：剩下两条的名称/行号不能错位 ----
            grid.ClearSelection();
            grid.Rows[1].Selected = true;
            Call(f, "DeleteSelected");
            Application.DoEvents();
            Check("删除后模型只剩 2 条", Queue(f).Count == 2, Queue(f).Count.ToString());
            Check("删除后名称按顺序前进",
                  string.Join(",", Queue(f).Select(t => t.Name)) == "任务1,任务2",
                  string.Join(",", Queue(f).Select(t => t.Name)));
            Check("删除后表格行数与模型一致", grid.Rows.Count == Queue(f).Count,
                  $"{grid.Rows.Count}/{Queue(f).Count}");
            Check("删除后第 2 行仍是任务2",
                  Convert.ToString(grid.Rows[1].Cells[0].Value) == "任务2",
                  Convert.ToString(grid.Rows[1].Cells[0].Value));

            // ---- 任务 id 稳定：删除不影响其它任务的状态回写 ----
            var idOfTask2 = Queue(f)[1].Id;
            var task2 = Queue(f)[1];
            Call(f, "SetStatus", idOfTask2, "完成");
            Application.DoEvents();
            Check("按 id 回写状态落到正确的行",
                  Convert.ToString(grid.Rows[1].Cells[1].Value) == "完成",
                  Convert.ToString(grid.Rows[1].Cells[1].Value));
            Call(f, "SetStatus", 99999, "完成");
            Application.DoEvents();
            Check("已删除任务的 id 回写被安全丢弃",
                  Convert.ToString(grid.Rows[0].Cells[1].Value) == "待下载",
                  Convert.ToString(grid.Rows[0].Cells[1].Value));
            _ = task2;

            // ---- 托盘：默认开启「最小化到托盘」，最小化后窗口隐藏、托盘图标可见，能恢复 ----
            Check("默认为最小化到托盘",
                  ((Dictionary<string, CheckBox>)GetField(f, "chk"))["minimizeToTray"].Checked);
            f.WindowState = FormWindowState.Minimized;
            Application.DoEvents();
            Application.DoEvents();
            Check("最小化后窗口隐藏", !f.Visible, f.Visible.ToString());
            var tray = typeof(MainForm).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f);
            Check("最小化后已创建托盘图标", tray is NotifyIcon, tray?.GetType().Name ?? "(null)");
            Check("托盘图标可见", tray is NotifyIcon ni && ni.Visible);
            Call(f, "RestoreFromTray");
            Application.DoEvents();
            Check("从托盘恢复后窗口可见", f.Visible, f.Visible.ToString());
            Check("从托盘恢复后回到普通状态", f.WindowState == FormWindowState.Normal, f.WindowState.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ============================================================
    // 3.5) 可执行文件探测（RE / FFmpeg）
    // ============================================================
    static void PathDetectTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_probe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), []);
            File.WriteAllBytes(Path.Combine(dir, "N_m3u8DL-RE.exe"), []);
            Environment.SetEnvironmentVariable("PATH", dir + ";" + oldPath);

            var t = typeof(MainForm);
            var onPath = t.GetMethod("OnPath", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check("OnPath 能命中带扩展名的名字", (bool)onPath.Invoke(null, ["ffmpeg.exe"])!);
            // 回归：Windows 的 File.Exists 不做 PATHEXT 解析，传 "ffmpeg" 时必须自己补 .exe，
            // 否则 PATH 里明明有 ffmpeg.exe，开始下载时也会误报"未在系统 PATH 中找到 FFmpeg"
            Check("OnPath 对无扩展名的名字也能命中（ffmpeg 不再误报）",
                  (bool)onPath.Invoke(null, ["ffmpeg"])!);
            Check("PATH 里不存在的名字仍返回 false",
                  !(bool)onPath.Invoke(null, ["definitely-not-on-path-xyz"])!);

            var detect = t.GetMethod("DetectReExecutable", BindingFlags.Static | BindingFlags.NonPublic)!;
            var found = (string)detect.Invoke(null, null)!;
            Check("DetectReExecutable 能从 PATH 找到 RE",
                  found.Equals(Path.Combine(dir, "N_m3u8DL-RE.exe"), StringComparison.OrdinalIgnoreCase), found);

            // 注册表兜底：执行过程不能抛（只读真实注册表，命中与否都算通过）
            var inReg = t.GetMethod("FindInRegistryPath", BindingFlags.Static | BindingFlags.NonPublic)!;
            var regHit = inReg.Invoke(null, ["N_m3u8DL-RE.exe"]) as string;
            Check("注册表 PATH 探测可正常执行（不抛异常）",
                  regHit is null || File.Exists(regHit), regHit ?? "(未命中)");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ============================================================
    // 4) 端到端
    // ============================================================
    static void E2ETests()
    {
        var work = Path.Combine(Path.GetTempPath(), "m3u8dl_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Path.Combine(work, "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(work, "dl"));
        var fakeSrc = Environment.GetEnvironmentVariable("FAKE_EXE");
        if (string.IsNullOrEmpty(fakeSrc) || !File.Exists(fakeSrc))
        {
            Check("找到假下载器", false, fakeSrc ?? "(未设置 FAKE_EXE)");
            return;
        }
        var ytExe = Path.Combine(bin, "yt-dlp.exe");
        var reExe = Path.Combine(bin, "N_m3u8DL-RE.exe");
        File.Copy(fakeSrc, ytExe, true);
        File.Copy(fakeSrc, reExe, true);

        Environment.SetEnvironmentVariable("PATH", bin + ";" + Environment.GetEnvironmentVariable("PATH"));
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(work, "appdata"));
        // 隔离目录必须先建好，否则 SaveTasks 写 tasks.json 时父目录不存在会失败，
        // 导致"已写入 tasks.json"断言与"重启恢复任务"场景连锁出错。
        Directory.CreateDirectory(Path.Combine(work, "appdata", "NM3U8DL_GUI"));

        try
        {
            // --- 场景 1：yt-dlp 正常下载 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                AddTask(f, "任务A", "http://x/a.m3u8");
                AddTask(f, "任务B", "http://x/b.m3u8");
                Start(f);
                WaitDone(f, 2);
                var st = Statuses(f);
                Check("yt-dlp 两个任务都完成", st.Count == 2 && st.All(s => s == "完成"), string.Join(",", st));
                Check("成品文件已生成",
                      File.Exists(Path.Combine(work, "dl", "任务A.mp4")) &&
                      File.Exists(Path.Combine(work, "dl", "任务B.mp4")));
                Check("日志含下载完成", Log(f).Contains("yt-dlp 下载完成"), Tail(Log(f)));
                Check("进度到 100", Progress(f).All(p => p == 100), string.Join(",", Progress(f)));
            }

            // --- 场景 2：成品已存在 → 跳过 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                File.WriteAllBytes(Path.Combine(work, "dl", "任务C.mp4"), new byte[2048]);
                var before = File.GetLastWriteTimeUtc(Path.Combine(work, "dl", "任务C.mp4"));
                AddTask(f, "任务C", "http://x/c.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("成品已存在被跳过", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("日志说明跳过", Log(f).Contains("成品已存在，跳过重复下载"), Tail(Log(f)));
                Check("成品文件未被重写",
                      File.GetLastWriteTimeUtc(Path.Combine(work, "dl", "任务C.mp4")) == before);
                // "成品已存在跳过" 不是失败，绝不能因此降级到 RE
                Check("跳过成品不触发降级", !Log(f).Contains("自动改用 N_m3u8DL-RE"), Tail(Log(f)));
            }

            // --- 场景 3：yt-dlp 合流失败（只留分离流） ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "partial");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                AddTask(f, "任务D", "http://x/d.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("合流失败判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("提示 FFmpeg 缺失", Log(f).Contains("FFmpeg"), Tail(Log(f)));
                Check("分离流不算成品", !File.Exists(Path.Combine(work, "dl", "任务D.mp4")));
            }

            // --- 场景 4：impersonate 不支持 → 自动降级重试 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "imp");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                AddTask(f, "任务E", "http://x/e.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("降级重试后成功", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("日志说明降级", Log(f).Contains("不支持 impersonate"), Tail(Log(f)));
            }

            // --- 场景 5：RE 引擎 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "useYtDlp", false);
                SetText(f, "txtExe", reExe);
                AddTask(f, "任务F", "http://x/f.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("RE 模式完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("RE 成品已生成", File.Exists(Path.Combine(work, "dl", "任务F.mp4")));
                Check("RE 探测到支持的参数", Log(f).Contains("已探测 RE 支持"), Tail(Log(f)));
            }

            // --- 场景 6：RE 没有产出文件 → 失败 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "nofile");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "useYtDlp", false);
                SetText(f, "txtExe", reExe);
                AddTask(f, "任务G", "http://x/g.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("无输出判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("提示未检测到输出文件", Log(f).Contains("未检测到输出文件"), Tail(Log(f)));
            }

            // --- 场景 7：粘贴 / 去重 / 复制格式 ---
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                // 剪贴板可能被其他程序独占（元气壁纸小组件、百度输入法等）。
                // PasteTasks 在读不到剪贴板时会弹模态对话框 —— 没人点确定就会挂死，
                // 所以先把剪贴板"写 + 读"都验证一遍，不可用就整个场景跳过。
                var pasteText = "剧集X|http://x/x.m3u8|REF|UA|CK|OR|CD\n# 注释行\n坏行没有竖线";
                if (!TryClipboardSet(pasteText) || !TryClipboardGet(out _))
                {
                    Console.WriteLine("  ⚠ 剪贴板被其他程序长期占用，跳过粘贴/复制场景");
                }
                else
                {
                    Paste(f);
                    var pasted = Queue(f).Count;
                    if (pasted == 0)
                    {
                        // 粘贴的瞬间剪贴板被别的程序抢走：MainForm 弹窗后直接返回，什么都没粘上。
                        // 这是环境噪声而不是程序缺陷，整轮跳过，不计失败。
                        Console.WriteLine("  ⚠ 粘贴时剪贴板被占用，本轮跳过粘贴/复制断言");
                    }
                    else
                    {
                        Check("粘贴 1 条任务", pasted == 1, pasted.ToString());
                        Check("跳过注释与坏行", Log(f).Contains("缺少 | 分隔符"), Tail(Log(f)));
                        var h = Queue(f)[0].Headers;
                        Check("请求头映射正确",
                              h.Contains("Referer: REF") && h.Contains("User-Agent: UA") &&
                              h.Contains("Cookie: CK") && h.Contains("Origin: OR") && h.Contains("x-cdn-auth: CD"),
                              string.Join(" / ", h));

                        Paste(f);
                        Check("重复粘贴被去重", Queue(f).Count == 1, Queue(f).Count.ToString());

                        Copy(f);
                        if (TryClipboardGet(out var text))
                            Check("复制回原始格式", text.Trim() == "剧集X|http://x/x.m3u8|REF|UA|CK|OR|CD", text);
                        else
                            Console.WriteLine("  ⚠ 复制后读不回剪贴板，本轮跳过复制格式断言");
                    }
                }
            }

            // --- 场景 8：任务持久化与恢复 ---
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                AddTask(f, "任务H", "http://x/h.m3u8");
                AddTask(f, "任务I", "http://x/i.m3u8");
                var file = TasksFilePath;
                Check("已写入 tasks.json", File.Exists(file), file);
                var json = File.ReadAllText(file);
                Check("任务已落盘", json.Contains("任务H") && json.Contains("http://x/i.m3u8"));
            }
            using (var f = NewForm(Path.Combine(work, "dl"), keepTasks: true))
            {
                Check("重启后恢复任务", Queue(f).Count >= 2, Queue(f).Count.ToString());
                Check("恢复日志", Log(f).Contains("已恢复"), Tail(Log(f)));
            }

            // --- 场景 9：终态（完成/失败/已取消）必须原样保留，不能被改写成"完成" ---
            {
                var dir = Path.GetDirectoryName(TasksFilePath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(TasksFilePath, """
                [
                  { "name": "态A", "url": "http://x/a.m3u8", "status": "完成", "headers": [] },
                  { "name": "态B", "url": "http://x/b.m3u8", "status": "失败", "headers": [] },
                  { "name": "态C", "url": "http://x/c.m3u8", "status": "已取消", "headers": [] },
                  { "name": "态D", "url": "http://x/d.m3u8", "status": "待下载", "headers": [] }
                ]
                """, new UTF8Encoding(false));

                using (var f = NewForm(Path.Combine(work, "dl"), keepTasks: true))
                {
                    var st = Statuses(f);
                    Check("完成状态保留", st.Contains("完成"), string.Join(",", st));
                    Check("失败状态保留", st.Contains("失败"), string.Join(",", st));
                    Check("已取消状态保留", st.Contains("已取消"), string.Join(",", st));
                    Check("待下载状态保留", st.Contains("待下载"), string.Join(",", st));

                    // 回写文件后状态也不能变
                    Call(f, "SaveTasks");
                    var after = File.ReadAllText(TasksFilePath);
                    Check("回写后已取消仍在", after.Contains("\"已取消\""), after);
                    Check("回写后失败仍在", after.Contains("\"失败\""), after);
                }
            }
            // --- 场景 10：yt-dlp 报 CBC 解密错误 → 中文诊断 + 自动降级 RE 成功 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "cbc");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetText(f, "txtExe", reExe);
                AddTask(f, "任务J", "http://x/j.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("CBC 错误后自动降级 RE 成功", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("降级后成品文件存在", File.Exists(Path.Combine(work, "dl", "任务J.mp4")));
                Check("日志写明降级到 RE", log.Contains("自动改用 N_m3u8DL-RE"), Tail(log));
                Check("日志含中文诊断建议", log.Contains("🔍 诊断：") && log.Contains("Cookie 文件"), Tail(log));
                // 归因正确：指向「播放地址的时效签名过期」，而不是 Cookie 作用域
                var diag = Diagnoses(log);
                Check("诊断指出签名过期", diag.Any(d => d.Contains("签名过期")), Tail(log));
                Check("诊断给出重新获取播放地址的建议", diag.Any(d => d.Contains("重新获取播放地址")), Tail(log));
                Check("诊断不含 Cookie 域名作用域的错误归因",
                      !diag.Any(d => d.Contains("限定在") || d.Contains("CDN 侧拿不到")), Tail(log));
                Check("保留了原失败结论行", log.Contains("未检测到输出文件"), Tail(log));
            }

            // --- 场景 11：没有 RE 可执行文件时不做降级，但仍有中文诊断 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "cbc");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetText(f, "txtExe", Path.Combine(work, "没有这个RE.exe"));
                AddTask(f, "任务K", "http://x/k.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("无 RE 时任务判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("无 RE 时不写降级日志", !log.Contains("自动改用 N_m3u8DL-RE"), Tail(log));
                Check("无 RE 时仍有中文诊断建议", log.Contains("🔍 诊断："), Tail(log));
            }

            // --- 场景 11b：与 Cookie/403 无关的失败 → 不降级 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "notfound");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetText(f, "txtExe", reExe);
                AddTask(f, "任务K2", "http://x/k2.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("无关失败判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("无关失败不降级到 RE", !log.Contains("自动改用 N_m3u8DL-RE"), Tail(log));
                // 无关失败不该浪费一次"重新拉播放列表"的重试
                Check("无关失败不触发重新获取播放列表", !log.Contains("重新获取播放列表"), Tail(log));
                Check("无关失败也有通用诊断建议", log.Contains("🔍 诊断："), Tail(log));
            }

            // --- 场景 12：Cookie 来源传入 yt-dlp 命令 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                var cookieFile = Path.Combine(work, "cookies.txt");
                File.WriteAllText(cookieFile, "# Netscape HTTP Cookie File\n");
                SetText(f, "txtCookieFile", cookieFile);
                AddTask(f, "任务L", "http://x/l.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("Cookie 文件任务仍能完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("yt-dlp 命令带 --cookies", log.Contains("--cookies"), Tail(log));
            }

            // --- 场景 13：Cookie 文件不存在 → 必须明确提示，不能静默失败 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetText(f, "txtCookieFile", Path.Combine(work, "不存在的cookies.txt"));
                AddTask(f, "任务M", "http://x/m.m3u8");
                Start(f);
                WaitDone(f, 1);
                Check("Cookie 文件不存在时有明确提示",
                      Log(f).Contains("Cookie 文件不存在"), Tail(Log(f)));
            }

            // --- 场景 14：浏览器 Cookie 模式 → 命令带 --cookies-from-browser ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetBrowser(f, "firefox");
                AddTask(f, "任务N", "http://x/n.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("浏览器 Cookie 任务仍能完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("yt-dlp 命令带 --cookies-from-browser", log.Contains("--cookies-from-browser"), Tail(log));
                Check("提示先关闭浏览器", log.Contains("请先完全关闭对应浏览器"), Tail(log));
            }

            // --- 场景 15：播放地址签名过期 → 重新获取播放列表重试后成功（不降级 RE）---
            // 真实故障：分片地址里的 auth_key 时效签名过期 → 分片返回 403 纯文本错误页
            // （22 字节，不是 16 的倍数）→ yt-dlp 做 AES-128-CBC 解密时报
            // "Data must be padded to 16 byte boundary in CBC mode"，且不会重试该分片。
            // 假下载器第 1 轮报这个错，第 2 轮（重新拉播放列表 → 新签名）成功。
            Environment.SetEnvironmentVariable("FAKE_MODE", "expired");
            var expiredState = Path.Combine(work, "expired.state");
            Environment.SetEnvironmentVariable("FAKE_STATE", expiredState);
            try { if (File.Exists(expiredState)) File.Delete(expiredState); } catch { }
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetText(f, "txtExe", reExe);
                AddTask(f, "任务O", "http://x/o.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("签名过期重试后任务完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("签名过期重试后成品存在", File.Exists(Path.Combine(work, "dl", "任务O.mp4")));
                Check("日志提示重新获取播放列表", log.Contains("重新获取播放列表"), Tail(log));
                Check("诊断指出签名过期", Diagnoses(log).Any(d => d.Contains("签名过期")), Tail(log));
                Check("诊断给出重新获取播放地址的建议",
                      Diagnoses(log).Any(d => d.Contains("重新获取播放地址")), Tail(log));
                Check("诊断不含 Cookie 域名作用域的错误归因",
                      !Diagnoses(log).Any(d => d.Contains("限定在") || d.Contains("CDN 侧拿不到")), Tail(log));
                Check("重试成功后不降级 RE", !log.Contains("自动改用 N_m3u8DL-RE"), Tail(log));
            }
            // --- 场景 16：失败自动重试（开关开启 + 轮数 1）→ 失败任务被自动重跑并成功 ---
            // 假下载器 flaky 模式：第 1 次调用报 404（既不触发 RE 降级，也不触发引擎内的
            // 「重新取播放列表」），第 2 次成功 —— 只有批次层真的重跑了任务才可能变成「完成」。
            Environment.SetEnvironmentVariable("FAKE_MODE", "flaky");
            var flakyState = Path.Combine(work, "flaky.state");
            Environment.SetEnvironmentVariable("FAKE_STATE", flakyState);
            try { if (File.Exists(flakyState + ".flaky")) File.Delete(flakyState + ".flaky"); } catch { }
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "autoRetryFailed", true);
                SetNud(f, "nudAutoRetry", 1);
                AddTask(f, "任务P", "http://x/p.m3u8");
                Start(f);
                WaitBatchFinished(f);
                var log = Log(f);
                Check("失败自动重试后任务完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("日志说明第 1/1 轮自动重试", log.Contains("失败自动重试 第 1/1 轮"), Tail(log));
                Check("失败任务确实被重跑了一次", CountOf(log, "===== 开始任务 1 |") == 2,
                      CountOf(log, "===== 开始任务 1 |").ToString());
                Check("重试成功后成品已落盘", File.Exists(Path.Combine(work, "dl", "任务P.mp4")));
                Check("重试后进度归零再回到 100", Progress(f).Single() == 100, string.Join(",", Progress(f)));
            }

            // --- 场景 17：开关关闭 → 即使轮数很大也不重跑 ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "flaky");
            try { if (File.Exists(flakyState + ".flaky")) File.Delete(flakyState + ".flaky"); } catch { }
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "autoRetryFailed", false);
                SetNud(f, "nudAutoRetry", 3);
                AddTask(f, "任务Q", "http://x/q.m3u8");
                Start(f);
                WaitBatchFinished(f);
                var log = Log(f);
                Check("开关关闭时失败任务保持失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("开关关闭时不出现自动重试日志", !log.Contains("失败自动重试"), Tail(log));
                Check("开关关闭时任务只跑了一次", CountOf(log, "===== 开始任务 1 |") == 1,
                      CountOf(log, "===== 开始任务 1 |").ToString());
            }

            // --- 场景 18：轮数 = 0 → 开关开着也不重跑（轮数是第二道闸）---
            Environment.SetEnvironmentVariable("FAKE_MODE", "flaky");
            try { if (File.Exists(flakyState + ".flaky")) File.Delete(flakyState + ".flaky"); } catch { }
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "autoRetryFailed", true);
                SetNud(f, "nudAutoRetry", 0);
                AddTask(f, "任务R", "http://x/r.m3u8");
                Start(f);
                WaitBatchFinished(f);
                Check("轮数为 0 时失败任务不重跑", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("轮数为 0 时无自动重试日志", !Log(f).Contains("失败自动重试"), Tail(Log(f)));
            }

            // --- 场景 19：轮数 2 且连续失败 2 次 → 第 2 轮才成功（验证轮次推进与文案不串轮）---
            Environment.SetEnvironmentVariable("FAKE_MODE", "flaky");
            Environment.SetEnvironmentVariable("FAKE_FAIL_TIMES", "2");
            try { if (File.Exists(flakyState + ".flaky")) File.Delete(flakyState + ".flaky"); } catch { }
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "autoRetryFailed", true);
                SetNud(f, "nudAutoRetry", 2);
                AddTask(f, "任务S", "http://x/s.m3u8");
                Start(f);
                WaitBatchFinished(f);
                var log = Log(f);
                Check("第 2 轮重试后任务完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("日志出现第 1/2 轮", log.Contains("失败自动重试 第 1/2 轮"), Tail(log));
                Check("日志出现第 2/2 轮", log.Contains("失败自动重试 第 2/2 轮"), Tail(log));
                Check("共跑了 3 次（首发 + 2 轮重试）", CountOf(log, "===== 开始任务 1 |") == 3,
                      CountOf(log, "===== 开始任务 1 |").ToString());
            }
            Environment.SetEnvironmentVariable("FAKE_FAIL_TIMES", "");

            // --- 场景 20：RE 模式 + 「RE 可执行」为空 → 自动识别，不再报「RE执行程序不存在」---
            // 真实故障：进程 PATH 是登录时的快照，用户新装的 RE 加进 PATH 后，
            // 已开着的 Explorer 仍是旧环境 → 启动时不报错、探测为空 → 点开始只弹一句
            // 「RE执行程序不存在！」加空路径，日志里一个字都没有。
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetChk(f, "useYtDlp", false);
                SetText(f, "txtExe", "");
                AddTask(f, "任务T", "http://x/t.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                var exeNow = ((TextBox)GetField(f, "txtExe")).Text;
                Check("RE 路径为空时自动识别", log.Contains("已自动识别 RE 引擎"), Tail(log));
                Check("自动识别后不再报「RE执行程序不存在」", !log.Contains("RE执行程序不存在"), Tail(log));
                Check("自动识别后 RE 任务完成", Statuses(f).Single() == "完成", string.Join(",", Statuses(f)));
                Check("RE 可执行已回填 PATH 上的 RE",
                      exeNow.EndsWith("N_m3u8DL-RE.exe", StringComparison.OrdinalIgnoreCase), exeNow);
                Check("日志记录了实际使用的引擎路径", log.Contains("ℹ 引擎：N_m3u8DL-RE（"), Tail(log));
            }

            // --- 场景 21：下载中删除任务（队列可随意增删，进程要被真杀掉） ---
            Environment.SetEnvironmentVariable("FAKE_MODE", "slow");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetNud(f, "nudConc", 1);
                AddTask(f, "删A", "http://x/del_a.m3u8");
                AddTask(f, "删B", "http://x/del_b.m3u8");
                AddTask(f, "删C", "http://x/del_c.m3u8");
                Start(f);

                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 20000 && !Statuses(f).Contains("下载中"))
                {
                    Application.DoEvents();
                    Thread.Sleep(20);
                }
                Check("删除前确实有任务在下载中", Statuses(f).Contains("下载中"), string.Join(",", Statuses(f)));

                var g = (DataGridView)GetField(f, "grid");
                g.ClearSelection();
                g.Rows[0].Selected = true;
                Call(f, "DeleteSelected");
                Application.DoEvents();
                Check("下载中删除：模型立即少一条", Queue(f).Count == 2, Queue(f).Count.ToString());
                Check("下载中删除：表格立即少一行", g.Rows.Count == 2, g.Rows.Count.ToString());
                Check("下载中删除：剩下任务名称未错位",
                      string.Join(",", Queue(f).Select(t => t.Name)) == "删B,删C",
                      string.Join(",", Queue(f).Select(t => t.Name)));
                Check("下载中删除：日志注明已中止", Log(f).Contains("已中止"), Tail(Log(f)));

                WaitBatchFinished(f, 60000);
                var st = Statuses(f);
                Check("删除后剩余任务全部完成", st.Count == 2 && st.All(s => s == "完成"), string.Join(",", st));
                Check("被删除的任务没有复活", Queue(f).All(t => t.Name != "删A"),
                      string.Join(",", Queue(f).Select(t => t.Name)));
                Check("删除后表格仍与模型一一对应", g.Rows.Count == Queue(f).Count,
                      $"{g.Rows.Count}/{Queue(f).Count}");
            }
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");

            Environment.SetEnvironmentVariable("FAKE_STATE", "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_MODE", "normal");
            Environment.SetEnvironmentVariable("FAKE_FAIL_TIMES", "");
            Environment.SetEnvironmentVariable("FAKE_STATE", "");
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ---------------- 反射辅助 ----------------
    static object GetField(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    static object? Call(object o, string name, params object?[] args)
    {
        var m = o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return m.Invoke(o, args);
    }

    /// <summary>递归枚举整棵控件树（自顶向下，不含 root 自身）。</summary>
    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var g in AllControls(c)) yield return g;
        }
    }

    // 每个场景默认清空隔离 APPDATA 下的 tasks.json，避免上一场景的历史任务被恢复进来互相干扰。
    // 仅"任务持久化与恢复"场景通过 keepTasks=true 保留文件。
    static MainForm NewForm(string workDir, bool keepTasks = false)
    {
        if (!keepTasks) ClearTasksFile();
        var f = new MainForm();
        f.Show();
        Application.DoEvents();
        SetText(f, "txtWorkDir", workDir);
        return f;
    }

    static string TasksFilePath =>
        Path.Combine(Environment.GetEnvironmentVariable("APPDATA")!, "NM3U8DL_GUI", "tasks.json");

    static void ClearTasksFile()
    {
        try { if (File.Exists(TasksFilePath)) File.Delete(TasksFilePath); } catch { }
    }

    static void SetText(MainForm f, string field, string value) =>
        ((TextBox)GetField(f, field)).Text = value;

    // 剪贴板可能被其他进程独占（0x800401D0 = CLIPBRD_E_CANT_OPEN）。
    // 本机的元气壁纸小组件、百度输入法等会长时间持有剪贴板，因此重试窗口放宽到 5 秒；
    // 仍失败则跳过该场景，而不是让整个测试进程崩溃。
    static bool TryClipboardSet(string text)
    {
        for (int i = 0; i < 50; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch { Thread.Sleep(100); }
        }
        return false;
    }

    static bool TryClipboardGet(out string text)
    {
        for (int i = 0; i < 50; i++)
        {
            try { text = Clipboard.GetText(); return true; }
            catch { Thread.Sleep(100); }
        }
        text = "";
        return false;
    }

    static void SetChk(MainForm f, string key, bool value) =>
        ((Dictionary<string, CheckBox>)GetField(f, "chk"))[key].Checked = value;

    static void SetNud(MainForm f, string field, decimal value) =>
        ((NumericUpDown)GetField(f, field)).Value = value;

    static int CountOf(string s, string needle)
    {
        var n = 0;
        for (var i = s.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = s.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// 等整批真正收尾（batchRunning 由 true 变 false）。
    /// WaitDone 只看行状态，遇到「失败 → 自动重试」会在第一轮的「失败」上提前返回，
    /// 无法反映重试是否已经跑完，所以验证自动重试必须用这个。
    /// </summary>
    static void WaitBatchFinished(MainForm f, int timeoutMs = 90000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (!(bool)GetField(f, "batchRunning")) return;
            Thread.Sleep(20);
        }
    }

    /// <summary>选择「浏览器 Cookie」下拉框里的某一项。</summary>
    static void SetBrowser(MainForm f, string browser)
    {
        var cmb = (ComboBox)GetField(f, "cmbCookieBrowser");
        var idx = cmb.Items.IndexOf(browser);
        if (idx < 0) throw new InvalidOperationException($"下拉框里没有 {browser}");
        cmb.SelectedIndex = idx;
    }

    static void AddTask(MainForm f, string name, string url) =>
        Call(f, "AddTask", name, url, "待下载", new List<string>(), false);

    static void Start(MainForm f) => Call(f, "StartBatch");

    static void Paste(MainForm f) => Call(f, "PasteTasks");

    static void Copy(MainForm f)
    {
        var grid = (DataGridView)GetField(f, "grid");
        grid.SelectAll();
        grid.CurrentCell = grid.Rows[0].Cells[0];
        Application.DoEvents();
        Call(f, "CopySelection");
    }

    // ============================================================
    // 窗口尺寸
    // ============================================================
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                    int X, int Y, int cx, int cy, uint uFlags);

    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// 把窗口拉到设计尺寸。
    /// Form.Size 最终走 SetBoundsCore，会被 SystemInformation.MaxWindowTrackSize（≈ 屏幕
    /// 工作区）夹住；GitHub runner 的虚拟屏幕只有 1024×768，1280 宽的主窗口会被压到 1028，
    /// 「工具栏按钮流不越界」这类按设计宽度写的断言就永远测不到真实宽度。
    /// 直接调 Win32 SetWindowPos，跳过 WinForms 那层夹取。
    /// </summary>
    static void ForceDesignSize(Form f, int width = 1280, int height = 800)
    {
        if (f.ClientSize.Width >= width) return;
        var frameW = f.Width - f.ClientSize.Width;
        var frameH = f.Height - f.ClientSize.Height;
        SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, width + frameW, height + frameH,
                     SWP_NOZORDER | SWP_NOACTIVATE);
        Application.DoEvents();
        var scr = Screen.PrimaryScreen!.Bounds;
        Console.WriteLine($"[环境] 屏幕 {scr.Width}×{scr.Height}，窗口拉到 client={f.ClientSize}");
    }

    static List<DownloadTask> Queue(MainForm f) =>
        (List<DownloadTask>)GetField(f, "queue");
    static List<string> Statuses(MainForm f) => Queue(f).Select(t => t.Status).ToList();
    static List<double> Progress(MainForm f)
    {
        var grid = (DataGridView)GetField(f, "grid");
        var list = new List<double>();
        foreach (DataGridViewRow r in grid.Rows)
        {
            var t = Convert.ToString(r.Cells[2].Value) ?? "";
            list.Add(t.EndsWith('%') && double.TryParse(t.TrimEnd('%'), out var d) ? d : 0);
        }
        return list;
    }

    static string Log(MainForm f) => ((RichTextBox)GetField(f, "txtLog")).Text;

    static string Tail(string s) => s.Length > 400 ? s[^400..] : s;

    /// <summary>取出日志里的「🔍 诊断：」行（只诊断文案，不含其它日志）。</summary>
    static List<string> Diagnoses(string log) =>
        log.Split('\n').Where(l => l.Contains("🔍 诊断：")).ToList();

    static void WaitDone(MainForm f, int count)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30000)
        {
            Application.DoEvents();
            var st = Statuses(f);
            if (st.Count >= count && st.All(s => s is "完成" or "失败" or "已取消")) return;
            Thread.Sleep(20);
        }
    }
}
