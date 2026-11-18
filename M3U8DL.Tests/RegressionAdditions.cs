using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace M3U8DL.Tests;

/// <summary>
/// 本批 Bug 修复新增的回归测试（独立文件，不改动 Tests.cs / QaRegression.cs 的任何既有断言）。
/// 由 Tests.Main() 末尾显式调用。所有涉及 MainForm 的用例都先把 APPDATA 指向隔离目录，
/// 绝不触碰用户真实任务库 %APPDATA%\NM3U8DL_GUI\tasks.json。
/// </summary>
internal static class RegressionAdditions
{
    static readonly MethodInfo CheckMi =
        typeof(Tests).GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)!;

    static void Check(string name, bool ok, string detail = "") =>
        CheckMi.Invoke(null, new object?[] { name, ok, detail });

    static object GetField(object o, string name) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    static object? Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!
         .Invoke(o, args);

    static string GuiDir(string root) => Path.Combine(root, "appdata", "NM3U8DL_GUI");

    public static void Run()
    {
        Console.WriteLine("=== 6) 新增回归（Bug 修复验证）===");
        RenameRegression();
        NoDirtyLoadRegression();
        DirtyLoadRegression();
        LogLinesRegression();
        S5Regression();
        RefactorRegression();
        DownloadSpeedRegression();
        SpeedColumnRegression();
    }

    /// <summary>反射调用 Engine.BuildYtDlpCmd（与 QaRegression / Tests 里的同名辅助保持同一签名）。</summary>
    static List<string>? BuildYtDlp(Engine eng, string url, string name, Opt o,
                                   List<string> headers, bool impersonate = true)
    {
        var m = typeof(Engine).GetMethod("BuildYtDlpCmd", BindingFlags.Instance | BindingFlags.NonPublic);
        return (List<string>?)m!.Invoke(eng, [url, name, o, headers, impersonate]);
    }

    /// <summary>
    /// 2026-11-18 下载加速回归。四类断言：
    /// <list type="number">
    /// <item>RE 必须带 --auto-select —— 多轨道片源会弹 Spectre 交互选单，GUI 无控制台直接抛
    ///       "Cannot see if a key has been pressed"，这是崩溃级问题，不是优化项；</item>
    /// <item>临时目录必须钉在保存目录内 —— 默认取进程工作目录（会在程序目录堆时间戳目录，
    ///       且跨盘时成品要整份复制）；</item>
    /// <item>合并方式互斥：跳过 / 二进制 / concat 分离器三选一，不能同时下发；</item>
    /// <item>yt-dlp 分片并发与「并发下载音视频」开关解耦，并带直链分块下载。</item>
    /// </list>
    /// </summary>
    static void DownloadSpeedRegression()
    {
        var eng = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, () => true);
        var o = new Opt
        {
            Executable = @"C:\tools\N_m3u8DL-RE.exe",
            WorkDir = @"C:\out",
            MaxThreads = 32,
            RetryCount = 3,
            Timeout = 100,
        };

        List<string> Build(Opt opt, HashSet<string> supported) =>
            (List<string>)Call(eng, "BuildReCmd", "http://x/a.m3u8", "剧集", opt,
                               new List<string>(), supported)!;

        // 空集合 = 「没探测到参数表」，兼容模式下所有参数都下发（覆盖老版本 RE）
        var cmd = Build(o, []);
        Check("RE 必带 --auto-select（多轨道时免交互，否则崩溃）", cmd.Contains("--auto-select"));
        Check("RE 关闭版本更新检查", cmd.Contains("--disable-update-check"));
        Check("RE 关闭 ANSI 颜色（否则进度正则被转义序列打断）", cmd.Contains("--no-ansi-color"));

        var ti = cmd.IndexOf("--tmp-dir");
        Check("RE 临时目录钉在保存目录内",
              ti >= 0 && ti + 1 < cmd.Count && cmd[ti + 1] == Engine.TempDirOf(@"C:\out"),
              ti >= 0 && ti + 1 < cmd.Count ? cmd[ti + 1] : "(缺 --tmp-dir)");
        Check("临时目录名固定为 .nm3u8dl-tmp",
              Engine.TempDirOf(@"C:\out") == Path.Combine(@"C:\out", ".nm3u8dl-tmp"));

        // 合并方式互斥
        Check("默认合并走 concat 分离器",
              cmd.Contains("--use-ffmpeg-concat-demuxer") && !cmd.Contains("--binary-merge"));
        var binCmd = Build(o with { BinaryMerge = true }, []);
        Check("二进制合并时不带 concat 分离器",
              binCmd.Contains("--binary-merge") && !binCmd.Contains("--use-ffmpeg-concat-demuxer"));
        var noMerge = Build(o with { NoMerge = true }, []);
        Check("跳过合并时不带任何合并参数",
              noMerge.Contains("--skip-merge")
              && !noMerge.Contains("--binary-merge")
              && !noMerge.Contains("--use-ffmpeg-concat-demuxer"));

        // 受限参数表：不支持的参数必须跳过，不能硬塞导致 RE 报未知参数
        var oldRe = Build(o, ["--thread-count", "--save-dir"]);
        Check("老版本 RE 不支持时跳过 --auto-select", !oldRe.Contains("--auto-select"));
        Check("受支持参数照常下发", oldRe.Contains("--thread-count"));

        // ---- yt-dlp ----
        var y = BuildYtDlp(eng, "http://x/a.m3u8", "剧集",
                           o with { UseYtDlp = true, ConcurrentDownload = false, MaxThreads = 24 },
                           []);
        if (y is null)
        {
            Check("yt-dlp 可用（加速断言跳过）", true, "未找到 yt-dlp，本机无 yt-dlp 时不阻塞其它用例");
        }
        else
        {
            var ci = y.IndexOf("--concurrent-fragments");
            Check("分片并发不受「并发下载音视频」开关影响",
                  ci >= 0 && ci + 1 < y.Count && y[ci + 1] == "24",
                  ci >= 0 && ci + 1 < y.Count ? y[ci + 1] : "(缺 --concurrent-fragments)");
            var hi = y.IndexOf("--http-chunk-size");
            Check("直链分块下载已开启",
                  hi >= 0 && hi + 1 < y.Count && y[hi + 1] == "10M");
        }
    }

    /// <summary>
    /// 2026-11-18 代码审查回归（框架整理后的行为契约）：
    /// - DownloadTask.Id 是运行期身份，不参与持久化；
    /// - 停止批次后「排队中 / 下载中」必须落到「已取消」终态，不能永远停在排队中；
    /// - 任务行解析只剩一份实现：合法行与非法行混排时，合法行照常入队并如实统计跳过数。
    /// </summary>
    static void RefactorRegression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_rf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var tasksFile = Path.Combine(GuiDir(dir), "tasks.json");
        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();

            List<DownloadTask> Q() => (List<DownloadTask>)GetField(f, "queue");
            List<string> St() => Q().Select(t => t.Status).ToList();
            string Log() => ((RichTextBox)GetField(f, "txtLog")).Text;

            // ---- 1) 运行期 id 不落盘：tasks.json 里出现 "id" 说明快照把内部身份写进了用户文件 ----
            Call(f, "AddTask", "持久化任务", "http://x/p.m3u8", "待下载", new List<string>(), false);
            var json = File.Exists(tasksFile) ? File.ReadAllText(tasksFile) : "";
            Check("tasks.json 不含运行期 id 字段", json.Length > 0 && !json.Contains("\"id\""), json);

            // ---- 2) 停止批次：排队中/下载中必须落终态，否则界面会永远停在「排队中」 ----
            Call(f, "AddTask", "排队任务", "http://x/q.m3u8", "排队中", new List<string>(), false);
            typeof(MainForm).GetField("batchRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(f, true);
            Call(f, "StopBatch");
            Application.DoEvents();
            Check("停止批次后「排队中」转为已取消", St().Contains("已取消"), string.Join(",", St()));
            Check("停止批次后不再残留排队中", !St().Contains("排队中"), string.Join(",", St()));
            Check("停止批次后按钮恢复为「开始下载」", ((Button)GetField(f, "btnStart")).Text == "开始下载",
                  ((Button)GetField(f, "btnStart")).Text);

            // ---- 3) 粘贴/导入共用同一份解析：坏行跳过、好行入队、日志如实统计 ----
            Call(f, "AddTasksFromText",
                 "合法任务|http://x/ok.m3u8\n坏任务|ftp://x/bad.m3u8\n#注释行\n没有分隔符的行");
            var names = Q().Select(t => t.Name).ToList();
            Check("统一解析：合法行入队", names.Contains("合法任务"), string.Join(",", names));
            Check("统一解析：非 http 地址不入队", !names.Contains("坏任务"), string.Join(",", names));
            Check("统一解析：跳过行数写入日志", Log().Contains("跳过 2 行"), Log().Split('\n').LastOrDefault() ?? "");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// S5 回归：
    /// - BUG-8 批量粘贴期间 bulkSave=true 抑制每条写盘，循环结束统一落盘一次且内容完整；
    /// - BUG-9 构造期后台预热不污染启动日志（无"未找到 yt-dlp"这类探测日志）；
    /// - BUG-10 Engine.TerminateAll 在空进程表上调用安全不抛。
    /// </summary>
    static void S5Regression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_s5_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var tasksFile = Path.Combine(GuiDir(dir), "tasks.json");
        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using (var f = new MainForm())
            {
                f.Show();
                Application.DoEvents();

                // BUG-9：构造期静默预热，启动日志不应出现探测类文案
                var log = ((RichTextBox)GetField(f, "txtLog")).Text;
                Check("BUG-9 预热不污染启动日志", !log.Contains("未找到 yt-dlp") && !log.Contains("探测 "), log);

                // BUG-8：模拟批量——bulkSave=true 时连续 AddTask 不应每条落盘，
                // 结束显式 SaveTasks 后文件应包含全部条目。
                var bulkField = typeof(MainForm).GetField("bulkSave", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bulkField.SetValue(f, true);
                try
                {
                    for (var i = 1; i <= 30; i++)
                        Call(f, "AddTask", $"批量{i}", $"http://x/bulk{i}.m3u8", "待下载", new List<string>(), false);
                    // bulkSave 期间不应产生任何落盘
                    Check("BUG-8 批量期间抑制单条写盘", !File.Exists(tasksFile),
                          File.Exists(tasksFile) ? "文件在批量期间已出现" : "ok");
                }
                finally { bulkField.SetValue(f, false); }

                Call(f, "SaveTasks");
                var json = File.Exists(tasksFile) ? File.ReadAllText(tasksFile) : "";
                Check("BUG-8 批量结束统一落盘全部条目",
                      json.Contains("批量1") && json.Contains("批量30"), json.Length.ToString());
            }

            // BUG-10：空进程表 TerminateAll 安全
            var eng = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, () => false);
            var terminated = true;
            try { eng.TerminateAll(); } catch { terminated = false; }
            Check("BUG-10 TerminateAll 空表安全", terminated);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>BUG-1：ApplyRename 后 queue 与 grid 同步、且落盘到 tasks.json。</summary>
    static void RenameRegression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_ren_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            Call(f, "AddTask", "原名", "http://x/rename.m3u8", "待下载", new List<string>(), false);
            var queue = (List<DownloadTask>)GetField(f, "queue");
            var grid = (DataGridView)GetField(f, "grid");
            Check("BUG-1 重命名前基线就绪", queue.Count == 1 && queue[0].Name == "原名", queue.Count.ToString());

            Call(f, "ApplyRename", 0, "新名称");

            Check("BUG-1 重命名后模型更新", queue.Count == 1 && queue[0].Name == "新名称",
                  queue.Count > 0 ? queue[0].Name : "empty");
            Check("BUG-1 重命名后表格同步",
                  Convert.ToString(grid.Rows[0].Cells[0].Value) == "新名称",
                  Convert.ToString(grid.Rows[0].Cells[0].Value) ?? "null");
            var tasksFile = Path.Combine(GuiDir(dir), "tasks.json");
            Check("BUG-1 重命名后落盘",
                  File.Exists(tasksFile) && File.ReadAllText(tasksFile).Contains("新名称"));

            // 同名/空名不得改动
            Call(f, "ApplyRename", 0, "新名称");
            Call(f, "ApplyRename", 0, "   ");
            Check("BUG-1 同值与空名不改动", queue[0].Name == "新名称", queue[0].Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>BUG-2：干净的 tasks.json 载入不应被回写（字节与 mtime 均不变）。</summary>
    static void NoDirtyLoadRegression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_nodirty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var tasksFile = Path.Combine(GuiDir(dir), "tasks.json");
        const string json = """
        [
          { "name": "任务甲", "url": "http://x/j.m3u8", "status": "完成", "headers": [] },
          { "name": "任务乙", "url": "http://x/y.m3u8", "status": "待下载", "headers": [] }
        ]
        """;
        File.WriteAllText(tasksFile, json, new UTF8Encoding(false));
        var oldTime = DateTime.UtcNow.AddMinutes(-10);
        File.SetLastWriteTimeUtc(tasksFile, oldTime);
        var beforeBytes = File.ReadAllBytes(tasksFile);
        var beforeMtime = File.GetLastWriteTimeUtc(tasksFile);

        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            Check("BUG-2 无 dirty 时内容不变", beforeBytes.SequenceEqual(File.ReadAllBytes(tasksFile)));
            Check("BUG-2 无 dirty 时 mtime 不变",
                  File.GetLastWriteTimeUtc(tasksFile) == beforeMtime,
                  $"{beforeMtime:O} vs {File.GetLastWriteTimeUtc(tasksFile):O}");
            var queue = (List<DownloadTask>)GetField(f, "queue");
            Check("BUG-2 历史任务仍正常载入", queue.Count == 2, queue.Count.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// BUG-2 + BUG-13：含清洗后同名（A:B 与 A/B）的 tasks.json 载入后应去重回写，
    /// 并生成 .bak 备份。验证 dirty 场景确实落盘、且去重键使用 Sanitize 后的名称。
    /// </summary>
    static void DirtyLoadRegression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_dirty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var tasksFile = Path.Combine(GuiDir(dir), "tasks.json");
        const string json = """
        [
          { "name": "A:B", "url": "http://x/same.m3u8", "status": "待下载", "headers": [] },
          { "name": "A/B", "url": "http://x/same.m3u8", "status": "待下载", "headers": [] }
        ]
        """;
        File.WriteAllText(tasksFile, json, new UTF8Encoding(false));

        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            var queue = (List<DownloadTask>)GetField(f, "queue");
            Check("BUG-13 清洗后同名被去重", queue.Count == 1, queue.Count.ToString());
            Check("BUG-13 去重后名称已 Sanitize",
                  queue.Count == 1 && queue[0].Name == "A_B",
                  queue.Count > 0 ? queue[0].Name : "empty");
            Check("BUG-2 dirty 场景生成 .bak 备份", File.Exists(tasksFile + ".bak"));
            var after = File.ReadAllText(tasksFile);
            var urlHits = System.Text.RegularExpressions.Regex.Matches(after, "http://x/same.m3u8").Count;
            Check("BUG-2 dirty 场景落盘为 1 条", urlHits == 1, urlHits.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>BUG-12：AppendLog 的 logLines 按实际行数累加（多行文本不再只算 1 行）。</summary>
    static void LogLinesRegression()
    {
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_log_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            var linesField = typeof(MainForm).GetField("logLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var txtLog = (RichTextBox)GetField(f, "txtLog");
            Call(f, "AppendLog", "第一行\n第二行\n第三行");
            var counted = (int)linesField.GetValue(f)!;
            Check("BUG-12 多行日志按实际行数计数", counted == 3, counted.ToString());
            Check("BUG-12 日志文本完整", txtLog.Text.Contains("第三行"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// 队列「速度」列回归。样例字符串取自真实输出，不是编出来的：
    /// RE 的进度行形如 <c>2/6 33.33% 1.33MB/3.98MB646.63KBps00:00:03</c>（速率紧跟已下载/总量，
    /// 后面那段 "00:00:03" 没有空格分隔，所以解析不能依赖 \b 或空白）。四类断言：
    /// <list type="number">
    /// <item>格式化：0 显示占位符而不是 "0 B/s"（刚起步时引擎给 0，显示 0 会像卡死）；</item>
    /// <item>解析：KBps/MBps/Bps 三种单位换算，以及 0 值判为「测不出」；</item>
    /// <item>列序：速度在进度与文件大小之间，加列不能破坏既有列偏移；</item>
    /// <item>生命周期：写入生效、归零回占位符、终态必须清空。</item>
    /// </list>
    /// </summary>
    static void SpeedColumnRegression()
    {
        Check("速度格式化：0 显示占位符", Naming.FmtSpeed(0) == "—", Naming.FmtSpeed(0));
        Check("速度格式化：负数显示占位符", Naming.FmtSpeed(-5) == "—", Naming.FmtSpeed(-5));
        Check("速度格式化：KB 档", Naming.FmtSpeed(1536) == "1.5 KB/s", Naming.FmtSpeed(1536));
        Check("速度格式化：MB 档", Naming.FmtSpeed(5 * 1024 * 1024) == "5.00 MB/s", Naming.FmtSpeed(5 * 1024 * 1024));

        var parse = typeof(Engine).GetMethod("ParseSpeedToken", BindingFlags.Static | BindingFlags.NonPublic)!;
        long P(string s) => (long)parse.Invoke(null, [s])!;

        const string reLine = "Vid 1280x720 | 2000 Kbps ------ 2/6 33.33% 1.33MB/3.98MB646.63KBps00:00:03";
        Check("RE 整行进度里取出速率（单位 KBps）", P(reLine) == 662149, P(reLine).ToString());
        Check("速率解析：MBps", P("1.20MBps") == 1258291, P("1.20MBps").ToString());
        Check("速率解析：裸 Bps", P("512Bps") == 512, P("512Bps").ToString());
        Check("速率解析：0.00Bps 视为测不出", P("0.00Bps") == -1, P("0.00Bps").ToString());
        Check("速率解析：无速率字段返回 -1", P("INFO : Done") == -1, P("INFO : Done").ToString());

        // yt-dlp 侧必须把 speed 塞进进度模板，否则拿不到速率
        var eng = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, () => true);
        var o = new Opt
        {
            Executable = @"C:\tools\N_m3u8DL-RE.exe",
            WorkDir = @"C:\out",
            UseYtDlp = true,
            MaxThreads = 8,
            RetryCount = 3,
            Timeout = 100,
        };
        var y = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", o, []);
        if (y is null)
        {
            Check("yt-dlp 可用（速率模板断言跳过）", true, "本机无 yt-dlp");
        }
        else
        {
            var ti = y.IndexOf("--progress-template");
            var tpl = ti >= 0 && ti + 1 < y.Count ? y[ti + 1] : "";
            Check("yt-dlp 进度模板带速率字段", tpl.Contains("%(progress.speed)s"), tpl);
        }

        // 端到端：真起一个子进程产出 4 段进度帧，验证「解析 → 回调」这段真的通了
        // （只测正则是不够的，回调没接上照样不显示）
        var fake = Environment.GetEnvironmentVariable("FAKE_EXE") ?? "";
        if (!File.Exists(fake))
        {
            Check("FakeDownloader 可用（速率端到端断言跳过）", true, "未设置 FAKE_EXE");
        }
        else
        {
            var work = Path.Combine(Path.GetTempPath(), "m3u8dl_spd_e2e_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                long got = -1;
                var eng2 = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, (_, bps) => got = bps, () => true);
                var opt = o with { WorkDir = work };
                var cmd = new List<string> { fake, "-o", Path.Combine(work, "e2e.%(ext)s"), "--newline" };
                var mi = typeof(Engine).GetMethod("YtDlpAttemptAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var t = (Task)mi.Invoke(eng2, [cmd, opt, "e2e", 7, DateTime.Now])!;
                t.Wait(20000);
                Check("yt-dlp 进度帧里的速率到达回调", got > 0, got.ToString());
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }

        // ---- UI：列序与写入/清空 ----
        var dir = Path.Combine(Path.GetTempPath(), "m3u8dl_spd_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(GuiDir(dir));
        var old = Environment.GetEnvironmentVariable("APPDATA");
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(dir, "appdata"));
        try
        {
            using var f = new MainForm();
            f.Show();
            Application.DoEvents();
            var grid = (DataGridView)GetField(f, "grid");
            var headers = string.Join("|", grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText));
            Check("速度列位于进度与文件大小之间",
                  grid.Columns.Count == 5
                  && Convert.ToString(grid.Columns[3].HeaderText) == "速度"
                  && Convert.ToString(grid.Columns[4].HeaderText) == "文件大小",
                  headers);

            Call(f, "AddTask", "示例", "http://x/speed.m3u8", "下载中", new List<string>(), false);
            var queue = (List<DownloadTask>)GetField(f, "queue");
            var id = queue[0].Id;
            Call(f, "SetSpeed", id, 5L * 1024 * 1024);
            Check("速率写入表格", Convert.ToString(grid.Rows[0].Cells[3].Value) == "5.00 MB/s",
                  Convert.ToString(grid.Rows[0].Cells[3].Value) ?? "null");
            Call(f, "SetSpeed", id, 0);
            Check("速率为 0 时回到占位符", Convert.ToString(grid.Rows[0].Cells[3].Value) == "—",
                  Convert.ToString(grid.Rows[0].Cells[3].Value) ?? "null");

            Call(f, "SetSpeed", id, 3L * 1024 * 1024);
            Call(f, "SetStatus", id, "完成");
            Check("完成后速度列清空（不留上一帧数值）",
                  Convert.ToString(grid.Rows[0].Cells[3].Value) == "—",
                  Convert.ToString(grid.Rows[0].Cells[3].Value) ?? "null");
            var speeds = (System.Collections.IDictionary)GetField(f, "speeds");
            Check("终态后不再计入总速度", speeds.Count == 0, speeds.Count.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDATA", old);
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
