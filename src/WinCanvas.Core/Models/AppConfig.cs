namespace WinCanvas.Core.Models;

/// <summary>
/// %APPDATA%/WinCanvas/config.json 的根配置模型。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置文件格式版本，便于将来迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>各窗口的持久化状态（按 <see cref="WindowState.Key"/> 唯一）。</summary>
    public List<WindowState> Windows { get; set; } = new();
}
