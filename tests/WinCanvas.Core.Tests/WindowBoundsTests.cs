using System.Text.Json;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 窗口位置记忆工作包的纯逻辑用例：
/// ① 「屏幕外矩形判定」 <see cref="WindowBounds.IsOnAnyMonitor"/> —— 恢复管线据此
///    跳过落在全部显示器之外的档案矩形（改分辨率 / 拔屏后不把窗口丢到屏幕外）；
/// ② 档案矩形的序列化往返 —— 几何字段经 ConfigurationService 真实落盘再读回必须无损
///    （恢复管线读的就是落盘后的档案，字段名或类型一变这里立刻红）。
/// 全部不触碰真实窗口（屏幕外判定是纯函数），任意环境可跑。
/// </summary>
public sealed class WindowBoundsTests
{
    private static readonly WindowBounds PrimaryMonitor = new(0, 0, 1920, 1080);
    private static readonly WindowBounds SecondaryMonitor = new(1920, 0, 2560, 1440);

    // ---------- IsOnAnyMonitor：屏幕外矩形判定 ----------

    [Fact]
    public void 完全在主显示器内_判定为可见()
    {
        var rect = new WindowBounds(100, 100, 800, 600);
        Assert.True(WindowBounds.IsOnAnyMonitor(rect, new[] { PrimaryMonitor }));
    }

    [Fact]
    public void 与副显示器相交_判定为可见()
    {
        // 跨在主 / 副显示器边界上的窗口：只要与任何一块显示器相交就允许恢复
        var rect = new WindowBounds(1800, 100, 400, 300); // 左边缘 1800 < 1920，右边缘 2200 > 1920
        Assert.True(WindowBounds.IsOnAnyMonitor(rect, new[] { PrimaryMonitor, SecondaryMonitor }));
    }

    [Fact]
    public void 完全落在全部显示器之外_判定为屏幕外()
    {
        // 拔掉副显示器后，原 (-2800, 500) 的档案矩形不再与任何显示器相交
        var rect = new WindowBounds(-2800, 500, 800, 600);
        Assert.False(WindowBounds.IsOnAnyMonitor(rect, new[] { PrimaryMonitor }));
    }

    [Fact]
    public void 仅边缘贴住_零像素交叠_判定为屏幕外()
    {
        // 矩形右边缘恰好贴住显示器左边缘（bounds.X + Width == monitor.X）：
        // 区间开闭判定为不相交——贴边窗口零像素可见，恢复它没有意义
        var rect = new WindowBounds(-800, 100, 800, 600); // 右边缘 = 0 = 主显示器左边缘
        Assert.False(WindowBounds.IsOnAnyMonitor(rect, new[] { PrimaryMonitor }));
    }

    [Fact]
    public void 显示器列表为空_恒判定为屏幕外()
    {
        // 显示器枚举失败 / 无显示器时宁可跳过恢复也不能乱放
        var rect = new WindowBounds(100, 100, 800, 600);
        Assert.False(WindowBounds.IsOnAnyMonitor(rect, Array.Empty<WindowBounds>()));
    }

    [Fact]
    public void 档案矩形尺寸非法_恒判定为屏幕外()
    {
        // 宽或高非正是档案损坏的特征值（含最小化 -32000 矩形被误存的兜底防线）
        Assert.False(WindowBounds.IsOnAnyMonitor(new WindowBounds(0, 0, 0, 600), new[] { PrimaryMonitor }));
        Assert.False(WindowBounds.IsOnAnyMonitor(new WindowBounds(0, 0, 800, -1), new[] { PrimaryMonitor }));
    }

    [Fact]
    public void 最小化挂起矩形_判定为屏幕外()
    {
        // IsIconic 预检之外的第二道防线：-32000 矩形不可能与任何显示器相交
        var rect = new WindowBounds(WindowBounds.MinimizedOffscreenX, WindowBounds.MinimizedOffscreenX, 800, 600);
        Assert.False(WindowBounds.IsOnAnyMonitor(rect, new[] { PrimaryMonitor, SecondaryMonitor }));

        // 特征常量必须钉住 Win32 的挂起位置数值（写错就挡不住 -32000 错位）
        Assert.Equal(-32000, WindowBounds.MinimizedOffscreenX);
    }

    // ---------- 序列化往返：档案矩形经真实落盘 / 读回无损 ----------

    [Fact]
    public async Task 窗口几何字段经配置落盘读回无损()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "WinCanvas.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string configPath = Path.Combine(tempDir, "config.json");
            using (var writer = new ConfigurationService(configPath, TimeSpan.FromMilliseconds(50)))
            {
                var original = new WindowState
                {
                    Key = "notepad|Notepad",
                    IsTopmost = true,
                    HasBounds = true,
                    X = -1200,
                    Y = 345,
                    Width = 1920,
                    Height = 1080,
                };

                var config = new AppConfig();
                config.Windows.Add(original);
                writer.Update(config);
                writer.Flush(); // 同步落盘（与退出路径同一收口方式）
            }

            // 全新服务实例读回（不带内存缓存），模拟「重启后恢复管线读档案」
            using (var reader = new ConfigurationService(configPath))
            {
                AppConfig loaded = await reader.LoadAsync();
                WindowState restored = Assert.Single(loaded.Windows);

                Assert.True(restored.HasBounds);
                Assert.Equal(-1200, restored.X); // 负坐标（副显示器在左侧）必须原样往返
                Assert.Equal(345, restored.Y);
                Assert.Equal(1920, restored.Width);
                Assert.Equal(1080, restored.Height);
                Assert.Equal((-1200, 345, 1920, 1080), (restored.X, restored.Y, restored.Width, restored.Height));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void 几何字段默认值_未捕获时不产生几何()
    {
        // 旧版本 config.json（无几何字段）反序列化后必须是「未捕获」状态：
        // 恢复管线按 HasBounds 判定，false 时绝不应用 X/Y —— 这是升级兼容的边界
        var state = JsonSerializer.Deserialize<WindowState>("{\"Key\":\"a|b\"}");

        Assert.NotNull(state);
        Assert.False(state!.HasBounds);
        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
        Assert.Equal(0, state.Width);
        Assert.Equal(0, state.Height);
    }

    [Fact]
    public void ToBounds与SetBounds_字段组装口径一致()
    {
        var state = new WindowState();
        state.SetBounds(new WindowBounds(10, 20, 300, 200));

        Assert.True(state.HasBounds);
        WindowBounds roundTrip = state.ToBounds();
        Assert.Equal(new WindowBounds(10, 20, 300, 200), roundTrip);
    }
}
