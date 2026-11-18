using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace M3U8DL;

/// <summary>
/// MainForm 的独立设置持久化部分（PRD 5.3，实施步骤 S6）。
/// 只写 %APPDATA%\NM3U8DL_GUI\settings.json；绝不触碰 tasks.json 结构/字段，
/// 绝不保存 Cookie 值本身（只存文件路径或浏览器名）。任何失败都吞掉并降级为内存态，不阻断主流程。
/// </summary>
internal sealed partial class MainForm : Form
{
    static readonly JsonSerializerOptions SettingsJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 配置目录：%APPDATA%\NM3U8DL_GUI。
    /// 优先读 APPDATA 环境变量（与 Python 版 os.environ.get("APPDATA") 一致），
    /// 这样测试/多实例重定向 APPDATA 即可隔离，不会互相覆盖真实配置或任务文件。
    /// </summary>
    static string AppDir
    {
        get
        {
            var appdata = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appdata))
                appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appdata, "NM3U8DL_GUI");
        }
    }

    /// <summary>settings.json 路径（与 tasks.json 同目录，同一套 APPDATA 隔离规则）。</summary>
    static string SettingsFile => Path.Combine(AppDir, "settings.json");

    /// <summary>可持久化的设置子集。字段集合受 PRD 5.3 硬边界约束，不得新增 Cookie 值字段。</summary>
    sealed class AppSettings
    {
        public bool UseYtDlp { get; set; } = true;
        public string WorkDir { get; set; } = "";
        public string Executable { get; set; } = "";
        public string MuxFile { get; set; } = "";
        public int Concurrent { get; set; } = 1;
        public int Threads { get; set; } = 32;
        public int Retry { get; set; } = 3;
        public int Timeout { get; set; } = 100;
        public int Speed { get; set; } = 0;
        /// <summary>批次结束后对失败任务自动重跑的轮数；0 = 关闭。与 Retry（引擎内单任务重试）无关。</summary>
        public int AutoRetryRounds { get; set; } = 1;
        public string CookieFile { get; set; } = "";   // 仅路径，不含 Cookie 内容
        public string CookieBrowser { get; set; } = ""; // 仅浏览器标识
        public Dictionary<string, bool> Toggles { get; set; } = [];
        public int SplitterDistance { get; set; } = 280;
        public bool AdvancedExpanded { get; set; } = false;
    }

    /// <summary>构造期加载设置并套用到控件；失败静默降级到默认值。</summary>
    void LoadSettings()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(SettingsFile))
                s = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsFile, Encoding.UTF8), SettingsJsonOpts);
        }
        catch { }

        s ??= new AppSettings();

        // 引擎选择 / 路径：存档值优先，缺省走探测默认值。
        txtExe.Text = string.IsNullOrWhiteSpace(s.Executable) ? DetectReExecutable() : s.Executable;
        txtWorkDir.Text = string.IsNullOrWhiteSpace(s.WorkDir) ? DefaultWorkDir() : s.WorkDir;
        txtMux.Text = s.MuxFile ?? "";
        txtCookieFile.Text = s.CookieFile ?? ""; // 只恢复路径

        if (!string.IsNullOrEmpty(s.CookieBrowser))
        {
            var idx = cmbCookieBrowser.Items.IndexOf(s.CookieBrowser);
            if (idx >= 0) cmbCookieBrowser.SelectedIndex = idx;
        }

        SetNud(nudConc, s.Concurrent, 1);
        SetNud(nudThreads, s.Threads, 32);
        SetNud(nudRetry, s.Retry, 3);
        SetNud(nudTimeout, s.Timeout, 100);
        SetNud(nudSpeed, s.Speed, 0);
        // 0 是有效值（= 关闭自动重试），不能用 SetNud（它把 ≤0 一律当缺省值），越界夹紧即可。
        nudAutoRetry.Value = Math.Clamp(s.AutoRetryRounds,
            (int)nudAutoRetry.Minimum, (int)nudAutoRetry.Maximum);
        concurrentLimit = Math.Max(1, (int)nudConc.Value);

        if (s.Toggles is not null)
            foreach (var kv in s.Toggles)
                if (chk.TryGetValue(kv.Key, out var c)) c.Checked = kv.Value;

        // BuildUi 里的刷新发生在本次填充之前（那时 txtExe 还是空串），必须在这里补一次，
        // 否则右下角的 RE 状态点会一直显示灰色不可用。
        RefreshDependencyDots();
    }

    /// <summary>收集当前控件状态写入 settings.json；失败静默（PRD：不得阻断主流程）。</summary>
    void SaveSettings()
    {
        try
        {
            var s = new AppSettings
            {
                UseYtDlp = C("useYtDlp"),
                WorkDir = txtWorkDir.Text.Trim(),
                Executable = txtExe.Text.Trim(),
                MuxFile = txtMux.Text.Trim(),
                Concurrent = (int)nudConc.Value,
                Threads = (int)nudThreads.Value,
                Retry = (int)nudRetry.Value,
                Timeout = (int)nudTimeout.Value,
                Speed = (int)nudSpeed.Value,
                AutoRetryRounds = (int)nudAutoRetry.Value,
                CookieFile = txtCookieFile.Text.Trim(), // 只存路径
                CookieBrowser = SelectedBrowser(),      // 只存浏览器标识（"不使用"→""）
                SplitterDistance = 280,
                AdvancedExpanded = false,
            };
            lock (chk)
                foreach (var kv in chk) s.Toggles[kv.Key] = kv.Value.Checked;

            var dir = Path.GetDirectoryName(SettingsFile)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, SettingsJsonOpts), new UTF8Encoding(false));
            File.Move(tmp, SettingsFile, true);
        }
        catch
        {
            // PRD 硬边界 5：持久化失败不得阻断主流程，退化为内存态即可。
        }
    }

    static void SetNud(NumericUpDown nud, int value, int fallback)
    {
        var v = value <= 0 ? fallback : value;
        nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, v));
    }

    /// <summary>默认保存目录：UserProfile/Downloads（硬编码路径清理后的约定）。</summary>
    static string DefaultWorkDir()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = Path.Combine(profile, "Downloads");
        return Directory.Exists(downloads) ? downloads : profile;
    }

    /// <summary>
    /// 默认 RE 路径。按「进程 PATH → 注册表 PATH → 程序自身目录 → 常见安装目录」四级探测，
    /// 找不到才返回空串（此时开跑 RE 模式会提示用户选择，见 EnsureReExecutable）。
    /// </summary>
    static string DetectReExecutable()
    {
        const string exe = "N_m3u8DL-RE.exe";

        // 1) 进程 PATH + 2) 注册表 PATH（见 FindOnPathAnywhere 的注释：进程 PATH 可能是登录时快照）
        var hit = FindOnPathAnywhere(exe);
        if (hit is not null) return hit;

        // 3) 程序自身目录 / 相邻的 tools、bin 子目录：把 RE 放在 GUI 旁边是最常见的便携用法
        var selfDir = AppContext.BaseDirectory;
        foreach (var d in new[] { selfDir, Path.Combine(selfDir, "tools"), Path.Combine(selfDir, "bin") })
        {
            try
            {
                var p = Path.Combine(d, exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }

        // 4) 常见安装位置。Fluent-M3U8 的解压目录带版本号（Fluent-M3U8-v0.11.0-Windows-x86_64），
        //    写死 "Fluent-M3U8" 必然漏掉，所以下面还要对目录名做一次通配匹配。
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(pf, "N_m3u8DL-RE", exe),
            Path.Combine(pf, "N_m3u8DL-RE", "N_m3u8DL-RE", exe),
            Path.Combine(pf, "Fluent-M3U8", "tools", exe),
            Path.Combine(localApp, "Programs", exe),
            Path.Combine(localApp, "Programs", "N_m3u8DL-RE", exe),
            Path.Combine(localApp, "Fluent-M3U8", "tools", exe),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;

        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                // OrderBy 只为结果稳定（文件系统枚举顺序不确定），与优先级无关
                foreach (var dir in Directory.EnumerateDirectories(root, "Fluent-M3U8*").OrderBy(x => x))
                {
                    var p = Path.Combine(dir, "tools", exe);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
        }
        return "";
    }

    /// <summary>
    /// PATH 里找可执行文件（进程 PATH 没命中时用注册表 PATH 兜底）。
    /// 实现见 <see cref="PathProbe"/>：Engine 探测 yt-dlp 走的是同一份逻辑，
    /// 否则会出现「状态点亮着却说找不到」的矛盾。
    /// </summary>
    static string? FindOnPathAnywhere(string name) => PathProbe.Find(name);

    /// <summary>从注册表的机器级 / 用户级 PATH 里找（绕开"进程 PATH 是登录快照"这个坑，测试用）。</summary>
    static string? FindInRegistryPath(string name) => PathProbe.FindInRegistryPath(name);
}
