using System.Windows.Forms;

namespace M3U8DL;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 高 DPI 模式由 csproj 的 ApplicationHighDpiMode 经 Initialize 统一应用；
        // 此处再调 SetHighDpiMode 属于重复设置（且必须在 Initialize 之前生效）。
        // GBK 等传统代码页由 Engine 的静态构造注册 —— N_m3u8DL-RE 的中文日志必须按 GBK 解码，
        // 而 Engine 在 MainForm 构造时就会实例化，早于任何下载进程启动。
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
