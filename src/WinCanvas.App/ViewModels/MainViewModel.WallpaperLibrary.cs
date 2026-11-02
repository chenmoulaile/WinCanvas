using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.Core.Models;
using WinCanvas.Wallpaper;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的壁纸库与声音部分（v0.3.0 新增）。
///
/// 两块能力：
/// ① <b>壁纸库自动检索</b>：扫描 Wallpaper Engine 的项目目录，把「上百个文件夹里
///    翻 project.json」变成「面板里点一下」。检索本身在 <c>WallpaperEngineLibrary</c>，
///    本文件只做界面状态与挂接动作。
/// ② <b>壁纸声音开关</b>：默认关闭。迁移前视频加载器把 IsMuted 硬编码成 true，
///    用户【没有任何办法】让带配乐的壁纸出声 —— 现在有了开关，且默认保持静音。
///
/// 单开 partial 文件的原因与 Shell 部分相同：MainViewModel 已经承担过多职责，
/// 新能力在文件层面先隔离。
/// </summary>
public partial class MainViewModel
{
    /// <summary>检索到的可挂接条目。</summary>
    public ObservableCollection<WallpaperLibraryItemViewModel> LibraryItems { get; } = new();

    /// <summary>正在检索（按钮禁用 + 进度环）。</summary>
    [ObservableProperty]
    private bool _isLibraryScanning;

    /// <summary>检索按钮是否可点（= 不在检索中）。用正向语义避免在 XAML 里做布尔取反。</summary>
    public bool CanScanLibrary => !IsLibraryScanning;

    partial void OnIsLibraryScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanScanLibrary));
    }

    /// <summary>检索结果说明（条目数、耗时、根目录数）。</summary>
    [ObservableProperty]
    private string _libraryStatusText = "尚未检索。可点「检索壁纸库」，或先添加扫描目录。";

    /// <summary>当前生效的扫描根（换行拼接，供界面显示）。</summary>
    [ObservableProperty]
    private string _libraryRootsText = "（未配置，将使用自动探测结果）";

    /// <summary>库面板的过滤关键字。</summary>
    [ObservableProperty]
    private string _libraryFilter = string.Empty;

    /// <summary>过滤后的显示集合（面板绑定的是这一份）。</summary>
    public ObservableCollection<WallpaperLibraryItemViewModel> FilteredLibraryItems { get; } = new();

    /// <summary>
    /// 是否允许壁纸发声。<b>默认 false</b>。
    /// 变更时立即作用到已在播的会话（由服务层负责），并持久化。
    /// </summary>
    [ObservableProperty]
    private bool _wallpaperAudioEnabled;

    /// <summary>声音关闭时的说明文案（含音频线索的壁纸需要被解释，否则像 bug）。</summary>
    public string AudioHintText => _wallpaperAudioEnabled
        ? "壁纸声音已开启：带音轨的视频壁纸会外放（音量 60%，可在系统音量合成器单独调）。"
        : "壁纸声音已关闭（默认）。带音乐的视频壁纸不会出声 —— 需要听就在下方打开开关。";

    partial void OnWallpaperAudioEnabledChanged(bool value)
    {
        _services.Wallpaper.AudioEnabled = value;
        _appConfig.WallpaperAudioEnabled = value;
        PersistConfig();
        OnPropertyChanged(nameof(AudioHintText));
    }

    partial void OnLibraryFilterChanged(string value)
    {
        RebuildFilteredLibrary();
    }

    /// <summary>
    /// 把服务的音频设置与配置的扫描根接到界面状态上。
    /// 由 <c>InitializeAsync</c> 在配置加载后调用。
    /// </summary>
    internal void InitializeWallpaperLibrary()
    {
        // 直接写字段而非属性：启动回填不该触发「持久化 + 重设播放器」这条写路径
        _wallpaperAudioEnabled = _appConfig.WallpaperAudioEnabled;
        _services.Wallpaper.AudioEnabled = _appConfig.WallpaperAudioEnabled;
        OnPropertyChanged(nameof(WallpaperAudioEnabled));
        OnPropertyChanged(nameof(AudioHintText));

        RefreshRootsText();

        // 配了根目录就自动检索一次：用户装好壁纸库后不该还要先点一下按钮
        if (_appConfig.WallpaperLibraryRoots.Count > 0)
        {
            _ = ScanLibraryAsync();
        }
    }

    private void RefreshRootsText()
    {
        List<string> roots = EffectiveRoots();
        _libraryRootsText = roots.Count == 0
            ? "（未配置，自动探测也没找到 —— 请手动添加 Wallpaper Engine 的 myprojects 目录）"
            : string.Join(Environment.NewLine, roots);
        OnPropertyChanged(nameof(LibraryRootsText));
    }

    /// <summary>
    /// 实际参与扫描的根：用户配置的在前，自动探测的并入并去重。
    /// 之所以两者都要：自动探测覆盖不了「非 Steam 安装」（实测本机就是这种），
    /// 而只靠手工配置又会让 Steam 用户每次都要自己指路径。
    /// </summary>
    private List<string> EffectiveRoots()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string r in _appConfig.WallpaperLibraryRoots)
        {
            if (!string.IsNullOrWhiteSpace(r) && seen.Add(r.Trim()))
            {
                result.Add(r.Trim());
            }
        }

        foreach (string detected in WallpaperEngineLibrary.DetectRoots())
        {
            if (seen.Add(detected))
            {
                result.Add(detected);
            }
        }

        return result;
    }

    /// <summary>检索壁纸库。</summary>
    [RelayCommand]
    private async Task ScanLibraryAsync()
    {
        if (_isLibraryScanning)
        {
            return; // 防重入：检索虽快，大库 + 网络盘仍可能几秒，连点会排多条扫描
        }

        _isLibraryScanning = true;
        OnPropertyChanged(nameof(IsLibraryScanning));

        try
        {
            List<string> roots = EffectiveRoots();
            RefreshRootsText();

            var entries = await Task.Run(() => WallpaperEngineLibrary.Scan(roots));

            LibraryItems.Clear();
            foreach (WallpaperLibraryEntry entry in entries)
            {
                LibraryItems.Add(new WallpaperLibraryItemViewModel(entry));
            }

            RebuildFilteredLibrary();

            int usable = LibraryItems.Count(i => i.IsUsable);
            _libraryStatusText = roots.Count == 0
                ? $"没有可扫描的目录：请点「添加目录」指定 Wallpaper Engine 的 projects/myprojects。"
                : $"检索到 {LibraryItems.Count} 项（可挂接 {usable}），来自 {roots.Count} 个目录。";
        }
        catch (Exception ex)
        {
            _libraryStatusText = $"检索失败：{ex.Message}";
            ShowInfo(InfoBarSeverity.Error, $"壁纸库检索失败：{ex.Message}");
        }
        finally
        {
            _isLibraryScanning = false;
            OnPropertyChanged(nameof(IsLibraryScanning));
        }
    }

    /// <summary>重建库面板的过滤视图。</summary>
    private void RebuildFilteredLibrary()
    {
        string keyword = _libraryFilter?.Trim() ?? string.Empty;
        FilteredLibraryItems.Clear();

        foreach (WallpaperLibraryItemViewModel item in LibraryItems)
        {
            if (keyword.Length == 0
                || item.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || item.KindLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                FilteredLibraryItems.Add(item);
            }
        }
    }

    /// <summary>
    /// 添加一个扫描目录。<paramref name="pickedPath"/> 由 View 的文件夹选择器传入
    /// （ViewModel 不直接碰 WinUI 对话框，保持可测试）。
    /// </summary>
    /// <param name="pickedPath">用户选中的目录；null 表示取消。</param>
    [RelayCommand]
    private async Task AddLibraryRootAsync(string? pickedPath)
    {
        if (string.IsNullOrWhiteSpace(pickedPath))
        {
            return;
        }

        string normalized = pickedPath.Trim().Trim('"');

        if (_appConfig.WallpaperLibraryRoots.Any(
                r => string.Equals(r.Trim().Trim('"'), normalized, StringComparison.OrdinalIgnoreCase)))
        {
            _libraryStatusText = $"该目录已在扫描列表里：{normalized}";
            return;
        }

        _appConfig.WallpaperLibraryRoots.Add(normalized);
        PersistConfig();
        RefreshRootsText();

        await ScanLibraryAsync();
    }

    /// <summary>移除一个扫描目录（按界面显示的列表项）。</summary>
    [RelayCommand]
    private async Task RemoveLibraryRootAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        int removed = _appConfig.WallpaperLibraryRoots.RemoveAll(r =>
            string.Equals(r.Trim().Trim('"'), path.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            PersistConfig();
            RefreshRootsText();
            await ScanLibraryAsync();
        }
    }

    /// <summary>
    /// 把选中的库条目挂到当前选中的窗口上。
    /// </summary>
    /// <param name="item">库面板里的条目。</param>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ApplyLibraryItemAsync(WallpaperLibraryItemViewModel? item)
    {
        var selected = Selected;
        if (selected == null || item == null)
        {
            ShowInfo(InfoBarSeverity.Warning, "请先在左侧选择一个窗口，再从壁纸库里挑一项。");
            return;
        }

        if (!item.IsUsable)
        {
            ShowInfo(InfoBarSeverity.Warning, $"「{item.Title}」无法挂接：{item.UnusableReason}");
            return;
        }

        string path = item.Path;
        WallpaperFilePathText = path;
        WallpaperStatusText = "正在后台加载背景资源…";
        IsWallpaperLoading = true;
        WallpaperProgress = 0;

        try
        {
            selected.State.BackgroundPath = path;
            selected.State.Background = DetectBackgroundType(path);
            PersistState(selected.State);

            await _services.Wallpaper.UpdateWallpaperAsync(selected.Hwnd, path);

            WallpaperProgress = 100;
            WallpaperStatusText = $"背景已加载（{item.KindLabel}：{item.Title}）。";
        }
        catch (Exception ex)
        {
            WallpaperStatusText = "背景加载失败。";
            ShowInfo(InfoBarSeverity.Error,
                $"「{item.Title}」加载失败：{ex.Message}");
        }
        finally
        {
            IsWallpaperLoading = false;
        }
    }

    /// <summary>开 / 关壁纸声音（全局热键与托盘菜单用）。</summary>
    private void ToggleWallpaperAudio()
    {
        WallpaperAudioEnabled = !WallpaperAudioEnabled;
        ShowInfo(InfoBarSeverity.Informational, WallpaperAudioEnabled
            ? "壁纸声音已开启。"
            : "壁纸声音已关闭（静音）。");
    }
}
