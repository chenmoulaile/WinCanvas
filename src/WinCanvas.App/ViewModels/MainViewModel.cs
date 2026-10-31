using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Services;
using WinCanvas.Core.Interfaces;
using WinCanvas.Core.Models;

namespace WinCanvas.App.ViewModels;

/// <summary>圆角偏好选项（供 RadioButtons 绑定展示）。</summary>
/// <param name="Value">对应的 Core 圆角枚举值。</param>
/// <param name="DisplayName">界面显示名。</param>
public sealed record CornerOption(CornerPreference Value, string DisplayName);

/// <summary>
/// 主界面 ViewModel（严格 MVVM）：
/// - 窗口列表：调用 Core 的 <see cref="IWindowControlService.EnumerateTopLevelWindowsAsync"/>（异步，不阻塞 UI）；
/// - 窗口属性：置顶 / 透明 / 穿透 / 圆角通过 <see cref="IWindowControlService"/> 应用，
///   并通过 <see cref="IConfigurationService"/> 实时保存；
/// - 壁纸：文件选择 + 后台加载（<see cref="IWallpaperService"/>），UI 显示进度；
/// - 设置：WinTips 提示开关（控制 <see cref="WinCanvas.Wallpaper.WinTipsIntegrationService.IsEnabled"/>）、
///   FPS 性能监控悬浮显示。
/// 业务逻辑全部在本类，code-behind 只做服务装配。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>图片背景支持的扩展名。</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>视频背景支持的扩展名。</summary>
    private static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv" };

    private readonly AppServices _services;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<Task<string?>> _pickWallpaperFile;
    private readonly Func<IntPtr> _getOwnHwnd;
    private readonly FpsMonitor _fpsMonitor;

    /// <summary>内存中的配置对象（与 ConfigurationService 共享引用，变更后 Update 触发防抖落盘）。</summary>
    private AppConfig _appConfig = new();

    /// <summary>反向同步标志：从选中项刷新镜像属性时不再回写选中项。</summary>
    private bool _suppressSelectedSync;

    /// <summary>上一个选中项（用于换选时退订 PropertyChanged）。</summary>
    private WindowItemViewModel? _previousSelected;

    public MainViewModel(
        AppServices services,
        DispatcherQueue dispatcherQueue,
        Func<Task<string?>> pickWallpaperFile,
        Func<IntPtr> getOwnHwnd)
    {
        _services = services;
        _dispatcherQueue = dispatcherQueue;
        _pickWallpaperFile = pickWallpaperFile;
        _getOwnHwnd = getOwnHwnd;
        _fpsMonitor = new FpsMonitor(dispatcherQueue);
        _selectedCorner = CornerOptions[0];

        // WinTips：诊断日志与本地回退提示 → InfoBar
        //（是否真正弹出浮窗 / 抛回退事件受「WinTips 提示」开关控制，见 OnWinTipsEnabledChanged）
        _services.WinTips.LogMessage += message =>
            ShowInfo(InfoBarSeverity.Informational, $"WinTips：{message}");
        _services.WinTips.NumLockTipRequested += isLocked =>
            ShowInfo(InfoBarSeverity.Informational, isLocked ? "NumLock 已锁定" : "NumLock 已解锁");

        // 壁纸：服务上报（pkg 复杂场景降级提示、加载错误等）→ InfoBar（工作线程触发，ShowInfo 内部封送）
        _services.Wallpaper.StatusReported += message =>
            ShowInfo(InfoBarSeverity.Informational, message);

        // FPS：每秒取样结果 → 悬浮指示文本
        _fpsMonitor.FpsChanged += (_, fps) =>
            _dispatcherQueue.TryEnqueue(() => FpsText = $"{fps} FPS");
    }

    /// <summary>窗口列表。</summary>
    public ObservableCollection<WindowItemViewModel> Windows { get; } = new();

    /// <summary>圆角偏好候选（无 / 大圆角 / 小圆角）。</summary>
    public IReadOnlyList<CornerOption> CornerOptions { get; } = new[]
    {
        new CornerOption(CornerPreference.DoNotRound, "无"),
        new CornerOption(CornerPreference.Round, "大圆角"),
        new CornerOption(CornerPreference.RoundSmall, "小圆角"),
    };

    // ---------- 列表 / 选中 ----------

    [ObservableProperty]
    private WindowItemViewModel? _selected;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _hasSelection;

    // ---------- 选中项的镜像属性（避免把控件 TwoWay 直接绑到可空的 Selected 上，
    // 防止换选时把旧控件值误写进新选中项） ----------

    [ObservableProperty]
    private string _selectedDisplayName = "（未选择窗口）";

    [ObservableProperty]
    private string _selectedSubtitle = "在左侧列表选择一个窗口后，可在此调整其属性。";

    [ObservableProperty]
    private string _selectedHwndText = string.Empty;

    [ObservableProperty]
    private bool _selectedIsTopmost;

    [ObservableProperty]
    private double _selectedAlpha = 255;

    [ObservableProperty]
    private bool _selectedClickThrough;

    [ObservableProperty]
    private CornerOption _selectedCorner;

    /// <summary>透明度滑块的数值展示（0~255）。</summary>
    public string SelectedAlphaText => $"{SelectedAlpha:0} / 255";

    // ---------- 壁纸 ----------

    [ObservableProperty]
    private string _wallpaperFilePathText = "（未选择）";

    [ObservableProperty]
    private bool _isWallpaperLoading;

    /// <summary>壁纸加载进度（0~100，ProgressBar 用）。</summary>
    [ObservableProperty]
    private double _wallpaperProgress;

    [ObservableProperty]
    private string _wallpaperStatusText = "选择图片 / 视频 / .pkg 文件作为所选窗口的背景。";

    // ---------- 设置 ----------

    [ObservableProperty]
    private bool _winTipsEnabled;

    [ObservableProperty]
    private bool _fpsOverlayEnabled;

    [ObservableProperty]
    private string _fpsText = "-- FPS";

    // ---------- InfoBar 提示 ----------

    [ObservableProperty]
    private bool _isInfoBarOpen;

    [ObservableProperty]
    private string _infoBarMessage = string.Empty;

    [ObservableProperty]
    private InfoBarSeverity _infoBarSeverity = InfoBarSeverity.Informational;

    // ---------- 可见性（供 x:Bind 使用） ----------

    public Visibility DetailVisibility => HasSelection ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyVisibility => HasSelection ? Visibility.Collapsed : Visibility.Visible;

    public Visibility FpsOverlayVisibility => FpsOverlayEnabled ? Visibility.Visible : Visibility.Collapsed;

    // ---------- 初始化 / 刷新 ----------

    /// <summary>启动初始化：加载配置 → 首次枚举窗口。</summary>
    public async Task InitializeAsync()
    {
        _appConfig = await _services.Configuration.LoadAsync();
        await RefreshAsync();
    }

    /// <summary>刷新窗口列表：后台线程枚举，完成后重建列表（不阻塞 UI 线程）。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var ownHwnd = _getOwnHwnd();
            var previous = Selected;
            var list = await _services.WindowControl.EnumerateTopLevelWindowsAsync();

            Windows.Clear();
            WindowItemViewModel? restored = null;
            foreach (var info in list)
            {
                // 排除应用自身主窗口，避免在界面上误操作自己（如把自己设成穿透）
                if (info.Hwnd == ownHwnd)
                {
                    continue;
                }

                // 只查找已保存的档案，不主动为每个窗口新建档案（落盘推迟到用户实际修改时）
                var state = _appConfig.Windows.FirstOrDefault(w => w.Key == info.Key)
                            ?? new WindowState
                            {
                                Key = info.Key,
                                HwndHash = info.HwndHash,
                                ProcessName = info.ProcessName,
                                Title = info.Title,
                                ProcessId = info.ProcessId,
                            };
                var item = new WindowItemViewModel(
                    info, state, _services.WindowControl,
                    PersistState,
                    message => ShowInfo(InfoBarSeverity.Error, message));
                Windows.Add(item);

                // 刷新后尽量按句柄 / 组合键恢复之前的选中项
                if (previous is not null && (info.Hwnd == previous.Hwnd || info.Key == previous.Info.Key))
                {
                    restored = item;
                }
            }

            Selected = restored;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"枚举窗口失败：{ex.Message}");
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    // ---------- 选中项切换与属性联动 ----------

    partial void OnSelectedChanged(WindowItemViewModel? value)
    {
        if (_previousSelected is not null)
        {
            _previousSelected.PropertyChanged -= OnSelectedItemPropertyChanged;
        }
        _previousSelected = value;

        _suppressSelectedSync = true;
        HasSelection = value is not null;
        SelectedDisplayName = value?.DisplayName ?? "（未选择窗口）";
        SelectedSubtitle = value?.Subtitle ?? "在左侧列表选择一个窗口后，可在此调整其属性。";
        SelectedHwndText = value?.HwndText ?? string.Empty;
        SelectedIsTopmost = value?.IsTopmost ?? false;
        SelectedAlpha = value?.Alpha ?? 255;
        SelectedClickThrough = value?.ClickThrough ?? false;
        SelectedCorner = value is null
            ? CornerOptions[0]
            : CornerOptions.FirstOrDefault(o => o.Value == value.Corner) ?? CornerOptions[0];
        WallpaperFilePathText = value?.State.BackgroundPath ?? "（未选择）";
        _suppressSelectedSync = false;

        if (value is not null)
        {
            value.PropertyChanged += OnSelectedItemPropertyChanged;
            // 选中即恢复该窗口已保存的属性档案（置顶/透明/穿透/圆角）
            _ = value.RestoreAsync();
        }
    }

    /// <summary>选中项属性被服务结果更新后（如置顶实际结果），把真实值同步回镜像属性。</summary>
    private void OnSelectedItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectedSync || Selected is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(WindowItemViewModel.IsTopmost):
                _suppressSelectedSync = true;
                SelectedIsTopmost = Selected.IsTopmost;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.Alpha):
                _suppressSelectedSync = true;
                SelectedAlpha = Selected.Alpha;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.ClickThrough):
                _suppressSelectedSync = true;
                SelectedClickThrough = Selected.ClickThrough;
                _suppressSelectedSync = false;
                break;
            case nameof(WindowItemViewModel.Corner):
                _suppressSelectedSync = true;
                SelectedCorner = CornerOptions.FirstOrDefault(o => o.Value == Selected.Corner) ?? CornerOptions[0];
                _suppressSelectedSync = false;
                break;
        }
    }

    // 控件 → 选中项（转发给 Core 服务应用 + 实时保存）
    partial void OnSelectedIsTopmostChanged(bool value)
    {
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetTopmostAsync(value);
        }
    }

    partial void OnSelectedAlphaChanged(double value)
    {
        OnPropertyChanged(nameof(SelectedAlphaText));
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetAlphaAsync((byte)Math.Round(value));
        }
    }

    partial void OnSelectedClickThroughChanged(bool value)
    {
        if (!_suppressSelectedSync)
        {
            _ = Selected?.SetClickThroughAsync(value);
        }
    }

    partial void OnSelectedCornerChanged(CornerOption value)
    {
        if (!_suppressSelectedSync && value is not null)
        {
            _ = Selected?.SetCornerAsync(value.Value);
        }
    }

    partial void OnHasSelectionChanged(bool value)
    {
        OnPropertyChanged(nameof(DetailVisibility));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    // ---------- 壁纸 ----------

    /// <summary>选择背景文件并后台加载（图片 / 视频 / .pkg）。</summary>
    [RelayCommand]
    private async Task PickWallpaperAsync()
    {
        var item = Selected;
        if (item is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先在左侧选择一个窗口。");
            return;
        }

        string? path;
        try
        {
            path = await _pickWallpaperFile();
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, $"打开文件选择器失败：{ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(path))
        {
            return; // 用户取消
        }

        WallpaperFilePathText = path;
        WallpaperStatusText = "正在后台加载背景资源…";
        IsWallpaperLoading = true;
        WallpaperProgress = 0;
        try
        {
            // 先把选择结果写入窗口档案并实时保存
            item.State.BackgroundPath = path;
            item.State.Background = DetectBackgroundType(path);
            PersistState(item.State);

            // 后台加载（真实 WallpaperService：图片解码缩放 / 视频 MF 解码 / repkg 解包均在线程池执行）
            await _services.Wallpaper.UpdateWallpaperAsync(item.Hwnd, path);
            WallpaperProgress = 100;
            WallpaperStatusText = $"背景已加载（类型：{DetectBackgroundType(path)}）。";
        }
        catch (Exception ex)
        {
            WallpaperStatusText = "背景加载失败。";
            ShowInfo(InfoBarSeverity.Error, $"背景加载失败：{ex.Message}");
        }
        finally
        {
            IsWallpaperLoading = false;
        }
    }

    /// <summary>按扩展名推断背景类型。</summary>
    private static BackgroundType DetectBackgroundType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ImageExtensions.Contains(ext))
        {
            return BackgroundType.Image;
        }
        if (VideoExtensions.Contains(ext))
        {
            return BackgroundType.Video;
        }
        if (ext == ".pkg")
        {
            return BackgroundType.Pkg;
        }
        return BackgroundType.None;
    }

    // ---------- 设置 ----------

    partial void OnWinTipsEnabledChanged(bool value)
    {
        // 开关直接控制 WinTipsIntegrationService 是否对外弹出提示
        //（开启且找到 WinTips.exe 时进程调用其 CLI 浮窗，否则走本地回退事件）
        _services.WinTips.IsEnabled = value;
        ShowInfo(InfoBarSeverity.Informational, value
            ? "WinTips 提示已开启：收到 NumLock 状态变化时将在此显示提示。"
            : "WinTips 提示已关闭。");
    }

    partial void OnFpsOverlayEnabledChanged(bool value)
    {
        _fpsMonitor.Enabled = value;
        OnPropertyChanged(nameof(FpsOverlayVisibility));
    }

    // ---------- 配置持久化 ----------

    /// <summary>把窗口档案并入配置并实时保存（ConfigurationService 内部防抖批量落盘）。</summary>
    private void PersistState(WindowState state)
    {
        if (!_appConfig.Windows.Contains(state))
        {
            _appConfig.Windows.Add(state);
        }
        _services.Configuration.Update(_appConfig);
    }

    // ---------- 提示 ----------

    /// <summary>在 InfoBar 显示一条提示（切回 UI 线程）。</summary>
    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        _dispatcherQueue.TryEnqueue(() =>
        {
            InfoBarSeverity = severity;
            InfoBarMessage = message;
            IsInfoBarOpen = true;
        });
    }
}
