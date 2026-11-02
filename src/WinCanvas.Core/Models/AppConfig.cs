namespace WinCanvas.Core.Models;

/// <summary>
/// %APPDATA%/WinCanvas/config.json 的根配置模型。
/// </summary>
public sealed class AppConfig
{
    /// <summary>
    /// 配置文件格式版本，便于将来迁移。
    /// 读写两侧都以此为准：<c>ConfigurationService.SupportedConfigVersion</c> 会在
    /// 加载时校验、落盘时统一钉成本值。
    /// 版本 2 新增 <see cref="Hotkeys"/>（全局热键绑定）——旧版本文件缺这个字段
    /// 会反序列化成空列表，运行期自动补默认绑定，因此 v1 文件无需迁移即可继续用。
    /// </summary>
    public int Version { get; set; } = 2;

    /// <summary>
    /// 启动时是否自动恢复已保存档案的窗口（置顶 / 透明 / 穿透 / 圆角 / 背景）（WP3）。
    /// 默认 true：档案恢复是本产品的核心语义——用户设置过背景与属性的窗口，
    /// 重启应用后不做任何操作即应恢复。关闭后启动时跳过整个恢复管线
    /// （MainViewModel.QueueRestorePipeline 检查此开关），已保存档案仍保留在配置中。
    /// </summary>
    public bool AutoRestoreOnStartup { get; set; } = true;

    /// <summary>各窗口的持久化状态（按 <see cref="WindowState.Key"/> 唯一）。</summary>
    public List<WindowState> Windows { get; set; } = new();

    /// <summary>
    /// 全局热键绑定表（版本 2 新增）。
    /// 为空不代表「无热键」——运行期由 <c>HotkeyTable.ApplyDefaults</c> 按动作补齐默认组合，
    /// 所以用户删掉某条只是回到默认，要真正停用请把该条的 <see cref="HotkeyBinding.Enabled"/> 置 false。
    /// </summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = new();
}
