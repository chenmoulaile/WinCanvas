using System.IO;
using System.Threading.Channels;

namespace WinCanvas.App.Services;

/// <summary>
/// 档案恢复管线诊断日志（WP3 的 DoD 验证通道）：
/// 把「进程启动 / 列表可用 / 管线开始 / 单窗口恢复 / 幂等跳过 / 文件缺失警告 / 管线完成」等打点
/// 写入 <c>%LOCALAPPDATA%\WinCanvas\logs\restore-diag.log</c>，供验收对照与问题定位。
/// 范式与 WP1 的 HostDiagnostics（Wallpaper 层 host-diag.log）、WP2 的 KeyboardDiagnostics
/// （Core 层 keyboard-diag.log）一致，但写入模型不同：本类的调用方包括【UI 线程】上的
/// ViewModel（启动打点、列表可用打点），因此采用「无界 Channel 队列 + 唯一后台任务落盘」——
/// <see cref="Log"/> 只做非阻塞入队（任意线程可调，UI 线程零文件 IO、零锁等待，
/// 满足全局约束「UI 线程不做文件 IO」），后台任务按入队顺序串行追加（SingleReader 保证顺序）。
/// 落盘失败静默吞掉：诊断日志绝不反噬恢复管线。
/// </summary>
internal static class RestoreDiagnostics
{
    /// <summary>日志文件完整路径（进程内固定；目录由后台任务惰性创建一次）。</summary>
    private static readonly string LogFilePath = BuildLogPath();

    /// <summary>
    /// 打点队列（无界）：生产方 = UI 线程（启动 / 列表打点）与线程池（管线打点），
    /// 消费方 = 唯一后台落盘任务。无界队列 TryWrite 恒成功（除写方已完成），永不阻塞调用方。
    /// </summary>
    private static readonly Channel<string> Queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>静态构造：进程内首次打点时启动唯一消费任务（类型初始化器天然只跑一次且线程安全）。</summary>
    static RestoreDiagnostics()
    {
        _ = Task.Run(ConsumeAsync);
    }

    /// <summary>
    /// 记录一条恢复管线打点（无节流：恢复是低频事件，逐条落盘）。
    /// </summary>
    /// <param name="message">已组好的人读文本（调用方负责拼上下文，如 hwnd、档案值、耗时）。</param>
    internal static void Log(string message)
    {
        // 与 HostDiagnostics / KeyboardDiagnostics 相同行格式：时刻 + 线程 Id + 文本，
        // 便于跨三份日志按时间轴对齐验收证据。
        Queue.Writer.TryWrite($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId:D2}] {message}");
    }

    /// <summary>
    /// 唯一的落盘消费任务：按入队顺序逐行追加；目录首次写入前惰性创建；
    /// 单行写失败只丢弃该行（磁盘满 / 权限等），不终止消费循环、不影响恢复管线。
    /// </summary>
    private static async Task ConsumeAsync()
    {
        await foreach (var line in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var dir = Path.GetDirectoryName(LogFilePath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                await File.AppendAllTextAsync(LogFilePath, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch
            {
                // 日志写失败不影响恢复管线运行，静默忽略（与既有诊断通道口径一致）
            }
        }
    }

    /// <summary>拼日志路径：%LOCALAPPDATA%\WinCanvas\logs\restore-diag.log（TEMP 兜底）。</summary>
    private static string BuildLogPath()
    {
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "WinCanvas", "logs", "restore-diag.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "WinCanvas", "logs", "restore-diag.log");
        }
    }
}
