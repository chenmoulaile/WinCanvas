using System;
using System.Security.Principal;

namespace WinCanvas.Core.Services;

/// <summary>
/// 进程完整性（提权）检测与 UIPI 失败的引导文案（WP5）。
///
/// 检测手段取舍（注释即 ADR）：用 <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/>
/// 判定 Administrator 角色而非 P/Invoke <c>IsUserAnAdmin</c> 或读令牌完整性级别（TokenElevation）：
/// - <c>IsUserAnAdmin</c> 是被微软标注「不要用于新代码」的 shell API（语义随调用方 shell 变化）；
/// - 完整性级别读法准确（High / Medium）但需要 OpenProcessToken + GetTokenInformation 两个 P/Invoke，
///   且「High 完整性但未进 Administrators 组」的边缘场景仍不是能跨过 UIPI 的提权态；
/// - BCL 的 WindowsPrincipal 一行纯托管、覆盖本产品全部真实场景（管理员令牌 ⇔ 能操作管理员窗口），
///   且结果进程生命周期内不变，缓存为静态只读值。UIPI 语义：低完整性进程无法向高完整性进程的
///   窗口发送多数窗口消息 / 改样式，Win32 层表现为错误 5（ERROR_ACCESS_DENIED）。
/// 线程约定：检测只在首次访问时执行一次（同步、微秒级、无 P/Invoke），随后全部读缓存——
/// UI 线程可安全访问（全局硬约束：UI 线程 16ms 预算）。
/// </summary>
public static class ProcessElevation
{
    /// <summary>当前进程是否以管理员令牌运行（进程生命周期内不变；首次访问时检测一次并缓存）。</summary>
    public static bool IsElevated { get; } = DetectElevated();

    /// <summary>
    /// 把「对目标窗口操作失败」的异常翻译成用户可执行的三要素文案（失败 + 原因 + 用户能做什么）。
    /// UIPI 专项（WP5）：错误码 5（ERROR_ACCESS_DENIED，UIPI 拦截低权限进程改高权限窗口的典型表现）
    /// 不再以裸 <c>ex.Message</c> 呈现，改为明确的「以管理员身份重启 WinCanvas」引导。
    /// 其余异常保持原消息透传（错误语义仍来自异常本体，不吞不捏造）。
    /// </summary>
    /// <param name="ex">操作抛出的异常。</param>
    /// <param name="operation">操作描述（如「切换窗口「记事本」置顶」），用于拼装失败语境。</param>
    /// <returns>可直接展示给用户的中文文案。</returns>
    public static string DescribeFailure(Exception ex, string operation)
    {
        if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        {
            return $"{operation}失败：目标窗口以更高权限运行（Win32 错误 5 = 拒绝访问，系统 UIPI 机制拦截" +
                   "低权限进程操作高权限窗口）。请以管理员身份重启 WinCanvas 后重试。";
        }

        return $"{operation}失败：{ex.Message}";
    }

    /// <summary>实际的提权检测（只在类型初始化时执行一次）。</summary>
    private static bool DetectElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 身份不可得（极端安全上下文）按「未提升」保守处理：宁可多引导，不误报已提权
            return false;
        }
    }
}
