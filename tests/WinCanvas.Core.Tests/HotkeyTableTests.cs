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
        Assert.All(defaults, b => Assert.NotNull(b.Modifiers));
        Assert.All(defaults, b => Assert.NotEqual(HotkeyAction.ShowHideMainWindow, default(HotkeyAction)));
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

            // 默认值本身必须过自己的校验规则
            Assert.Null(HotkeyTable.Validate(b, new[] { b }));
        }
    }
}
