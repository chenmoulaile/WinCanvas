namespace WinCanvas.Core.Models;

/// <summary>
/// 单个被管理窗口的持久化状态。
/// 窗口的唯一标识 = 「进程名 + 类名」的组合键（<see cref="Key"/>，成员转义防碰撞），
/// 辅以 hwnd 句柄值的哈希（<see cref="HwndHash"/>）用于运行期快速定位。
/// </summary>
public sealed class WindowState
{
    /// <summary>窗口唯一组合键："进程名|类名"（成员转义后拼接，见 <see cref="WindowInfo.Key"/>）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// hwnd 句柄值的哈希（当前实现取句柄的整数值）。
    /// 注意：hwnd 在进程重启后可能变化，持久化恢复时以 <see cref="Key"/> 为主键，
    /// <see cref="HwndHash"/> 仅作运行期会话内的辅助匹配。
    /// </summary>
    public long HwndHash { get; set; }

    /// <summary>窗口所属进程名（如 "notepad"）。</summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>窗口标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>窗口所属进程 Id。</summary>
    public int ProcessId { get; set; }

    /// <summary>当前是否置顶（WS_EX_TOPMOST）。</summary>
    public bool IsTopmost { get; set; }

    /// <summary>窗口透明度，0（全透明）~ 255（不透明），对应 SetLayeredWindowAttributes 的 bAlpha。</summary>
    public byte Alpha { get; set; } = 255;

    /// <summary>当前是否鼠标穿透（WS_EX_TRANSPARENT）。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>圆角偏好（对应 DWMWA_WINDOW_CORNER_PREFERENCE）。</summary>
    public CornerPreference Corner { get; set; } = CornerPreference.Default;

    /// <summary>背景类型：图片 / 视频 / pkg 打包资源。</summary>
    public BackgroundType Background { get; set; } = BackgroundType.None;

    /// <summary>背景资源的路径（图片/视频文件路径或 pkg 包路径），无背景时为 null。</summary>
    public string? BackgroundPath { get; set; }

    /// <summary>
    /// 是否开启「窗口隐私模式」（隐私窗口工作包新增）：开启后本窗口失去前台时
    /// 自动把 alpha 降到遮蔽值、重新聚焦时恢复到 <see cref="Alpha"/>（档案值，非硬编码 255）。
    /// 默认 false：遮蔽会改变窗口外观，属于用户逐窗口显式授权的行为了解成本，不可默认替用户开启。
    /// </summary>
    public bool PrivacyEnabled { get; set; }

    // ===== 窗口几何（窗口位置记忆工作包新增） =====
    // 坐标与尺寸均为物理像素（与 Win32 GetWindowRect / SetWindowPos 同一口径，见 WindowBounds）。
    // 只有 HasBounds=true 时 X/Y/Width/Height 才有意义：几何只在用户点「记住当前位置」
    // 或应用真退出时捕获一次，不捕获就恢复等于替用户乱动窗口。

    /// <summary>是否已捕获过窗口几何。false 时下列四个字段不参与恢复（保留默认值）。</summary>
    public bool HasBounds { get; set; }

    /// <summary>档案记录的窗口左上角 X（物理像素）。</summary>
    public int X { get; set; }

    /// <summary>档案记录的窗口左上角 Y（物理像素）。</summary>
    public int Y { get; set; }

    /// <summary>档案记录的窗口宽度（物理像素）。</summary>
    public int Width { get; set; }

    /// <summary>档案记录的窗口高度（物理像素）。</summary>
    public int Height { get; set; }

    /// <summary>把几何字段组装成 <see cref="WindowBounds"/>（口径单一来源，避免散落的字段拼装）。</summary>
    public WindowBounds ToBounds() => new(X, Y, Width, Height);

    /// <summary>用 <see cref="WindowBounds"/> 覆盖几何字段并置 HasBounds=true（捕获路径统一走这里）。</summary>
    public void SetBounds(WindowBounds bounds)
    {
        HasBounds = true;
        X = bounds.X;
        Y = bounds.Y;
        Width = bounds.Width;
        Height = bounds.Height;
    }
}
