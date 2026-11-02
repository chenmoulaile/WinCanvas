using System;

using WinCanvas.Core.Models;

namespace WinCanvas.Core.Services;

/// <summary>
/// 全局热键的默认绑定表与人读文本格式化（v0.2.0 新增）。
///
/// 默认组合的统一取位：<c>Ctrl+Alt+X</c>。理由是这套前缀在 Windows 上
/// 几乎不与系统键（Win+…）、应用内键（Ctrl+…）或常见输入法冲突；
/// 只用了字母与方向键，避开 F1~F12（多媒体键与笔记本功能层常占用）。
///
/// 每条默认值都是「建议」而不是「保证」：真正注册时若被别的程序占了，
/// <see cref="GlobalHotkeyService"/> 会把该条标记为冲突并在界面上说明，
/// 用户可以改键，而不是启动就失败。
/// </summary>
public static class HotkeyTable
{
    /// <summary>
    /// 功能动作的人读名称（设置界面左列）。
    /// 顺序即界面展示顺序，与 <see cref="HotkeyAction"/> 的枚举值顺序一致。
    /// </summary>
    public static readonly (HotkeyAction Action, string Label, string Description)[] Catalog =
    {
        (HotkeyAction.ToggleTopmost, "置顶切换", "对当前选中的窗口切换置顶"),
        (HotkeyAction.IncreaseOpacity, "提高透明度", "选中窗口不透明度 +32（上限 255）"),
        (HotkeyAction.DecreaseOpacity, "降低透明度", "选中窗口不透明度 -32（下限 0）"),
        (HotkeyAction.ToggleClickThrough, "鼠标穿透切换", "选中窗口是否忽略鼠标事件"),
        (HotkeyAction.CycleCornerPreference, "圆角循环", "无 → 大圆角 → 小圆角 → 无"),
        (HotkeyAction.RefreshWindowList, "刷新窗口列表", "重新枚举全部可见顶层窗口"),
        (HotkeyAction.ToggleWallpaperPause, "壁纸渲染暂停", "暂停 / 恢复全部背景壁纸的渲染"),
        (HotkeyAction.ClearWallpaper, "清除背景", "移除选中窗口的壁纸背景"),
        (HotkeyAction.ShowHideMainWindow, "显示 / 隐藏主界面", "配合托盘：窗口收进托盘后叫回来"),
        (HotkeyAction.ToggleWinTipsIndicator, "NumLock 提示开关", "开 / 关低级键盘钩子与浮窗提示"),
        (HotkeyAction.ToggleWallpaperAudio, "壁纸声音开关", "开 / 关壁纸发声（默认静音）"),
    };

    /// <summary>
    /// 默认快捷键。Ctrl+Alt+ 前缀 + 一个语义相关的主键。
    /// 方向键用 VK_UP(0x26)/VK_DOWN(0x28)，透明度增减与「上下」直觉对齐。
    /// </summary>
    private static readonly Dictionary<HotkeyAction, (HotkeyModifiers Mods, int Vk)> Defaults = new()
    {
        [HotkeyAction.ToggleTopmost] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54),            // T
        [HotkeyAction.IncreaseOpacity] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x26),          // ↑
        [HotkeyAction.DecreaseOpacity] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x28),          // ↓
        [HotkeyAction.ToggleClickThrough] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4B),       // K
        [HotkeyAction.CycleCornerPreference] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x52),    // R
        [HotkeyAction.RefreshWindowList] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x46),        // F
        [HotkeyAction.ToggleWallpaperPause] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x57),     // W
        [HotkeyAction.ClearWallpaper] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x58),           // X
        [HotkeyAction.ShowHideMainWindow] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x43),       // C
        [HotkeyAction.ToggleWinTipsIndicator] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4E),   // N
        [HotkeyAction.ToggleWallpaperAudio] = (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4D),     // M（Mute/Music）
    };

    /// <summary>取某动作的默认绑定。</summary>
    public static HotkeyBinding GetDefault(HotkeyAction action)
    {
        var (mods, vk) = Defaults.TryGetValue(action, out var d)
            ? d
            : (HotkeyModifiers.Control | HotkeyModifiers.Alt, 0);

        return new HotkeyBinding
        {
            Action = action,
            Modifiers = mods,
            VirtualKey = vk,
            Enabled = true,
        };
    }

    /// <summary>
    /// 把配置里的绑定表补齐成「每个动作都有一条」：
    /// 配置里缺的动作补默认值（所以用户删掉一条只是回到默认，不是关掉它），
    /// 多出来的（旧版本遗留的动作名）原样保留，不做删除，避免升级丢配置。
    /// </summary>
    public static List<HotkeyBinding> Normalize(IList<HotkeyBinding>? stored)
    {
        var byAction = new Dictionary<HotkeyAction, HotkeyBinding>();

        if (stored != null)
        {
            foreach (var binding in stored)
            {
                byAction[binding.Action] = binding;
            }
        }

        var result = new List<HotkeyBinding>(Catalog.Length);
        foreach (var (action, _, _) in Catalog)
        {
            result.Add(byAction.TryGetValue(action, out var existing) ? existing : GetDefault(action));
        }

        return result;
    }

    /// <summary>
    /// 组合键的人读文本，如 <c>Ctrl+Alt+T</c>；未设置主键时返回「未设置」。
    /// 修饰键顺序固定 Ctrl→Alt→Shift→Win，与 Windows 设置里的书写习惯一致。
    /// </summary>
    public static string Format(HotkeyBinding? binding)
    {
        if (binding is null || binding.VirtualKey == 0)
        {
            return "未设置";
        }

        var parts = new List<string>(5);
        var mods = binding.Modifiers;

        if (mods.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (mods.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (mods.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (mods.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(DescribeKey(binding.VirtualKey));
        return string.Join("+", parts);
    }

    /// <summary>
    /// 虚拟键码 → 人读主键名。
    /// 只覆盖本项目默认值会用到的区间 + 常用功能键；其余回落成十六进制码，
    /// 宁可显示「键 0x5F」也不要显示空白让人以为没设置上。
    /// </summary>
    public static string DescribeKey(int virtualKey)
    {
        return virtualKey switch
        {
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),                 // A~Z
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),                 // 0~9
            >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x6F),                     // F1~F24
            0x0D => "Enter",
            0x20 => "Space",
            0x09 => "Tab",
            0x1B => "Esc",
            0x21 => "PgUp",
            0x22 => "PgDn",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "←",
            0x26 => "↑",
            0x27 => "→",
            0x28 => "↓",
            0x2D => "Insert",
            0x2E => "Delete",
            0x60 => "Num0",
            >= 0x61 and <= 0x69 => "Num" + (virtualKey - 0x60),
            0x08 => "Backspace",
            0x0C or 0x13 => "Pause",
            0x14 => "CapsLock",
            0x2C => "PrintScreen",
            0x2A or 0x29 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xDE => "'",
            0xBF => "/",
            0xBC => ",",
            0xBE => ".",
            _ => $"键 0x{virtualKey:X2}",
        };
    }

    /// <summary>
    /// 校验一条绑定是否可用。返回 null = 通过；否则为可直接显示给用户的原因。
    ///
    /// 为什么拦「无修饰键」：全局热键会【抢在任意程序之前】吃掉那组按键。
    /// 允许 F1 或单键做全局热键，等于让用户在别的软件里突然按不动那个键，
    /// 而且这种冲突极难归因。所以至少要求 Ctrl 或 Alt 之一。
    /// </summary>
    public static string? Validate(HotkeyBinding binding, IList<HotkeyBinding> all)
    {
        if (binding.VirtualKey == 0)
        {
            return "未按下主键。";
        }

        var mods = binding.Modifiers & ~HotkeyModifiers.Shift;
        if (mods == HotkeyModifiers.None)
        {
            return "全局热键必须至少包含 Ctrl 或 Alt，否则会抢掉其它程序的普通按键。";
        }

        // Shift 参与区分（Ctrl+Shift+A 与 Ctrl+A 是两个合法组合）
        string self = Format(binding);
        foreach (var other in all)
        {
            if (ReferenceEquals(other, binding) || !other.Enabled || other.VirtualKey == 0)
            {
                continue;
            }

            if (other.Modifiers == binding.Modifiers && other.VirtualKey == binding.VirtualKey)
            {
                return $"与「{LabelOf(other.Action)}」使用了同一个组合（{self}）。";
            }
        }

        return null;
    }

    /// <summary>动作的人读名称（找不到时回落成枚举名，不让界面显示空串）。</summary>
    public static string LabelOf(HotkeyAction action)
    {
        foreach (var entry in Catalog)
        {
            if (entry.Action == action)
            {
                return entry.Label;
            }
        }

        return action.ToString();
    }
}
