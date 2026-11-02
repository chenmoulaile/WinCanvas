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

    /// <summary>
    /// 逐帧/每 Commit「成功」类打点的独立开关，默认【关】。
    ///
    /// 为什么单开一档：旧实现在每次 DComp 上屏成功后都无条件写一行日志，
    /// 而 File.AppendAllText 是「开文件 → 写 → 关文件」且持锁——GIF 动画按帧推进时
    /// 等于每秒 60 次同步磁盘 IO 压在 pump/渲染线程上，画面越动越卡，
    /// 而这行日志对正常运行【没有任何诊断价值】（只有失败才值得记）。
    /// 失败路径的打点仍在 Enabled 档下无条件保留，这里只关「成功也记」。
    /// </summary>
    internal static bool Verbose;

    /// <summary>
    /// 单文件大小上限：超过则滚动成 .old（保留一份历史）后重新写。
    /// 修掉的缺陷：旧实现无轮转无上限，长期挂着壁纸 + 开跟随模式拖动，
    /// 日志会一直长到占满磁盘；本程序连日志目录都不清理。
    /// </summary>
    private const long MaxLogBytes = 4 * 1024 * 1024;

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
    /// 记录一条逐帧/每 Commit 的「成功」打点：受 <see cref="Verbose"/> 单独控制，默认丢弃。
    /// 失败类打点请继续用 <see cref="Log"/>，不要塞进这一档。
    /// </summary>
    /// <param name="message">已组好的文本。</param>
    internal static void LogRender(string message)
    {
        if (!Enabled || !Verbose)
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

    /// <summary>实际落盘：目录惰性创建 + 超限轮转，写失败静默吞掉（诊断日志绝不反噬宿主功能）。</summary>
    private static void Write(string line)
    {
        try
        {
            lock (_writeGate)
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 日志写失败（磁盘满 / 目录权限等）不影响宿主运行，静默忽略
        }
    }

    /// <summary>
    /// 超上限则把当前日志滚动成 .old（覆盖上一份）。调用方须已持 <see cref="_writeGate"/>。
    /// 滚动失败就继续追加：宁可文件暂时超限，也不让诊断日志反过来打断壁纸渲染路径。
    /// </summary>
    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxLogBytes)
            return;

        string rolled = LogFilePath + ".old";
        try
        {
            if (File.Exists(rolled))
                File.Delete(rolled);

            File.Move(LogFilePath, rolled);
        }
        catch
        {
            // 忽略：见方法注释
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
