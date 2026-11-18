using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// 验收截图工具：隔离 APPDATA，构造空态/满态/设置浮层三种状态并截图。
/// 截图方式：整窗 DrawToBitmap 渲染控件树（headless 会话下 CopyFromScreen 会抓到撕裂帧）。
/// 另对每个高频开关行单独 DrawToBitmap 出 diag_*.png，作为像素级无重叠的确定性证据。
/// </summary>
internal static class ShotProgram
{
    static string outDir = "";

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        outDir = @"D:\Program Files\aria2-1.37.0-win-64bit-build1\screenshots";
        Directory.CreateDirectory(outDir);

        // 隔离 APPDATA：绝不触碰用户真实任务库
        var iso = Path.Combine(Path.GetTempPath(), "m3u8dl_shot_" + Guid.NewGuid().ToString("N")[..8]);
        var gui = Path.Combine(iso, "NM3U8DL_GUI");
        Directory.CreateDirectory(gui);
        Environment.SetEnvironmentVariable("APPDATA", iso);
        var tasksFile = Path.Combine(gui, "tasks.json");

        try
        {
            // ---- 空态 ----
            if (File.Exists(tasksFile)) File.Delete(tasksFile);
            using (var f = new MainForm())
                RunCapture(f, [(RenderToggleRows, "01_empty.png")]);

            // ---- 满态：写入多条不同状态任务再构造 ----
            var sb = new StringBuilder();
            sb.AppendLine("[");
            var statuses = new[] { "完成", "完成", "完成", "失败", "下载中", "待下载", "完成", "待下载" };
            for (var i = 0; i < statuses.Length; i++)
                sb.AppendLine($"  {{\"name\":\"剧集 第{i + 1}集\",\"url\":\"http://x/{i + 1}.m3u8\",\"status\":\"{statuses[i]}\",\"headers\":[]}}{(i < statuses.Length - 1 ? "," : "")}");
            sb.AppendLine("]");
            File.WriteAllText(tasksFile, sb.ToString(), new UTF8Encoding(false));
            using (var f = new MainForm())
                RunCapture(f,
                [
                    (null, "02_filled.png"),
                    (x =>
                    {
                        var open = typeof(MainForm).GetMethod("OpenSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
                        open.Invoke(x, null);
                        RenderCompatHost(x);
                    }, "03_settings.png"),
                ]);

            Console.WriteLine("SHOT_OK");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("SHOT_ERR: " + e);
            return 1;
        }
        finally
        {
            try { Directory.Delete(iso, true); } catch { }
        }
    }

    static void RunCapture(MainForm f, (Action<MainForm>? before, string path)[] steps)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        var idx = 0;
        var phase = 0;
        timer.Tick += (_, _) =>
        {
            try
            {
                if (phase == 0) { phase = 1; return; }
                if (phase == 1) { steps[idx].before?.Invoke(f); phase = 2; return; }
                Capture(f, Path.Combine(outDir, steps[idx].path));
                idx++;
                if (idx >= steps.Length) { timer.Stop(); f.Close(); }
                else phase = 1;
            }
            catch (Exception e) { Console.WriteLine("TICK_ERR: " + e); timer.Stop(); f.Close(); }
        };
        f.Shown += (_, _) => timer.Start();
        Application.Run(f);
        timer.Dispose();
    }

    static void Capture(Form f, string path)
    {
        f.Activate();
        f.Refresh();
        Application.DoEvents();
        Thread.Sleep(120);
        var rect = f.Bounds;
        using var bmp = new Bitmap(rect.Width, rect.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(rect.Location, Point.Empty, rect.Size);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"SAVED {path} ({rect.Width}x{rect.Height})");
    }

    /// <summary>对开关宿主整体 DrawToBitmap：像素级证明 5 行开关文字清晰、无遮挡无重叠。</summary>
    static void RenderToggleRows(Form f)
    {
        Control? host = null;
        void Walk(Control c)
        {
            if (c is ToggleSwitch && c.Parent is not null) { host = c.Parent; return; }
            foreach (Control ch in c.Controls) { Walk(ch); if (host is not null) return; }
        }
        Walk(f);
        if (host is null) { Console.WriteLine("TOGGLE_HOST_NOT_FOUND"); return; }
        var w = Math.Max(1, host.Width);
        var h = Math.Max(1, host.Height);
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White); // 白底：让透明自绘开关与文字清晰可辨
            host.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
        }
        var p = Path.Combine(outDir, "diag_toggle_all.png");
        bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"TOGGLE_HOST {w}x{h}");
    }

    /// <summary>确定性渲染 compatHost 子树（设置浮层 5 个分组），绕开 CopyFromScreen 的屏幕抓帧缺陷。</summary>
    static void RenderCompatHost(Form f)
    {
        var host = (Control?)typeof(MainForm)
            .GetField("compatHost", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f);
        if (host is null) { Console.WriteLine("COMPAT 未找到"); return; }
        host.PerformLayout();
        using var bmp = new Bitmap(host.Width, host.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            host.DrawToBitmap(bmp, new Rectangle(0, 0, host.Width, host.Height));
        }
        bmp.Save(Path.Combine(outDir, "s8_settings_groups.png"), System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine($"COMPAT {host.Width}x{host.Height}");
    }
}
