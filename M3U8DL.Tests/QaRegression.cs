using System.Reflection;

namespace M3U8DL.Tests;

/// <summary>
/// QA（严过关）独立回归测试。
///
/// 单独成文件，避免与其他改动并行修改 Tests.cs 时互相覆盖。
/// 覆盖两类内容：
///   A. 已确认缺陷的回归用例（当前会失败，修复后应转绿）
///   B. 修复过程中暴露出的覆盖缺口（路径含空格/中文、小写 cookie: 头、
///      值里含冒号的请求头、同时指定两种 Cookie 来源、多种错误同时出现的可读性）
/// </summary>
internal static class QaRegression
{
    static readonly MethodInfo CheckMi =
        typeof(Tests).GetMethod("Check", BindingFlags.Static | BindingFlags.NonPublic)!;

    static void Check(string name, bool ok, string detail = "") =>
        CheckMi.Invoke(null, new object?[] { name, ok, detail });

    public static void Run()
    {
        Console.WriteLine("=== 5) QA 独立回归（严过关）===");
        FalsePositive403Tests();
        CookieArgumentTests();
        HeaderParsingTests();
        DiagnosisReadabilityTests();
        E2EFalsePositiveTests();
    }

    /// <summary>
    /// 端到端证明误判会真的多跑一遍 N_m3u8DL-RE（而不只是判定函数返回 true）。
    /// 用隔离的 APPDATA，绝不碰用户真实任务库。
    /// </summary>
    static void E2EFalsePositiveTests()
    {
        var fakeSrc = Environment.GetEnvironmentVariable("FAKE_EXE");
        if (string.IsNullOrEmpty(fakeSrc) || !File.Exists(fakeSrc))
        {
            Check("找到假下载器（跳过端到端回归）", false, fakeSrc ?? "(未设置 FAKE_EXE)");
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "m3u8dl_qa_" + Guid.NewGuid().ToString("N")[..8]);
        var bin = Path.Combine(work, "bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(work, "dl"));
        var ytExe = Path.Combine(bin, "yt-dlp.exe");
        var reExe = Path.Combine(bin, "N_m3u8DL-RE.exe");
        File.Copy(fakeSrc, ytExe, true);
        File.Copy(fakeSrc, reExe, true);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldAppData = Environment.GetEnvironmentVariable("APPDATA");
        var oldMode = Environment.GetEnvironmentVariable("FAKE_MODE");
        Environment.SetEnvironmentVariable("PATH", bin + ";" + oldPath);
        // 隔离 APPDATA：MainForm 构造时会 load→save，绝不能写回用户真实 tasks.json
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(work, "appdata"));
        Directory.CreateDirectory(Path.Combine(work, "appdata", "NM3U8DL_GUI"));

        try
        {
            // ---- 端到端 1：URL 含字面量 "403" 的无关 404，不该降级 RE ----
            Environment.SetEnvironmentVariable("FAKE_MODE", "notfound_vid");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetExe(f, reExe);
                // 与用户真实任务 7 同构的 URL：hash 里含 "403"
                AddTask(f, "任务403", "http://x/api/v1/m3u8/proxy?path=jpd%2F20261107%2Fmq%2Fos%2Fvr%2Fwo%2F76c2f740358444279b5cf210ca446b31.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("URL 含 403 的无关 404 判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("URL 含 403 的无关 404 不降级到 RE",
                      !log.Contains("自动改用 N_m3u8DL-RE"),
                      "误判为 403 类失败 → 多跑了一遍 RE：" + Tail(log));
                Check("URL 含 403 的无关 404 不被诊断成 403",
                      !log.Contains("服务器返回 403"), Tail(log));
            }

            // ---- 端到端 2：有 cookie 作用域提示的无关 404，也不该降级 RE ----
            Environment.SetEnvironmentVariable("FAKE_MODE", "notfound_cookie");
            using (var f = NewForm(Path.Combine(work, "dl")))
            {
                SetExe(f, reExe);
                AddTask(f, "任务Cookie提示", "http://x/k.m3u8");
                Start(f);
                WaitDone(f, 1);
                var log = Log(f);
                Check("有 cookie 作用域提示的无关 404 判为失败", Statuses(f).Single() == "失败", string.Join(",", Statuses(f)));
                Check("有 cookie 作用域提示的无关 404 不降级到 RE",
                      !log.Contains("自动改用 N_m3u8DL-RE"),
                      "cookie 弃用警告每次都有，不能单独当降级依据：" + Tail(log));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("APPDATA", oldAppData);
            Environment.SetEnvironmentVariable("FAKE_MODE", oldMode ?? "normal");
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ---------------- 端到端辅助（复用 Tests 的私有工具）----
    /// <summary>调用 Tests 的静态 NewForm（会清空隔离 APPDATA 下的 tasks.json 并 Show 出窗体）。</summary>
    static MainForm NewForm(string workDir)
    {
        var m = typeof(Tests).GetMethod("NewForm", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (MainForm)m.Invoke(null, new object?[] { workDir, false })!;
    }

    // SetText / AddTask / Start 都是 Tests 的**静态**辅助方法（不是 MainForm 的实例方法），
    // 直接在 MainForm 上反射会拿到 null 并抛 NullReferenceException。
    static void SetExe(MainForm f, string exe) =>
        CallStatic("SetText", f, "txtExe", exe);

    static void AddTask(MainForm f, string name, string url) =>
        CallStatic("AddTask", f, name, url);

    static void Start(MainForm f) => CallStatic("Start", f);

    static void WaitDone(MainForm f, int count) =>
        CallStatic("WaitDone", f, count);

    static List<string> Statuses(MainForm f) =>
        (List<string>)CallStatic("Statuses", f)!;

    static string Log(MainForm f) => (string)CallStatic("Log", f)!;

    static string Tail(string s) => s.Length > 400 ? s[^400..] : s;

    /// <summary>Tests 里的这些辅助都是 static（MainForm 相关的一律通过第一个参数传入）。</summary>
    static object? CallStatic(string name, params object?[] args)
    {
        var m = typeof(Tests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        return m.Invoke(null, args);
    }

    // ============================================================
    // A. 已确认缺陷的回归用例
    // ============================================================

    /// <summary>
    /// 缺陷 1：降级判定用裸子串 Contains("403")，会把 URL 里的 "403" 当成 HTTP 403。
    /// 缺陷 2：软证据里的 Contains("unable to download") 过宽，404 也会触发降级。
    ///
    /// 下面的错误文本全部来自真实 yt-dlp（本机实测 + 本地 HTTP 服务器），
    /// 其中 video_id 由 URL 推导，会原样出现在 ERROR 行里。
    /// </summary>
    static void FalsePositive403Tests()
    {
        // 用户真实任务 7 的 URL 同构：hash 里含字面量 "403"
        // （形如 https://<站点>/api/v1/m3u8/proxy?path=...76c2f740358444279b5cf210ca446b31.m3u8）
        // 真实 yt-dlp 输出（服务器返回 404，与 Cookie/403 毫无关系）：
        const string real404 =
            "ERROR: [generic] proxy?path=jpd/20261107/mq/os/vr/wo/76c2f740358444279b5cf210ca446b31: " +
            "Unable to download webpage: HTTP Error 404: Not Found (caused by <HTTPError 404: Not Found>)";
        // 真实 yt-dlp 输出（服务器真的返回 403）：
        const string real403 =
            "ERROR: [generic] v: Unable to download webpage: HTTP Error 403: Forbidden (caused by <HTTPError 403: Forbidden>)";

        Check("真实 403 仍应触发降级", Engine.IsCookieOrDecryptFailure(real403));

        // 缺陷 1：404 + URL 含 "403" 不该降级
        Check("404 且 URL 含字面量 403 不触发降级",
              !Engine.IsCookieOrDecryptFailure(real404),
              "误判为 403 类失败 → 会多跑一遍 N_m3u8DL-RE");
        Check("404 且 URL 含字面量 403 时也不误诊为 403",
              !Engine.DiagnoseYtDlpError(real404).Any(t => t.Contains("服务器返回 403")),
              "诊断把 404 说成 403，会把用户引向错误方向");

        // 缺陷 2：无关失败 + cookie 作用域提示也不该降级
        const string videoData404 = "ERROR: unable to download video data: HTTP Error 404: Not Found";
        Check("视频数据 404 不触发降级（无 cookie 提示）",
              !Engine.IsCookieOrDecryptFailure(videoData404));
        Check("视频数据 404 不触发降级（即使有 cookie 作用域提示）",
              !Engine.IsCookieOrDecryptFailure(videoData404, cookieScopeWarn: true),
              "cookie 作用域提示每次用 header Cookie 都会出现，不能单独当降级依据");

        // "cbc" 同为裸子串：URL/文件名里含 cbc 不该被当成 CBC 解密失败
        const string cbcInUrl =
            "ERROR: [generic] abcbc12345: Unable to download webpage: HTTP Error 404: Not Found";
        Check("URL 含字面量 cbc 不触发降级",
              !Engine.IsCookieOrDecryptFailure(cbcInUrl),
              "裸子串 Contains(\"cbc\") 会误命中");

        // 真正的 CBC 解密失败必须仍然降级
        Check("真实 CBC 解密失败触发降级",
              Engine.IsCookieOrDecryptFailure("ERROR: Data must be padded to 16 byte boundary in CBC mode"));
    }

    // ============================================================
    // B. 覆盖缺口
    // ============================================================

    /// <summary>Cookie 文件路径含空格/中文时，必须作为单个参数原样传递。</summary>
    static void CookieArgumentTests()
    {
        var eng = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, () => true);
        var opt = new Opt { WorkDir = @"C:\work", UseYtDlp = true, MaxThreads = 32, RetryCount = 3, Timeout = 100 };

        foreach (var path in new[]
        {
            @"C:\tmp\cookies.txt",
            @"C:\tmp\my cookies.txt",
            @"C:\tmp\中文 目录\我的 cookie 文件.txt",
            @"C:\tmp\带#号&符号\cookies.txt",
        })
        {
            var cmd = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", opt with { CookieFile = path }, [])
                      ?? throw new InvalidOperationException("未找到 yt-dlp");
            var idx = cmd.IndexOf("--cookies");
            Check($"Cookie 路径原样传入：{Path.GetFileName(path)}",
                  idx >= 0 && idx + 1 < cmd.Count && cmd[idx + 1] == path,
                  string.Join(" ", cmd));
            // ArgumentList 语义：路径必须是列表里的独立元素，不能被空格拆开
            Check($"Cookie 路径未被拆成多个参数：{Path.GetFileName(path)}",
                  cmd.Count(c => c == path) == 1 && cmd.Skip(1).All(c => !c.Contains(" ") || c == path));
        }

        // 浏览器名同样要原样传递
        var cmdB = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", opt with { CookieBrowser = "chrome" }, [])
                   ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("浏览器名原样传入",
              cmdB.Contains("--cookies-from-browser") &&
              cmdB[cmdB.IndexOf("--cookies-from-browser") + 1] == "chrome");
    }

    /// <summary>请求头名大小写不敏感；值里含冒号也要能正确切分。</summary>
    static void HeaderParsingTests()
    {
        var eng = new Engine(_ => { }, (_, _) => { }, (_, _) => { }, () => true);
        var opt = new Opt { WorkDir = @"C:\work", UseYtDlp = true, MaxThreads = 32, RetryCount = 3, Timeout = 100 };

        // 小写 cookie: 也应被识别为 Cookie 头并去重
        foreach (var name in new[] { "Cookie", "cookie", "COOKIE", "CoOkIe" })
        {
            var cmd = BuildYtDlp(eng, "http://x/a.m3u8", "剧集",
                                 opt with { CookieFile = @"C:\tmp\c.txt" },
                                 [$"{name}: a=b", "Referer: http://r"])
                      ?? throw new InvalidOperationException("未找到 yt-dlp");
            Check($"指定 Cookie 来源后 {name}: 头被去重",
                  !HasHeader(cmd, "cookie:"), string.Join(" ", cmd));
            Check($"指定 Cookie 来源后 {name}: 其它请求头照旧传入",
                  HasHeader(cmd, "Referer: http://r"));
        }

        // 值里含冒号的 Cookie 头必须被正确识别（切分点取第一个冒号）
        var cmdColon = BuildYtDlp(eng, "http://x/a.m3u8", "剧集",
                                  opt with { CookieFile = @"C:\tmp\c.txt" },
                                  ["Cookie: a=1; b=2", "Referer: http://x/y"])
                       ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("值里含冒号的 Cookie 头被识别并去重",
              !HasHeader(cmdColon, "cookie:"), string.Join(" ", cmdColon));
        Check("值里含冒号的 Cookie 头不影响 Referer",
              HasHeader(cmdColon, "Referer: http://x/y"));

        // 未指定 Cookie 来源时，header Cookie 必须照旧传（向后兼容）
        var cmdPlain = BuildYtDlp(eng, "http://x/a.m3u8", "剧集", opt, ["Cookie: a=1; b=2", "Referer: http://r"])
                       ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("未指定 Cookie 来源时仍传 header Cookie",
              HasHeader(cmdPlain, "Cookie: a=1; b=2"), string.Join(" ", cmdPlain));
        Check("未指定 Cookie 来源时值里含冒号的 header 完整保留",
              cmdPlain.Contains("Cookie: a=1; b=2"));
        Check("未指定 Cookie 来源时不带 --cookies", !cmdPlain.Contains("--cookies"));
        Check("未指定 Cookie 来源时不带 --cookies-from-browser", !cmdPlain.Contains("--cookies-from-browser"));

        // 同时指定 Cookie 文件和浏览器 Cookie：两个参数都要传
        var cmdBoth = BuildYtDlp(eng, "http://x/a.m3u8", "剧集",
                                 opt with { CookieFile = @"C:\tmp\c.txt", CookieBrowser = "edge" },
                                 ["Cookie: a=b"])
                      ?? throw new InvalidOperationException("未找到 yt-dlp");
        Check("同时指定时 --cookies 存在",
              cmdBoth.Contains("--cookies") &&
              cmdBoth[cmdBoth.IndexOf("--cookies") + 1] == @"C:\tmp\c.txt", string.Join(" ", cmdBoth));
        Check("同时指定时 --cookies-from-browser 存在",
              cmdBoth.Contains("--cookies-from-browser") &&
              cmdBoth[cmdBoth.IndexOf("--cookies-from-browser") + 1] == "edge", string.Join(" ", cmdBoth));
        Check("同时指定时 header Cookie 仍被去重",
              !HasHeader(cmdBoth, "cookie:"), string.Join(" ", cmdBoth));
    }

    /// <summary>多种错误同时出现时，诊断输出仍要可读、有条理。</summary>
    static void DiagnosisReadabilityTests()
    {
        var tips = Engine.DiagnoseYtDlpError(
            "ERROR: [generic] v: Unable to download webpage: HTTP Error 403: Forbidden\n" +
            "ERROR: Data must be padded to 16 byte boundary in CBC mode\n" +
            "Deprecated Feature: Passing cookies as a header ... Please consider loading cookies from a file or browser instead.");

        Check("多种错误同时出现时产出多条诊断", tips.Count >= 3, tips.Count.ToString());
        Check("多种错误同时出现时诊断不重复",
              tips.Distinct().Count() == tips.Count, string.Join(" | ", tips));
        Check("多种错误同时出现时提到签名过期",
              tips.Any(t => t.Contains("签名过期")));
        Check("多种错误同时出现时提到 Cookie 文件",
              tips.Any(t => t.Contains("Cookie 文件")));
        Check("多种错误同时出现时提到 N_m3u8DL-RE",
              tips.Any(t => t.Contains("N_m3u8DL-RE")));
        Check("诊断不把 cookie 弃用警告当成失败原因",
              tips.Any(t => t.Contains("通用弃用警告") || t.Contains("未必相关")),
              string.Join(" | ", tips));

        // 签名过期判定不能被证书过期误伤
        Check("证书过期不判为播放地址签名过期",
              !Engine.IsPlaylistSignatureExpired("ERROR: SSL: certificate verify failed: certificate has expired"));
    }

    // ---------------- 反射辅助 ----------------
    static bool HasHeader(List<string> cmd, string prefix)
    {
        for (var i = 0; i < cmd.Count - 1; i++)
            if (cmd[i] == "--add-header" &&
                cmd[i + 1].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    static List<string>? BuildYtDlp(Engine eng, string url, string name, Opt o,
                                   List<string> headers, bool impersonate = true)
    {
        var m = typeof(Engine).GetMethod("BuildYtDlpCmd", BindingFlags.Instance | BindingFlags.NonPublic);
        return (List<string>?)m!.Invoke(eng, new object?[] { url, name, o, headers, impersonate });
    }
}
