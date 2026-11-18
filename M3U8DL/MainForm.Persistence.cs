using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace M3U8DL;

/// <summary>
/// MainForm 的任务持久化部分。SaveTasks 保留原名与同步行为（Tests.cs:600 直接反射调用）。
/// </summary>
internal sealed partial class MainForm : Form
{
    // BUG-8：批量抑制标志。
    // 单次 AddTask 立即落盘（Tests.cs:569 在两次 AddTask 后无消息泵地立即检查文件）；
    // 批量粘贴期间用 bulkSave 抑制每条写，结束统一落盘，实现 100 条 → 1 次写盘。
    bool bulkSave;
    /// <summary>
    /// 落盘串行化。SaveTasks 会写同一个临时文件名再 Move 覆盖，
    /// 一旦 UI 线程的终态回写与批量粘贴的收尾写盘撞上就会互相踩掉临时文件。
    /// </summary>
    readonly object saveLock = new();

    /// <summary>关闭前的强制落盘（FormClosing 调用）。</summary>
    void FlushPendingSave() => SaveTasks();

    // 与 settings.json 同一个配置目录（AppDir 见 MainForm.Settings.cs）
    static string TasksFile => Path.Combine(AppDir, "tasks.json");

    void SaveTasks()
    {
        try
        {
            lock (saveLock)
            {
                var dir = Path.GetDirectoryName(TasksFile)!;
                Directory.CreateDirectory(dir);
                List<DownloadTask> snapshot;
                lock (queueLock) snapshot = queue.Select(t => new DownloadTask
                { Name = t.Name, Url = t.Url, Status = t.Status, Headers = [.. t.Headers] }).ToList();
                var tmp = TasksFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts), new UTF8Encoding(false));
                // BUG-2：写盘前留一份可回滚副本；备份失败不能阻断主写盘流程。
                try { if (File.Exists(TasksFile)) File.Copy(TasksFile, TasksFile + ".bak", true); }
                catch { }
                File.Move(tmp, TasksFile, true);
            }
        }
        catch { }
    }

    void LoadTasks()
    {
        try
        {
            if (!File.Exists(TasksFile)) return;
            // 必须与 SaveTasks 使用同一套 JsonOpts：camelCase 命名策略。
            // 否则默认大小写敏感匹配会把 camelCase 字段名绑不到 PascalCase 属性上，导致任务全部丢失。
            var data = JsonSerializer.Deserialize<List<DownloadTask>>(
                File.ReadAllText(TasksFile, Encoding.UTF8), JsonOpts);
            if (data is null) return;
            var resumed = 0;
            var skipped = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            // BUG-2：只在加载过程发生"有损规范化"时才回写。原实现无条件 SaveTasks，
            // 会把空名/去重/状态改写/名称清洗的结果永久写回用户文件 —— 一次启动即不可逆丢数据。
            var dirty = false;
            loading = true;
            try
            {
                foreach (var t in data)
                {
                    if (t is null) continue;
                    var name = (t.Name ?? "").Trim();
                    var url = (t.Url ?? "").Trim();
                    if (name.Length == 0 || url.Length == 0) { skipped++; dirty = true; continue; }
                    // BUG-13：去重键与 AddTask:513 的 Naming.Sanitize 保持一致，
                    // 否则 "A:B" 与 "A/B" 清洗后都变成 "A_B" 却不会被当成重复项拦下。
                    var key = Naming.Sanitize(name, "").ToLowerInvariant() + "|" + url;
                    if (!seen.Add(key)) { skipped++; dirty = true; continue; }
                    var status = string.IsNullOrEmpty(t.Status) ? "待下载" : t.Status;
                    if (status is "下载中" or "排队中") { status = "待下载"; resumed++; dirty = true; }
                    // AddTask 内部会做 Naming.Sanitize，若发生改名则标记 dirty
                    var sanitized = Naming.Sanitize(name, $"task_{grid.Rows.Count + 1}");
                    if (!string.Equals(sanitized, name, StringComparison.Ordinal)) dirty = true;
                    AddTask(name, url, status, t.Headers ?? []);
                    added++;
                }
            }
            finally { loading = false; }
            if (dirty) SaveTasks();
            if (added > 0)
            {
                var msg = $"📂 已恢复 {added} 条历史任务";
                if (resumed > 0) msg += $"（{resumed} 条未完成任务，点击「开始队列下载」继续）";
                if (skipped > 0) msg += $"，已忽略 {skipped} 条重复记录";
                AppendLog(msg);
            }
        }
        catch (Exception e) { AppendLog($"⚠ 读取历史任务失败：{e.Message}"); }
    }
}
