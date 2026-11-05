using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinCanvas.App.Native;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;

namespace WinCanvas.App.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的「隐私窗口」部分（隐私窗口工作包）：
/// ① 主窗口失焦遮罩：设置总开关 <see cref="PrivacyOverlayEnabled"/>（配置持久化，默认开启）+
///    运行期状态 <see cref="MainWindowPrivacyActive"/>（XAML 遮罩层的可见性真源）。
///    WinUI Window.Activated/Deactivated 由 MainWindow code-behind 只做装配转发到
///    <see cref="SetMainWindowFocusState"/>，判定逻辑全部在本类（严格 MVVM）。
/// ② 被管理窗口的失焦自动遮蔽：前台监听（<see cref="ForegroundWatcherService"/>，
///    后台专用线程）→ 事件封送回 UI 线程 → <see cref="PrivacyMaskRules"/> 纯决策 →
///    串行迁移链（<see cref="_privacyTransitionChain"/>）执行 Win32 与状态更新。
///
/// 并发纪律（详见 docs/adr-privacy-window.md 决策四）：
/// - 本类全部字段【只在 UI 线程读写】：监听回调经 DispatcherQueue.TryEnqueue 封送；
///   迁移链节点从 UI 线程发起、await 以默认上下文续接（WinUI UI 线程装有
///   DispatcherQueue 同步上下文，跨 await 续接回 UI 线程），同一窗口的遮蔽/恢复
///   严格按事件顺序串行，不存在并发写者，故不加锁——加锁反而掩盖「锁外误用」。
/// - <see cref="_privacyWatcher"/> 自身线程安全（内部 _stateGate，同 KeyboardIndicatorService 规格），
///   其事件在 pump 线程触发，仅经封送后才触碰本类字段。
/// </summary>
public partial class MainViewModel
{
    // ---------- 主窗口遮罩 ----------

    /// <summary>设置区开关「主窗口失焦显示隐私遮罩」的存储字段（镜像 AppConfig.PrivacyOverlayOnDeactivated）。</summary>
    private bool _privacyOverlayEnabled = true;

    /// <summary>反向同步标志：初始化从配置回填开关时不触发回写与提示。</summary>
    private bool _suppressPrivacyOverlaySync;

    /// <summary>
    /// 主窗口当前是否处于前台。初值 true：窗口构造即激活，首个 Deactivated 事件到来之前
    /// 不应显示遮罩（若初值 false 且配置回填早于首个 Activated 事件，会闪现一次假遮罩）。
    /// </summary>
    private bool _mainWindowActivated = true;

    /// <summary>遮罩层可见性真源（XAML 经 PrivacyOverlayVisibility 绑定）。</summary>
    private bool _mainWindowPrivacyActive;

    /// <summary>
    /// 设置区开关「主窗口失焦显示隐私遮罩」（默认开启，配置持久化）。
    /// 手写属性而非 [ObservableProperty]：需要携带「写配置 + 提示 + 立即刷新遮罩」三件副作用，
    /// 与 <see cref="AutoRestoreOnStartup"/> 同一模式（复用 SetProperty 完成变更通知）。
    /// </summary>
    public bool PrivacyOverlayEnabled
    {
        get => _privacyOverlayEnabled;
        set
        {
            if (SetProperty(ref _privacyOverlayEnabled, value))
            {
                OnPrivacyOverlayEnabledChanged(value);
            }
        }
    }

    /// <summary>开关变化：写回内存配置走既有防抖落盘通道，并按新开关立即刷新遮罩态。</summary>
    private void OnPrivacyOverlayEnabledChanged(bool value)
    {
        if (_suppressPrivacyOverlaySync)
        {
            return; // 初始化从配置回填：值本就来自配置，不回写、不提示
        }

        _appConfig.PrivacyOverlayOnDeactivated = value;
        PersistConfig();
        UpdateMainWindowPrivacy();

        ShowInfo(InfoBarSeverity.Informational, value
            ? "隐私遮罩已开启：主窗口失焦（切到其它窗口）时自动遮蔽内容，点回本窗口立即恢复。该选择已保存。"
            : "隐私遮罩已关闭：主窗口失焦时不再自动遮蔽内容，旁人可直视窗口内容。该选择已保存。");
    }

    /// <summary>主窗口遮罩层当前是否可见（XAML 绑定 PrivacyOverlayVisibility）。</summary>
    public bool MainWindowPrivacyActive => _mainWindowPrivacyActive;

    /// <summary>遮罩层可见性（供 x:Bind 的 Visibility 绑定使用）。</summary>
    public Visibility PrivacyOverlayVisibility =>
        _mainWindowPrivacyActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 主窗口焦点状态入口（由 MainWindow code-behind 在 Activated/Deactivated 里调用，UI 线程）。
    /// code-behind 只装配事件，判定与状态更新都收口在这里。
    /// </summary>
    /// <param name="isActivated">true = 窗口已激活（前台）；false = 已失焦。</param>
    public void SetMainWindowFocusState(bool isActivated)
    {
        _mainWindowActivated = isActivated;
        UpdateMainWindowPrivacy();
    }

    /// <summary>按「总开关 + 前台状态」重算遮罩态，变更时通知 XAML。</summary>
    private void UpdateMainWindowPrivacy()
    {
        bool active = _privacyOverlayEnabled && !_mainWindowActivated;
        if (SetProperty(ref _mainWindowPrivacyActive, active, nameof(MainWindowPrivacyActive)))
        {
            OnPropertyChanged(nameof(PrivacyOverlayVisibility));
        }
    }

    // ---------- 被管理窗口的失焦自动遮蔽 ----------

    /// <summary>
    /// 前台监听服务（后台专用线程 + 全局 EVENT_SYSTEM_FOREGROUND 钩子）。
    /// 进程内单实例，随 ViewModel Dispose 一起停；启停由隐私开关数量驱动（见 SyncPrivacyWatcher）。
    /// </summary>
    private readonly ForegroundWatcherService _privacyWatcher = new();

    /// <summary>
    /// 隐私迁移任务链的尾节点：同一时刻最多一条迁移在执行，新迁移等上一条完成再执行。
    /// 只在 UI 线程读写（发起方全部经 TryEnqueue 封送在 UI 线程）。
    /// </summary>
    private Task _privacyTransitionChain = Task.CompletedTask;

    /// <summary>
    /// 构造函数里接好监听事件（pump 线程触发 → TryEnqueue 封送 UI 线程）。
    /// </summary>
    private void AttachPrivacyWatcher()
    {
        _privacyWatcher.ForegroundChanged += foreground =>
            _dispatcherQueue.TryEnqueue(() => ApplyPrivacyForeground(foreground));

        _privacyWatcher.Diagnostics += message => App.LogFatal(null, $"前台监听：{message}");

        // 安装失败必须在界面上说出来（失败 + 原因 + 怎么办），不能只写日志装没看见
        _privacyWatcher.HookInstallFailed += reason => ShowInfo(InfoBarSeverity.Warning,
            $"窗口隐私的失焦监听未能启动：{reason}。" +
            "隐私开关仍可设置，但失焦时不会自动遮蔽内容；请重启应用重试，若反复出现请检查系统策略或反馈日志。");
    }

    /// <summary>前台已切换（UI 线程）：按当前被监视窗口快照决策迁移并投入串行链。</summary>
    private void ApplyPrivacyForeground(IntPtr foreground)
    {
        var watched = CollectPrivacyWatched();
        if (watched.Count == 0)
        {
            return;
        }

        var transitions = PrivacyMaskRules.DecideTransitions(foreground, watched);
        if (transitions.Count > 0)
        {
            EnqueuePrivacyTransitions(transitions);
        }
    }

    /// <summary>收集当前全部被监视窗口快照（UI 线程调用）：开启隐私且句柄有效的列表项。</summary>
    private List<PrivacyMaskRules.PrivacyWatchedWindow> CollectPrivacyWatched()
    {
        var watched = new List<PrivacyMaskRules.PrivacyWatchedWindow>();
        foreach (var item in Windows)
        {
            if (item.State.PrivacyEnabled && item.Hwnd != IntPtr.Zero)
            {
                watched.Add(new PrivacyMaskRules.PrivacyWatchedWindow(item.Hwnd, item.State.Alpha, item.IsPrivacyMasked));
            }
        }

        return watched;
    }

    /// <summary>把一批隐私迁移指令追加到串行链（UI 线程调用）。</summary>
    private void EnqueuePrivacyTransitions(IReadOnlyList<PrivacyMaskRules.PrivacyMaskTransition> transitions)
    {
        _privacyTransitionChain = RunPrivacyTransitionLinkAsync(_privacyTransitionChain, transitions);
    }

    /// <summary>
    /// 迁移链的单个节点：等上一条跑完（顺序语义），再逐条执行遮蔽/恢复。
    /// 【必须从 UI 线程调用】：await previous 以默认上下文续接回 UI 线程，节点体全程在
    /// UI 线程——状态字段读写与 Win32 调用发起都在同一线程，链条天然互斥。
    /// Win32 主体由 WindowControlService 派发线程池，UI 线程只停留毫秒级的状态同步。
    /// </summary>
    private async Task RunPrivacyTransitionLinkAsync(
        Task previous, IReadOnlyList<PrivacyMaskRules.PrivacyMaskTransition> transitions)
    {
        // 链条不因单个节点失败而断：上一节点抛异常只吞掉（其内部已各自报告），本节点照常执行
        try
        {
            await previous.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 前一节点已自行报告失败；链的顺序语义不受影响
        }

        foreach (var transition in transitions)
        {
            var item = Windows.FirstOrDefault(w => w.Hwnd == transition.Hwnd);
            if (item is null)
            {
                continue; // 窗口已关闭或已被刷新出列表：不再操作句柄（防止打到复用句柄的新窗口）
            }

            if (transition.Masked && !item.State.PrivacyEnabled)
            {
                continue; // 迁移决策与开关状态竞争（用户刚关掉开关）：以开关为准，跳过遮蔽
            }

            try
            {
                await _services.WindowControl.SetTransparencyAsync(transition.Hwnd, transition.TargetAlpha);
                item.IsPrivacyMasked = transition.Masked;
            }
            catch (Win32Exception wex) when (wex.NativeErrorCode == 1400)
            {
                // 1400 = 句柄已失效（窗口已关闭）：静默降级——把遮蔽态对齐到目标值，
                // 防止之后每次前台事件都重试死句柄；刷新列表后该条目自然消失。
                item.IsPrivacyMasked = transition.Masked;
                App.LogFatal(null, $"隐私迁移跳过：窗口 0x{transition.Hwnd:X} 已关闭（Win32 1400），不再重试。");
            }
            catch (Exception ex)
            {
                // 失败时【不】更新遮蔽态：保持旧值，下一次前台事件按旧状态重试（自愈语义）
                ShowInfo(InfoBarSeverity.Error, ProcessElevation.DescribeFailure(ex,
                    transition.Masked
                        ? $"遮蔽窗口「{item.DisplayName}」（隐私模式失焦遮蔽）"
                        : $"恢复窗口「{item.DisplayName}」的透明度（隐私模式聚焦恢复）"));
            }
        }
    }

    /// <summary>
    /// 列表重建后的隐私初始同步（UI 线程调用，刷新管线末尾）：
    /// ① 按是否有隐私窗口启停前台监听；② 把所有「开启隐私且未遮蔽」的窗口一次性遮蔽
    /// （初始态下它们必然不在前台；已在链上排队但未生效的会重复遮蔽一次，幂等无害）。
    /// </summary>
    private void ApplyPrivacyInitialState()
    {
        var watched = CollectPrivacyWatched();

        if (watched.Count == 0)
        {
            SyncPrivacyWatcher();
            return;
        }

        SyncPrivacyWatcher();

        var transitions = PrivacyMaskRules.DecideInitialTransitions(watched);
        if (transitions.Count > 0)
        {
            EnqueuePrivacyTransitions(transitions);
        }
    }

    /// <summary>
    /// 按隐私开关数量启停前台监听（UI 线程调用；由列表项的隐私变化回调触发）。
    /// Start 幂等（已在运行时空操作）；全部开关关闭时 Stop 释放钩子线程，
    /// 不给系统留一条无人使用的全局钩子。
    /// </summary>
    internal void SyncPrivacyWatcher()
    {
        bool anyEnabled = false;
        foreach (var item in Windows)
        {
            if (item.State.PrivacyEnabled)
            {
                anyEnabled = true;
                break;
            }
        }

        if (anyEnabled)
        {
            _privacyWatcher.Start();
        }
        else
        {
            _privacyWatcher.Stop();
        }
    }

    /// <summary>列表项隐私开关变化回调（WindowItemViewModel 注入的动作）：维护监听启停。</summary>
    private void OnItemPrivacyChanged()
    {
        SyncPrivacyWatcher();
    }

    /// <summary>
    /// 档案恢复完成后的补遮蔽（WP3 恢复管线路径调用，UI 线程）。
    /// 为什么需要：恢复管线与初始遮蔽是两条独立的异步链，恢复管线可能【晚于】遮蔽链
    /// 把 alpha 抬回档案值（表现为隐私窗口短暂甚至持续可见，且遮蔽态已登记为 true、
    /// 前台事件不会再触发遮蔽——卡死在可见态）。恢复完成后从这条链补一次遮蔽决策，
    /// 串行链保证它落在恢复之后，竞态收口。
    /// </summary>
    private void QueuePrivacyRemaskAfterRestore(WindowItemViewModel item)
    {
        if (!item.State.PrivacyEnabled || item.Hwnd == IntPtr.Zero)
        {
            return;
        }

        if (item.IsPrivacyMasked)
        {
            return; // 链上遮蔽已生效（或已排队）：无需重复
        }

        if (ShellNativeMethods.GetForegroundWindow() == item.Hwnd)
        {
            return; // 正在前台：不该遮蔽（用户正在使用）
        }

        EnqueuePrivacyTransitions(new List<PrivacyMaskRules.PrivacyMaskTransition>
        {
            new(item.Hwnd, PrivacyMaskRules.ComputeMaskAlpha(item.State.Alpha), Masked: true),
        });
    }

    /// <summary>隐私配置回填（InitializeAsync 调用）：抑制回写，恢复用户上次的选择。</summary>
    internal void InitializePrivacyFromConfig()
    {
        _suppressPrivacyOverlaySync = true;
        PrivacyOverlayEnabled = _appConfig.PrivacyOverlayOnDeactivated;
        _suppressPrivacyOverlaySync = false;

        AttachPrivacyWatcher();
    }

    /// <summary>停止前台监听（Dispose 路径）：幂等，进程退出前显式卸钩子。</summary>
    private void DisposePrivacy()
    {
        _privacyWatcher.Dispose();
    }
}
