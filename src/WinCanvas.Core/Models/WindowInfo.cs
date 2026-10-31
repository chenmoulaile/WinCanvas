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
    /// <summary>
    /// 窗口持久化标识：「进程名|类名」，与 <see cref="WindowState.Key"/> 同一规则。
    /// 不采用标题：浏览器 / 记事本等窗口标题随时变化，标题一变档案就匹配不到，
    /// 已保存的置顶 / 穿透 / 壁纸档案会全部失效；类名 + 进程名在同一应用内是稳定的。
    /// 分隔符 '|' 与转义符 '\' 在拼接前都做转义（<see cref="Encode"/>），
    /// 保证不同窗口不会碰撞出相同 Key。
    /// 已知取舍：同一进程 + 同一类名的多个窗口（如多个 Chrome 窗口）共享同一份档案。
    /// </summary>
    public string Key => $"{Encode(ProcessName)}|{Encode(ClassName)}";

    /// <summary>hwnd 句柄值哈希，与 <see cref="WindowState.HwndHash"/> 同一规则。</summary>
    public long HwndHash => Hwnd.ToInt64();

    /// <summary>
    /// 组合键成员转义：反斜杠写成两个反斜杠、竖线写成「反斜杠 + 竖线」，
    /// 使 '|' 在组合键中永远是分隔符、不会因成员内容产生歧义或碰撞。
    /// </summary>
    private static string Encode(string part) =>
        part.Replace("\\", "\\\\").Replace("|", "\\|");
}
