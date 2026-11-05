using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 全局热键表（默认值 / 补齐 / 文本化 / 校验）的纯逻辑用例。
///
/// 本可放在 App 层，但 HotkeyTable 是从 App.Services 移到 Core.Services 的：
/// 它不依赖任何 UI 类型，而「校验规则」是快捷键功能唯一真正会咬人的地方
/// （允许无修饰键的全局热键 = 抢掉用户在别的程序里的按键）。
/// 放在 Core 才能进现有测试工程 —— 迁移前 App 层是 0 覆盖的。
/// </summary>
public sealed class HotkeyTableTests
{
    // ---------- Normalize：配置补齐 ----------

    [Fact]
    public void Normalize_空配置_每个动作都补上默认绑定()
    {
        var result = HotkeyTable.Normalize(null);

        Assert.Equal(HotkeyTable.Catalog.Length, result.Count);
        Assert.All(result, b => Assert.NotEqual(0, b.VirtualKey));
        Assert.All(result, b => Assert.True(b.Enabled));
    }

    [Fact]
    public void Normalize_保留用户改过的值_只补缺失的()
    {
        var custom = new HotkeyBinding
        {
            Action = HotkeyAction.ToggleTopmost,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift,
            VirtualKey = 0x51, // Q
            Enabled = false,
        };

        var result = HotkeyTable.Normalize(new[] { custom });

        var topmost = Assert.Single(result, b => b.Action == HotkeyAction.ToggleTopmost);
        Assert.Same(custom, topmost); // 用户那条必须原样保留，不能被默认值覆盖
        Assert.False(topmost.Enabled);

        // 其余动作仍被补齐
        Assert.Equal(HotkeyTable.Catalog.Length, result.Count);
    }

    [Fact]
    public void Normalize_同一动作重复出现_取最后一条且不产生重复行()
    {
        // 手改配置可能出现重复项；界面按动作逐行渲染，重复会让同一动作出现两次
        var dupes = new[]
        {
            new HotkeyBinding { Action = HotkeyAction.RefreshWindowList, VirtualKey = 0x41 },
            new HotkeyBinding { Action = HotkeyAction.RefreshWindowList, VirtualKey = 0x42 },
        };

        var result = HotkeyTable.Normalize(dupes);
        var refreshes = result.Where(b => b.Action == HotkeyAction.RefreshWindowList).ToList();

        Assert.Single(refreshes);
        Assert.Equal(0x42, refreshes[0].VirtualKey);
    }

    // ---------- Format：人读文本 ----------

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54, "Ctrl+Alt+T")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x26, "Ctrl+Alt+↑")]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x70, "Ctrl+Alt+Shift+F1")]
    [InlineData(HotkeyModifiers.Win, 0x31, "Win+1")]
    public void Format_按固定顺序拼出组合键(HotkeyModifiers mods, int vk, string expected)
    {
        var binding = new HotkeyBinding { Modifiers = mods, VirtualKey = vk };
        Assert.Equal(expected, HotkeyTable.Format(binding));
    }

    [Fact]
    public void Format_未设置主键_显示未设置而不是空串()
    {
        // 空串会让界面看起来「已设置但看不见」，是最难归因的一类显示缺陷
        Assert.Equal("未设置", HotkeyTable.Format(new HotkeyBinding { VirtualKey = 0 }));
        Assert.Equal("未设置", HotkeyTable.Format(null));
    }

    [Fact]
    public void Format_未知键码_回落十六进制而不抛()
    {
        var text = HotkeyTable.Format(new HotkeyBinding
        {
            Modifiers = HotkeyModifiers.Control,
            VirtualKey = 0xF5,
        });

        Assert.Contains("0xF5", text);
    }

    // ---------- Validate：真正会咬人的规则 ----------

    [Fact]
    public void Validate_无修饰键_被拒绝()
    {
        // 全局热键抢在焦点窗口之前。允许单键 / 仅 Shift 做全局热键，
        // 等于让用户在其它软件里突然按不动那个键。
        var all = new List<HotkeyBinding>();
        var bad = new HotkeyBinding { Action = HotkeyAction.ToggleTopmost, Modifiers = HotkeyModifiers.None, VirtualKey = 0x41 };

        Assert.NotNull(HotkeyTable.Validate(bad, all));

        var shiftOnly = new HotkeyBinding
        {
            Action = HotkeyAction.ToggleTopmost,
            Modifiers = HotkeyModifiers.Shift,
            VirtualKey = 0x41,
        };
        Assert.NotNull(HotkeyTable.Validate(shiftOnly, all));
    }

    [Fact]
    public void Validate_主键为0_被拒绝()
    {
        var all = new List<HotkeyBinding>();
        var result = HotkeyTable.Validate(
            new HotkeyBinding { Modifiers = HotkeyModifiers.Control, VirtualKey = 0 }, all);

        Assert.NotNull(result);
    }

    [Fact]
    public void Validate_与其它行动作重复_报出对方名字()
    {
        var other = new HotkeyBinding
        {
            Action = HotkeyAction.ClearWallpaper,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            VirtualKey = 0x54,
            Enabled = true,
        };

        var all = new List<HotkeyBinding>
        {
            other,
            new HotkeyBinding
            {
                Action = HotkeyAction.ToggleTopmost,
                Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
                VirtualKey = 0x54,
            },
        };

        var reason = HotkeyTable.Validate(all[1], all);

        Assert.NotNull(reason);
        Assert.Contains("清除背景", reason); // 说清楚撞了谁，而不是只说「重复」
    }

    [Fact]
    public void Validate_已停用的行不视为冲突()
    {
        // 关掉的热键不会注册，因此不该拦住新绑定
        var disabled = new HotkeyBinding
        {
            Action = HotkeyAction.ClearWallpaper,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            VirtualKey = 0x54,
            Enabled = false,
        };

        var candidate = new HotkeyBinding
        {
            Action = HotkeyAction.ToggleTopmost,
            Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt,
            VirtualKey = 0x54,
        };

        Assert.Null(HotkeyTable.Validate(candidate, new[] { disabled, candidate }));
    }

    [Fact]
    public void Validate_默认表自身无冲突()
    {
        // 默认值一旦互相撞车，首次启动就有一批热键注册失败 —— 这是必须钉住的不变式
        var defaults = HotkeyTable.Normalize(null);
        var combos = defaults.Select(b => (b.Modifiers, b.VirtualKey)).ToList();

        Assert.Equal(defaults.Count, combos.Distinct().Count());
    }

    // ---------- 目录完整性 ----------

    [Fact]
    public void Catalog_覆盖所有动作且都有中文名与说明()
    {
        var catalogActions = HotkeyTable.Catalog.Select(c => c.Action).ToList();
        var allActions = Enum.GetValues<HotkeyAction>();

        // 新增枚举值忘了登记目录 → 界面上没有这一行，用户无法改它的热键
        Assert.Equal(allActions.Length, catalogActions.Count);
        Assert.All(allActions, a => Assert.Contains(a, catalogActions));

        Assert.All(HotkeyTable.Catalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        Assert.All(HotkeyTable.Catalog, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }

    [Fact]
    public void GetDefault_每个动作都有可用组合()
    {
        foreach (var (action, _, _) in HotkeyTable.Catalog)
        {
            var b = HotkeyTable.GetDefault(action);

            Assert.Equal(action, b.Action);
            Assert.NotEqual(0, b.VirtualKey);
            Assert.True(b.Enabled);

            // 默认值必须满足自家校验规则：至少含 Ctrl 或 Alt。
            // （原先这里写的是 Assert.NotNull(b.Modifiers) —— 枚举值类型恒非空，
            //  等于没断言，xUnit 分析器也直接报 xUnit2002。）
            Assert.True(b.Modifiers.HasFlag(HotkeyModifiers.Control) || b.Modifiers.HasFlag(HotkeyModifiers.Alt),
                $"{action} 的默认组合缺少 Ctrl/Alt，会抢掉其它程序的普通按键");

            // 默认值本身必须过自己的校验规则
            Assert.Null(HotkeyTable.Validate(b, new[] { b }));
        }
    }

    // ---------- 焦点窗口系列（v0.3.3）：默认绑定 / 旧配置补齐 / 步进钳制 ----------

    [Theory]
    [InlineData(HotkeyAction.FocusToggleTopmost, 0x54)]      // T
    [InlineData(HotkeyAction.FocusToggleClickThrough, 0x50)] // P
    [InlineData(HotkeyAction.FocusIncreaseOpacity, 0x26)]    // ↑
    [InlineData(HotkeyAction.FocusDecreaseOpacity, 0x28)]    // ↓
    [InlineData(HotkeyAction.FocusRestoreOpaque, 0x4F)]      // O
    [InlineData(HotkeyAction.FocusCycleCorner, 0x55)]        // U
    public void GetDefault_焦点窗口系列_默认Win加Alt与语义主键(HotkeyAction action, int expectedVk)
    {
        var binding = HotkeyTable.GetDefault(action);

        // 焦点系列统一 Win+Alt 前缀：与「选中窗口」系列的 Ctrl+Alt 从组合上区分开，
        // 且避开 Win+字母（开始菜单搜索）、Win+Shift+S（截图）等系统保留键
        Assert.Equal(HotkeyModifiers.Win | HotkeyModifiers.Alt, binding.Modifiers);
        Assert.Equal(expectedVk, binding.VirtualKey);

        // 修复口径的核心断言：默认开启 —— 用户预期「按了就有反应」，不许默认静默
        Assert.True(binding.Enabled);
    }

    [Fact]
    public void Normalize_旧版本配置没有焦点动作_补默认组合且保留旧条目()
    {
        // 升级场景：config.json 里只有 v0.3.2 的 11 条（无 Focus* 动作）。
        // 新动作必须按默认值补齐（否则新热键整体不可用，等于白修），
        // 旧条目必须原样保留（用户改过的键与开关不能被升级冲掉）。
        var oldConfig = HotkeyTable.Normalize(null)
            .Where(b => (int)b.Action <= 10)
            .ToList();

        var result = HotkeyTable.Normalize(oldConfig);

        Assert.Equal(HotkeyTable.Catalog.Length, result.Count);
        Assert.All(result.Where(b => (int)b.Action >= 11 && b.Action != HotkeyAction.NextWallpaper), b =>
        {
            Assert.Equal(HotkeyModifiers.Win | HotkeyModifiers.Alt, b.Modifiers);
            Assert.True(b.Enabled);
        });

        // NextWallpaper（壁纸库轮换工作包，值 18）属于「选中/全局」系列的 Ctrl+Alt 前缀，
        // 不在焦点系列的 Win+Alt 组里——单独钉住它的默认组合
        var next = Assert.Single(result, b => b.Action == HotkeyAction.NextWallpaper);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, next.Modifiers);
        Assert.True(next.Enabled);

        Assert.All(result.Where(b => (int)b.Action <= 10), b => Assert.Contains(b, oldConfig));
    }

    [Fact]
    public void Format_焦点窗口系列组合_按CtrlAltShiftWin固定顺序显示()
    {
        // Format 的修饰键顺序固定 Ctrl→Alt→Shift→Win：Win+Alt 显示为「Alt+Win」，
        // 与设置界面其余行书写一致（这里顺带钉住显示顺序，防止改键后两套写法并存）
        var binding = new HotkeyBinding
        {
            Action = HotkeyAction.FocusToggleTopmost,
            Modifiers = HotkeyModifiers.Win | HotkeyModifiers.Alt,
            VirtualKey = 0x54,
        };

        Assert.Equal("Alt+Win+T", HotkeyTable.Format(binding));
    }

    [Fact]
    public void Validate_接受Win加Alt组合_不被缺少Ctrl规则误杀()
    {
        // 焦点系列默认组合只带 Win+Alt：规则要求的是「至少 Ctrl 或 Alt 之一」，
        // Win+Alt 满足 —— 若校验把 Win 也算进「必须」，6 条新热键改键时全部被拒
        var candidate = new HotkeyBinding
        {
            Action = HotkeyAction.FocusToggleTopmost,
            Modifiers = HotkeyModifiers.Win | HotkeyModifiers.Alt,
            VirtualKey = 0x54,
        };

        Assert.Null(HotkeyTable.Validate(candidate, new[] { candidate }));
    }

    [Theory]
    [InlineData(200u, true, 210u)]  // 常规步进 +10
    [InlineData(200u, false, 190u)] // 常规步进 -10
    [InlineData(250u, true, 255u)]  // 250+10 → 钳到上限 255
    [InlineData(5u, false, 0u)]     // 5-10 → 钳到下限 0
    [InlineData(255u, true, 255u)]  // 已达上限再按 → 保持 255 不回绕
    [InlineData(0u, false, 0u)]     // 已达下限再按 → 保持 0 不回绕
    public void NextFocusAlpha_步进10并钳制到0到255(byte current, bool increase, byte expected)
    {
        // 回绕是这里最危险的缺陷：byte 溢出让 255+10 变成 9，
        // 表现为「按多了透明度突然跳回几乎全透明」
        Assert.Equal(expected, HotkeyTable.NextFocusAlpha(current, increase));
    }

    [Fact]
    public void FocusOpacityStep_常量为10_与README及描述文案同口径()
    {
        // 步进是「焦点窗口透明度 ±10」这个产品口径的唯一事实来源，
        // 被执行路径与两处文案共用 —— 改这里必须同步改文案
        Assert.Equal(10, HotkeyTable.FocusOpacityStep);
    }

    [Fact]
    public void 默认表_新旧两个系列组合互不冲突()
    {
        // 旧系列（Ctrl+Alt+）与新系列（Win+Alt+）若发生组合重叠，
        // 重叠的那条必有一个注册失败（同一组合只能被注册一次）
        var defaults = HotkeyTable.Normalize(null);

        // 目录条目数 = 动作枚举数；隐私窗口工作包追加 FocusTogglePrivacy(17)、
        // 壁纸轮换工作包追加 NextWallpaper(18) → 19 条
        Assert.Equal(19, defaults.Count);
        Assert.Equal(defaults.Count, defaults.Select(b => (b.Modifiers, b.VirtualKey)).Distinct().Count());
    }

    // ---------- 下一张壁纸（壁纸库轮换工作包）：默认绑定 / 旧配置补齐 ----------

    [Fact]
    public void GetDefault_下一张壁纸_默认CtrlAltB且默认开启()
    {
        var binding = HotkeyTable.GetDefault(HotkeyAction.NextWallpaper);

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Modifiers);
        Assert.Equal(0x42, binding.VirtualKey); // B
        Assert.True(binding.Enabled);
        Assert.Equal("Ctrl+Alt+B", HotkeyTable.Format(binding));
    }

    [Fact]
    public void Normalize_旧版本配置没有下一张壁纸_补默认组合且保留旧条目()
    {
        // 升级场景：config.json 里只有上一版（无 NextWallpaper 动作）的绑定表。
        // 新动作按默认值补齐；旧条目（用户改过的键与开关）必须原样保留。
        var oldConfig = HotkeyTable.Normalize(null)
            .Where(b => b.Action != HotkeyAction.NextWallpaper)
            .ToList();

        var result = HotkeyTable.Normalize(oldConfig);

        Assert.Equal(HotkeyTable.Catalog.Length, result.Count);
        var next = Assert.Single(result, b => b.Action == HotkeyAction.NextWallpaper);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, next.Modifiers);
        Assert.Equal(0x42, next.VirtualKey);
        Assert.All(oldConfig, b => Assert.Contains(b, result));
    }
}
