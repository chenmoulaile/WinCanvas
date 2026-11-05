using System.Text;
using System.Text.Json;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 隐私窗口工作包的【纯逻辑】验证：遮蔽决策规则（<see cref="PrivacyMaskRules"/>）与
/// 配置持久化字段（AppConfig.PrivacyOverlayOnDeactivated / WindowState.PrivacyEnabled）。
/// 不触碰 Win32；真实窗口路径见 PrivacyRealWindowTests（RequiresWindowsSession）。
/// </summary>
public sealed class PrivacyMaskRulesTests
{
    // ---------- ComputeMaskAlpha：遮蔽目标 alpha = min(30, 档案值) ----------

    [Fact]
    public void ComputeMaskAlpha_档案完全不透明_取默认遮蔽值()
    {
        Assert.Equal(PrivacyMaskRules.MaskAlphaDefault, PrivacyMaskRules.ComputeMaskAlpha(255));
        Assert.Equal(PrivacyMaskRules.MaskAlphaDefault, PrivacyMaskRules.ComputeMaskAlpha(200));
    }

    [Fact]
    public void ComputeMaskAlpha_档案比遮蔽值更透明_取档案值绝不放大可见度()
    {
        // 协同硬规则：用户已把窗口调到 10/255，遮蔽不能把它变「亮」回 30
        Assert.Equal(10, PrivacyMaskRules.ComputeMaskAlpha(10));
        Assert.Equal(0, PrivacyMaskRules.ComputeMaskAlpha(0));
    }

    [Fact]
    public void MaskAlphaDefault_遮蔽值足够暗_内容不可辨认()
    {
        // 30/255 ≈ 12%：防肩窥的下限语义（见常量注释）。钉住常量防止将来无意调亮。
        Assert.True(PrivacyMaskRules.MaskAlphaDefault <= 30, "遮蔽值调高会让失焦内容可辨认，须同步重审 ADR。");
    }

    // ---------- DecideTransitions：前台切换的迁移决策 ----------

    [Fact]
    public void DecideTransitions_失焦未遮蔽_产生遮蔽迁移目标取遮蔽值()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0x1111, ProfileAlpha: 255, IsMasked: false),
        };

        var transitions = PrivacyMaskRules.DecideTransitions((IntPtr)0x9999, watched);

        var t = Assert.Single(transitions);
        Assert.Equal((IntPtr)0x1111, t.Hwnd);
        Assert.Equal(PrivacyMaskRules.MaskAlphaDefault, t.TargetAlpha);
        Assert.True(t.Masked);
    }

    [Fact]
    public void DecideTransitions_重新聚焦已遮蔽_产生恢复迁移目标取档案值而非硬编码255()
    {
        // 协同硬规则（ADR 决策五）：恢复 = 档案 alpha（用户设过 100 就恢复 100）
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0x2222, ProfileAlpha: 100, IsMasked: true),
        };

        var transitions = PrivacyMaskRules.DecideTransitions((IntPtr)0x2222, watched);

        var t = Assert.Single(transitions);
        Assert.Equal((IntPtr)0x2222, t.Hwnd);
        Assert.Equal(100, t.TargetAlpha);
        Assert.False(t.Masked);
    }

    [Fact]
    public void DecideTransitions_已处于目标态_不产生迁移()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0x3333, ProfileAlpha: 255, IsMasked: false), // 就是前台：无需动作
            new((IntPtr)0x4444, ProfileAlpha: 255, IsMasked: true),  // 非前台且已遮蔽：无需重复
        };

        var transitions = PrivacyMaskRules.DecideTransitions((IntPtr)0x3333, watched);

        Assert.Empty(transitions);
    }

    [Fact]
    public void DecideTransitions_多窗口混合_各自独立决策()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0x11, ProfileAlpha: 255, IsMasked: true),   // 前台回归 → 恢复
            new((IntPtr)0x22, ProfileAlpha: 40, IsMasked: false),   // 失焦 → 遮蔽（档案低于遮蔽值，取档案）
            new((IntPtr)0x33, ProfileAlpha: 255, IsMasked: false),  // 失焦 → 遮蔽
        };

        var transitions = PrivacyMaskRules.DecideTransitions((IntPtr)0x11, watched);

        Assert.Equal(3, transitions.Count);
        Assert.Contains(transitions, t => t.Hwnd == (IntPtr)0x11 && t.TargetAlpha == 255 && !t.Masked); // 恢复=档案值
        // 0x22 档案 40 > 遮蔽值 30 → 取遮蔽值 30（「档案低于遮蔽值取档案」由 ComputeMaskAlpha 专项用例钉住）
        Assert.Contains(transitions, t => t.Hwnd == (IntPtr)0x22 && t.TargetAlpha == PrivacyMaskRules.MaskAlphaDefault && t.Masked);
        Assert.Contains(transitions, t => t.Hwnd == (IntPtr)0x33 && t.TargetAlpha == PrivacyMaskRules.MaskAlphaDefault && t.Masked);
    }

    // ---------- DecideInitialTransitions：初始一次性遮蔽 ----------

    [Fact]
    public void DecideInitialTransitions_全部未遮蔽_全部遮蔽()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0xA1, ProfileAlpha: 255, IsMasked: false),
            new((IntPtr)0xA2, ProfileAlpha: 15, IsMasked: false),
        };

        var transitions = PrivacyMaskRules.DecideInitialTransitions(watched);

        Assert.Equal(2, transitions.Count);
        Assert.All(transitions, t => Assert.True(t.Masked));
        Assert.Contains(transitions, t => t.TargetAlpha == PrivacyMaskRules.MaskAlphaDefault);
        Assert.Contains(transitions, t => t.TargetAlpha == 15);
    }

    [Fact]
    public void DecideInitialTransitions_已遮蔽_跳过()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>
        {
            new((IntPtr)0xB1, ProfileAlpha: 255, IsMasked: true),
        };

        Assert.Empty(PrivacyMaskRules.DecideInitialTransitions(watched));
    }

    [Fact]
    public void DecideInitialTransitions_空清单_空迁移()
    {
        Assert.Empty(PrivacyMaskRules.DecideInitialTransitions(Array.Empty<PrivacyMaskRules.PrivacyWatchedWindow>()));
    }
}

/// <summary>
/// 隐私窗口的配置持久化往返：新字段经 ConfigurationService 落盘再读回必须原样恢复。
/// 用临时目录注入文件路径，不触碰真实 %APPDATA%（与 ConfigurationServiceTests 同约定）。
/// </summary>
public sealed class PrivacyConfigPersistenceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "WinCanvas.Core.Tests", Guid.NewGuid().ToString("N"));

    private readonly string _configPath;

    public PrivacyConfigPersistenceTests()
    {
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task 新字段_落盘再读回_原样恢复()
    {
        var service = new ConfigurationService(_configPath, debounceDelay: TimeSpan.FromMilliseconds(120));
        var written = new AppConfig
        {
            PrivacyOverlayOnDeactivated = false, // 显式非默认值：证明写读往返而非默认值巧合
            Windows =
            {
                new WindowState { Key = "notepad|Notepad::privacy", PrivacyEnabled = true, Alpha = 128 },
            },
        };

        service.Update(written);
        service.Flush();
        service.Dispose();

        var reader = new ConfigurationService(_configPath, debounceDelay: TimeSpan.FromMilliseconds(120));
        var loaded = await reader.LoadAsync();
        reader.Dispose();

        Assert.False(loaded.PrivacyOverlayOnDeactivated);
        var state = Assert.Single(loaded.Windows);
        Assert.True(state.PrivacyEnabled);
        Assert.Equal(128, state.Alpha);
    }

    [Fact]
    public async Task 旧版本配置_无新字段_读回默认值不报错()
    {
        // 老用户的 config.json（v0.4.0 前格式）：没有 PrivacyOverlayOnDeactivated / PrivacyEnabled。
        // System.Text.Json 反序列化缺失成员保持属性默认值——必须继续可用，不允许升级破坏。
        const string legacyJson = /* lang=json */ """
            {
              "Version": 2,
              "AutoRestoreOnStartup": true,
              "Windows": [ { "Key": "old|App::legacy", "HwndHash": 7, "Alpha": 200 } ]
            }
            """;
        await File.WriteAllTextAsync(_configPath, legacyJson, new UTF8Encoding(false));

        var service = new ConfigurationService(_configPath, debounceDelay: TimeSpan.FromMilliseconds(120));
        var loaded = await service.LoadAsync();

        Assert.True(loaded.PrivacyOverlayOnDeactivated); // 主窗口遮罩默认开启
        var state = Assert.Single(loaded.Windows);
        Assert.False(state.PrivacyEnabled); // 单窗口隐私默认关闭（须用户显式授权）
        Assert.Equal(200, state.Alpha);
        service.Dispose();
    }

    [Fact]
    public void 新字段_序列化输出_包含约定键名()
    {
        // 钉住 JSON 键名：属性名即键名（大小写策略由 ConfigurationService 统一），
        // 改名 = 旧配置静默丢字段，必须显式过测试这一关。
        var json = JsonSerializer.Serialize(new AppConfig { PrivacyOverlayOnDeactivated = true });
        Assert.Contains("PrivacyOverlayOnDeactivated", json);

        var stateJson = JsonSerializer.Serialize(new WindowState { PrivacyEnabled = true });
        Assert.Contains("PrivacyEnabled", stateJson);
    }
}

/// <summary>隐私热键动作的默认绑定与目录（配合既有 HotkeyTableTests 的口径）。</summary>
public sealed class PrivacyHotkeyTableTests
{
    [Fact]
    public void 热键目录_包含隐私模式切换_默认Win_Alt_H()
    {
        var binding = HotkeyTable.GetDefault(HotkeyAction.FocusTogglePrivacy);

        Assert.Equal(HotkeyModifiers.Win | HotkeyModifiers.Alt, binding.Modifiers);
        Assert.Equal(0x48, binding.VirtualKey); // H
        Assert.True(binding.Enabled);
        Assert.Equal("隐私模式切换（焦点窗口）", HotkeyTable.LabelOf(HotkeyAction.FocusTogglePrivacy));
    }

    [Fact]
    public void Normalize_缺隐私条目的配置_自动补默认()
    {
        var stored = new List<HotkeyBinding>
        {
            new() { Action = HotkeyAction.ToggleTopmost, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x54 },
        };

        var normalized = HotkeyTable.Normalize(stored);

        Assert.Contains(normalized, b => b.Action == HotkeyAction.FocusTogglePrivacy
            && b.Modifiers == (HotkeyModifiers.Win | HotkeyModifiers.Alt)
            && b.VirtualKey == 0x48);
    }
}
