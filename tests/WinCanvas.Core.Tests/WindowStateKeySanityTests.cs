using WinCanvas.Core.Models;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP0 环境基线的 sanity 测试：验证「窗口档案键」的组合与相等性语义。
/// 被测实现：<see cref="WindowInfo.Key"/>（组合与转义）与 <see cref="WindowState.Key"/>（持久化载体）。
/// 该键是配置持久化的主键（%APPDATA%/WinCanvas/config.json 的 Windows 列表按它匹配），
/// 组合规则或转义规则一旦回归，已保存的置顶 / 透明 / 穿透 / 壁纸档案会全部匹配失效，
/// 因此在测试骨架建立之初就把它钉住。
/// </summary>
public class WindowStateKeySanityTests
{
    [Fact]
    public void Key_组合与相等性_同进程同类名产生相同键()
    {
        // 同一 (进程名, 类名) 的两次枚举（窗口重启前后）必须得到相同 Key，
        // 这是「重启后档案仍能匹配」的根本前提。
        var first = new WindowInfo(Hwnd: new IntPtr(0x1001), Title: "未命名 - 记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 111);
        var second = new WindowInfo(Hwnd: new IntPtr(0x2002), Title: "另存为 - 记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 111);

        Assert.Equal(first.Key, second.Key);
        Assert.Equal("notepad|Notepad", first.Key);
    }

    [Fact]
    public void Key_组合与相等性_不同进程或不同类名产生不同键()
    {
        var notepad = new WindowInfo(Hwnd: new IntPtr(0x1001), Title: "记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 111);
        var explorer = new WindowInfo(Hwnd: new IntPtr(0x1002), Title: "文件资源管理器", ClassName: "CabinetWClass", ProcessName: "explorer", ProcessId: 222);
        // 同进程不同类名（如 WPF 主窗口与弹出窗口）也不得共享档案
        var sameProcessOtherClass = notepad with { ClassName = "NotepadOther", Hwnd = new IntPtr(0x1003) };

        Assert.NotEqual(notepad.Key, explorer.Key);
        Assert.NotEqual(notepad.Key, sameProcessOtherClass.Key);
    }

    [Fact]
    public void Key_转义_成员含分隔符时不产生跨窗口碰撞()
    {
        // 若不做转义，("a|b","c") 与 ("a","b|c") 会拼出同一个 "a|b|c"，
        // 两个语义不同的窗口共享一份档案。转义后 '|' 永远是真分隔符，二者必须不同。
        var withPipeInProcess = new WindowInfo(Hwnd: new IntPtr(0x3001), Title: "t1", ClassName: "c", ProcessName: "a|b", ProcessId: 1);
        var withPipeInClass = new WindowInfo(Hwnd: new IntPtr(0x3002), Title: "t2", ClassName: "b|c", ProcessName: "a", ProcessId: 2);

        Assert.NotEqual(withPipeInProcess.Key, withPipeInClass.Key);
    }

    [Fact]
    public void Key_转义_成员含反斜杠时不产生跨窗口碰撞()
    {
        // 同理：'\' 是转义引子，("x\y","z") 与 ("x","y\z") 未经转义时会拼出同一个 "x\y|z"。
        var withBackslashInProcess = new WindowInfo(Hwnd: new IntPtr(0x4001), Title: "t1", ClassName: "z", ProcessName: "x\\y", ProcessId: 1);
        var withBackslashInClass = new WindowInfo(Hwnd: new IntPtr(0x4002), Title: "t2", ClassName: "y\\z", ProcessName: "x", ProcessId: 2);

        Assert.NotEqual(withBackslashInProcess.Key, withBackslashInClass.Key);
    }

    [Fact]
    public void Key_转义_普通成员不被误转义()
    {
        // 转义只应影响 '|' 与 '\' 两个保留字符；常规类名 / 进程名必须原样保留，
        // 否则历史配置（按未转义键写入）会与新生成的键失配。
        var info = new WindowInfo(Hwnd: new IntPtr(0x5001), Title: "记事本", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 1);

        Assert.DoesNotContain("\\", info.Key);
    }

    [Fact]
    public void HwndHash_取句柄整数值_与Key语义互不混淆()
    {
        // HwndHash 只用于运行期会话内的快速定位（hwnd 重启后会变），
        // Key 才是持久化主键——两者来源不同（句柄值 vs 进程+类名），断言各自语义。
        var hwnd = new IntPtr(0xABCDEF);
        var info = new WindowInfo(Hwnd: hwnd, Title: "t", ClassName: "Notepad", ProcessName: "notepad", ProcessId: 1);
        var state = new WindowState { Key = info.Key, HwndHash = info.HwndHash };

        Assert.Equal(hwnd.ToInt64(), info.HwndHash);
        Assert.Equal(hwnd.ToInt64(), state.HwndHash);
        Assert.Equal(info.Key, state.Key);
        // Key 中不含句柄值：句柄变化不影响持久化档案的匹配
        Assert.DoesNotContain("11259375", state.Key);
    }
}
