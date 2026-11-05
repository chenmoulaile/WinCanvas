using System;

namespace WinCanvas.Core.Models;

/// <summary>
/// 全局热键可绑定的功能动作。
///
/// 设计口径：一个动作 = 一条可独立配置的快捷键，枚举值即持久化标识
/// （<see cref="HotkeyBinding.Action"/> 存枚举名，改名字会让旧配置失效，
/// 所以新增动作只能追加，不要重排或删除）。
/// </summary>
public enum HotkeyAction
{
    /// <summary>切换当前选中窗口的置顶状态。</summary>
    ToggleTopmost = 0,

    /// <summary>提高当前选中窗口的不透明度（alpha +32，上限 255）。</summary>
    IncreaseOpacity = 1,

    /// <summary>降低当前选中窗口的不透明度（alpha -32，下限 0）。</summary>
    DecreaseOpacity = 2,

    /// <summary>切换当前选中窗口的鼠标穿透。</summary>
    ToggleClickThrough = 3,

    /// <summary>循环当前选中窗口的圆角档位（无 → 大 → 小 → 无）。</summary>
    CycleCornerPreference = 4,

    /// <summary>重新枚举窗口列表。</summary>
    RefreshWindowList = 5,

    /// <summary>暂停 / 恢复全部壁纸渲染（省电、让出 GPU）。</summary>
    ToggleWallpaperPause = 6,

    /// <summary>清除当前选中窗口的背景壁纸。</summary>
    ClearWallpaper = 7,

    /// <summary>显示 / 隐藏主窗口（配合系统托盘：窗口收进托盘后靠它叫回来）。</summary>
    ShowHideMainWindow = 8,

    /// <summary>开 / 关 WinTips NumLock 提示（即低级键盘钩子的启停）。</summary>
    ToggleWinTipsIndicator = 9,

    /// <summary>
    /// 开 / 关壁纸声音（v0.3.0 新增）。
    /// 追加在枚举末尾：值即持久化标识，插入或重排会让旧配置的绑定错位。
    /// </summary>
    ToggleWallpaperAudio = 10,

    // ---- 焦点窗口系列（v0.3.3 新增，修复「焦点窗口按快捷键无反应」）----
    // 与上面 0~10 的根本差别在【作用对象】：0~10 作用于主界面列表中「已选中的窗口」，
    // 本组作用于 GetForegroundWindow()（当前焦点窗口），因此任何窗口获得焦点时按键都生效。
    // 依旧只能追加：枚举值是 config.json 里的持久化标识，插入或重排会让旧配置错位。

    /// <summary>切换当前焦点窗口（前台窗口）的置顶状态。</summary>
    FocusToggleTopmost = 11,

    /// <summary>切换当前焦点窗口的鼠标穿透。</summary>
    FocusToggleClickThrough = 12,

    /// <summary>当前焦点窗口不透明度 +10（上限 255）。</summary>
    FocusIncreaseOpacity = 13,

    /// <summary>当前焦点窗口不透明度 -10（下限 0）。</summary>
    FocusDecreaseOpacity = 14,

    /// <summary>当前焦点窗口恢复完全不透明（alpha = 255）。</summary>
    FocusRestoreOpaque = 15,

    /// <summary>循环当前焦点窗口的圆角档位（无 → 大 → 小 → 无）。</summary>
    FocusCycleCorner = 16,

    /// <summary>
    /// 切换当前焦点窗口的「窗口隐私模式」（隐私窗口工作包新增）。
    /// 开启后该窗口失焦自动遮蔽内容、聚焦自动恢复；只能追加在枚举末尾
    /// （枚举值是 config.json 里的持久化标识，插入或重排会让旧配置错位）。
    /// </summary>
    FocusTogglePrivacy = 17,

    /// <summary>
    /// 「下一张壁纸」（壁纸库轮换工作包新增）：立即把下一条可用库条目挂到
    /// 当前所有已挂背景的窗口。只能追加在枚举末尾
    /// （枚举值是 config.json 里的持久化标识，插入或重排会让旧配置错位）。
    /// </summary>
    NextWallpaper = 18,
}

/// <summary>
/// 全局热键的修饰键组合。数值直接取 Win32 <c>RegisterHotKey</c> 的
/// <c>MOD_ALT / MOD_CONTROL / MOD_SHIFT / MOD_WIN</c>，无需再转换。
/// </summary>
[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// 一条全局热键绑定（持久化进 config.json 的 Hotkeys 数组）。
/// </summary>
public sealed class HotkeyBinding
{
    /// <summary>绑定的功能动作。</summary>
    public HotkeyAction Action { get; set; }

    /// <summary>修饰键组合。至少要有 Ctrl 或 Alt 之一，否则极易与系统/应用内快捷键撞车。</summary>
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    /// <summary>
    /// 主键的 Win32 虚拟键码（与 <c>Windows.System.VirtualKey</c> 数值同构，可直接互转）。
    /// 0 = 未设置。
    /// </summary>
    public int VirtualKey { get; set; }

    /// <summary>本条绑定是否生效（关闭后热键被注销，动作仍可通过界面触发）。</summary>
    public bool Enabled { get; set; } = true;
}
