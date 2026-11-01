using System;
using System.ComponentModel;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP5 UIPI 引导文案测试：钉住「错误码 5（ERROR_ACCESS_DENIED）→ 以管理员身份重启」的
/// 文案映射与三要素结构（失败 + 原因 + 用户能做什么），以及其余异常的透传语义（不吞不捏造）。
/// 说明：真实的「管理员记事本为目标」端到端场景需要交互式 UAC 提权（无法在无人值守测试中构造），
/// 由独立诊断探针与人工清单覆盖（见 WORKLOG WP5 验证证据）；本组用例覆盖的是文案映射的纯逻辑。
/// </summary>
public sealed class ProcessElevationTests
{
    [Fact]
    public void DescribeFailure_AccessDenied_MapsToElevationGuidance()
    {
        var ex = new Win32Exception(5); // ERROR_ACCESS_DENIED：UIPI 拦截的典型错误码

        var message = ProcessElevation.DescribeFailure(ex, "切换窗口「记事本」置顶");

        Assert.Contains("切换窗口「记事本」置顶", message);      // 失败（含操作语境）
        Assert.Contains("更高权限", message);                    // 原因
        Assert.Contains("以管理员身份重启 WinCanvas", message);  // 用户能做什么（规格固定文案）
        Assert.DoesNotContain(ex.Message, message);             // 裸异常文本不再直出
    }

    [Fact]
    public void DescribeFailure_NonFiveWin32Error_PassesThrough()
    {
        // 错误码 1400（ERROR_INVALID_WINDOW_HANDLE）等非 UIPI 错误保持原语义透传，不误导向提权
        var ex = new Win32Exception(1400, "无效的窗口句柄。");

        var message = ProcessElevation.DescribeFailure(ex, "设置窗口透明度");

        Assert.Equal($"设置窗口透明度失败：{ex.Message}", message);
        Assert.DoesNotContain("管理员", message);
    }

    [Fact]
    public void DescribeFailure_OtherExceptionTypes_PassThrough()
    {
        var ex = new NotSupportedException("不支持的壁纸类型：.txt。");

        var message = ProcessElevation.DescribeFailure(ex, "加载背景");

        Assert.Equal($"加载背景失败：{ex.Message}", message);
    }

    [Fact]
    public void IsElevated_IsStableReadonlyValue()
    {
        // 进程生命周期内不变（静态只读属性）：两次读取必然一致；本测试宿主默认未提权，
        // 但不在断言里写死 False——CI 若以管理员运行也不应失败。
        var first = ProcessElevation.IsElevated;
        var second = ProcessElevation.IsElevated;
        Assert.Equal(first, second);
    }
}
