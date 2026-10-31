using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;
using WinCanvas.Core.Native;

namespace WinCanvas.Core.Services;

/// <summary>
/// <see cref="IWindowControlService"/> 的 Win32 实现。
/// 所有原生调用均检查返回值，失败时抛出 <see cref="Win32Exception"/>（携带 GetLastError 的错误码）。
/// </summary>
public sealed class WindowControlService : IWindowControlService
{
    /// <summary>GetClassName 用的缓冲区长度（类名一般不超过 256 字符）。</summary>
    private const int ClassNameBufferLength = 256;

    /// <inheritdoc/>
    public Task<IReadOnlyList<WindowInfo>> EnumerateTopLevelWindowsAsync(CancellationToken cancellationToken = default)
    {
        // EnumWindows 是同步阻塞调用，放到线程池后台线程执行，避免卡住 UI 线程。
        return Task.Run<IReadOnlyList<WindowInfo>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = new List<WindowInfo>();

            // EnumWindows 对每个顶层窗口回调一次；返回 false 会终止枚举，
            // 这里正常遍历完返回 true。
            bool callback(IntPtr hwnd, IntPtr lParam)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 过滤：只保留可见的顶层窗口（任务栏/隐藏窗口等被排除）。
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true; // 继续枚举下一个窗口
                }

                // 读取标题：先取长度，再按长度分配缓冲区（+1 为结尾 '\0'）。
                int titleLength = NativeMethods.GetWindowTextLength(hwnd);
                var titleBuilder = new StringBuilder(titleLength + 1);
                NativeMethods.GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
                string title = titleBuilder.ToString();

                // 过滤：无标题的可见窗口（多为消息辅助窗、工具窗）不进入列表。
                if (title.Length == 0)
                {
                    return true;
                }

                // 读取类名（固定 256 字符缓冲区足够容纳所有常见类名）。
                var classBuilder = new StringBuilder(ClassNameBufferLength);
                NativeMethods.GetClassName(hwnd, classBuilder, classBuilder.Capacity);
                string className = classBuilder.ToString();

                // 读取进程 Id，再经 Process 查询进程名；目标进程可能刚好退出，失败则跳过。
                uint pid = NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
                _ = pid; // 返回值为线程 Id，本程序不使用
                string processName;
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    processName = process.ProcessName;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    return true; // 进程已退出，跳过该窗口
                }

                result.Add(new WindowInfo(hwnd, title, className, processName, (int)processId));
                return true; // 继续枚举
            }

            if (!NativeMethods.EnumWindows(callback, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows 枚举顶层窗口失败。");
            }

            return result;
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> ToggleTopmostAsync(IntPtr hwnd)
    {
        return Task.Run(() =>
        {
            // 先读当前扩展样式，判断窗口是否已在置顶组（WS_EX_TOPMOST）。
            int exStyle = NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
            bool isTopmost = (exStyle & NativeMethods.WS_EX_TOPMOST) != 0;

            IntPtr insertAfter = isTopmost ? NativeMethods.HWND_NOTOPMOST : NativeMethods.HWND_TOPMOST;

            // SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE：只改 Z 序，不动位置尺寸、不抢焦点。
            uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
            if (!NativeMethods.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, flags))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetWindowPos 切换窗口 0x{hwnd:X} 置顶状态失败。");
            }

            return !isTopmost; // 切换后的置顶状态
        });
    }

    /// <inheritdoc/>
    public Task SetTransparencyAsync(IntPtr hwnd, byte alpha)
    {
        return Task.Run(() =>
        {
            // 确保窗口带 WS_EX_LAYERED 样式（SetLayeredWindowAttributes 的前提）。
            EnsureLayeredStyle(hwnd);

            // LWA_ALPHA：按 bAlpha 做整体透明度，0 全透明 ~ 255 不透明。
            if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetLayeredWindowAttributes 设置窗口 0x{hwnd:X} 透明度 {alpha} 失败。");
            }
        });
    }

    /// <inheritdoc/>
    public Task EnableClickThroughAsync(IntPtr hwnd, bool enable)
    {
        return Task.Run(() =>
        {
            // WS_EX_TRANSPARENT 只有在 WS_EX_LAYERED 同时存在时才会生效，
            // 因此两者必须成对设置 / 成对移除（见蓝图要求）。
            int oldExStyle = NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
            int newExStyle = enable
                ? oldExStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED // 同时置位
                : oldExStyle & ~(NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED); // 同时清零

            if (newExStyle == oldExStyle)
            {
                return; // 样式已处于目标状态，无需调用 API
            }

            SetExStyle(hwnd, newExStyle);
        });
    }

    /// <inheritdoc/>
    public Task SetRoundedCornersAsync(IntPtr hwnd, bool enableSmallRadius = false)
    {
        // enableSmallRadius=false -> 标准大圆角(DWMWCP_ROUND=2)；true -> 小圆角(DWMWCP_ROUNDSMALL=3)。
        int preference = enableSmallRadius ? NativeMethods.DWMWCP_ROUNDSMALL : NativeMethods.DWMWCP_ROUND;
        return SetCornerPreferenceCoreAsync(hwnd, preference);
    }

    /// <inheritdoc/>
    public Task SetCornerPreferenceAsync(IntPtr hwnd, CornerPreference preference)
    {
        // 模型枚举的数值与 DWMWCP_* 一致：Default=0(交由 DWM 默认)、DoNotRound=1、Round=2、RoundSmall=3。
        int preferenceValue = (int)preference;
        return SetCornerPreferenceCoreAsync(hwnd, preferenceValue);
    }

    /// <summary>
    /// DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE) 的公共实现。
    /// </summary>
    private static Task SetCornerPreferenceCoreAsync(IntPtr hwnd, int preferenceValue)
    {
        return Task.Run(() =>
        {
            int previous = NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, // 属性 Id = 33
                ref preferenceValue,
                sizeof(int)); // 整数属性的字节数

            if (previous < 0) // HRESULT 为负表示失败
            {
                throw new Win32Exception(
                    Marshal.GetHRForLastWin32Error(),
                    $"DwmSetWindowAttribute 设置窗口 0x{hwnd:X} 圆角偏好({preferenceValue})失败，HRESULT=0x{previous:X8}。");
            }
        });
    }

    /// <summary>
    /// 确保目标窗口带 WS_EX_LAYERED 样式；没有则补上。
    /// </summary>
    private static void EnsureLayeredStyle(IntPtr hwnd)
    {
        int exStyle = NativeMethods.GetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE).ToInt32();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            SetExStyle(hwnd, exStyle | NativeMethods.WS_EX_LAYERED);
        }
    }

    /// <summary>
    /// 写回 GWL_EXSTYLE，并按 Win32 约定检查失败：SetWindowLongPtr 返回 0 且
    /// GetLastError 非 0 才视为失败（旧值恰好为 0 是合法情况）。
    /// </summary>
    private static void SetExStyle(IntPtr hwnd, int newExStyle)
    {
        Marshal.SetLastSystemError(0); // 清零后调用，便于区分"旧值为 0"与"真失败"
        IntPtr previous = NativeMethods.SetWindowLongPtrCompat(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newExStyle));

        if (previous == IntPtr.Zero)
        {
            int error = Marshal.GetLastSystemError();
            if (error != 0)
            {
                throw new Win32Exception(error, $"SetWindowLongPtr 设置窗口 0x{hwnd:X} 扩展样式 0x{newExStyle:X} 失败。");
            }
        }
    }
}
