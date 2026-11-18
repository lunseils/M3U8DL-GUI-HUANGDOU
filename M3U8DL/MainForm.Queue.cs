using System.Diagnostics;

namespace M3U8DL;

/// <summary>
/// MainForm 的队列增删改查、剪贴板交互、文件定位、路径浏览部分。
/// AddTask / PasteTasks / CopySelection 等反射契约方法保留在 MainForm 类型本身。
/// </summary>
internal sealed partial class MainForm : Form
{
    // ============================================================
    // 基础动作
    // ============================================================
    void BrowseExe()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 N_m3u8DL-RE 可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            InitialDirectory = SafeDir(txtExe.Text.Trim()),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtExe.Text = Path.GetFullPath(dlg.FileName);
            AppendLog($"✅ 已选择执行程序：{dlg.FileName}");
        }
    }

    void BrowseDir()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "选择保存目录",
            UseDescriptionForTitle = true,
            SelectedPath = SafeDir(txtWorkDir.Text.Trim()),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtWorkDir.Text = Path.GetFullPath(dlg.SelectedPath);
            AppendLog($"✅ 已选择保存目录：{dlg.SelectedPath}");
        }
    }

    void BrowseMux()
    {
        using var dlg = new OpenFileDialog { Title = "选择混流文件", Filter = "所有文件 (*.*)|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK) txtMux.Text = dlg.FileName;
    }

    /// <summary>选择 cookies.txt（Netscape 格式的 Cookie 文件）。</summary>
    void BrowseCookieFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择 Cookie 文件（cookies.txt）",
            Filter = "Cookie 文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            InitialDirectory = SafeDir(txtCookieFile.Text.Trim()),
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            txtCookieFile.Text = Path.GetFullPath(dlg.FileName);
            AppendLog($"✅ 已选择 Cookie 文件：{dlg.FileName}");
        }
    }

    static string SafeDir(string path)
    {
        try { var d = Path.GetDirectoryName(path); return !string.IsNullOrEmpty(d) && Directory.Exists(d) ? d : Environment.CurrentDirectory; }
        catch { return Environment.CurrentDirectory; }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    void OpenSaveDir()
    {
        var dir = txtWorkDir.Text.Trim();
        if (dir.Length == 0) { MessageBox.Show(this, "尚未设置保存目录", "提示"); return; }
        if (!Directory.Exists(dir))
        {
            if (MessageBox.Show(this, $"保存目录不存在：\n{dir}\n\n是否现在创建并打开？", "目录不存在",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try { Directory.CreateDirectory(dir); }
            catch (Exception e) { MessageBox.Show(this, $"无法创建目录：\n{e.Message}", "错误"); return; }
        }
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); AppendLog($"📂 已打开保存目录：{dir}"); }
        catch (Exception e) { MessageBox.Show(this, $"无法打开目录：\n{e.Message}", "错误"); }
    }

    void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
            AppendLog($"📂 已定位文件：{path}");
        }
        catch (Exception e) { MessageBox.Show(this, $"无法打开文件位置：\n{e.Message}", "错误"); }
    }

    // ============================================================
    // 任务队列操作
    // ============================================================
    /// <summary>当前选中的行号（升序）。</summary>
    List<int> SelectedRows() =>
        grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToList();

    int TaskIdAt(int row)
    {
        lock (queueLock) return row >= 0 && row < queue.Count ? queue[row].Id : -1;
    }

    string StatusAt(int row)
    {
        lock (queueLock) return row >= 0 && row < queue.Count ? queue[row].Status : "";
    }

    bool IsFinishedRow(int row) => StatusAt(row) is "完成" or "失败" or "已取消";

    bool AddTask(string name, string url, string status, List<string>? headers = null, bool dedupe = false)
    {
        headers ??= [];
        name = Naming.Sanitize(name, $"task_{grid.Rows.Count + 1}");
        if (dedupe && !loading && HasUrl(url)) return false;
        if (!loading && HasName(name))
            AppendLog($"⚠ 任务名重复：{name}（同名任务的文件大小统计会互相混淆）");

        // 先入模型（拿 id），再补视图：两者必须同步增长，行号才能直接对应 Queue 下标
        var task = new DownloadTask { Id = ++taskSeq, Name = name, Url = url, Status = status, Headers = headers };
        lock (queueLock) queue.Add(task);

        RenderRow(grid.Rows.Add(), task);

        // BUG-8：批量粘贴时 bulkSave=true 抑制每条写盘，由 PasteTasks 结束统一 SaveTasks。
        // 单次添加仍立即落盘（保持既有行为与落盘断言）。
        if (!loading && !bulkSave) SaveTasks();
        // 空闲时也要刷新状态栏：否则粘贴完任务后左下角还停在「共 0 个任务」
        UpdateQueueStatusLabel();
        if (batchRunning) batchRows.Add(task.Id);
        return true;
    }

    bool HasUrl(string url)
    {
        lock (queueLock) return queue.Any(t => t.Url == url);
    }

    bool HasName(string name)
    {
        lock (queueLock) return queue.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把模型写进一行表格（新增任务 / 排序搬行共用，避免两处分别维护单元格格式）。</summary>
    void RenderRow(int row, DownloadTask t)
    {
        if (row < 0 || row >= grid.Rows.Count) return;
        updating = true;
        try
        {
            var r = grid.Rows[row];
            r.Cells[ColName].Value = t.Name;
            r.Cells[ColName].ToolTipText = t.Url;
            r.Cells[ColStatus].Value = t.Status;
            r.Cells[ColStatus].Style.ForeColor = StatusColor(t.Status);
            r.Cells[ColProgress].Value = t.Status == "完成" ? "100%" : "—";
            r.Cells[ColProgress].Style.ForeColor = t.Status == "完成" ? Theme.Done : Theme.Muted;
            r.Cells[ColProgress].Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            r.Cells[ColSpeed].Value = "—";
            r.Cells[ColSpeed].Style.ForeColor = Theme.Muted;
            r.Cells[ColSpeed].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            r.Cells[ColSize].Value = "—";
            r.Cells[ColSize].Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
        finally { updating = false; }
    }

    /// <summary>
    /// 删除选中任务。下载中也可以删（需求：下载中可随意删减队列）——
    /// 正在下载的那条会先中止进程，再按 id 清理批次登记，行号漂移不会影响其它任务的进度回写。
    /// </summary>
    void DeleteSelected()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) { MessageBox.Show(this, "请先选中要删除的任务", "提示"); return; }
        RemoveRows(rows);
    }

    void RemoveRows(List<int> rowsAsc)
    {
        var ids = new List<int>();
        var killed = 0;
        foreach (var r in rowsAsc)
        {
            var id = TaskIdAt(r);
            if (id < 0) continue;
            ids.Add(id);
            // 正在下载的任务先杀进程，否则它会继续占带宽、继续写文件
            if (StatusAt(r) is "下载中" or "排队中" && engine.TerminateTask(id)) killed++;
        }
        if (ids.Count == 0) return;

        updating = true;
        try
        {
            for (var i = rowsAsc.Count - 1; i >= 0; i--)
            {
                var r = rowsAsc[i];
                if (r < 0 || r >= grid.Rows.Count) continue;
                grid.Rows.RemoveAt(r);
                lock (queueLock) if (r < queue.Count) queue.RemoveAt(r);
            }
        }
        finally { updating = false; }

        foreach (var id in ids)
        {
            batchRows.Remove(id);
            // 令牌直接作废：在途的引擎回调经 OwnsRow 判空后会自行丢弃，不会写到别人的行上
            lock (rowLock) rowToken.Remove(id);
            lock (queueLock) speeds.Remove(id);   // 否则底部「总速度」会把已删任务的速率一直加着
        }
        SaveTasks();
        UpdateQueueStatusLabel();
        AppendLog($"🗑 已删除 {ids.Count} 个任务" + (killed > 0 ? $"（其中 {killed} 个正在下载，已中止）" : ""));
    }

    void ClearAll()
    {
        if (QueueCount == 0) return;
        if (batchRunning &&
            MessageBox.Show(this, "队列正在下载。\n清空会中止所有正在下载的任务，确定继续？", "清空全部任务",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        // 先杀进程再清表：避免清空后进程还在后台写文件
        engine.TerminateAll();
        updating = true;
        try
        {
            grid.Rows.Clear();
            lock (queueLock) { queue.Clear(); speeds.Clear(); }
            batchRows.Clear();
            lock (rowLock) rowToken.Clear();
        }
        finally { updating = false; }
        SaveTasks();
        UpdateQueueStatusLabel();
        AppendLog("已清空任务队列");
    }

    /// <summary>移动到队首 / 上移 / 下移 / 队尾：只对单选生效，下载中也可用（身份是 id，不依赖行号）。</summary>
    void MoveSelected(int delta)
    {
        var rows = SelectedRows();
        if (rows.Count != 1) { MessageBox.Show(this, "请只选中一行再调整顺序", "提示"); return; }
        var row = rows[0];
        int target;
        lock (queueLock)
        {
            target = delta switch
            {
                int.MinValue => 0,
                int.MaxValue => queue.Count - 1,
                _ => row + delta,
            };
            target = Math.Clamp(target, 0, Math.Max(0, queue.Count - 1));
            if (target == row) return;
            var task = queue[row];
            queue.RemoveAt(row);
            queue.Insert(target, task);
        }

        // 视图：把整行各单元格的内容与配色搬到目标位置（不动模型，模型已移好）。
        // 列数用 grid.Columns.Count 而非常量，将来再加列不会漏搬一列。
        var cols = grid.Columns.Count;
        updating = true;
        try
        {
            var backup = new (object? Value, Color Fore, DataGridViewContentAlignment Align, string Tip)[cols];
            for (var c = 0; c < cols; c++)
            {
                var cell = grid.Rows[row].Cells[c];
                backup[c] = (cell.Value, cell.Style.ForeColor, cell.Style.Alignment, cell.ToolTipText);
            }
            grid.Rows.RemoveAt(row);
            grid.Rows.Insert(target, 1);
            for (var c = 0; c < cols; c++)
            {
                var cell = grid.Rows[target].Cells[c];
                cell.Value = backup[c].Value;
                cell.Style.ForeColor = backup[c].Fore;
                cell.Style.Alignment = backup[c].Align;
                cell.ToolTipText = backup[c].Tip;
            }
            grid.ClearSelection();
            grid.Rows[target].Selected = true;
            grid.CurrentCell = grid.Rows[target].Cells[ColName];
        }
        finally { updating = false; }
        AppendLog($"↕ 任务已移动到第 {target + 1} 位");
        SaveTasks();
    }

    void InvertSelection()
    {
        updating = true;
        try
        {
            foreach (DataGridViewRow r in grid.Rows) r.Selected = !r.Selected;
        }
        finally { updating = false; }
    }

    /// <summary>复制选中任务的 URL（右键菜单「复制链接」）。</summary>
    void CopySelectedUrls()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var urls = new List<string>();
        lock (queueLock)
            foreach (var r in rows)
                if (r >= 0 && r < queue.Count) urls.Add(queue[r].Url);
        if (urls.Count == 0) return;
        if (!TryClipboardSet(string.Join(Environment.NewLine, urls)))
        {
            MessageBox.Show(this, "剪贴板正被其他程序占用，复制失败，请稍后重试", "提示");
            return;
        }
        AppendLog($"🔗 已复制 {urls.Count} 条任务链接");
    }

    /// <summary>用系统默认播放器打开选中任务的成品文件。</summary>
    void OpenTaskFile()
    {
        var idx = SingleSelectedRow("请只选中一个任务");
        if (idx < 0) return;
        var task = TaskAt(idx);
        if (task is null) return;
        if (task.Value.Status != "完成")
        {
            MessageBox.Show(this, $"任务「{task.Value.Name}」尚未下载完成（当前状态：{task.Value.Status}）。", "提示");
            return;
        }
        var target = Outputs.FindTaskFile(txtWorkDir.Text.Trim(), task.Value.Name);
        if (target is null)
        {
            AppendLog($"⚠ 未在保存目录中找到与「{task.Value.Name}」匹配的文件");
            OpenSaveDir();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            AppendLog($"▶ 已打开文件：{Path.GetFileName(target)}");
        }
        catch (Exception e) { MessageBox.Show(this, $"无法打开文件：\n{e.Message}", "错误"); }
    }

    /// <summary>只重试选中的任务（右键菜单），与「重试全部失败任务」区分。</summary>
    void RetrySelected()
    {
        if (batchRunning) { MessageBox.Show(this, "批量下载进行中，请先停止队列", "提示"); return; }
        var rows = SelectedRows();
        var ids = rows.Where(r => StatusAt(r) is "失败" or "已取消")
                      .Select(TaskIdAt).Where(id => id >= 0).ToList();
        if (ids.Count == 0) { MessageBox.Show(this, "选中的任务里没有失败/已取消的任务", "提示"); return; }
        ResetRowsForRetry(ids);
        AppendLog($"🔄 已将 {ids.Count} 个任务重置为「待下载」，开始重试");
        StartBatch();
    }

    void RetryFailed()
    {
        if (batchRunning) { MessageBox.Show(this, "批量下载进行中，请先停止队列", "提示"); return; }
        List<int> failed;
        lock (queueLock) failed = queue.Where(t => t.Status is "失败" or "已取消").Select(t => t.Id).ToList();
        if (failed.Count == 0) { MessageBox.Show(this, "当前没有失败的任务", "提示"); return; }
        ResetRowsForRetry(failed); // 与批次内「失败自动重试」共用同一套重置语义（含落盘、进度归零）
        AppendLog($"🔄 已将 {failed.Count} 个失败任务重置为「待下载」，开始重试");
        StartBatch();
    }

    void RenameSelected()
    {
        if (batchRunning) { MessageBox.Show(this, "批量下载进行中，请先停止队列再重命名", "提示"); return; }
        var idx = SingleSelectedRow("请先选中要重命名的任务（一次只能重命名一个）");
        if (idx < 0) return;
        var oldName = TaskAt(idx)?.Name ?? "";
        var asked = PromptDialog.Ask(this, $"任务 {idx + 1} 的新名称：", "重命名任务", oldName);
        if (asked is null) return;
        ApplyRename(idx, asked);
    }

    /// <summary>
    /// 应用重命名：① 先改模型 → ② 抑制事件回写 → ③ 显式落盘。
    /// BUG-1 根因：原来只 set cell.Value，同步触发 CellValueChanged 被 updating 吞掉，模型与落盘全跳过。
    /// 非模态，供右键菜单与反射测试直接调用（测试看门狗会关掉模态对话框，不能靠 RenameSelected 测）。
    /// </summary>
    void ApplyRename(int row, string newNameRaw)
    {
        if (batchRunning) return;
        var newName = Naming.Sanitize(newNameRaw, "");
        if (newName.Length == 0) return;
        string oldName;
        lock (queueLock)
        {
            if (row < 0 || row >= queue.Count) return;
            oldName = queue[row].Name;
            if (newName == oldName) return;
            queue[row].Name = newName;                  // ① 先改模型，不依赖事件
        }
        updating = true;                                // ② 只抑制事件回写
        try { if (row < grid.Rows.Count) grid.Rows[row].Cells[0].Value = newName; }
        finally { updating = false; }
        SaveTasks();                                    // ③ 显式落盘
        AppendLog($"✏ 任务 {row + 1} 重命名：{oldName} → {newName}");
    }

    void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (updating || e.ColumnIndex != 0 || e.RowIndex < 0) return;
        var row = e.RowIndex;
        var raw = Convert.ToString(grid.Rows[row].Cells[0].Value) ?? "";
        var newName = Naming.Sanitize(raw, "");
        if (batchRunning)
        {
            RevertName(row);
            MessageBox.Show(this, "批量下载进行中，无法修改任务名", "提示");
            return;
        }
        if (raw.Trim().Length == 0 || newName.Length == 0) { RevertName(row); return; }
        if (newName != raw)
        {
            updating = true;
            try { grid.Rows[row].Cells[0].Value = newName; }
            finally { updating = false; }
        }
        string oldName;
        lock (queueLock)
        {
            if (row >= queue.Count) return;
            oldName = queue[row].Name;
            if (oldName == newName) return;
            queue[row].Name = newName;
        }
        SaveTasks();
        AppendLog($"✏ 任务 {row + 1} 重命名：{oldName} → {newName}");
    }

    void RevertName(int row)
    {
        updating = true;
        try
        {
            lock (queueLock)
                if (row < queue.Count) grid.Rows[row].Cells[0].Value = queue[row].Name;
        }
        finally { updating = false; }
    }

    void PasteTasks()
    {
        if (!TryClipboardGet(out var text))
        {
            MessageBox.Show(this, "剪贴板正被其他程序占用，请稍后重试", "提示");
            return;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "剪贴板为空，请先复制任务文本", "提示");
            return;
        }
        PasteText(text, "剪贴板");
    }

    /// <summary>
    /// 把一段任务文本加入队列并报告结果。<paramref name="source"/> 只用于日志措辞。
    /// 空态点击的静默粘贴直接复用这里，不再二次读剪贴板 —— 两次读取之间剪贴板可能已被
    /// 别的程序改掉，会出现"检查时有内容、真正粘贴时却是空"的错位。
    /// </summary>
    void PasteText(string text, string source)
    {
        var (added, skipped) = AddTaskLines(text);
        if (added == 0)
        {
            AppendLog($"⚠ {source}中没有符合 name|url|referer|ua|cookie|origin|x-cdn-auth 格式的任务行");
            foreach (var s in skipped.Take(10)) AppendLog($"  ⚠ {s}");
            return;
        }
        var msg = source == "剪贴板" ? $"✅ 已从剪贴板粘贴 {added} 条任务" : $"✅ 已添加 {added} 条任务";
        if (skipped.Count > 0) msg += $"（跳过 {skipped.Count} 行无效数据）";
        if (batchRunning) msg += "，将自动加入当前批次执行";
        AppendLog(msg);
        foreach (var s in skipped.Take(10)) AppendLog($"  ⚠ {s}");
        if (skipped.Count > 10) AppendLog($"  … 还有 {skipped.Count - 10} 行无效数据被跳过");
    }

    /// <summary>
    /// 任务行文本 → 队列（粘贴 / 拖放 .txt / 手动输入共用）。
    /// 返回实际加入条数与逐行跳过原因；落盘在这里统一做一次（BUG-8：批量期间抑制逐条写盘）。
    /// </summary>
    (int Added, List<string> Skipped) AddTaskLines(string text)
    {
        var added = 0;
        var skipped = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        bulkSave = true;
        try
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (!TaskLines.TryParse(lines[i], i + 1, out var parsed, out var reason))
                {
                    if (reason.Length > 0) skipped.Add(reason);
                    continue;
                }
                if (AddTask(parsed.Name, parsed.Url, "待下载", parsed.Headers, dedupe: true)) added++;
                else skipped.Add($"第{i + 1}行：该 URL 已在队列中，已跳过");
            }
        }
        finally { bulkSave = false; }
        if (added > 0) SaveTasks();
        return (added, skipped);
    }

    /// <summary>空态点击触发的静默粘贴：剪贴板无任务文本时不弹任何提示。</summary>
    void PasteFromEmptyState()
    {
        if (!TryClipboardGet(out var text) || string.IsNullOrWhiteSpace(text)) return;
        PasteText(text, "剪贴板");
    }

    void CopySelection()
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        var lines = new List<string>();
        lock (queueLock)
            foreach (var r in rows)
                if (r >= 0 && r < queue.Count) lines.Add(TaskLines.ToLine(queue[r]));
        if (!TryClipboardSet(string.Join('\n', lines)))
        {
            MessageBox.Show(this, "剪贴板正被其他程序占用，复制失败，请稍后重试", "提示");
            return;
        }
        AppendLog($"📋 已复制 {lines.Count} 条任务（下载数据格式）");
    }

    /// <summary>
    /// 读取剪贴板文本。剪贴板可能被其他进程短暂独占（CLIPBRD_E_CANT_OPEN），
    /// 此时重试若干次；全部失败则返回 false，调用方应提示用户而不是当成"空剪贴板"。
    /// </summary>
    static bool TryClipboardGet(out string text)
    {
        for (int i = 0; ; i++)
        {
            try { text = Clipboard.GetText(); return true; }
            catch when (i < 50) { Thread.Sleep(100); }
            catch { text = ""; return false; }
        }
    }

    /// <summary>写入剪贴板文本，失败时重试；全部失败返回 false。</summary>
    static bool TryClipboardSet(string text)
    {
        for (int i = 0; ; i++)
        {
            try { Clipboard.SetText(text); return true; }
            catch when (i < 50) { Thread.Sleep(100); }
            catch { return false; }
        }
    }

    /// <summary>当前单选行；不是恰好一行时提示并返回 -1。</summary>
    int SingleSelectedRow(string prompt)
    {
        var rows = SelectedRows();
        if (rows.Count != 1) { MessageBox.Show(this, prompt, "提示"); return -1; }
        return rows[0];
    }

    /// <summary>某行的任务名与状态；行号越界（已被删）返回 null。</summary>
    (string Name, string Status)? TaskAt(int row)
    {
        lock (queueLock)
        {
            if (row < 0 || row >= queue.Count) return null;
            return (queue[row].Name, queue[row].Status);
        }
    }

    void LocateTaskFile(int? row = null)
    {
        var workDir = txtWorkDir.Text.Trim();
        if (workDir.Length == 0 || !Directory.Exists(workDir))
        {
            MessageBox.Show(this, $"保存目录不存在或未设置：\n{workDir}\n\n请先选择保存目录。", "提示");
            return;
        }
        // BUG-4：名为「定位文件」，未选中时必须明确提示，不再静默改开保存目录。
        // 「打开保存目录」由独立的「打开目录」按钮承担。
        var idx = row ?? SingleSelectedRow("请先选中要定位的任务");
        if (idx < 0) return;

        var task = TaskAt(idx);
        if (task is null) return;
        if (task.Value.Status is not ("完成" or "失败" or "已取消"))
        {
            MessageBox.Show(this, $"任务「{task.Value.Name}」尚未下载完成（当前状态：{task.Value.Status}）。", "提示");
            return;
        }
        var target = Outputs.FindTaskFile(workDir, task.Value.Name);
        if (target is not null) { RevealInExplorer(target); return; }
        AppendLog($"⚠ 未在保存目录中找到与「{task.Value.Name}」匹配的文件，已改为打开目录");
        OpenSaveDir();
    }
}
