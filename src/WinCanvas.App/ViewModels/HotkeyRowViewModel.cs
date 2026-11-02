using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using WinCanvas.App.Services;
using WinCanvas.Core.Models;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// 快捷键设置界面里的一行：一个功能 + 它的组合键 + 启用开关。
///
/// 为什么单独一个类而不是直接把 <see cref="HotkeyBinding"/> 绑上去：
/// 配置模型是 POCO（要序列化、要稳定），界面要的是可通知属性 +
/// 「人读文本」（Ctrl+Alt+T 而不是 mods=3 vk=84）。
/// 混在一起会让 config.json 的格式被 UI 需求牵着走。
/// </summary>
public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly Action<HotkeyRowViewModel> _changed;

    /// <summary>请求进入按键捕获（由外层 ViewModel 实现单行互斥）。</summary>
    private readonly Action<HotkeyRowViewModel> _capture;

    /// <summary>本行对应的功能动作。</summary>
    public HotkeyAction Action { get; }

    /// <summary>功能名（左列标题）。</summary>
    public string Label { get; }

    /// <summary>这个功能做什么（副标题，降低「看懂名字但不知道后果」的成本）。</summary>
    public string Description { get; }

    /// <summary>
    /// 组合键的人读文本（按钮上显示的当前值）。
    /// 由 <see cref="CommitCapture"/> 与外部重算时更新。
    /// </summary>
    [ObservableProperty]
    private string _comboText = "未设置";

    /// <summary>本行是否正在等待用户按键（进入捕获态时按钮文案变「请按下组合键…」）。</summary>
    [ObservableProperty]
    private bool _isCapturing;

    /// <summary>是否启用该热键。</summary>
    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>注册失败 / 校验失败的原因；非空时界面标红并显示。</summary>
    [ObservableProperty]
    private string? _error;

    /// <summary>错误文本的可见性（XAML 不能直接绑 string? 判空，这里代劳）。</summary>
    public Visibility ErrorVisibility =>
        string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(ErrorVisibility));
    }

    /// <summary>当前绑定的原始数据（供持久化）。</summary>
    public HotkeyBinding Binding { get; private set; }

    /// <summary>点击组合键按钮 → 进入捕获态。供 XAML 直接绑定。</summary>
    public ICommand CaptureCommand { get; }

    public HotkeyRowViewModel(HotkeyAction action, string label, string description,
        HotkeyBinding binding, Action<HotkeyRowViewModel> changed, Action<HotkeyRowViewModel> capture)
    {
        Action = action;
        Label = label;
        Description = description;
        Binding = binding;
        _changed = changed;
        _capture = capture;

        CaptureCommand = new RelayCommand(() => _capture(this));

        _comboText = HotkeyTable.Format(binding);
        _enabled = binding.Enabled;
    }

    /// <summary>
    /// 接受一次按键捕获结果：写入绑定并刷新显示。
    /// </summary>
    /// <param name="modifiers">修饰键组合。</param>
    /// <param name="virtualKey">主键虚拟键码。</param>
    /// <returns>校验通过返回 null；否则返回可直接显示的原因（并【不】写入）。</returns>
    public string? CommitCapture(HotkeyModifiers modifiers, int virtualKey, IList<HotkeyBinding> all)
    {
        var candidate = new HotkeyBinding
        {
            Action = Action,
            Modifiers = modifiers,
            VirtualKey = virtualKey,
            Enabled = true,
        };

        // 先校验再落库：把非法组合写进配置，下次启动注册失败，用户根本不知道是哪一步错的
        string? problem = HotkeyTable.Validate(candidate, all);
        if (problem != null)
        {
            Error = problem;
            return problem;
        }

        Binding = candidate;
        Enabled = true;
        ComboText = HotkeyTable.Format(candidate);
        Error = null;
        _changed(this);
        return null;
    }

    /// <summary>退出捕获态并恢复文案显示。</summary>
    public void EndCapture()
    {
        IsCapturing = false;
        ComboText = HotkeyTable.Format(Binding);
    }

    /// <summary>重算显示文本（外部改完配置或注册结果后调用）。</summary>
    public void RefreshDisplay()
    {
        ComboText = HotkeyTable.Format(Binding);
    }

    partial void OnEnabledChanged(bool value)
    {
        Binding.Enabled = value;
        _changed(this);
    }

    /// <summary>恢复该动作的默认组合键。</summary>
    public void ResetToDefault()
    {
        Binding = HotkeyTable.GetDefault(Action);
        Enabled = true;
        Error = null;
        RefreshDisplay();
        _changed(this);
    }
}
