namespace M3U8DL;

/// <summary>
/// MainForm 的批次编排、引擎调用、并发与 token 部分。
/// Snapshot / StartBatch 等反射契约方法保留在 MainForm 类型本身。
/// </summary>
internal sealed partial class MainForm : Form
{
    // ============================================================
    // 队列执行
    // ============================================================
    Opt Snapshot() => new()
    {
        Executable = txtExe.Text.Trim(),
        WorkDir = txtWorkDir.Text.Trim(),
        MuxFile = txtMux.Text.Trim(),
        DelAfterMerge = C("delAfterMerge"),
        NoDateInName = C("noDateInName"),
        NoSystemProxy = C("noSystemProxy"),
        OnlyParseM3u8 = C("onlyParseM3u8"),
        MuxWhileDownload = C("muxWhileDownload"),
        NoMerge = C("noMerge"),
        BinaryMerge = C("binaryMerge"),
        NoLog = C("noLog"),
        CheckSegmentsCount = C("checkSegmentsCount"),
        ConcurrentDownload = C("concurrentDownload"),
        AppendUrlParams = C("appendUrlParams"),
        UseYtDlp = C("useYtDlp"),
        SkipExisting = C("skipExisting"),
        CleanStaleParts = C("cleanStaleParts"),
        MaxThreads = (int)nudThreads.Value,
        RetryCount = (int)nudRetry.Value,
        Timeout = (int)nudTimeout.Value,
        LimitSpeed = (int)nudSpeed.Value,
        CookieFile = txtCookieFile.Text.Trim(),
        CookieBrowser = SelectedBrowser(),
    };

    /// <summary>下拉框里选的浏览器；"(不使用)" 视为空。</summary>
    string SelectedBrowser()
    {
        var s = Convert.ToString(cmbCookieBrowser.SelectedItem) ?? "";
        return s == "(不使用)" ? "" : s.Trim();
    }

    void StartBatch()
    {
        if (batchRunning) return;
        // 依赖状态点在这里重扫一次：用户可能刚装好 yt-dlp / FFmpeg
        depsProbed = false;
        RefreshDependencyDots();
        if (!OnPath("ffmpeg"))
            AppendLog("⚠ 警告：未找到 FFmpeg（进程 PATH 与注册表 PATH 都查过了）。合并/混流功能依赖 FFmpeg。");

        // RE 模式：路径为空或已失效时先自动重探（构造窗体时的探测可能已过期），
        // 探不到再请用户当场选 —— 原来这里只弹一句「RE执行程序不存在！」加一个空路径，
        // 既不说去哪配，日志里也一个字都不留，属于死胡同提示。
        if (!C("useYtDlp") && !EnsureReExecutable()) return;

        var o = Snapshot();
        if (o.UseYtDlp && !o.OnlyParseM3u8 && !engine.YtDlpAvailable())
        {
            MessageBox.Show(this, "使用 yt-dlp 下载需要 yt-dlp 和 curl-cffi。\n\n请执行：pip install \"yt-dlp[curl-cffi]\"", "错误");
            return;
        }
        if (o.WorkDir.Length == 0) { MessageBox.Show(this, "请设置保存目录", "错误"); return; }

        // Cookie 来源校验：文件不存在必须明确提示，不能静默失败
        // （yt-dlp 对不存在的 cookie 文件是静默忽略，用户会误以为 Cookie 已生效）
        if (o.CookieFile.Length > 0 && !File.Exists(o.CookieFile))
            AppendLog($"⚠ Cookie 文件不存在：{o.CookieFile}（yt-dlp 会静默忽略它，等于没有带任何 Cookie，请重新选择正确的 cookies.txt）");
        if (o.CookieBrowser.Length > 0)
            AppendLog($"ℹ 使用浏览器 Cookie（{o.CookieBrowser}）：读取浏览器 Cookie 数据库前请先完全关闭对应浏览器，否则读取会失败。");

        try
        {
            if (!Directory.Exists(o.WorkDir)) Directory.CreateDirectory(o.WorkDir);
        }
        catch (Exception e) { MessageBox.Show(this, $"无法创建保存目录：{e.Message}", "错误"); return; }
        lock (queueLock)
            if (queue.Count == 0) { MessageBox.Show(this, "任务队列为空，请先粘贴任务", "提示"); return; }

        batchRunning = true;
        SetBatchButtons(false);
        StartBatchWorker(o);
    }

    /// <summary>
    /// RE 模式开跑前自检：路径有效直接过；失效则再探测一次（窗体构造时的探测可能已过期）；
    /// 仍找不到就写日志 + 请用户当场选择，选好后写回「RE 可执行」。
    /// </summary>
    bool EnsureReExecutable()
    {
        var current = txtExe.Text.Trim();
        if (current.Length > 0 && File.Exists(current)) return true;

        var found = DetectReExecutable();
        if (found.Length > 0)
        {
            txtExe.Text = found;
            AppendLog($"✅ 已自动识别 RE 引擎：{found}");
            return true;
        }

        AppendLog("❌ 未找到 N_m3u8DL-RE.exe（RE 引擎）。已检查：进程 PATH、系统/用户 PATH（注册表）、" +
                  @"程序所在目录与其 tools/bin 子目录、Program Files\N_m3u8DL-RE、Program Files\Fluent-M3U8*\tools。");
        if (current.Length > 0)
            AppendLog($"   当前「RE 可执行」指向的文件不存在：{current}");

        var ask = MessageBox.Show(this,
            "未找到 N_m3u8DL-RE.exe（RE 引擎）。\n\n" +
            (current.Length > 0 ? $"当前路径不存在：\n{current}\n\n" : "") +
            "是否现在选择 N_m3u8DL-RE.exe？\n" +
            "选「否」也可以稍后在「设置 → 路径与依赖 → RE 可执行」里设置。",
            "提示", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return false;

        using var dlg = new OpenFileDialog
        {
            Title = "选择 N_m3u8DL-RE.exe",
            Filter = "RE 引擎 (N_m3u8DL-RE.exe)|N_m3u8DL-RE.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            InitialDirectory = SafeDir(current),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return false;
        txtExe.Text = Path.GetFullPath(dlg.FileName);
        AppendLog($"✅ 已选择 RE 引擎：{txtExe.Text}");
        return true;
    }

    void StartBatchWorker(Opt o)
    {
        var epoch = ++batchEpoch;
        List<(int Id, DownloadTask Task)> jobs;
        lock (queueLock)
            jobs = queue.Where(t => t.Status is not ("完成" or "失败")).Select(t => (t.Id, t)).ToList();

        if (jobs.Count == 0)
        {
            batchRunning = false;
            AppendLog("\n===== 队列中没有需要执行的任务 =====");
            OnBatchFinished(epoch);
            return;
        }
        batchRows = [.. jobs.Select(j => j.Id)];
        UpdateQueueStatusLabel();
        var engineName = o.UseYtDlp ? "yt-dlp" : "N_m3u8DL-RE";
        AppendLog($"\n===== 开始批量下载（{engineName}），共 {jobs.Count} 个任务，并发 {(int)nudConc.Value}（可实时调整） =====");
        // 把实际使用的引擎路径打进日志：以后"找不到 RE / 用了另一个 RE"这类问题不用猜
        AppendLog(o.UseYtDlp
            ? $"ℹ 引擎：yt-dlp（{FindOnPathAnywhere("yt-dlp") ?? "PATH"}）"
            : $"ℹ 引擎：N_m3u8DL-RE（{o.Executable}）");
        AppendLog($"ℹ FFmpeg：{FindOnPathAnywhere("ffmpeg") ?? "未找到（只下载不合并可以忽略）"}");
        // 加速项在日志里留痕：出问题时一眼能看出本次用的是哪套参数，不必去翻完整命令行
        AppendLog(o.UseYtDlp
            ? $"⚡ 加速：分片并发 {Math.Max(1, o.MaxThreads)} 路 · 直链分块下载 10M"
            : $"⚡ 加速：自动选流 · 线程 {o.MaxThreads} · 同盘临时目录 · concat 分离器合并 · 已关闭版本检查");
        // 「失败自动重试」轮数必须在 UI 线程读取：批次线程不能跨线程碰控件。
        // 0 = 关闭（开关未勾选时也不重试）。
        var retryRounds = C("autoRetryFailed") ? Math.Max(0, (int)nudAutoRetry.Value) : 0;
        if (retryRounds > 0)
            AppendLog($"ℹ 失败自动重试已开启：全部任务跑完后，失败的任务会自动重跑，最多 {retryRounds} 轮");
        _ = Task.Run(() => BatchLoopAsync(jobs, o, epoch, retryRounds));
    }

    /// <summary>
    /// 批次主循环。<paramref name="autoRetryRounds"/> 是「整批跑完后对失败任务自动重跑的轮数」，
    /// 与 <see cref="Opt.RetryCount"/>（引擎内部单任务的分片重试次数）是两个不同的概念。
    /// </summary>
    async Task BatchLoopAsync(List<(int Id, DownloadTask Task)> jobs, Opt o, int epoch, int autoRetryRounds)
    {
        // submitted 跨轮保留：重试的任务仍由本批次独占，否则第二轮会被下面的「自动拾取」重复入队。
        var submitted = new HashSet<int>();
        var running = new Dictionary<int, Task>();
        var pending = new Queue<(int Id, DownloadTask Task)>(jobs);

        // 跑完一轮待下载队列。返回 false = 用户已停止或批次已过期，调用方直接收尾，不再重试。
        async Task<bool> RunRoundAsync()
        {
            while (batchRunning && epoch == batchEpoch)
            {
                var conc = Math.Max(1, concurrentLimit);
                while (running.Count < conc && pending.Count > 0)
                {
                    var job = pending.Dequeue();
                    submitted.Add(job.Id);
                    // 排队期间被用户删掉的任务：直接跳过，绝不为了它去动表格
                    if (!TaskExists(job.Id)) continue;
                    Ui(() => SetStatus(job.Id, "排队中"));
                    running[job.Id] = Task.Run(() => RunOneTaskAsync(job.Id, job.Task, o, epoch));
                }

                // 下载中继续添加的任务会被自动拾取（身份是 id，删过的任务不会再被捞回来）
                List<(int Id, DownloadTask Task)> extra = [];
                lock (queueLock)
                    extra = queue.Where(t => !submitted.Contains(t.Id) && t.Status == "待下载")
                                 .Select(t => (t.Id, t)).ToList();
                foreach (var j in extra)
                {
                    submitted.Add(j.Id);
                    pending.Enqueue(j);
                }
                if (extra.Count > 0) Ui(() => AppendLog($"➕ 已自动拾取 {extra.Count} 个新任务加入当前批次"));

                var done = running.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList();
                foreach (var k in done)
                {
                    try { await running[k]; }
                    catch (Exception e) { Ui(() => AppendLog($"⚠ 任务线程异常：{e.Message}")); }
                    running.Remove(k);
                }

                if (running.Count == 0 && pending.Count == 0)
                {
                    bool still;
                    lock (queueLock)
                        still = queue.Any(t => !submitted.Contains(t.Id) && t.Status == "待下载");
                    if (!still) return true;
                }
                await Task.Delay(QueuePollMs);
            }
            return false;
        }

        try
        {
            for (var round = 0; ; round++)
            {
                if (!await RunRoundAsync()) break;
                if (round >= autoRetryRounds) break;

                // 只捞「失败」。用户按停止产生的是「已取消」，那是主动行为，不能被自动重跑。
                List<int> failed;
                lock (queueLock)
                    failed = queue.Where(t => t.Status == "失败").Select(t => t.Id).ToList();
                if (failed.Count == 0) break;

                ResetRowsForRetry(failed);
                // 文案必须在投递前算好：Ui() 是 BeginInvoke，lambda 里的 round 会被
                // 循环末尾的 round++ 改掉，直接插值会打印成「第 2/1 轮」。
                var roundNo = round + 1;
                var msg = $"\n===== 失败自动重试 第 {roundNo}/{autoRetryRounds} 轮：重新执行 {failed.Count} 个失败任务 =====";
                Ui(() => AppendLog(msg));
                foreach (var id in failed)
                {
                    var t = TaskById(id);
                    if (t is not null) pending.Enqueue((id, t));
                }
            }
        }
        catch (Exception e) { Ui(() => AppendLog($"❌ 队列线程异常：{e.Message}")); }
        finally
        {
            var wasRunning = batchRunning && epoch == batchEpoch;
            if (epoch == batchEpoch) batchRunning = false;
            if (wasRunning) Ui(() => AppendLog("\n===== 队列全部执行完毕 ====="));
            Ui(() => OnBatchFinished(epoch));
        }
    }

    /// <summary>把指定任务（按 id）重置为「待下载」并清进度：手动重试与自动重试共用同一套重置语义。</summary>
    void ResetRowsForRetry(List<int> ids)
    {
        if (ids.Count == 0) return;
        UiSync(() =>
        {
            updating = true;
            try
            {
                foreach (var id in ids)
                {
                    var row = RowOf(id);
                    if (row < 0) continue;                 // 已被删除
                    lock (queueLock) queue[row].Status = "待下载";
                    if (row < grid.Rows.Count)
                    {
                        grid.Rows[row].Cells[1].Value = "待下载";
                        grid.Rows[row].Cells[1].Style.ForeColor = Theme.Muted;
                        grid.Rows[row].Cells[2].Value = "—";
                        grid.Rows[row].Cells[2].Style.ForeColor = Theme.Muted;
                    }
                }
            }
            finally { updating = false; }
            SaveTasks();
            UpdateQueueStatusLabel();
        });
    }

    async Task RunOneTaskAsync(int id, DownloadTask task, Opt o, int epoch)
    {
        // 过期批次的残留线程：不写状态，也不碰别批次的任务
        if (epoch != batchEpoch) return;
        if (!TaskExists(id)) return;                  // 排队期间被用户删掉了
        if (!batchRunning) { Ui(() => SetStatus(id, "已取消")); return; }

        var workDir = o.WorkDir;
        var name = task.Name;
        int myToken;
        lock (rowLock) { rowToken[id] = rowToken.GetValueOrDefault(id) + 1; myToken = rowToken[id]; }

        // 开始前先看成品是否已经存在：否则"停止队列后重新开始"会从 0% 重下一遍
        if (o.SkipExisting)
        {
            var existing = Outputs.FindFinished(workDir, name, SiblingNames(id));
            if (existing is not null)
            {
                long size = 0;
                try { size = new FileInfo(existing).Length; } catch { }
                // BUG-7：跳过分支的 UI 更新加 token 守卫，避免终态竞态下覆盖新批次的任务。
                Ui(() => { if (OwnsRow(id, myToken)) { SetSize(id, size, 1); SetProgress(id, 100); SetStatus(id, "完成"); } });
                Ui(() => AppendLog($"⏭ [{RowOf(id) + 1}] 成品已存在，跳过重复下载：{Path.GetFileName(existing)}（{Naming.FmtSize(size)}）"));
                if (o.CleanStaleParts)
                {
                    var n = Outputs.CleanupStaleParts(workDir, name);
                    if (n > 0) Ui(() => AppendLog($"   🧹 已清理 {n} 个 .part/分片临时残留"));
                }
                return;
            }
        }

        Ui(() => { if (OwnsRow(id, myToken)) SetStatus(id, "下载中"); });
        Ui(() => AppendLog($"\n===== 开始任务 {RowOf(id) + 1} | {name} ====="));
        var ok = false;
        var startedAt = DateTime.Now;
        try
        {
            // 第 5 个参数是任务 id（引擎内部原样回传，不再当行号用），删任务后行号漂移也不会串行。
            try { ok = await engine.RunAsync(task.Url, name, o, task.Headers, id, startedAt); }
            catch (Exception e) { Ui(() => AppendLog($"❌ 任务 {RowOf(id) + 1} 异常：{e.Message}")); ok = false; }
            // 进程被停止/退出码异常，但成品其实已经落盘 → 按完成处理
            if (!ok && Outputs.Exists(workDir, name, startedAt))
            {
                ok = true;
                Ui(() => AppendLog($"✅ [{RowOf(id) + 1}] 检测到成品文件已生成，按完成处理"));
            }
        }
        finally
        {
            // 成品大小要在后台线程算：OwnFilesSize 内部会轮询等文件句柄落定（最坏 5×200ms），
            // 放进 UiSync 里就是把 UI 线程按在那里等 1 秒，并发 20 时窗口直接假死。
            var (finalSize, finalCount) = ok ? Outputs.OwnFilesSize(workDir, name) : (0L, 0);
            // 顺手清掉本次留下的空临时目录（目录里还有别的任务在写时它自己会跳过）
            Outputs.CleanupTmpDir(workDir);
            // BUG-7：终态必须在同一 UI 帧内"设状态 + 落盘"，且用任务 id 定位（不再用行号）。
            if (OwnsRow(id, myToken))
            {
                UiSync(() =>
                {
                    if (!OwnsRow(id, myToken)) return; // 双重检查：排队到 UI 线程期间任务可能已被删除
                    if (finalSize > 0) SetSize(id, finalSize, finalCount);
                    if (ok) SetStatus(id, "完成");
                    else if (!batchRunning) SetStatus(id, "已取消");
                    else SetStatus(id, "失败");
                });
            }
        }
    }

    /// <summary>任务仍在队列里、且令牌未被作废（删除/重排会让令牌失效），才允许写它的行。</summary>
    bool OwnsRow(int id, int token)
    {
        lock (rowLock) if (rowToken.GetValueOrDefault(id) != token) return false;
        return TaskExists(id);
    }

    string[] SiblingNames(int excludeId)
    {
        lock (queueLock)
            return queue.Where(t => t.Id != excludeId)
                        .Select(t => t.Name)
                        .Where(n => !string.IsNullOrEmpty(n)).ToArray();
    }

    void StopBatch()
    {
        if (!batchRunning) return;
        batchRunning = false;
        engine.TerminateAll();
        // 停止后必须把「排队中 / 下载中」落成终态：RunOneTaskAsync 只处理已经跑起来的任务，
        // 还堵在 pending 队列里、或被并发上限压在后面的那些没有机会回到终态，
        // 会永远停在「排队中」——用户看到的就是"已经停了，可任务还在转"。
        CancelActiveTasks();
        // S4 双态按钮：统一走 SetBatchButtons，恢复「开始下载」主色态
        SetBatchButtons(true);
        UpdateQueueStatusLabel();
        AppendLog("🛑 队列已停止");
    }

    /// <summary>把所有「下载中 / 排队中」的任务一次性置为「已取消」并落盘（只写一次盘）。</summary>
    void CancelActiveTasks()
    {
        List<int> ids;
        lock (queueLock)
            ids = queue.Where(t => t.Status is "排队中" or "下载中").Select(t => t.Id).ToList();
        if (ids.Count == 0) return;

        int changed = 0;
        updating = true;
        try
        {
            foreach (var id in ids)
            {
                var row = RowOf(id);
                if (row < 0) continue;
                lock (queueLock) queue[row].Status = "已取消";
                if (row >= grid.Rows.Count) continue;
                grid.Rows[row].Cells[1].Value = "已取消";
                grid.Rows[row].Cells[1].Style.ForeColor = Theme.Cancel;
                var cur = Convert.ToString(grid.Rows[row].Cells[2].Value) ?? "";
                if (!cur.EndsWith('%')) grid.Rows[row].Cells[2].Value = "—";
                grid.Rows[row].Cells[2].Style.ForeColor = Theme.Cancel;
                changed++;
            }
        }
        finally { updating = false; }
        if (changed > 0) SaveTasks();
    }

    void OnBatchFinished(int epoch)
    {
        // 过期批次的收尾信号直接忽略：否则会把正在跑的新批次的按钮重新启用
        if (epoch != batchEpoch) return;
        SetBatchButtons(true);
        batchRows.Clear();   // 批次结束，登记表已无意义；留着只会在下次开跑前被误读
        SaveTasks();
        // 缩在托盘里跑完时给个气泡提示，否则用户不知道已经下完了
        if (tray is { Visible: true } && !Visible)
        {
            try
            {
                tray.ShowBalloonTip(1500, "下载完成", $"队列已执行完毕，共 {QueueCount} 个任务。", ToolTipIcon.Info);
            }
            catch { }
        }
    }
}
