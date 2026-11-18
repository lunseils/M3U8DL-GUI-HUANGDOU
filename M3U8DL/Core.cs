using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace M3U8DL;

/// <summary>队列中的一个下载任务。</summary>
internal sealed class DownloadTask
{
    /// <summary>
    /// 任务唯一 id（入队时发号，删除后不复用）。
    /// 下载中允许随时增删任务、调整顺序，行号会漂移，因此引擎回调、批次进度、
    /// 令牌守卫一律以 Id 为身份，绝不使用行号。
    /// 运行期状态，不参与持久化：每次启动由 AddTask 重新发号，写进 tasks.json 只会是
    /// 一排 "id": 0 的无意义字段。
    /// </summary>
    [JsonIgnore]
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Status { get; set; } = "待下载";
    public List<string> Headers { get; set; } = [];
}

/// <summary>界面选项快照：一次运行期间保持不变，交给工作线程使用。</summary>
internal sealed record Opt
{
    public string Executable { get; init; } = "";
    public string WorkDir { get; init; } = "";
    public string MuxFile { get; init; } = "";
    public bool DelAfterMerge { get; init; }
    public bool NoDateInName { get; init; }
    public bool NoSystemProxy { get; init; }
    public bool OnlyParseM3u8 { get; init; }
    public bool MuxWhileDownload { get; init; }
    public bool NoMerge { get; init; }
    public bool BinaryMerge { get; init; }
    public bool NoLog { get; init; }
    public bool CheckSegmentsCount { get; init; }
    public bool ConcurrentDownload { get; init; }
    public bool AppendUrlParams { get; init; }
    public bool UseYtDlp { get; init; }
    public bool SkipExisting { get; init; }
    public bool CleanStaleParts { get; init; }
    public int MaxThreads { get; init; }
    public int RetryCount { get; init; }
    public int Timeout { get; init; }
    public int LimitSpeed { get; init; }

    /// <summary>Cookie 文件路径（Netscape cookies.txt）。非空时用 yt-dlp --cookies 传入。</summary>
    public string CookieFile { get; init; } = "";

    /// <summary>浏览器名（chrome / edge / firefox …）。非空时用 yt-dlp --cookies-from-browser 传入。</summary>
    public string CookieBrowser { get; init; } = "";
}

/// <summary>文件名规则与成品识别规则（与 Python 版行为完全一致）。</summary>
internal static class Naming
{
    public static readonly string[] MediaExts =
        [".mp4", ".mkv", ".ts", ".flv", ".avi", ".mov", ".webm",
         ".m4a", ".mp3", ".aac", ".flac", ".wav",
         ".srt", ".ass", ".vtt", ".ssa"];

    public static readonly string[] VideoExts =
        [".mp4", ".mkv", ".ts", ".flv", ".avi", ".mov", ".webm"];

    public static readonly string[] AudioExts =
        [".m4a", ".mp3", ".aac", ".flac", ".wav"];

    // 下载中间产物的后缀：.part / .ytdl / 分片缓存等
    static readonly string[] TempMarks =
        [".part", ".ytdl", ".temp", ".tmp", ".download", ".!qb"];

    // 只有 -Frag12 这种带编号的才算临时产物；
    // 用 "-frag" 子串判断会把 my-fragments.mp4 这种正常成品永久判为未完成
    static readonly Regex FragRe = new(@"-frag\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // yt-dlp 音视频分离时的单流文件：Name.f137.mp4 / Name.f251.webm
    static readonly Regex StreamRestRe = new(@"^\.f[0-9A-Za-z_-]{1,24}\.[A-Za-z0-9]{2,5}$", RegexOptions.Compiled);

    static readonly HashSet<string> ReservedNames =
        ["CON", "PRN", "AUX", "NUL",
         ..Enumerable.Range(1, 9).Select(i => $"COM{i}"),
         ..Enumerable.Range(1, 9).Select(i => $"LPT{i}")];

    /// <summary>文件是否属于某个任务（任务名 + . _ - 空格 分隔的后缀）。</summary>
    public static bool Matches(string filename, string name)
    {
        if (string.IsNullOrEmpty(name) || !filename.StartsWith(name, StringComparison.Ordinal))
            return false;
        var rest = filename[name.Length..];
        return rest.Length == 0 || rest[0] is '.' or '_' or '-' or ' ';
    }

    /// <summary>是否是未完成的中间产物，绝不能当成"下载完成"的证据。</summary>
    public static bool IsTempOutput(string filename)
    {
        var low = filename.ToLowerInvariant();
        foreach (var m in TempMarks)
            if (low.EndsWith(m, StringComparison.Ordinal)) return true;
        return FragRe.IsMatch(low);
    }

    /// <summary>是否是 yt-dlp 分离出来的单流文件（相对任务名判断，避免误伤）。</summary>
    public static bool IsPartialStream(string filename, string name)
    {
        if (string.IsNullOrEmpty(name) || !filename.StartsWith(name, StringComparison.Ordinal))
            return false;
        return StreamRestRe.IsMatch(filename[name.Length..]);
    }

    /// <summary>把任务名清洗成合法文件名，避免下载器创建文件时直接失败。</summary>
    public static string Sanitize(string? name, string fallback = "task")
    {
        var sb = new StringBuilder(name?.Length ?? 0);
        foreach (var ch in name ?? "")
            sb.Append(ch is < ' ' or '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : ch);
        // 先截断再去尾：截断很可能刚好落在空格或点上，Windows 无法创建这种文件名
        var s = sb.ToString().Trim();
        s = s[..Math.Min(s.Length, 150)].TrimEnd(' ', '.');
        if (s.Length == 0) s = fallback;
        var head = s.Split('.')[0].Trim().ToUpperInvariant();
        if (ReservedNames.Contains(head)) s = "_" + s;
        return s;
    }

    public static string FmtSize(long n)
    {
        if (n <= 0) return "—";
        if (n < 1024) return $"{n} B";
        if (n < 1024 * 1024) return FormattableString.Invariant($"{n / 1024.0:0.0} KB");
        if (n < 1024L * 1024 * 1024) return FormattableString.Invariant($"{n / 1024.0 / 1024:0.00} MB");
        if (n < 1024L * 1024 * 1024 * 1024) return FormattableString.Invariant($"{n / 1024.0 / 1024 / 1024:0.00} GB");
        return FormattableString.Invariant($"{n / 1024.0 / 1024 / 1024 / 1024:0.00} TB");
    }
}

/// <summary>
/// 任务行格式（name|url|referer|ua|cookie|origin|x-cdn-auth）的解析与还原。
/// 剪贴板粘贴、拖放 .txt、手动输入、复制回剪贴板四条路径共用这一份实现 ——
/// 之前粘贴与导入各自写了一套，字段截断/补空/校验顺序各不相同，同一条数据走不同入口
/// 会得到不同结果（一个报「URL 无效」，另一个静默丢掉）。
/// </summary>
internal static class TaskLines
{
    /// <summary>严格字段顺序。下标 0/1 是名称与地址，2 起映射成请求头。</summary>
    public static readonly string[] FieldOrder = ["name", "url", "referer", "ua", "cookie", "origin", "x-cdn-auth"];

    static readonly Dictionary<string, string> HeaderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["referer"] = "Referer",
        ["ua"] = "User-Agent",
        ["cookie"] = "Cookie",
        ["origin"] = "Origin",
        ["x-cdn-auth"] = "x-cdn-auth",
    };

    internal sealed record Parsed(string Name, string Url, List<string> Headers);

    /// <summary>
    /// 解析一行任务文本。返回 false 时：<paramref name="reason"/> 为空表示「空行 / 注释」这种正常跳过，
    /// 非空则是需要提示用户的坏行原因。
    /// </summary>
    public static bool TryParse(string raw, int lineNo, out Parsed parsed, out string reason)
    {
        parsed = new Parsed("", "", []);
        reason = "";

        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) return false;
        if (!line.Contains('|')) { reason = $"第{lineNo}行：缺少 | 分隔符"; return false; }

        var parts = line.Split('|');
        var name = parts[0].Trim();
        var url = parts[1].Trim();
        if (url.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"第{lineNo}行：URL 无效";
            return false;
        }
        if (name.Length == 0) name = DeriveName(url);
        if (name.Length == 0) name = $"task_{lineNo}";

        var headers = new List<string>();
        for (var k = 2; k < parts.Length && k < FieldOrder.Length; k++)
        {
            var val = parts[k].Trim();
            if (val.Length == 0) continue;
            if (!HeaderLabels.TryGetValue(FieldOrder[k], out var label)) continue;
            headers.Add($"{label}: {val}");
        }

        parsed = new Parsed(name, url, headers);
        return true;
    }

    /// <summary>把任务还原成一行文本（复制回剪贴板用），与 <see cref="TryParse"/> 互逆。</summary>
    public static string ToLine(DownloadTask t)
    {
        var hmap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in t.Headers)
        {
            var i = h.IndexOf(':');
            if (i > 0) hmap[h[..i].Trim()] = h[(i + 1)..].Trim();
        }
        return string.Join('|', [t.Name, t.Url,
            hmap.GetValueOrDefault("Referer", ""), hmap.GetValueOrDefault("User-Agent", ""),
            hmap.GetValueOrDefault("Cookie", ""), hmap.GetValueOrDefault("Origin", ""),
            hmap.GetValueOrDefault("x-cdn-auth", "")]);
    }

    /// <summary>名称留空时用地址末段兜底：去掉查询串与末尾斜杠。</summary>
    static string DeriveName(string url) => url.TrimEnd('/').Split('/')[^1].Split('?')[0];
}

/// <summary>
/// PATH 探测：进程 PATH → 注册表 PATH，并自动补 .exe。
/// <para>
/// 为什么单独成类：MainForm 与 Engine 原先各写了一份 —— MainForm 走注册表兜底，
/// Engine 只读进程 PATH。于是会出现「状态栏 yt-dlp 亮点着、点开始下载却说未找到」这种自相矛盾，
/// 根因就是同一件事有两份实现。现在两边都走这里。
/// </para>
/// <para>
/// 为什么必须查注册表：进程 PATH 是"登录时"从注册表拷贝的快照。用户新装了工具、
/// 往 PATH 里加了目录后，已经开着的 Explorer 仍然是旧环境 —— 从桌面快捷方式启动的程序
/// 继承的也是这份旧 PATH，于是"明明装了、明明在 PATH 里"却探测不到。
/// </para>
/// </summary>
internal static class PathProbe
{
    /// <summary>进程 PATH + 注册表 PATH 都查一遍。</summary>
    public static string? Find(string name) => FindOnProcessPath(name) ?? FindInRegistryPath(name);

    public static bool Exists(string name) => Find(name) is not null;

    public static string? FindOnProcessPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        return Search(Environment.ExpandEnvironmentVariables(path)
                               .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries), name);
    }

    /// <summary>从注册表的机器级 / 用户级 PATH 里找。</summary>
    public static string? FindInRegistryPath(string name)
    {
        foreach (var (hive, sub) in new (Microsoft.Win32.RegistryKey, string)[]
                 {
                     (Microsoft.Win32.Registry.LocalMachine,
                      @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                     (Microsoft.Win32.Registry.CurrentUser, "Environment"),
                 })
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key?.GetValue("Path") is not string raw || raw.Length == 0) continue;
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                var hit = Search(expanded.Split(';', StringSplitOptions.RemoveEmptyEntries), name);
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 在目录里找文件。Windows 上 File.Exists 不做 PATHEXT 解析，
    /// 没带扩展名的名字（例如 "ffmpeg"）必须自己补 .exe，否则恒为 false。
    /// </summary>
    static string? Search(string[] dirs, string name)
    {
        // 原样优先，再补 .exe：避免误判同名无扩展名文件
        var names = Path.HasExtension(name) ? new[] { name } : new[] { name, name + ".exe" };
        foreach (var dir in dirs)
        {
            var t = dir.Trim().Trim('"');
            if (t.Length == 0) continue;
            foreach (var n in names)
            {
                try
                {
                    var full = Path.Combine(t, n);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
        }
        return null;
    }
}

/// <summary>保存目录里的成品 / 中间产物识别。</summary>
internal static class Outputs
{
    /// <summary>
    /// 找出该任务**已经下载完成**的成品路径，找不到返回 null。
    /// 不看 mtime，只看是否已落盘且不是中间产物，也不是 yt-dlp 的单流文件。
    /// </summary>
    public static string? FindFinished(string workDir, string name, IReadOnlyList<string> siblingNames)
    {
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(name) || !Directory.Exists(workDir))
            return null;

        var validExts = Naming.VideoExts.Concat(Naming.AudioExts).ToArray();
        // 只关心比自己长的名字：更短的本就是自己的前缀匹配
        var longer = siblingNames.Where(n => n.Length > name.Length).ToArray();

        string[] entries;
        try { entries = Directory.GetFiles(workDir); }
        catch { return null; }

        var hits = new List<string>();
        foreach (var full in entries)
        {
            var f = Path.GetFileName(full);
            var low = f.ToLowerInvariant();
            if (!validExts.Any(e => low.EndsWith(e, StringComparison.Ordinal))) continue;
            if (Naming.IsTempOutput(f)) continue;
            if (!Naming.Matches(f, name)) continue;
            if (Naming.IsPartialStream(f, name)) continue;
            // 别抢兄弟任务的文件（队列里有「S01E01」和「S01E01 4K」时）
            if (longer.Any(s => Naming.Matches(f, s))) continue;
            try { if (new FileInfo(full).Length <= 0) continue; }
            catch { continue; }
            hits.Add(full);
        }
        if (hits.Count == 0) return null;

        // 优先「去掉扩展名后正好等于任务名」，其次视频，最后多余字符最少
        return hits.OrderBy(p => Rank(p, name)).First();
    }

    static (int Exact, int Video, int Extra) Rank(string path, string name)
    {
        var file = Path.GetFileName(path);
        var baseName = Path.GetFileNameWithoutExtension(file);
        var exact = string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        var video = Naming.VideoExts.Any(e => file.ToLowerInvariant().EndsWith(e, StringComparison.Ordinal)) ? 0 : 1;
        return (exact, video, baseName.Length - name.Length);
    }

    /// <summary>任务成品是否已落盘（可选按起始时间过滤，避免认成上次残留）。</summary>
    public static bool Exists(string workDir, string saveName, DateTime? startedAt = null)
    {
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(saveName) || !Directory.Exists(workDir))
            return false;
        var validExts = Naming.VideoExts.Concat(Naming.AudioExts).ToArray();
        try
        {
            foreach (var full in Directory.GetFiles(workDir))
            {
                var f = Path.GetFileName(full);
                if (!Naming.Matches(f, saveName)) continue;
                if (!validExts.Any(e => f.ToLowerInvariant().EndsWith(e, StringComparison.Ordinal))) continue;
                // 分离的音视频流不是成品，合流失败时不能算成功
                if (Naming.IsPartialStream(f, saveName)) continue;
                var fi = new FileInfo(full);
                if (!fi.Exists || fi.Length <= 0) continue;
                // 只认本次任务开始之后写入的文件
                if (startedAt is not null && fi.LastWriteTime < startedAt.Value.AddSeconds(-2)) continue;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>列出 yt-dlp 的单流残留（有流文件、却没有合流成品 = 合流失败）。</summary>
    public static List<string> FindPartialStreams(string workDir, string name)
    {
        var outp = new List<string>();
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(name) || !Directory.Exists(workDir))
            return outp;
        try
        {
            foreach (var full in Directory.GetFiles(workDir))
            {
                var f = Path.GetFileName(full);
                if (!Naming.IsPartialStream(f, name)) continue;
                try { if (new FileInfo(full).Length > 0) outp.Add(f); }
                catch { }
            }
        }
        catch { }
        return outp;
    }

    /// <summary>成品已存在时，清理该任务遗留的 .part / 分片临时文件。</summary>
    public static int CleanupStaleParts(string workDir, string name)
    {
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(name) || !Directory.Exists(workDir))
            return 0;
        var removed = 0;
        var now = DateTime.Now;
        try
        {
            foreach (var full in Directory.GetFiles(workDir))
            {
                var f = Path.GetFileName(full);
                if (!Naming.IsTempOutput(f) || !Naming.Matches(f, name)) continue;
                var fi = new FileInfo(full);
                // 只清理 10 秒以上的旧残留，避免碰到其它仍在下任务的临时文件
                if (!fi.Exists || (now - fi.LastWriteTime).TotalSeconds < 10) continue;
                try { fi.Delete(); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>
    /// 重试前清理该任务自己的临时文件（.part / .ytdl / 分片缓存等），不留年龄限制。
    /// 重拉播放列表后分片地址（时效签名）会变，沿用上一次的残留会让 yt-dlp 接着用旧签名下载。
    /// 只删「任务名 + . 开头」的文件，避免误伤兄弟任务（如「任务A 4K」）。
    /// </summary>
    public static int CleanupTaskTemps(string workDir, string name)
    {
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(name) || !Directory.Exists(workDir))
            return 0;
        var removed = 0;
        try
        {
            foreach (var full in Directory.GetFiles(workDir))
            {
                var f = Path.GetFileName(full);
                if (!Naming.IsTempOutput(f)) continue;
                if (!f.StartsWith(name, StringComparison.Ordinal)) continue;
                // 只认「任务名.后缀」这种自己的文件；「任务A 4K.xxx」「任务A-4K.xxx」属于兄弟任务
                var rest = f[name.Length..];
                if (rest.Length == 0 || rest[0] != '.') continue;
                try { File.Delete(full); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }

    /// <summary>统计该任务名下所有媒体文件的总大小与数量。</summary>
    public static (long Size, int Count) OwnFilesSize(string workDir, string name)
    {
        if (string.IsNullOrEmpty(workDir) || string.IsNullOrEmpty(name) || !Directory.Exists(workDir))
            return (0, 0);
        (long Size, int Count) last = (0, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            long total = 0;
            var count = 0;
            try
            {
                foreach (var full in Directory.GetFiles(workDir))
                {
                    var f = Path.GetFileName(full);
                    if (!Naming.Matches(f, name)) continue;
                    if (!Naming.MediaExts.Any(e => f.ToLowerInvariant().EndsWith(e, StringComparison.Ordinal))) continue;
                    try { total += new FileInfo(full).Length; count++; } catch { }
                }
            }
            catch { }
            last = (total, count);
            if (total > 0) return last;
            Thread.Sleep(200);
        }
        return last;
    }

    /// <summary>
    /// 清掉下载过程在保存目录里留下的空临时目录（<see cref="Engine.TempDirName"/>）。
    /// RE 的 --del-after-done 只删目录里的文件，目录本身会留下；每个任务留一个空目录，
    /// 跑一段时间保存目录里就全是空壳。目录里还有别的任务在写时保持不动。
    /// </summary>
    public static void CleanupTmpDir(string workDir)
    {
        if (string.IsNullOrEmpty(workDir)) return;
        try
        {
            var dir = Engine.TempDirOf(workDir);
            if (!Directory.Exists(dir)) return;
            if (Directory.EnumerateFileSystemEntries(dir).Any()) return;   // 还有任务在用
            Directory.Delete(dir, false);
        }
        catch { }
    }

    /// <summary>在保存目录里找到与任务名匹配的文件（用于「定位文件位置」）。</summary>
    public static string? FindTaskFile(string workDir, string name)
    {
        if (string.IsNullOrEmpty(name) || !Directory.Exists(workDir)) return null;
        string[] entries;
        try { entries = Directory.GetFiles(workDir); } catch { return null; }

        var prefixHits = entries.Where(full =>
        {
            var f = Path.GetFileName(full);
            return Naming.MediaExts.Any(e => f.ToLowerInvariant().EndsWith(e, StringComparison.Ordinal))
                && Naming.Matches(f, name);
        }).ToList();
        if (prefixHits.Count > 0)
        {
            var video = prefixHits.FirstOrDefault(full =>
                Naming.VideoExts.Any(e => full.ToLowerInvariant().EndsWith(e, StringComparison.Ordinal)));
            return video ?? prefixHits[0];
        }
        var loose = entries.FirstOrDefault(full => Path.GetFileName(full).Contains(name, StringComparison.OrdinalIgnoreCase));
        return loose;
    }
}
