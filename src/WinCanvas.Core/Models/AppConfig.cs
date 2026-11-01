namespace WinCanvas.Core.Models;

/// <summary>
/// %APPDATA%/WinCanvas/config.json 的根配置模型。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置文件格式版本，便于将来迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// 启动时是否自动恢复已保存档案的窗口（置顶 / 透明 / 穿透 / 圆角 / 背景）（WP3）。
    /// 默认 true：档案恢复是本产品的核心语义——用户设置过背景与属性的窗口，
    /// 重启应用后不做任何操作即应恢复。关闭后启动时跳过整个恢复管线
    /// （MainViewModel.QueueRestorePipeline 检查此开关），已保存档案仍保留在配置中。
    /// </summary>
    public bool AutoRestoreOnStartup { get; set; } = true;

    /// <summary>各窗口的持久化状态（按 <see cref="WindowState.Key"/> 唯一）。</summary>
    public List<WindowState> Windows { get; set; } = new();
}
