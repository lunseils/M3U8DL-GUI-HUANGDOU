using System.Text;

// 假下载器：按调用方式同时模拟 yt-dlp 与 N_m3u8DL-RE，
// 用来验证 C# 版引擎的命令构造、进度解析、成品判定与失败识别。
// 行为由环境变量 FAKE_MODE 控制：
//   normal(默认) 正常下载 / partial 只留分离流 / imp 不支持 impersonate
//   nofile RE 不产出文件 / cbc 解密失败 / notfound 无关的 404
//   notfound_vid URL 含字面量 403 的无关 404 / notfound_cookie 带 cookie 弃用警告的无关 404
//   expired 签名过期：第 1 轮报 CBC + 403 Forbidden: expired，第 2 轮成功
//   slow 慢速下载（约 2.5 秒，用于「下载中删任务」场景）
var mode = Environment.GetEnvironmentVariable("FAKE_MODE") ?? "normal";

if (args.Contains("--version"))
{
    Console.WriteLine("fake-yt-dlp 0.0.1");
    return 0;
}

if (args.Contains("--help"))
{
    foreach (var f in new[]
    {
        "--save-name", "--save-dir", "--mux-import", "--no-date-info", "--no-log",
        "--no-log-file", "--use-system-proxy", "--skip-download", "--del-after-done",
        "--live-real-time-merge", "--live-pipe-mux", "--skip-merge", "--binary-merge",
        "--concurrent-download", "--append-url-params", "--check-segments-count",
        "--thread-count", "--download-retry-count", "--http-request-timeout", "--max-speed",
        // 加速相关：真实 RE 20260628 全部支持，假下载器必须同步声明，
        // 否则 SupportedFlags 会把这些参数判为"不支持"而跳过，测试就测不到它们
        "--auto-select", "--disable-update-check", "--no-ansi-color", "--tmp-dir",
        "--use-ffmpeg-concat-demuxer",
    })
        Console.WriteLine($"  {f} <value>");
    return 0;
}

var hasTemplate = args.Contains("-o");
if (hasTemplate)
{
    // ---------- yt-dlp 模式 ----------
    var template = ValueAfter(args, "-o");
    if (mode == "imp" && args.Contains("--impersonate"))
    {
        Console.WriteLine("ERROR: Unrecognized arguments: --impersonate");
        return 1;
    }
    if (mode == "cbc")
    {
        // 模拟真实故障：服务端返回的不是加密分片本体（403 错误页 / 登录页），
        // yt-dlp 用 AES-128-CBC 解密时抛出 "Data must be padded to 16 byte boundary"，
        // 并且不产出任何文件。RE 分支在该模式下照常成功，用于验证自动降级。
        Console.WriteLine("ERROR: Data must be padded to 16 byte boundary in CBC mode");
        return 1;
    }
    if (mode == "notfound")
    {
        // 与 Cookie / 解密 / 403 无关的失败：不该触发降级到 RE
        Console.WriteLine("ERROR: unable to download video data: HTTP Error 404: Not Found");
        return 1;
    }
    if (mode == "notfound_vid")
    {
        // 复刻真实 yt-dlp 的输出格式：ERROR 行里带上从 URL 推导出的 video_id。
        // 用户真实任务 7 的 URL hash 含字面量 "403"（...f74035...），
        // 真实 yt-dlp 的 ERROR 行会原样带出它 —— 用来暴露
        // "裸子串 Contains(\"403\")" 把 URL 里的 403 当成 HTTP 403 的误判。
        Console.WriteLine($"ERROR: [generic] {VideoIdFromUrl(args)}: Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)");
        return 1;
    }
    if (mode == "notfound_cookie")
    {
        // yt-dlp 用了 header Cookie 时**每次**都会打这行弃用警告（走 stderr），
        // 与本次失败是否与 Cookie 无关无关。
        Console.Error.WriteLine("Deprecated Feature: Passing cookies as a header is a potential security risk; they will be scoped to the domain of the downloaded urls. Please consider loading cookies from a file or browser instead.");
        // 随后是一个与 Cookie/403 毫无关系的 404
        Console.WriteLine("ERROR: unable to download video data: HTTP Error 404: Not Found");
        return 1;
    }
    if (mode == "flaky")
    {
        // 「首次失败、重试成功」：专门验证批次级「失败自动重试」是否真的重跑了任务。
        // 计数器落在状态文件里（每次真实下载调用 +1，只有第 1 次失败）。
        // 用 404 而不是 403/CBC：既不会触发 RE 降级，也不会触发引擎内的「重新取播放列表」，
        // 失败一定冒到批次层，只能由 autoRetry 处理。
        var flakyStatePath = ExpiredStatePath(template) + ".flaky";
        var attempts = 0;
        try { if (File.Exists(flakyStatePath)) attempts = int.Parse(File.ReadAllText(flakyStatePath).Trim()); } catch { }
        attempts++;
        try
        {
            var stateDir = Path.GetDirectoryName(flakyStatePath);
            if (!string.IsNullOrEmpty(stateDir)) Directory.CreateDirectory(stateDir);
            File.WriteAllText(flakyStatePath, attempts.ToString());
        }
        catch { }
        Console.WriteLine($"[info] attempt {attempts}");
        // FAKE_FAIL_TIMES：前 N 次调用失败（默认 1），用来验证多轮自动重试与轮次耗尽
        var failTimes = 1;
        try
        {
            var raw = Environment.GetEnvironmentVariable("FAKE_FAIL_TIMES");
            if (!string.IsNullOrEmpty(raw)) failTimes = int.Parse(raw);
        }
        catch { }
        if (attempts <= failTimes)
        {
            Console.WriteLine("ERROR: unable to download video data: HTTP Error 404: Not Found");
            return 1;
        }
    }
    if (mode == "expired")
    {
        // 模拟真实故障第 1 轮：播放地址里的时效签名（auth_key）已过期，
        // 分片返回的是 403 纯文本错误页（22 字节，不是 16 的倍数），
        // yt-dlp 对它做 AES-128-CBC 解密 → "Data must be padded to 16 byte boundary in CBC mode"。
        // 第 2 轮（重新拉取播放列表 → 拿到新签名）成功，用来验证「重新获取播放列表重试」。
        var statePath = ExpiredStatePath(template);
        var firstRound = !File.Exists(statePath);
        try
        {
            var stateDir = Path.GetDirectoryName(statePath);
            if (!string.IsNullOrEmpty(stateDir)) Directory.CreateDirectory(stateDir);
            File.WriteAllText(statePath, "1");
        }
        catch { }
        if (firstRound)
        {
            Console.WriteLine("ERROR: [generic] The request is not allowed to access the resource: 403 Forbidden: expired");
            Console.WriteLine("ERROR: Data must be padded to 16 byte boundary in CBC mode");
            return 1;
        }
        // 第 2 轮：签名已刷新 → 走下面的正常下载流程
    }
    Console.WriteLine("[info] Downloading 1 format(s)");
    if (mode == "slow")
    {
        // 「下载中删任务」场景专用：每次 +10% 停 250ms，约 2.5 秒。
        // 进程活着才能验证 TerminateTask 真的杀掉了它，以及行号漂移后进度不会串行。
        for (var i = 0; i <= 100; i += 10)
        {
            Console.WriteLine($"PROGRESS|{i}.0%|{i}|100|{(i + 1) * 512 * 1024}");
            Console.Out.Flush();
            Thread.Sleep(250);
        }
    }
    else
    {
        // 4 段：百分比|已下载|总量|速率（字节/秒）—— 与真实 yt-dlp 的
        // --progress-template "…|%(progress.speed)s" 输出一致
        for (var i = 0; i <= 100; i += 25)
            Console.WriteLine($"PROGRESS|{i}.0%|{i}|100|{(i + 1) * 512 * 1024}");
    }
    if (mode == "partial")
    {
        var partDir = Path.GetDirectoryName(template)!;
        Directory.CreateDirectory(partDir);
        File.WriteAllBytes(Path.Combine(partDir, ReplaceExt(template, "f137.mp4")), new byte[1024]);
        return 0;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(template)!);
    File.WriteAllBytes(ReplaceExt(template, "mp4"), new byte[4096]);
    return 0;
}

// ---------- N_m3u8DL-RE 模式 ----------
var name = ValueAfter(args, "--save-name");
var dir = ValueAfter(args, "--save-dir");
for (var i = 1; i <= 4; i++)
    Console.WriteLine($"[INFO ] Downloading segment {i}/4  {i * 25}.0% ...");
if (mode == "nofile")
{
    Console.WriteLine("[WARN ] something went wrong");
    return 1;
}
if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
var outPath = Path.Combine(dir ?? ".", (name ?? "output") + ".mp4");
File.WriteAllBytes(outPath, new byte[2048]);
Console.WriteLine("[INFO ] Done");
return 0;

static string ValueAfter(string[] args, string key){
    var i = Array.IndexOf(args, key);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : "";
}

/// <summary>
/// 从命令行里的 URL 推导 video_id（模仿 yt-dlp generic extractor 的做法：
/// 取 URL 里 host 之后的部分，query 里的 %2F 还原成 /）。
/// 只用于让假下载器复刻真实 yt-dlp 的 ERROR 行格式。
/// </summary>
static string VideoIdFromUrl(string[] args){
    var url = args.Length > 0 ? args[0] : "";
    var i = url.IndexOf("://", StringComparison.Ordinal);
    if (i >= 0)
    {
        var rest = url[(i + 3)..];
        var slash = rest.IndexOf('/');
        url = slash >= 0 ? rest[slash..] : rest;
    }
    return url.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 签名过期场景的"第几轮"状态文件路径。
/// 优先用环境变量 FAKE_STATE（测试里指定，落在临时目录里）；
/// 没指定就放在输出模板所在目录，文件名带 "." 前缀，不会被引擎当成成品或临时产物。
/// </summary>
static string ExpiredStatePath(string template){
    var explicitPath = Environment.GetEnvironmentVariable("FAKE_STATE");
    if (!string.IsNullOrEmpty(explicitPath)) return explicitPath;
    var dir = Path.GetDirectoryName(template) ?? ".";
    return Path.Combine(dir, ".fake_expired_state");
}

static string ReplaceExt(string template, string ext)
{
    var dir = Path.GetDirectoryName(template) ?? "";
    var stem = Path.GetFileName(template);
    var idx = stem.IndexOf("%(ext)s", StringComparison.Ordinal);
    if (idx >= 0) stem = stem[..idx] + ext;
    else stem = Path.ChangeExtension(stem, "." + ext);
    return Path.Combine(dir, stem);
}
