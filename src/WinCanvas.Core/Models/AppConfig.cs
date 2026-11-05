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
    /// WinTips NumLock 提示开关（版本 2 新增持久化）。
    ///
    /// 修掉的缺陷：这个开关此前【在配置里根本没有字段】——
    /// OnWinTipsEnabledChanged 只设 WinTips.IsEnabled 与钩子 Start/Stop，从不写盘，
    /// 启动时也无处回填。用户每次开机都要重新打开一次 NumLock 提示，
    /// 而它恰恰属于「一次设定、长期生效」的偏好。
    /// 默认 false 与迁移前的界面初值一致，所以老用户升级后不会突然被装上键盘钩子。
    /// </summary>
    public bool WinTipsIndicatorEnabled { get; set; }

    /// <summary>
    /// 是否允许壁纸发声。<b>默认 false（静音）</b>。
    /// 壁纸是背景，默认外放会在用户不知情时突然出声，且工坊素材的音量完全不可控。
    /// </summary>
    public bool WallpaperAudioEnabled { get; set; }

    /// <summary>
    /// 壁纸库扫描根（绝对路径列表）。
    /// 为空时运行期使用自动探测结果（创意工坊 / 系统主题 / 图片库）；
    /// 非空则以用户配置的为准（自动探测的结果仍会并入，去重）。
    /// 之所以必须允许手工配置：实测存在「Wallpaper Engine 不是通过 Steam 安装、
    /// 放在自己盘上的某个目录」的情形，注册表与标准路径都探不到。
    /// </summary>
    public List<string> WallpaperLibraryRoots { get; set; } = new();

    /// <summary>
    /// 壁纸库扫描结果的持久化缓存（v0.4.0 新增，按扫描根记账，见
    /// <see cref="WallpaperLibraryRootCache"/>）。
    ///
    /// 语义：启动时【先】用它回填库面板（重启秒开），【再】在后台做增量刷新——
    /// 根目录签名没变的整根复用，变过的才重扫。缓存只影响「第一次显示多快」，
    /// 不影响最终正确性：后台刷新完成后会整体替换界面数据并把本字段写回。
    /// </summary>
    public List<WallpaperLibraryRootCache> WallpaperLibraryCache { get; set; } = new();

    /// <summary>
    /// 全局热键绑定表（版本 2 新增）。
    /// 为空不代表「无热键」——运行期由 <c>HotkeyTable.Normalize</c> 按动作补齐默认组合，
    /// 所以用户删掉某条只是回到默认，要真正停用请把该条的 <see cref="HotkeyBinding.Enabled"/> 置 false。
    /// </summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = new();

    /// <summary>
    /// 主窗口失焦时是否显示隐私遮罩（仿 QQ 新版「隐私窗口」，隐私窗口工作包新增）。
    /// 默认 true：遮蔽是防肩窥的安全特性，默认开启才符合「用户没配置过 = 想要保护」的直觉；
    /// 被遮罩只出现在本窗口失去前台时，点击窗口即恢复，不构成打扰。
    /// 单窗口粒度的隐私开关在 <see cref="WindowState.PrivacyEnabled"/>，两者互不影响。
    /// </summary>
    public bool PrivacyOverlayOnDeactivated { get; set; } = true;
}
