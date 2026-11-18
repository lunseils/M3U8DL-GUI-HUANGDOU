using System.Windows.Forms;

namespace M3U8DL;

/// <summary>
/// 尺寸变化时整块重绘的 Panel。
/// <para>
/// 用于承载自绘内容（虚线拖放框、空态边框）：<see cref="Control.ResizeRedraw"/> 默认 false，
/// WM_SIZE 之后只让新增区域失效，旧位置的描边像素会残留 —— 拉高窗口就能看到
/// 「虚线框裂成两截」。该属性是 protected，只能在派生类构造函数里打开。
/// </para>
/// </summary>
internal sealed class RedrawPanel : Panel
{
    public RedrawPanel() => ResizeRedraw = true;
}
