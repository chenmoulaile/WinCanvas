using System.IO;

namespace WinCanvas.Core.Services;

/// <summary>
/// NumLock 键盘钩子诊断日志（WP2 的 DoD 验证通道）：
/// 把「钩子安装 / 状态变化 / 去抖抑制 / Unhook 完成」等打点写入
/// <c>%LOCALAPPDATA%\WinCanvas\logs\keyboard-diag.log</c>，供验收对照（如「UnhookWindowsHookEx 已调用」
/// 的日志证据）与问题定位。范式与 WP1 的 HostDiagnostics（Wallpaper 层 host-diag.log）一致。
/// 线程安全性：内部锁保护文件追加；调用方（钩子线程 / 线程池 / Stop 路径）可并发调用。
/// 性能约定：只允许在非 UI 线程调用（钩子热路径仅 1s 节流的抑制打点会触达）。
/// </summary>
internal static class KeyboardDiagnostics
{
    /// <summary>日志文件完整路径（进程内固定；目录惰性创建一次）。</summary>
    private static readonly string LogFilePath = BuildLogPath();

    /// <summary>保护日志文件追加与目录创建的锁（钩子线程 / 线程池并发写）。</summary>
    private static readonly object _writeGate = new();

    /// <summary>总开关：false 时全部打点静默跳过（生产可关；验收阶段保持 true）。</summary>
    internal static bool Enabled = true;

    /// <summary>抑制类打点的节流窗口：同批重复事件 1 秒内只落盘一次（按住 NumLock 自动重复时高频触发）。</summary>
    private const int SuppressThrottleMs = 1000;

    /// <summary>上次「抑制」打点的落盘时刻（Tick 毫秒）；由 <see cref="_writeGate"/> 保护。</summary>
    private static long _lastSuppressLogMs;

    /// <summary>
    /// 记录一条钩子生命周期 / 状态变化打点（无节流，这类事件本身低频）。
    /// </summary>
    /// <param name="message">已组好的人读文本（调用方负责拼上下文）。</param>
    internal static void Log(string message)
    {
        if (!Enabled)
            return;

        Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}");
    }

    /// <summary>
    /// 记录一条抑制类打点（1 秒节流）。
    /// </summary>
    /// <param name="message">已组好的人读文本。</param>
    /// <returns>true = 本次实际落盘（调用方据此决定是否同步进 Diagnostics 事件）；false = 节流窗口内跳过。</returns>
    internal static bool LogThrottled(string message)
    {
        if (!Enabled)
            return false;

        var now = Environment.TickCount64;
        lock (_writeGate)
        {
            if (now - _lastSuppressLogMs < SuppressThrottleMs)
                return false; // 节流窗口内：跳过落盘（自动重复事件频率远高于 1Hz，全记会拖慢钩子回调）

            _lastSuppressLogMs = now;
        }

        Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}");
        return true;
    }

    /// <summary>实际落盘：目录惰性创建，写失败静默吞掉（诊断日志绝不反噬钩子功能）。</summary>
    private static void Write(string line)
    {
        try
        {
            lock (_writeGate)
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写失败（磁盘满 / 目录权限等）不影响钩子运行，静默忽略
        }
    }

    /// <summary>拼日志路径：%LOCALAPPDATA%\WinCanvas\logs\keyboard-diag.log（TEMP 兜底）。</summary>
    private static string BuildLogPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "WinCanvas", "logs", "keyboard-diag.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "WinCanvas", "logs", "keyboard-diag.log");
        }
    }
}
