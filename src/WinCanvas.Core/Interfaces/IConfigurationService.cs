using WinCanvas.Core.Models;

namespace WinCanvas.Core.Interfaces;

/// <summary>
/// 配置服务：负责 %APPDATA%/WinCanvas/config.json 的读写，
/// 采用内存缓存 + 延迟防抖批量写盘策略。
/// </summary>
public interface IConfigurationService
{
    /// <summary>
    /// 从磁盘加载配置（若文件不存在返回默认配置）并缓存到内存。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 用新配置覆盖内存缓存，并安排一次延迟写盘（多次调用自动合并，防抖批量落盘）。
    /// </summary>
    /// <param name="config">新的配置。</param>
    void Update(AppConfig config);

    /// <summary>
    /// 立即将内存缓存写盘（应用退出前调用，确保不丢数据）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task FlushAsync(CancellationToken cancellationToken = default);
}
