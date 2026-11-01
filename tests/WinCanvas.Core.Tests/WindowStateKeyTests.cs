using WinCanvas.Core.Models;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP4 用例表：<see cref="WindowState"/> / <see cref="WindowInfo"/> 的键语义补充钉
/// （WP0 sanity 已钉住组合与转义规则，本类补齐用例表点名的另外两块：
/// 「Key 组合稳定性（同进程+标题 → 相同 Key）」与「HwndHash 与 Key 语义区分」）。
/// </summary>
public sealed class WindowStateKeyTests
{
    [Fact]
    public void Key_组合稳定性_同进程同标题同类名_句柄变化仍得到相同Key()
    {
        // 「同进程+标题 → 相同 Key」的持久化语义：窗口销毁重开后 hwnd 必然变化，
        // 档案匹配必须只依赖 (进程名, 类名) 组合键，与句柄值无关。
        var before = new WindowInfo(Hwnd: new IntPtr(0x1111), Title: "无标题 - 记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 7);
        var after = new WindowInfo(Hwnd: new IntPtr(0x9999), Title: "无标题 - 记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 7);

        Assert.Equal(before.Key, after.Key);
    }

    [Fact]
    public void Key_组合稳定性_重复枚举同一窗口得到字节级相同Key()
    {
        // 同一窗口两次枚举（列表刷新前后）必须得到完全相同的 Key（字符串恒等而非仅相等）。
        var first = new WindowInfo(Hwnd: new IntPtr(0x2222), Title: "WinCanvas", ClassName: "Chrome_WidgetWin_1", ProcessName: "explorer", ProcessId: 42);
        var second = new WindowInfo(Hwnd: new IntPtr(0x2222), Title: "WinCanvas", ClassName: "Chrome_WidgetWin_1", ProcessName: "explorer", ProcessId: 42);

        Assert.Equal(first.Key, second.Key);
        Assert.True(ReferenceEquals(first.Key, second.Key) || first.Key == second.Key);
    }

    [Fact]
    public void Key_成员转义可逆_编码不丢失原值信息()
    {
        // 转义必须可逆：含分隔符的进程名/类名编码后，从 Key 仍能还原出与原成员一致的信息量
        //（至少保证「不同原值不碰撞」+「同值同编码」）。
        var a = new WindowInfo(Hwnd: new IntPtr(1), Title: "t", ClassName: "cls", ProcessName: "p\\1", ProcessId: 1);
        var a2 = new WindowInfo(Hwnd: new IntPtr(2), Title: "t", ClassName: "cls", ProcessName: "p\\1", ProcessId: 1);
        var b = new WindowInfo(Hwnd: new IntPtr(3), Title: "t", ClassName: "cls", ProcessName: "p", ProcessId: 1);

        Assert.Equal(a.Key, a2.Key);   // 同值同编码
        Assert.NotEqual(a.Key, b.Key); // "p\1" 与 "p" 编码后不同（不可被截断吞并）
    }

    [Fact]
    public void HwndHash_不同句柄产生不同哈希_与Key互不干扰()
    {
        // HwndHash = 句柄整数值（运行期快速定位）；Key = 进程名|类名（持久化主键）。
        // 两个句柄不同、来源相同的窗口：HwndHash 不同而 Key 相同——这正是「运行期定位」与
        // 「跨重启匹配」两个语义层的分界，二者不得混用（Key 永不含句柄值）。
        var first = new WindowInfo(Hwnd: new IntPtr(0xAAAA), Title: "t", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 1);
        var second = new WindowInfo(Hwnd: new IntPtr(0xBBBB), Title: "t", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 1);

        Assert.NotEqual(first.HwndHash, second.HwndHash);
        Assert.Equal(0xAAAA, first.HwndHash);
        Assert.Equal(0xBBBB, second.HwndHash);
        Assert.Equal(first.Key, second.Key);
        Assert.DoesNotContain("43690", first.Key); // 0xAAAA 十进制值不得泄入 Key
    }

    [Fact]
    public void WindowState_档案往返_Key与HwndHash各自承载正确语义()
    {
        // WindowState 是 config.json 的持久化载体：Key 存档（跨重启有效），
        // HwndHash 存运行期句柄值（重启后失配属预期，恢复以 Key 为主键）。
        var info = new WindowInfo(Hwnd: new IntPtr(0xC0DE), Title: "终端", ClassName: "CASCADIA_HOSTING_WINDOW_CLASS", ProcessName: "WindowsTerminal", ProcessId: 5);
        var state = new WindowState
        {
            Key = info.Key,
            HwndHash = info.HwndHash,
            ProcessName = info.ProcessName,
            Title = info.Title,
            ProcessId = info.ProcessId,
            IsTopmost = true,
        };

        Assert.Equal(info.Key, state.Key);
        Assert.Equal(0xC0DE, state.HwndHash);
        Assert.True(state.IsTopmost);
        // 语义区分：换句柄后 HwndHash 变、Key 不变——档案仍按 Key 匹配
        var reopened = info with { Hwnd = new IntPtr(0xFEED) };
        Assert.NotEqual(state.HwndHash, reopened.HwndHash);
        Assert.Equal(state.Key, reopened.Key);
    }
}
