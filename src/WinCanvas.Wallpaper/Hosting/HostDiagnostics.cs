using System;
using System.IO;
using System.Threading;

namespace WinCanvas.Wallpaper.Hosting;

/// <summary>
/// 宿主挂接诊断日志（WP1 阶段 A 的 DoD 验证通道）：
/// 把「挂接建立 / 宿主矩形 vs 目标矩形 / 跟随耗时 / 降级 / 销毁」等打点写入
/// <c>%LOCALAPPDATA%\WinCanvas\logs\host-diag.log</c>，供验收对照与问题定位。
/// 线程安全性：内部锁保护文件追加；调用方（pump 线程 / 线程池）可并发调用。
/// 性能约定：只允许在非 UI 线程调用（pump / 线程池），跟随热路径带 200ms 节流，
/// 避免 IO 拖慢拖动跟随。
/// </summary>
internal static class HostDiagnostics
{
    /// <summary>日志文件完整路径（进程内固定；目录惰性创建一次）。</summary>
    private static readonly string LogFilePath = BuildLogPath();

    /// <summary>保护日志文件追加与目录创建的锁（pump 线程 / 线程池并发写）。</summary>
    private static readonly object _writeGate = new();

    /// <summary>总开关：false 时全部打点静默跳过（生产可关；验收阶段保持 true）。</summary>
    internal static bool Enabled = true;

    /// <summary>跟随类打点的节流窗口：同类型消息 200ms 内只落盘一次（拖动中 LOCATIONCHANGE 高频触发）。</summary>
    private const int FollowThrottleMs = 200;

    /// <summary>上次「跟随」打点的落盘时刻（Tick 毫秒）；由 <see cref="_writeGate"/> 保护。</summary>
    private static long _lastFollowLogMs;

    /// <summary>
    /// 记录一条挂接 / 生命周期打点（无节流，挂接类事件本身低频）。
    /// </summary>
    /// <param name="message">已组好的人读文本（调用方负责拼上下文，如目标 hwnd、模式）。</param>
    internal static void Log(string message)
    {
        if (!Enabled)
            return;

        Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}");
    }

    /// <summary>
    /// 记录一条跟随类打点（200ms 节流）：只记录「事件到达 → 同步完成」的耗时与前后矩形。
    /// </summary>
    /// <param name="message">已组好的文本（含耗时 ms 与矩形对比）。</param>
    internal static void LogFollow(string message)
    {
        if (!Enabled)
            return;

        var now = Environment.TickCount64;
        lock (_writeGate)
        {
            if (now - _lastFollowLogMs < FollowThrottleMs)
                return; // 节流窗口内：跳过落盘（拖动中事件频率远高于 5Hz，全记会拖慢 pump）

            _lastFollowLogMs = now;
        }

        Write($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}");
    }

    /// <summary>实际落盘：目录惰性创建，写失败静默吞掉（诊断日志绝不反噬宿主功能）。</summary>
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
            // 日志写失败（磁盘满 / 目录权限等）不影响宿主运行，静默忽略
        }
    }

    /// <summary>拼日志路径：%LOCALAPPDATA%\WinCanvas\logs\host-diag.log（TEMP 兜底）。</summary>
    private static string BuildLogPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "WinCanvas", "logs", "host-diag.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "WinCanvas", "logs", "host-diag.log");
        }
    }
}
