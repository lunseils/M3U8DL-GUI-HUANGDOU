using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace M3U8DL;

/// <summary>
/// 下载执行层：N_m3u8DL-RE 与 yt-dlp 两条路径，
/// 负责起进程、解析输出、折算进度、判断成品是否落盘。
/// </summary>
internal sealed class Engine
{
    const int TermWaitMs = 2000;
    const int KillWaitMs = 1000;
    const double ProgressMinDelta = 0.5;
    const double ProgressMinInterval = 0.3;

    /// <summary>签名过期 / CBC 解密失败后，重新拉取播放列表重试 yt-dlp 的次数。</summary>
    const int PlaylistRefreshRetries = 2;

    static readonly Encoding Gbk;

    static Engine()
    {
        // 启用 GBK 等传统代码页：N_m3u8DL-RE 的中文日志必须按 GBK 解码。
        // 放在这里而不是 Program 里，单独引用本类的其它程序也能正常工作。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    static readonly Regex ReProgress = new(@"PROGRESS\|([\d.]+)%?\|(\d+|NA|None)\|(\d+|NA|None)", RegexOptions.Compiled);
    static readonly Regex ReFormats = new(@"Downloading (\d+) format", RegexOptions.Compiled);
    static readonly Regex ReRatio = new(@"(\d+)\s*/\s*(\d+)\s+(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    static readonly Regex RePctOnly = new(@"(\d+(?:\.\d+)?)\s*%", RegexOptions.Compiled);
    static readonly Regex ReFlag = new(@"--[a-zA-Z][\w-]*", RegexOptions.Compiled);

    // ============================================================
    // HTTP 状态码识别
    // ============================================================
    // 绝不能用裸子串 Contains("403") 判断 403：用户的播放地址里经常出现 403 这类数字
    // （例如 .../76c2f740358444279b5cf210ca446b31.m3u8），会把 404 误判成 403，
    // 结果是白跑一遍 N_m3u8DL-RE，还把用户引向"补 Cookie"这个错误方向。
    static readonly Regex ReHttpStatus = new(
        @"(?:http\s*error|httperror|http\s*status|status\s*code)\s*[:=]?\s*(\d{3})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "403 Forbidden" 这种没有 HTTP 前缀的写法；前置断言避免命中 URL / 路径里的裸数字
    static readonly Regex ReBareStatus = new(
        @"(?<![\w./\-])(\d{3})\s+(forbidden|unauthorized|bad\s+request|not\s+found|gone)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    readonly Action<string> _log;
    readonly Action<int, double> _progress;
    readonly Func<bool> _isRunning;
    /// <summary>
    /// 正在运行的下载进程 → 所属任务 id（探测进程不入表）。
    /// 一张表同时承担两件事：TerminateAll 遍历全部进程，TerminateTask 按任务 id 精确杀。
    /// 早先拆成「进程集合 + 进程→任务」两张表，每次增删都要两处同步，漏一处就是孤儿进程或泄漏。
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<Process, int> _procs = new();
    readonly object _cmdLock = new();

    HashSet<string>? _supportedFlags;
    string? _flagsExe;
    string[]? _ytDlpCmd;
    /// <summary>上次 yt-dlp 探测的时刻（TickCount64）。用于失效「找不到」的负缓存。</summary>
    long _ytDlpProbedAt;

    /// <summary>「找不到 yt-dlp」的负缓存有效期：用户常常刚 pip install 完就点开始下载。</summary>
    const int YtDlpNegativeCacheMs = 30_000;

    /// <summary>RE 的临时目录名（落在保存目录内，理由见 <see cref="BuildReCmd"/>）。</summary>
    public const string TempDirName = ".nm3u8dl-tmp";

    /// <summary>某个保存目录对应的 RE 临时目录。</summary>
    public static string TempDirOf(string workDir) => Path.Combine(workDir, TempDirName);

    /// <param name="status">
    /// 兼容保留：状态回写现在由 MainForm 自己按任务 id 反查行号完成（行号会随删除/排序漂移，
    /// 引擎只持有 id，没资格直接写表格），因此这里不再使用。
    /// </param>
    public Engine(Action<string> log, Action<int, string> status, Action<int, double> progress, Func<bool> isRunning)
    {
        _log = log;
        _ = status;
        _progress = progress;
        _isRunning = isRunning;
    }

    public void TerminateAll()
    {
        // BUG-10：有界重试，避免 Kill 抛异常/进程树尚未退出时留下孤儿进程；最多 3 次 × 50ms。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var anyAlive = false;
            foreach (var p in _procs.Keys)
            {
                try
                {
                    if (!p.HasExited) { p.Kill(entireProcessTree: true); anyAlive = true; }
                }
                catch { }
            }
            if (!anyAlive) break;
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// 只中止指定任务（任务 id）的下载进程。「下载中删除某一个任务」时用：
    /// 不能整批停掉，用户还要继续下其它任务。返回是否真的杀掉了进程。
    /// </summary>
    public bool TerminateTask(int taskId)
    {
        var killed = false;
        foreach (var kv in _procs)
        {
            if (kv.Value != taskId) continue;
            try
            {
                if (!kv.Key.HasExited) { kv.Key.Kill(entireProcessTree: true); killed = true; }
            }
            catch { }
        }
        return killed;
    }

    // ============================================================
    // 入口
    // ============================================================
    public async Task<bool> RunAsync(string url, string saveName, Opt o,
                                    IReadOnlyList<string> headers, int taskId, DateTime startedAt)
    {
        if (o.UseYtDlp && !o.OnlyParseM3u8)
            return await YtDlpAsync(url, saveName, o, headers, taskId, startedAt);
        return await ReAsync(url, saveName, o, headers, taskId, startedAt);
    }

    // ============================================================
    // yt-dlp
    // ============================================================
    string[]? FindYtDlp(bool quiet = false)
    {
        lock (_cmdLock)
        {
            if (_ytDlpCmd is not null)
            {
                if (_ytDlpCmd.Length > 0) return _ytDlpCmd;
                // 负缓存到期后重新探测：否则整个进程生命周期内都认为 yt-dlp 不存在，
                // 用户装好后必须重启程序才能用。
                if (Environment.TickCount64 - _ytDlpProbedAt < YtDlpNegativeCacheMs) return null;
            }

            var candidates = new List<string[]>();
            // PathProbe 自带 .exe 补全，不必再分别试 "yt-dlp.exe" / "yt-dlp"
            var onPath = FindOnPath("yt-dlp");
            if (onPath is not null) candidates.Add([onPath]);

            var appdata = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appdata))
            {
                var pyRoot = Path.Combine(appdata, "Python");
                if (Directory.Exists(pyRoot))
                    foreach (var d in Directory.GetDirectories(pyRoot))
                    {
                        var cand = Path.Combine(d, "Scripts", "yt-dlp.exe");
                        if (File.Exists(cand)) candidates.Add([cand]);
                    }
            }

            var py = FindOnPath("py.exe");
            if (py is not null) candidates.Add([py, "-m", "yt_dlp"]);
            var python = FindOnPath("python.exe");
            if (python is not null) candidates.Add([python, "-m", "yt_dlp"]);

            _ytDlpProbedAt = Environment.TickCount64;
            foreach (var c in candidates)
            {
                if (ProbeOk(c, "--version", quiet))
                {
                    _ytDlpCmd = c;
                    if (!quiet) _log($"✅ 使用 yt-dlp：{string.Join(' ', c)}");
                    return c;
                }
            }
            _ytDlpCmd = [];
            if (!quiet) _log("⚠ 未找到 yt-dlp。请执行 pip install \"yt-dlp[curl-cffi]\"。");
            return null;
        }
    }

    /// <summary>yt-dlp 是否可用（启动队列前检查一次）。</summary>
    public bool YtDlpAvailable() => FindYtDlp() is not null;

    /// <summary>
    /// BUG-9：构造期后台预热，静默填充 _cmdLock 缓存，避免首次点击「开始下载」时同步探测最坏 64 秒卡 UI。
    /// quiet=true 不写日志，避免污染启动日志/测试断言。结果只影响缓存，StartBatch 再读缓存即可。
    /// </summary>
    public void PrewarmYtDlp() => FindYtDlp(quiet: true);

    bool ProbeOk(string[] cmd, string arg, bool quiet = false)
    {
        try
        {
            return RunProbe([.. cmd, arg], 15000, out _, out var exitCode) && exitCode == 0;
        }
        catch (Exception e)
        {
            if (!quiet) _log($"⚠ 探测 {string.Join(' ', cmd)} 失败：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 跑一个短命探测命令（--version / --help）并收集输出。
    /// 超时必须 Kill：探测进程虽不入追踪表，泄漏出去同样会长期占着管道与句柄，
    /// 表现就是"点开始下载后卡住"——WaitForExit 超时返回 false 但进程还在跑，
    /// 随后的 ReadToEnd 会一直等到它自己退出。
    /// </summary>
    bool RunProbe(IReadOnlyList<string> cmd, int timeoutMs, out string output, out int exitCode)
    {
        output = "";
        exitCode = -1;
        Process? p = null;
        try
        {
            p = StartProc(cmd, Gbk, track: false); // 探测进程不纳入追踪
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                p.WaitForExit(1000);
            }
            outTask.Wait(1000);
            errTask.Wait(1000);
            output = (outTask.IsCompletedSuccessfully ? outTask.Result : "") + "\n" +
                     (errTask.IsCompletedSuccessfully ? errTask.Result : "");
            exitCode = p.HasExited ? p.ExitCode : -1;
            return p.HasExited;
        }
        finally
        {
            if (p is not null) { _procs.TryRemove(p, out _); p.Dispose(); }
        }
    }

    List<string>? BuildYtDlpCmd(string url, string saveName, Opt o, IReadOnlyList<string> headers, bool impersonate)
    {
        var baseCmd = FindYtDlp();
        if (baseCmd is null) return null;

        // 任务名里的 % 在 yt-dlp 输出模板里是占位符，必须转义
        var safeName = saveName.Replace("%", "%%");
        var outTemplate = Path.Combine(o.WorkDir, $"{safeName}.%(ext)s");

        var cmd = new List<string>(baseCmd) { url };
        if (impersonate)
        {
            cmd.Add("--impersonate");
            cmd.Add("chrome");
            cmd.Add("--extractor-args");
            cmd.Add("generic:impersonate");
        }
        cmd.AddRange(["-o", outTemplate, "--merge-output-format", "mp4",
                      "--no-playlist", "--newline", "--progress"]);
        cmd.Add("--progress-template");
        cmd.Add("download:PROGRESS|%(progress._percent)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s");
        cmd.Add("--no-warnings");

        // 分片并发数是 yt-dlp 侧最大的速度杠杆：默认 1 意味着 HLS/DASH 逐个分片串行下载，
        // 跑满带宽基本不可能。之前它被绑在「并发下载音视频」开关上，开关一关就退化成 1 ——
        // 但那个开关对 RE 的含义是"音视频多轨并行"（-mt），与分片并发是两件事，
        // 复用同一个开关会让「只想单轨下载」的用户连带丢掉分片并行。现在解耦，始终按线程数并发。
        cmd.Add("--concurrent-fragments");
        cmd.Add(Math.Max(1, o.MaxThreads).ToString());
        cmd.Add("--retries");
        cmd.Add(o.RetryCount.ToString());
        cmd.Add("--fragment-retries");
        cmd.Add(o.RetryCount.ToString());
        cmd.Add("--socket-timeout");
        cmd.Add(o.Timeout.ToString());
        // 直链（非 HLS/DASH 分片）默认是单连接下载，限速型 CDN 上单连接往往只有几百 KB/s。
        // 开分块后 yt-dlp 用 Range 请求多连接拉同一文件；服务器不支持 Range 时它会自动退回
        // 单连接，不会因此失败，所以可以无条件开。
        cmd.Add("--http-chunk-size");
        cmd.Add("10M");
        if (o.LimitSpeed > 0)
        {
            cmd.Add("--limit-rate");
            cmd.Add($"{o.LimitSpeed}K");
        }

        // Cookie 来源：yt-dlp 官方推荐用 cookiejar（文件 / 浏览器）而不是请求头，
        // 这样 Cookie 带上域名作用域，HLS 的 key 与分片请求也能带上。
        // 注意：请求头里的 Cookie 只作用于播放列表域名，CDN 分片拿不到 —— 这是 yt-dlp 的既有行为，
        // 但它并不是「AES-128-CBC 解密失败」的成因：那类失败的成因是播放地址的时效签名过期。
        var useCookieBrowser = !string.IsNullOrWhiteSpace(o.CookieBrowser);
        var useCookieFile = !string.IsNullOrWhiteSpace(o.CookieFile);
        var hasCookieSource = useCookieBrowser || useCookieFile;
        if (useCookieBrowser)
        {
            cmd.Add("--cookies-from-browser");
            cmd.Add(o.CookieBrowser.Trim());
        }
        if (useCookieFile)
        {
            cmd.Add("--cookies");
            cmd.Add(o.CookieFile.Trim());
        }

        foreach (var h in headers)
            if (h.Contains(':'))
            {
                // 已经用带作用域的 Cookie 来源了，就不要再以 header 形式传 Cookie：
                // 否则 yt-dlp 会警告，且可能与 cookiejar 里的 Cookie 组成重复的 Cookie 头。
                if (hasCookieSource && IsCookieHeader(h)) continue;
                cmd.Add("--add-header");
                cmd.Add(h);
            }
        if (o.NoSystemProxy) cmd.AddRange(["--proxy", ""]);
        if (o.NoMerge) cmd.Add("--keep-video");
        cmd.Add("--rm-cache-dir");
        return cmd;
    }

    /// <summary>失败原因看起来是 impersonate / 参数不被支持时，值得降级重试。</summary>
    static bool ImpersonateRetryWorthy(string text)
    {
        var low = (text ?? "").ToLowerInvariant();
        return new[] { "impersonate", "curl_cffi", "curl-cffi", "unrecognized arguments", "no such option", "not a valid" }
               .Any(low.Contains);
    }

    /// <summary>请求头名是否为 Cookie（大小写不敏感）。</summary>
    static bool IsCookieHeader(string header)
    {
        var i = header.IndexOf(':');
        if (i <= 0) return false;
        return header[..i].Trim().Equals("cookie", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从 yt-dlp 的错误文本里取出真正的 HTTP 状态码；文本里没有则返回 null。
    /// </summary>
    /// <remarks>
    /// 只认 "HTTP Error 403" / "HTTPError 403" / "403 Forbidden" 这类带上下文的写法，
    /// 不认 URL、路径、任务名里的裸数字。
    /// 403 / 401 优先返回：它们才是 Cookie / 权限类的硬证据。
    /// </remarks>
    public static int? FindHttpStatus(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return null;

        var codes = new List<int>();
        foreach (Match m in ReHttpStatus.Matches(errorText))
            if (int.TryParse(m.Groups[1].Value, out var c)) codes.Add(c);
        foreach (Match m in ReBareStatus.Matches(errorText))
            if (int.TryParse(m.Groups[1].Value, out var c)) codes.Add(c);

        if (codes.Count == 0) return null;
        foreach (var c in codes)
            if (c is 401 or 403) return c;
        return codes[0];
    }

    /// <summary>
    /// 是否是 AES-128-CBC 解密失败。
    /// </summary>
    /// <remarks>
    /// 必须匹配"解密"语境，不能用裸子串 Contains("cbc")：
    /// URL / 路径 / 任务名里经常出现 cbc（例如 abcbc12345），会被误判成解密失败。
    /// </remarks>
    static bool IsCbcDecryptFailure(string low)
    {
        // 最强特征：解密时发现数据长度不是 16 的倍数（服务端返回的是错误页）
        if (low.Contains("padded to 16 byte boundary")) return true;
        if (!low.Contains("cbc")) return false;
        return low.Contains("decrypt") || low.Contains("解密")
            || low.Contains("cbc mode") || low.Contains("aes-128-cbc") || low.Contains("aes128")
            || low.Contains("cipher");
    }

    /// <summary>
    /// 判断 yt-dlp 的失败是否属于「Cookie / 解密 / 403」类。
    /// 这类错误用 N_m3u8DL-RE 再试一次有意义：RE 自己实现 AES-128-CBC 解密，且请求头对所有域名生效。
    /// </summary>
    /// <remarks>
    /// 判定依据是"失败本身"（CBC 解密失败 / 真正的 HTTP 401·403），
    /// 不是"cookie 作用域提示"本身 —— 那条提示每次用请求头 Cookie 都会打印，
    /// 成功下载时照样打印，拿它当失败依据会让无关失败也被降级。
    /// 错误文本里若带有明确的其它状态码（如 404），一律不降级：
    /// 那是地址失效 / 资源不存在，RE 再跑一遍同样会失败。
    /// </remarks>
    /// <param name="errorText">yt-dlp 的错误行（ERROR / WARNING）。</param>
    /// <param name="cookieScopeWarn">
    /// yt-dlp 是否提示过「header Cookie 会被限定在下载域名」。该提示在每次用 header Cookie 时都会出现，
    /// 单独拿它当失败依据会对无关失败也降级，所以必须与真正的失败行一起判断。
    /// </param>
    public static bool IsCookieOrDecryptFailure(string? errorText, bool cookieScopeWarn = false)
    {
        var low = (errorText ?? "").ToLowerInvariant();
        if (low.Length == 0) return false;

        // 硬证据 1：AES-128-CBC 解密失败
        if (IsCbcDecryptFailure(low)) return true;

        // 硬证据 2：真正的 HTTP 状态码
        var status = FindHttpStatus(low);
        if (status is 401 or 403) return true;
        // 明确的其它状态码（404 / 500 …）与 Cookie / 解密无关，不降级
        if (status is not null) return false;

        // 没有状态码、但明说 Forbidden / Unauthorized
        if (low.Contains("forbidden") || low.Contains("unauthorized")) return true;

        // 软证据：yt-dlp 已提示 header Cookie 的作用域问题，且确实是页面拉取失败
        if (cookieScopeWarn &&
            (low.Contains("unable to download webpage")
             || low.Contains("failed to parse")))
            return true;

        return false;
    }

    /// <summary>
    /// 判断失败是否属于「播放地址时效签名过期」类：重新拉一次播放列表就能拿到新签名。
    /// </summary>
    /// <remarks>
    /// 实测证据：这类 HLS 的分片地址带 auth_key 时效签名，签名过期后分片返回
    /// "403 Forbidden: expired"（纯文本错误页，长度不是 16 的倍数），
    /// yt-dlp 对它做 AES-128-CBC 解密就会抛 "Data must be padded to 16 byte boundary in CBC mode"，
    /// 且不会重试该分片 —— 一个坏分片就让整个任务失败。
    /// </remarks>
    public static bool IsPlaylistSignatureExpired(string? errorText)
    {
        var low = (errorText ?? "").ToLowerInvariant();
        if (low.Length == 0) return false;

        // 硬证据：解密时报「不是 16 字节对齐」—— 说明服务端返回的是错误页，而不是加密分片本体
        if (IsCbcDecryptFailure(low)) return true;

        // 硬证据：403 + 过期（服务端明说票据已过期）
        var status = FindHttpStatus(low);
        if (low.Contains("expired") &&
            (status is 401 or 403 || (status is null && low.Contains("forbidden"))))
            return true;

        // 硬证据：明说签名 / auth_key 过期
        if (low.Contains("expired") &&
            (low.Contains("signature") || low.Contains("auth_key") || low.Contains("签名")))
            return true;

        // 其余的 "expired"（如证书过期）与播放地址签名无关，不应触发重拉播放列表
        return false;
    }

    /// <summary>
    /// 把 yt-dlp 的英文报错翻译成可执行的中文排查建议（只输出建议，不改写原有的失败结论行）。
    /// </summary>
    public static List<string> DiagnoseYtDlpError(string? errorText)
    {
        var tips = new List<string>();
        var low = (errorText ?? "").ToLowerInvariant();
        if (low.Length == 0) return tips;

        var status = FindHttpStatus(low);
        var isCbc = IsCbcDecryptFailure(low);
        var isCookieHint = low.Contains("consider loading cookies") || low.Contains("cookies as a header");
        // 只有真正的 HTTP 401 / 403（或没有状态码但明说 Forbidden）才算 403；
        // URL 里恰好含 "403" 的 404 不能被说成 403
        var is403 = status is 401 or 403 ||
                    (status is null && (low.Contains("forbidden") || low.Contains("unauthorized")));
        var is404 = status == 404;
        var isParse = low.Contains("unable to download webpage") || low.Contains("failed to parse");
        var isCookieLoad = low.Contains("failed to load cookies") || low.Contains("cookiefile")
            || low.Contains("unable to open cookie") || low.Contains("cookie file");
        // 签名过期：分片地址里的 auth_key 之类的时效签名到期后，分片返回 403 错误页。
        // 必须同时出现"过期"与 403/签名 之一，
        // 否则像"证书已过期"这种无关错误会被误判成播放地址签名过期。
        var isExpired = low.Contains("expired") &&
                        (status is 401 or 403 || low.Contains("signature") || low.Contains("auth_key")
                         || (status is null && low.Contains("forbidden")));

        if (isCbc)
        {
            tips.Add("结论：服务端返回的不是加密分片本体，而是错误页（典型是 403 Forbidden: expired），因此 AES-128-CBC 解密失败。");
            tips.Add("根因：这类 HLS 的分片地址带时效签名（auth_key），签名过期后分片返回 403 错误页；yt-dlp 遇到解密失败不会重试分片，一个坏分片就会让整个任务失败。");
            tips.Add("建议 1：重新获取播放地址再重试（每次请求播放地址都会签发新的签名）——本程序已自动重新获取播放列表重试，仍失败请重新复制播放地址。");
            tips.Add("建议 2：使用本程序新增的「Cookie 文件 / 浏览器 Cookie」（yt-dlp 官方推荐用法，与本次故障无直接关系，但能覆盖需要登录态的站点）。");
            tips.Add("建议 3：改用 N_m3u8DL-RE 引擎（本程序会自动降级重试一次）。");
        }
        if (isCookieHint)
        {
            tips.Add("注意：yt-dlp 打印的「Please consider loading cookies from a file or browser instead.」只是它对「用请求头传 Cookie」这种用法的通用弃用警告，每次运行都会打印，与本次失败未必相关，不要据此判断原因。");
            tips.Add("如需消除该警告并让 Cookie 带上正确的域名作用域，请在「基础选项」使用「Cookie 文件（cookies.txt）」或「浏览器 Cookie」。");
        }
        if (isCookieLoad)
        {
            tips.Add("Cookie 读取失败：请确认「Cookie 文件（cookies.txt）」路径存在且为 Netscape 格式；用浏览器 Cookie 时请先完全关闭对应浏览器（浏览器占用会导致数据库读取失败）。");
        }
        // 已经是 CBC 解密失败时，上面已给出准确结论（签名过期），
        // 这里不能再补"缺少登录态 Cookie" —— 那是自相矛盾的错误归因
        if (is403 && !isCbc)
        {
            if (isExpired)
            {
                tips.Add("服务器返回 403 且提示签名过期：这是播放地址的时效签名（auth_key）到期，重新获取播放地址即可恢复。");
                tips.Add("建议：重新获取播放地址再重试（本程序会自动重新获取播放列表并重试）；若站点同时需要登录态，再配合「Cookie 文件 / 浏览器 Cookie」。");
            }
            else
            {
                tips.Add("服务器返回 403：通常是缺少登录态 Cookie，或 Referer / User-Agent 与浏览器不一致。");
                tips.Add("建议：改用「Cookie 文件」或「浏览器 Cookie」提供带作用域的 Cookie，同时核对任务里的 Referer / User-Agent。");
            }
        }
        if (is404)
        {
            tips.Add("服务器返回 404：播放地址不存在或已失效（与 Cookie / 解密无关），请核对并重新复制播放地址。");
        }
        else if (isParse && !isCbc)
        {
            tips.Add("无法下载或解析播放列表页面：多半被风控拦截，或缺少 Referer / User-Agent / Cookie。");
            tips.Add("建议：补齐请求头（Referer / User-Agent），Cookie 用「Cookie 文件」或「浏览器 Cookie」；仍失败可改用 N_m3u8DL-RE 引擎重试。");
        }
        if (tips.Count == 0)
            tips.Add("建议：查看上方 [yt-dlp] 原始日志定位原因；常见处理是补齐 Referer / User-Agent / Cookie，或改用 N_m3u8DL-RE 引擎重试。");
        return tips;
    }

    async Task<bool> YtDlpAsync(string url, string saveName, Opt o,
                               IReadOnlyList<string> headers, int taskId, DateTime startedAt)
    {
        var filtered = headers.Where(h => !string.IsNullOrWhiteSpace(h)).ToArray();
        var useCookieFile = !string.IsNullOrWhiteSpace(o.CookieFile);
        var useCookieBrowser = !string.IsNullOrWhiteSpace(o.CookieBrowser);
        var hasCookieSource = useCookieFile || useCookieBrowser;

        if (useCookieFile)
        {
            _log($"ℹ 使用 Cookie 文件：{o.CookieFile}（带域名作用域，CDN 分片请求也会带上）");
            // yt-dlp 对不存在的 cookie 文件是"静默忽略"（cookies.py: os.access 不通过就不 load），
            // 用户会以为 Cookie 已经生效，其实等于没带 —— 必须明确警告
            if (!File.Exists(o.CookieFile))
                _log($"⚠ Cookie 文件不存在：{o.CookieFile}（yt-dlp 会静默忽略它，等于没有带任何 Cookie）");
        }
        if (useCookieBrowser)
            _log($"ℹ 使用浏览器 Cookie：{o.CookieBrowser}（读取浏览器 Cookie 数据库前请先关闭对应浏览器）");
        if (hasCookieSource)
            _log("ℹ 已启用带作用域的 Cookie 来源：任务行里的 Cookie 请求头不会再传给 yt-dlp（避免重复 Cookie 头）");
        else if (filtered.Any(IsCookieHeader))
            _log("ℹ 使用任务附加请求头里的 Cookie（yt-dlp 只把它用于播放列表域名，CDN 分片请求不会带上；需要登录态的站点建议改用「Cookie 文件 / 浏览器 Cookie」）");
        else
            _log("⚠ 未提供 Cookie。若站点有 Cloudflare 保护，可能返回 403。");

        // 先带 impersonate（可绕过 TLS 指纹），当前环境不支持时自动降级重试一次。
        // 以前是写死 --impersonate chrome：只要 yt-dlp 没装 curl_cffi，所有任务全部直接失败。
        var lastErr = "";
        var cookieScopeWarn = false;
        var lastUseImp = true;
        foreach (var useImp in (bool[])[true, false])
        {
            var cmd = BuildYtDlpCmd(url, saveName, o, filtered, useImp);
            if (cmd is null)
            {
                _log("❌ 无法使用 yt-dlp：未找到可执行文件");
                return false;
            }
            _log("🚀 使用 yt-dlp 下载" + (useImp ? "（impersonate chrome）…" : "（已关闭 impersonate）…"));
            _log($"[yt-dlp] 命令: {Quote(cmd)}");

            var (ok, err, scopeWarn) = await YtDlpAttemptAsync(cmd, o, saveName, taskId, startedAt);
            if (ok) return true;
            lastErr = err;
            cookieScopeWarn |= scopeWarn;
            lastUseImp = useImp;
            if (!_isRunning()) return false;
            if (useImp && ImpersonateRetryWorthy(err))
            {
                _log("↩ 当前 yt-dlp / curl_cffi 不支持 impersonate，自动改用普通请求重试一次");
                continue;
            }
            break;
        }

        // 签名过期兜底：HLS 分片地址带时效签名（auth_key），过期后分片返回 403 错误页，
        // yt-dlp 对非 16 字节对齐的响应做 AES-128-CBC 解密就会抛
        // "Data must be padded to 16 byte boundary in CBC mode"，且不会重试分片 —— 一个坏分片即全盘失败。
        // 重新跑一次 yt-dlp 会重新请求播放列表 → 拿到新签名 → 往往就成功了。
        var refresh = 0;
        while (IsPlaylistSignatureExpired(lastErr) && refresh < PlaylistRefreshRetries && _isRunning())
        {
            refresh++;
            _log($"↻ 播放地址可能已过期，重新获取播放列表并重试（第 {refresh}/{PlaylistRefreshRetries} 次）");
            // 旧分片残留带着过期签名，必须清掉，否则重试会接着用旧地址下载
            var cleaned = Outputs.CleanupTaskTemps(o.WorkDir, saveName);
            if (cleaned > 0) _log($"   🧹 已清理 {cleaned} 个上次下载留下的临时文件");

            var cmd = BuildYtDlpCmd(url, saveName, o, filtered, lastUseImp);
            if (cmd is null) break;
            _log("🚀 重新获取播放列表后再次使用 yt-dlp 下载…");
            _log($"[yt-dlp] 命令: {Quote(cmd)}");

            var (ok, err, scopeWarn) = await YtDlpAttemptAsync(cmd, o, saveName, taskId, startedAt);
            if (ok) return true;
            cookieScopeWarn |= scopeWarn;
            if (!_isRunning()) return false;
            // BUG-11 短路：本轮错误与上一轮完全相同，说明重拉播放列表没有带来任何变化
            // （典型是纯 CBC 解密失败 —— 不是真的签名过期）。继续重试只是白跑 2 次。
            // 注意：不收窄 IsPlaylistSignatureExpired 的判定（Tests.cs:280-281 明确断言纯 CBC = 签名过期）。
            var sameAsLast = string.Equals(err, lastErr, StringComparison.Ordinal);
            lastErr = err;
            if (sameAsLast) break;
        }

        // 兜底：Cookie / 解密 / 403 类失败自动改用 N_m3u8DL-RE 再试一次（最多一次）。
        // RE 自己实现 AES-128-CBC 解密，且请求头对所有域名生效，对反爬更宽容。
        if (IsCookieOrDecryptFailure(lastErr, cookieScopeWarn) &&
            !string.IsNullOrWhiteSpace(o.Executable) && File.Exists(o.Executable))
        {
            _log("⚠ yt-dlp 失败 → 自动改用 N_m3u8DL-RE 重试（RE 自带 AES-128-CBC 解密，对反爬更宽容）");
            // RE 不认识 cookiejar，只能用任务行里的请求头；好在 RE 的请求头对所有域名生效（含 CDN）
            if (hasCookieSource && !filtered.Any(IsCookieHeader))
                _log("ℹ RE 不支持 Cookie 文件/浏览器 Cookie，本次重试只能依赖任务行里的请求头");
            if (await ReAsync(url, saveName, o, headers, taskId, startedAt)) return true;
            _log("❌ N_m3u8DL-RE 兜底重试同样失败，请按上面的诊断建议调整 Cookie 来源后再试");
        }
        return false;
    }

    async Task<(bool Ok, string Err, bool CookieScopeWarn)> YtDlpAttemptAsync(List<string> cmd, Opt o, string saveName,
                                                                           int taskId, DateTime startedAt)
    {
        var errLines = new List<string>();
        var allLines = new List<string>();
        var cookieScopeWarn = false;
        double lastPct = -1, shownPct = -1, lastEmit = 0;
        var totalStreams = 1;
        var streamIdx = 0;

        try
        {
            using var proc = StartProc(cmd, Gbk, taskId, workingDir: o.WorkDir);
            var exitCode = await ReadProcAsync(proc, line =>
            {
                var s = line.TrimEnd();
                if (s.Length == 0) return;

                var mf = ReFormats.Match(s);
                if (mf.Success && int.TryParse(mf.Groups[1].Value, out var nStreams))
                    totalStreams = Math.Max(totalStreams, nStreams);
                if (s.Contains("Destination:", StringComparison.Ordinal))
                {
                    streamIdx++;
                    if (streamIdx > totalStreams) totalStreams = streamIdx;
                    if (totalStreams > 1)
                        _log($"🎞 开始下载第 {streamIdx}/{totalStreams} 个流（音视频分离时属正常，不是从头重下）");
                }

                var m = ReProgress.Match(s);
                if (m.Success)
                {
                    if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                        return;
                    // 把每个流的 0~100% 折算成整集的整体进度，
                    // 否则第 1 个流跑满后第 2 个流又从 0% 开始，看起来像"下载完成又重新下载"
                    raw = Math.Clamp(raw, 0, 100);
                    var stage = Math.Clamp(streamIdx - 1, 0, totalStreams - 1);
                    var pct = totalStreams > 1
                        ? (stage + raw / 100.0) / totalStreams * 100.0
                        : raw;
                    pct = Math.Round(Math.Min(pct, 99.0), 1);
                    if (pct < shownPct) pct = shownPct;

                    var now = Environment.TickCount64 / 1000.0;
                    if (Math.Abs(pct - lastPct) >= ProgressMinDelta || now - lastEmit >= ProgressMinInterval)
                    {
                        lastPct = pct;
                        shownPct = pct;
                        lastEmit = now;
                        if (taskId >= 0) _progress(taskId, pct);
                    }
                    return;
                }
                if (s.Contains("PROGRESS|", StringComparison.Ordinal)) return;   // 解析不了的进度帧别刷屏

                _log($"[yt-dlp] {s}");
                allLines.Add(s);
                // 大小写不敏感：不同 yt-dlp 版本可能输出 "ERROR:" / "Error:"
                if (s.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || s.StartsWith("WARNING"))
                    errLines.Add(s);
                // yt-dlp 的 header Cookie 作用域提示（"Deprecated Feature: Passing cookies as a header..."）
                // 走 stderr 且不是 WARNING 行，单独记下来，不能混进错误行
                if (s.Contains("consider loading cookies", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains("cookies as a header", StringComparison.OrdinalIgnoreCase))
                    cookieScopeWarn = true;
            });

            var output = string.Join('\n', allLines);
            if (!_isRunning())
            {
                _log("⏹ 任务已停止");
                return (false, string.Join('\n', errLines), cookieScopeWarn);
            }
            if (Outputs.Exists(o.WorkDir, saveName, startedAt))
            {
                if (taskId >= 0) _progress(taskId, 100.0);
                _log("✅ yt-dlp 下载完成（文件已生成）");
                return (true, "", cookieScopeWarn);
            }

            var err = string.Join('\n', errLines);
            var partial = Outputs.FindPartialStreams(o.WorkDir, saveName);
            if (partial.Count > 0)
            {
                // 以前这种半成品会被当成"下载完成"，之后还会因为"成品已存在"永久跳过，
                // 用户拿到的是一堆废文件
                _log("❌ yt-dlp 未能合流成成品，只留下分离的音视频流：" + string.Join("、", partial.Take(3)));
                _log("💡 这是 FFmpeg 不可用的典型症状。请安装 FFmpeg 并确认 ffmpeg.exe 在 PATH 中，再重试该任务。");
            }
            else
            {
                _log($"❌ yt-dlp 下载失败：未检测到输出文件（退出码 {exitCode}）");
            }
            // 只有真正的 HTTP 401 / 403 才提示补 Cookie：
            // URL 里恰好含 "403" 的 404 会被这句话误导到错误方向
            var errStatus = FindHttpStatus(err);
            if (errStatus is 401 or 403)
                _log("💡 服务器返回 403。可尝试在任务行末尾追加 Cookie/Referer 等请求头。");
            // 中文诊断建议：追加在失败结论之后，不取代上面的结论行
            foreach (var tip in DiagnoseYtDlpError(output))
                _log("🔍 诊断：" + tip);
            return (false, err, cookieScopeWarn);
        }
        catch (Exception e)
        {
            _log($"⚠ yt-dlp 异常：{e.Message}");
            foreach (var tip in DiagnoseYtDlpError(e.Message))
                _log("🔍 诊断：" + tip);
            return (false, e.Message, false);
        }
    }

    // ============================================================
    // N_m3u8DL-RE
    // ============================================================
    HashSet<string> SupportedFlags(string exe)
    {
        lock (_cmdLock)
        {
            if (_supportedFlags is not null && _flagsExe == exe) return _supportedFlags;
            try
            {
                RunProbe([exe, "--help"], 20000, out var text, out _);
                _supportedFlags = [.. ReFlag.Matches(text).Select(m => m.Value).Distinct()];
                _flagsExe = exe;
                if (_supportedFlags.Count > 0) _log($"🔍 已探测 RE 支持 {_supportedFlags.Count} 个参数");
            }
            catch (Exception e)
            {
                _supportedFlags = [];
                _flagsExe = exe;
                _log($"⚠ 参数探测失败，将按兼容模式构建命令：{e.Message}");
            }
            return _supportedFlags;
        }
    }

    /// <summary>
    /// 构造 N_m3u8DL-RE 命令行。
    /// <para>
    /// <paramref name="supported"/> 为空集表示「没探测到参数表」，此时按兼容模式全部添加。
    /// 单独成方法是为了让测试能直接断言参数内容 —— ReAsync 内部要起进程、解析输出，
    /// 没法做纯函数式断言，参数一旦拼错只能靠真实下载才发现。
    /// </para>
    /// </summary>
    List<string> BuildReCmd(string url, string saveName, Opt o,
                            IReadOnlyList<string> headers, IReadOnlySet<string> supported)
    {
        var cmd = new List<string> { o.Executable, url };
        if (!string.IsNullOrEmpty(saveName)) cmd.AddRange(["--save-name", saveName]);
        if (!string.IsNullOrEmpty(o.WorkDir)) cmd.AddRange(["--save-dir", o.WorkDir]);

        var allHeaders = headers.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
        if (!allHeaders.Any(h => h.ToLowerInvariant().StartsWith("user-agent:")))
            allHeaders.Add("User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                           "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        foreach (var h in allHeaders) cmd.AddRange(["-H", h]);

        void AddFlag(string flag, params string[] values)
        {
            if (supported.Count > 0 && !supported.Contains(flag))
            {
                _log($"⚠ 当前 RE 版本不支持参数 {flag}，已跳过");
                return;
            }
            cmd.Add(flag);
            cmd.AddRange(values);
        }
        void AddBool(string flag, bool value) => AddFlag(flag, value ? "true" : "false");

        // ---------------- 无条件开启：不是"可选优化"，是正确性 + 加速 ----------------
        //
        // 1) --auto-select：片源有多个轨道时（720p/1080p 并存是常态），RE 会弹
        //    Spectre.Console 的多选交互。GUI 进程没有控制台，读键会直接抛
        //    "Cannot see if a key has been pressed when either application does not have a console"，
        //    整个任务当场崩溃 —— 实测必现。带上它才会自动选最高码率轨道并跳过等待输入。
        AddBool("--auto-select", true);
        // 2) 每次启动都向 GitHub 查版本；网络被墙时每个任务都要白等一轮超时，批量跑时按任务数累加。
        AddBool("--disable-update-check", true);
        // 3) ANSI 色码会插进进度行，ReRatio / RePctOnly 的数字匹配被转义序列打断；
        //    日志里也全是乱码转义。关掉之后进度解析和日志可读性都更稳。
        AddBool("--no-ansi-color", true);
        // 4) 临时目录默认取「进程工作目录」，与 --save-dir 无关：会在程序目录里堆一堆
        //    <任务名>_<时间戳>，而且与保存目录跨盘时成品要整份复制（几个 GB 白等一遍 I/O）。
        //    固定到保存目录下的子目录，成品就退化成同盘 rename。
        if (!string.IsNullOrEmpty(o.WorkDir)) AddFlag("--tmp-dir", TempDirOf(o.WorkDir));

        if (!string.IsNullOrEmpty(o.MuxFile)) AddFlag("--mux-import", o.MuxFile);
        if (o.NoDateInName) AddFlag("--no-date-info");
        if (o.NoLog)
        {
            if (supported.Count > 0 && !supported.Contains("--no-log") && supported.Contains("--no-log-file"))
                AddFlag("--no-log-file");
            else
                AddFlag("--no-log");
        }
        AddBool("--use-system-proxy", !o.NoSystemProxy);

        if (o.OnlyParseM3u8)
        {
            AddFlag("--skip-download");
        }
        else
        {
            AddBool("--del-after-done", o.DelAfterMerge);
            if (o.MuxWhileDownload)
            {
                AddFlag("--live-real-time-merge");
                AddFlag("--live-pipe-mux");
            }
            // 合并方式三选一：跳过 > 二进制 > concat 分离器。
            // 默认的 concat 协议会让 ffmpeg 一次性拿到全部片段路径，几千个分片时
            // 会撞上命令行长度 / 文件句柄上限，表现就是"下完了却合并失败"。
            if (o.NoMerge) AddFlag("--skip-merge");
            else if (o.BinaryMerge) AddFlag("--binary-merge");
            else AddFlag("--use-ffmpeg-concat-demuxer");
            if (o.ConcurrentDownload) AddFlag("--concurrent-download");
            if (o.AppendUrlParams) AddFlag("--append-url-params");
            AddBool("--check-segments-count", o.CheckSegmentsCount);
            AddFlag("--thread-count", o.MaxThreads.ToString());
            AddFlag("--download-retry-count", o.RetryCount.ToString());
            AddFlag("--http-request-timeout", o.Timeout.ToString());
            if (o.LimitSpeed > 0) AddFlag("--max-speed", $"{o.LimitSpeed}K");
        }
        return cmd;
    }

    async Task<bool> ReAsync(string url, string saveName, Opt o,
                             IReadOnlyList<string> headers, int taskId, DateTime startedAt)
    {
        var supported = SupportedFlags(o.Executable);
        var cmd = BuildReCmd(url, saveName, o, headers, supported);
        _log($"命令: {Quote(cmd)}");

        var tail = new Queue<string>();
        double lastPct = -1, shownPct = -1, lastEmit = 0;

        try
        {
            using var proc = StartProc(cmd, Gbk, taskId, workingDir: o.WorkDir);
            var exitCode = await ReadProcAsync(proc, line =>
            {
                var s = line.TrimEnd();
                if (s.Length == 0) return;
                tail.Enqueue(s);
                while (tail.Count > 20) tail.Dequeue();

                double? pct = null;
                var m = ReRatio.Match(s);
                if (m.Success)
                {
                    if (!double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) pct = null;
                    else pct = v;
                }
                else if (s.Contains("----", StringComparison.Ordinal))
                {
                    var m2 = RePctOnly.Match(s);
                    if (m2.Success &&
                        double.TryParse(m2.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v2))
                        pct = v2;
                }

                if (pct is { } p && p is >= 0 and <= 100)
                {
                    var now = Environment.TickCount64 / 1000.0;
                    var capped = Math.Min(p, 99.0);
                    if (capped < shownPct) capped = shownPct;   // 音轨/视频流各自计数，防止回退
                    if (capped != lastPct &&
                        (Math.Abs(capped - lastPct) >= ProgressMinDelta || now - lastEmit >= ProgressMinInterval))
                    {
                        lastPct = capped;
                        shownPct = capped;
                        lastEmit = now;
                        if (taskId >= 0) _progress(taskId, capped);
                    }
                    var residue = ReRatio.Replace(s, "");
                    residue = RePctOnly.Replace(residue, "").Trim(' ', '-', ':', '|', '\t');
                    if (residue.Length == 0) return;
                }
                _log(s);
            });

            if (!_isRunning())
            {
                _log("⏹ 任务已停止");
                return false;
            }
            if (Outputs.Exists(o.WorkDir, saveName, startedAt))
            {
                if (taskId >= 0) _progress(taskId, 100.0);
                _log(exitCode != 0
                    ? $"⚠ 退出码 {exitCode} 非0，但检测到输出文件已生成"
                    : "✅ 任务完成（文件已生成）");
                return true;
            }
            _log($"❌ RE 下载失败：未检测到输出文件（退出码 {exitCode}）");
            var tailText = string.Join('\n', tail);
            var tailStatus = FindHttpStatus(tailText);
            if (tailStatus is 401 or 403)
                _log("💡 提示：服务器返回 403 Forbidden。建议改用 yt-dlp 并在任务行末追加 Cookie/Referer。");
            else if (tailText.Contains("Unrecognized command") || tailText.Contains("Unrecognized option"))
                _log("💡 提示：RE 报告了未知参数。");
            else if (tailText.Contains("INFO : Done") || tailText.TrimEnd().EndsWith("Done"))
                _log("⚠ 日志中出现 Done 但文件缺失，常见原因：FFmpeg 未安装/不在 PATH。");
            return false;
        }
        catch (Exception e)
        {
            _log($"异常：{e.Message}");
            return false;
        }
    }

    // ============================================================
    // 进程工具
    // ============================================================
    Process StartProc(IEnumerable<string> cmd, Encoding? enc, int taskId = -1, bool track = true,
                      string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cmd.First(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 工作目录必须钉在保存目录：RE 的临时目录、日志文件默认都取「进程工作目录」，
        // 不设的话会堆在 GUI 的启动目录（可能是 Program Files，还可能写不进去），
        // 而且与保存目录跨盘时成品要整份复制而不是 rename。
        if (!string.IsNullOrEmpty(workingDir) && Directory.Exists(workingDir))
            psi.WorkingDirectory = workingDir;
        foreach (var a in cmd.Skip(1)) psi.ArgumentList.Add(a);
        if (enc is not null)
        {
            psi.StandardOutputEncoding = enc;
            psi.StandardErrorEncoding = enc;
        }
        var p = Process.Start(psi)!;
        // BUG-10：探测类短命令不需要纳入 _procs 追踪（它们自己同步 Dispose），
        // 只有真正的下载进程才追踪，避免 TerminateAll 误杀/泄漏。
        // 记录归属：删单个任务时只杀它自己的进程
        if (track) _procs[p] = taskId;
        // BUG-10：启动后若队列已停止，立即杀掉刚起来的进程，避免残留孤儿进程继续下载。
        if (track && !_isRunning())
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
        return p;
    }

    /// <summary>读取 stdout/stderr，合并成一条行流；返回退出码。</summary>
    async Task<int> ReadProcAsync(Process proc, Action<string> onLine)
    {
        var stderrLines = new List<string>();
        var errTask = Task.Run(async () =>
        {
            try
            {
                var stderr = proc.StandardError;
                if (stderr is not null)
                    while (await stderr.ReadLineAsync() is { } line)
                        lock (stderrLines) stderrLines.Add(line);
            }
            catch { }
        });

        try
        {
            try
            {
                var stdout = proc.StandardOutput;
                if (stdout is not null)
                    while (await stdout.ReadLineAsync() is { } line)
                    {
                        onLine(line);
                        if (!_isRunning())
                        {
                            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                        }
                    }
            }
            catch { }

            try { await proc.WaitForExitAsync(); } catch { }
            try { await errTask.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }

            lock (stderrLines)
                foreach (var l in stderrLines)
                    onLine("⚠ " + l);

            return proc.HasExited ? proc.ExitCode : -1;
        }
        finally
        {
            // BUG-10：无论中途如何异常，都必须从追踪表移除，避免 TerminateAll 遍历到已释放进程。
            _procs.TryRemove(proc, out _);
        }
    }

    /// <summary>
    /// PATH 探测入口（保留该名字：诊断与测试会反射它）。
    /// 走 <see cref="PathProbe"/>：与主窗体状态点亮的是同一份结果，含注册表兜底，
    /// 避免"状态点绿、下载说找不到"这种自相矛盾。
    /// </summary>
    static string? FindOnPath(string name) => PathProbe.Find(name);

    public static string Quote(IEnumerable<string> cmd)
    {
        var parts = cmd.Select(c => c.Contains(' ') ? $"\"{c}\"" : c);
        return string.Join(' ', parts);
    }
}
