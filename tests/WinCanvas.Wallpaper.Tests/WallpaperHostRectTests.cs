using WinCanvas.Wallpaper.Hosting;
using Xunit;

namespace WinCanvas.Wallpaper.Tests;

/// <summary>
/// WP4 用例表：<see cref="WallpaperHostWindow"/> 的挂接矩形计算纯函数与 DPI 缩放换算。
/// 三个纯函数不含任何 Win32 调用（宿主创建 / 挂接本体需要真实桌面会话与消息线程，
/// 不在单元测试范围——真实挂接冒烟由 WP1 的 tools/Wp1AHostDiag 命令承担）。
/// 坐标口径：全部为屏幕物理像素（DWMWA_EXTENDED_FRAME_BOUNDS / GetWindowRect 同一坐标系）。
/// </summary>
public sealed class WallpaperHostRectTests
{
    private static HostNativeMethods.RECT Rect(int left, int top, int right, int bottom) => new()
    {
        Left = left, Top = top, Right = right, Bottom = bottom,
    };

    // ---------------- A1：目标客户区 → 宿主矩形（左上恒 0,0，宽高一致） ----------------

    [Fact]
    public void ComputeEmbedHostRect_标准客户区_宿主从零点起同宽高()
    {
        var host = WallpaperHostWindow.ComputeEmbedHostRect(Rect(0, 0, 1920, 1080));

        // 宿主是目标的 WS_CHILD，位置恒在客户区 (0,0)，宽高 = 目标客户区宽高
        Assert.True(WallpaperHostWindow.RectsEqual(Rect(0, 0, 1920, 1080), host));
    }

    [Fact]
    public void ComputeEmbedHostRect_客户区含非零偏移_仍归一到零点起()
    {
        // GetClientRect 理论上恒返回 (0,0) 起点；这里钉住「以 Right-Left / Bottom-Top 求宽高、
        // 强制归零起点」的纯函数语义——即使传入含偏移的矩形也不把偏移带进宿主位置。
        var host = WallpaperHostWindow.ComputeEmbedHostRect(Rect(10, 20, 310, 220));

        Assert.True(WallpaperHostWindow.RectsEqual(Rect(0, 0, 300, 200), host));
    }

    [Fact]
    public void ComputeEmbedHostRect_零尺寸客户区_宿主零尺寸()
    {
        // 最小化/销毁边界：客户区为 0 时宿主矩形为 0 尺寸（服务层据此跳过渲染）
        var host = WallpaperHostWindow.ComputeEmbedHostRect(Rect(0, 0, 0, 0));

        Assert.True(WallpaperHostWindow.RectsEqual(Rect(0, 0, 0, 0), host));
    }

    // ---------------- A2：目标外接矩形 → 宿主矩形（平移不缩放） ----------------

    [Fact]
    public void ComputeFollowHostRect_外接矩形_宿主等大同位()
    {
        var bounds = Rect(100, 200, 1500, 900);
        var host = WallpaperHostWindow.ComputeFollowHostRect(bounds);

        // 跟随语义：宿主与目标等大同位（不动 Z 序、不改尺寸），直接原样复制
        Assert.True(WallpaperHostWindow.RectsEqual(bounds, host));
    }

    [Fact]
    public void ComputeFollowHostRect_负坐标屏幕_原样保留()
    {
        // 多显示器负坐标区（主屏左侧的副屏）：矩形原样保留，不做任何钳制
        var bounds = Rect(-1920, -100, -10, 900);
        var host = WallpaperHostWindow.ComputeFollowHostRect(bounds);

        Assert.True(WallpaperHostWindow.RectsEqual(bounds, host));
    }

    // ---------------- RectsEqual ----------------

    [Fact]
    public void RectsEqual_逐字段比对()
    {
        Assert.True(WallpaperHostWindow.RectsEqual(Rect(1, 2, 3, 4), Rect(1, 2, 3, 4)));
        Assert.False(WallpaperHostWindow.RectsEqual(Rect(1, 2, 3, 4), Rect(9, 2, 3, 4))); // Left 异
        Assert.False(WallpaperHostWindow.RectsEqual(Rect(1, 2, 3, 4), Rect(1, 2, 3, 9))); // Bottom 异
    }

    // ---------------- DPI 缩放换算（WP4 新增纯函数，WM_DPICHANGED 诊断比对用） ----------------

    [Theory]
    [InlineData(1920, 1080, 96, 192, 3840, 2160)]   // 100% → 200%
    [InlineData(1920, 1080, 192, 96, 960, 540)]     // 200% → 100%（跨屏返回主屏）
    [InlineData(800, 600, 96, 120, 1000, 750)]      // 100% → 125%
    [InlineData(800, 600, 96, 144, 1200, 900)]      // 100% → 150%
    [InlineData(1920, 1080, 96, 96, 1920, 1080)]    // 同 DPI 恒等（理论不发生，防御口径）
    public void ComputeDpiScaledSize_标准DPI档位_按比例缩放四舍五入(
        int w, int h, uint oldDpi, uint newDpi, int expectedW, int expectedH)
    {
        var (width, height) = WallpaperHostWindow.ComputeDpiScaledSize(w, h, oldDpi, newDpi);

        Assert.Equal((expectedW, expectedH), (width, height));
    }

    [Fact]
    public void ComputeDpiScaledSize_非整除尺寸_四舍五入而非截断()
    {
        // 33px @ 96→120 = 41.25 → 41（截断同为 41，区分度不足）；再取 33 @ 96→144 = 49.5：
        // Math.Round 默认「银行家舍入」(ToEven) → 50（50 为偶数）；若是截断会得 49，AwayFromZero 会得 50。
        // 本用例钉住「非截断」这一行为语义（截断会造成连续换算误差单向累积）。
        var (w1, _) = WallpaperHostWindow.ComputeDpiScaledSize(33, 1, 96, 120);
        Assert.Equal(41, w1);

        var (w2, _) = WallpaperHostWindow.ComputeDpiScaledSize(33, 1, 96, 144);
        Assert.Equal(50, w2);
    }

    [Fact]
    public void ComputeDpiScaledSize_DPI为0_防御性原样返回()
    {
        // GetDpiForWindow 极端异常路径返回 0：换算无意义，原样返回尺寸（不除零不抛）
        var (width, height) = WallpaperHostWindow.ComputeDpiScaledSize(800, 600, 0, 96);
        Assert.Equal((800, 600), (width, height));

        (width, height) = WallpaperHostWindow.ComputeDpiScaledSize(800, 600, 96, 0);
        Assert.Equal((800, 600), (width, height));
    }
}
