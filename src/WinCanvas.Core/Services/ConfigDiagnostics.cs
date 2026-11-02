using System.IO;

namespace WinCanvas.Core.Services;

/// <summary>
/// 配置读写诊断日志：把「主文件损坏 / 从 .bak 恢复 / 隔离坏文件 / 落盘失败 /
/// 版本高于本实现」等**数据完整性事件**写入
/// <c>%LOCALAPPDATA%\WinCanvas\logs\config-diag.log</c>。
///
/// 为什么需要它：ConfigurationService 的失败路径全部是「静默回退默认值」——
/// 用户看不到，日志里也没有，事后无法判断档案是怎么丢的。本类把这些事件留痕。
///
/// 与 KeyboardDiagnostics / HostDiagnostics 的差别：那两个只追加、**无轮转无上限**
/// （已作为 P1 缺陷单独修），本类从第一版就带上限，不再复制同一个毛病。
/// 范式（锁保护追加、写失败静默吞掉、TEMP 兜底路径）与它们保持一致。
/// </summary>
internal static class ConfigDiagnostics
{
    /// <summary>日志文件完整路径（进程内固定；目录惰性创建一次）。</summary>
    private static readonly string LogFilePath = BuildLogPath();

    /// <summary>保护日志追加与轮转的锁。</summary>
    private static readonly object _writeGate = new();

    /// <summary>总开关：false 时全部打点静默跳过。</summary>
    internal static bool Enabled = true;

    /// <summary>
    /// 单文件大小上限：超过则把当前文件滚动成 .old（保留一份历史）后重新写。
    /// 取 256KB——数据完整性事件本身极低频，正常永远碰不到这个上限；
    /// 它防的是「落盘持续失败 + 上层反复重试」这类刷盘场景。
    /// </summary>
    private const long MaxLogBytes = 256 * 1024;

    /// <summary>
    /// 记录一条数据完整性事件。写失败静默吞掉：诊断日志绝不反噬配置功能。
    /// </summary>
    /// <param name="message">已组好的人读文本。</param>
    internal static void Log(string message)
    {
        if (!Enabled)
        {
            return;
        }

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";

        try
        {
            lock (_writeGate)
            {
                string? dir = Path.GetDirectoryName(LogFilePath);
                if (dir != null && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 磁盘满 / 权限不足等：静默忽略
        }
    }

    /// <summary>超上限则滚动为 .old（覆盖上一份 .old），调用方须持 <see cref="_writeGate"/>。</summary>
    private static void RotateIfNeeded()
    {
        var info = new FileInfo(LogFilePath);
        if (!info.Exists || info.Length < MaxLogBytes)
        {
            return;
        }

        string rolled = LogFilePath + ".old";
        try
        {
            if (File.Exists(rolled))
            {
                File.Delete(rolled);
            }

            File.Move(LogFilePath, rolled);
        }
        catch
        {
            // 滚动失败就继续追加：宁可文件超限，也不要因诊断日志抛出来干扰配置路径
        }
    }

    /// <summary>拼日志路径：%LOCALAPPDATA%\WinCanvas\logs\config-diag.log（TEMP 兜底）。</summary>
    private static string BuildLogPath()
    {
        try
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "WinCanvas", "logs", "config-diag.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "WinCanvas", "logs", "config-diag.log");
        }
    }
}
