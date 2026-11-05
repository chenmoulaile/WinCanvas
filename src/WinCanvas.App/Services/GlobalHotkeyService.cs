using WinCanvas.Core.Services;
using System.Runtime.InteropServices;
using WinCanvas.App.Native;
using WinCanvas.Core.Models;

namespace WinCanvas.App.Services;

/// <summary>
/// 全局热键的注册与派发（v0.2.0 新增）。
///
/// 语义要点：
/// · 「全局」= 本程序不在前台、甚至收在托盘里也能触发，靠的是 RegisterHotKey
///   在系统层截获按键。它【优先于焦点窗口】，所以键位取错会直接影响用户在
///   别的软件里的输入 —— 校验与冲突上报不是可选项。
/// · 注册/注销全部在 <see cref="MessagePumpWindow"/> 的泵线程上执行
///   （WM_HOTKEY 投给注册线程，见 InvokeAsync 的注释）。
/// · 一条热键被别的程序占了，不影响其它热键：逐条注册、逐条报告，
///   绝不做成「有一条失败就整批不注册」。
/// </summary>
internal sealed class GlobalHotkeyService : IDisposable
{
    /// <summary>
    /// 热键 Id 基址。Id 直接由动作枚举值偏移得到，好处是 WM_HOTKEY 的 wParam
    /// 能反查回动作而不必维护第二张表；基址避开托盘菜单 Id 区间。
    /// </summary>
    private const int HotkeyIdBase = 0x5000;

    private readonly MessagePumpWindow _window;

    /// <summary>当前已成功注册的热键 Id → 动作。</summary>
    private readonly Dictionary<int, HotkeyAction> _registered = new();

    private bool _disposed;

    /// <summary>热键命中（已解析成功能动作）。在泵线程触发，订阅方需自行封送到 UI。</summary>
    internal event Action<HotkeyAction>? Triggered;

    /// <summary>
    /// 一次 <see cref="ApplyAsync"/> 的结果：失败清单（动作 + 可直接显示的原因）。
    /// </summary>
    /// <param name="Failures">未能注册的热键。</param>
    internal sealed record ApplyResult(IReadOnlyList<(HotkeyAction Action, string Reason)> Failures)
    {
        /// <summary>是否全部注册成功。</summary>
        public bool AllOk => Failures.Count == 0;
    }

    public GlobalHotkeyService(MessagePumpWindow window)
    {
        _window = window;
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>WM_HOTKEY 命中：wParam 是注册时给的 Id，反查回动作后对外抛。</summary>
    private void OnHotkeyPressed(int id)
    {
        // 只认自己注册过的 Id：同一条消息线程上若还有别处注册的热键，
        // 不该被这里当成壁纸/窗口操作误触发。
        if (_registered.TryGetValue(id, out var action))
        {
            Triggered?.Invoke(action);
        }
    }

    /// <summary>把动作映射成注册用的 Id。</summary>
    private static int IdOf(HotkeyAction action) => HotkeyIdBase + (int)action;

    /// <summary>
    /// 按 <paramref name="bindings"/> 重建全部注册（先全撤再注册，语义上是「替换」）。
    /// 在泵线程执行，异步等待结果。
    /// </summary>
    /// <param name="bindings">期望生效的绑定表（含未启用的项，内部会过滤）。</param>
    internal async Task<ApplyResult> ApplyAsync(IReadOnlyList<HotkeyBinding> bindings)
    {
        var failures = new List<(HotkeyAction, string)>();

        var result = await _window.InvokeAsync<FailureList>(() =>
        {
            var list = new FailureList();

            // 1) 撤掉上一轮的全部注册。不先撤会撞 HOTKEY_ALREADY_REGISTERED(1409)——
            //    同一个窗口重复注册同一组合也会失败。
            foreach (int id in _registered.Keys.ToList())
            {
                ShellNativeMethods.UnregisterHotKey(_window.Hwnd, id);
            }

            _registered.Clear();

            // 2) 逐条注册。同一条内再按「组合重复」防一次：
            //    配置被手改成两条相同组合时，第二条必然失败，与其让系统报错
            //    不如在这里给出「与 X 用了同一组合」这种人话。
            var usedCombinations = new HashSet<(uint, uint)>();

            foreach (var binding in bindings)
            {
                if (!binding.Enabled || binding.VirtualKey == 0)
                {
                    continue;
                }

                uint mods = (uint)binding.Modifiers;
                uint vk = (uint)binding.VirtualKey;

                if (!usedCombinations.Add((mods, vk)))
                {
                    list.Add(binding.Action,
                        $"与列表中另一条使用了同一个组合（{HotkeyTable.Format(binding)}）。");
                    continue;
                }

                int id = IdOf(binding.Action);
                if (ShellNativeMethods.RegisterHotKey(_window.Hwnd, id, mods, vk))
                {
                    _registered[id] = binding.Action;
                    continue;
                }

                int err = Marshal.GetLastWin32Error();

                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED：被别的程序占了。
                // 这是最常见失败，必须给出「去改键」的可执行指引而不是甩个错误码。
                // 文案按「失败 + 原因 + 怎么办」三要素组织（换键或关掉该条都算出路）。
                string reason = err == 1409
                    ? $"{HotkeyTable.Format(binding)} 已被系统或其它程序占用（错误 1409），请到设置里换一个组合，或在快捷键列表关闭该条。"
                    : $"注册 {HotkeyTable.Format(binding)} 失败（Win32 错误 {err}）。请到设置里换一个组合；若反复出现请把日志反馈给开发者。";

                usedCombinations.Remove((mods, vk));
                list.Add(binding.Action, reason);
            }

            return list;
        }, TimeSpan.FromSeconds(5));

        if (result is null)
        {
            // 泵线程没响应（正忙或已回收）：如实报告，不假装成功
            failures.Add((HotkeyAction.ShowHideMainWindow, "系统消息线程未在 5 秒内响应，热键未重新注册。"));
            return new ApplyResult(failures);
        }

        return new ApplyResult(result.Items);
    }

    /// <summary>当前已生效的热键数量（诊断与界面提示用）。</summary>
    internal int ActiveCount
    {
        get { return _registered.Count; }
    }

    /// <summary>
    /// 注销全部热键并断开事件。进程退出时系统也会自动回收，
    /// 但显式注销能立刻把按键【还给别的程序】—— 全局热键占着不放是不礼貌的。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.HotkeyPressed -= OnHotkeyPressed;

        IntPtr hwnd = _window.Hwnd;
        if (hwnd != IntPtr.Zero)
        {
            foreach (int id in _registered.Keys)
            {
                ShellNativeMethods.UnregisterHotKey(hwnd, id);
            }
        }

        _registered.Clear();
    }

    /// <summary>泵线程上收集失败项的小容器（InvokeAsync 要求返回引用类型）。</summary>
    private sealed class FailureList
    {
        internal List<(HotkeyAction Action, string Reason)> Items { get; } = new();

        internal void Add(HotkeyAction action, string reason) => Items.Add((action, reason));
    }
}
