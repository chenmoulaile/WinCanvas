namespace WinCanvas.Core.Models;

/// <summary>
/// 枚举到的顶层窗口的运行期快照（不持久化，仅用于界面列表展示）。
/// </summary>
/// <param name="Hwnd">顶层窗口句柄。</param>
/// <param name="Title">窗口标题（GetWindowText）。</param>
/// <param name="ClassName">窗口类名（GetClassName）。</param>
/// <param name="ProcessName">所属进程名。</param>
/// <param name="ProcessId">所属进程 Id。</param>
public sealed record WindowInfo(
    IntPtr Hwnd,
    string Title,
    string ClassName,
    string ProcessName,
    int ProcessId)
{
    /// <summary>「进程名|标题」组合键，与 <see cref="WindowState.Key"/> 同一规则。</summary>
    public string Key => $"{ProcessName}|{Title}";

    /// <summary>hwnd 句柄值哈希，与 <see cref="WindowState.HwndHash"/> 同一规则。</summary>
    public long HwndHash => Hwnd.ToInt64();
}
