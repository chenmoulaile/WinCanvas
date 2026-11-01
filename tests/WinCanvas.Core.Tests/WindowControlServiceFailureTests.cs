using System.ComponentModel;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP4 用例表：<see cref="WindowControlService"/> 对 <see cref="IntPtr.Zero"/> 的失败语义——
/// 证明「失败真实上抛 <see cref="Win32Exception"/> 且 Win32 错误码非 0」，而非静默吞掉。
/// 无效句柄路径不创建任何真实窗口（无 GUI 依赖），任意环境可跑。
/// </summary>
public sealed class WindowControlServiceFailureTests
{
    private readonly WindowControlService _service = new();

    /// <summary>断言异步操作抛出的 Win32Exception 错误码非 0（错误码为 0 = 失败语义未真实上抛）。</summary>
    private static async Task AssertWin32ErrorNonZeroAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<Win32Exception>(action);
        Assert.NotEqual(0, ex.NativeErrorCode);
    }

    [Fact]
    public Task ToggleTopmostAsync_零句柄_抛Win32Exception且错误码非0()
    {
        // SetWindowPos(0, ...) 失败（ERROR_INVALID_WINDOW_HANDLE=1400），失败语义上抛
        return AssertWin32ErrorNonZeroAsync(() => _service.ToggleTopmostAsync(IntPtr.Zero));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SetTopmostAsync_零句柄_抛Win32Exception且错误码非0(bool topmost)
    {
        return AssertWin32ErrorNonZeroAsync(() => _service.SetTopmostAsync(IntPtr.Zero, topmost));
    }

    [Fact]
    public Task SetTransparencyAsync_零句柄_抛Win32Exception且错误码非0()
    {
        // EnsureLayeredStyle（SetWindowLongPtr 失败）或 SetLayeredWindowAttributes 失败，两者都会上抛
        return AssertWin32ErrorNonZeroAsync(() => _service.SetTransparencyAsync(IntPtr.Zero, 128));
    }

    [Fact]
    public Task EnableClickThroughAsync_启用_零句柄_抛Win32Exception且错误码非0()
    {
        // enable=true 会真正写 GWL_EXSTYLE：SetWindowLongPtr(0, ...) 返回 0 且错误码非 0 → 上抛
        return AssertWin32ErrorNonZeroAsync(() => _service.EnableClickThroughAsync(IntPtr.Zero, enable: true));
    }

    [Fact]
    public async Task EnableClickThroughAsync_关闭_零句柄_按设计无操作不抛()
    {
        // enable=false 的语义是「仅清穿透位」：旧样式 0 本就不含穿透位 → 新旧样式相等 →
        // 服务按「样式已处于目标状态，无需调用 API」短路（WindowControlService.SetExStyle 的幂等设计）。
        // 这是既定幂等语义而非失败吞没，本用例把它钉住防将来误改成「无效句柄也报错」。
        await _service.EnableClickThroughAsync(IntPtr.Zero, enable: false);
    }

    [Fact]
    public Task SetRoundedCornersAsync_零句柄_抛Win32Exception且错误码非0()
    {
        return AssertWin32ErrorNonZeroAsync(() => _service.SetRoundedCornersAsync(IntPtr.Zero));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task SetCornerPreferenceAsync_零句柄_抛Win32Exception且错误码非0(bool smallRadius)
    {
        return AssertWin32ErrorNonZeroAsync(() =>
            _service.SetCornerPreferenceAsync(IntPtr.Zero, smallRadius ? CornerPreference.RoundSmall : CornerPreference.Round));
    }

    [Fact]
    public async Task GetIsTopmostAsync_零句柄_按设计返回false不抛()
    {
        // 纯读取路径（GetWindowLongPtr 对无效句柄返回 0）没有可上抛的失败语义——
        // 置顶位读不到即视为「不在置顶组」，与真实窗口的未置顶状态同型。
        Assert.False(await _service.GetIsTopmostAsync(IntPtr.Zero));
    }
}
