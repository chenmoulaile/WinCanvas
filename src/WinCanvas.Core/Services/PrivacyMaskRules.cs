namespace WinCanvas.Core.Services;

/// <summary>
/// 「窗口隐私模式」失焦遮蔽的纯决策规则（隐私窗口工作包）。
/// 把「哪些窗口此刻该遮蔽 / 该恢复、目标 alpha 是多少」的全部计算抽成无副作用静态函数，
/// 供 MainViewModel 在 UI 线程调用，也供单元测试钉住语义（不牵扯 Win32）。
///
/// 核心语义：
/// - 遮蔽目标 alpha = min(<see cref="MaskAlphaDefault"/>, 档案 alpha)——档案值比遮蔽值更透明时
///   取档案值，遮蔽绝不把窗口变得【比用户设置】更可见；
/// - 恢复 alpha = 档案 alpha（<see cref="PrivacyWatchedWindow.ProfileAlpha"/>），绝不硬编码 255——
///   用户手动设置的透明度（档案值）是唯一恢复真源。
/// </summary>
public static class PrivacyMaskRules
{
    /// <summary>
    /// 失焦遮蔽的默认目标不透明度（0~255）。
    /// 取 30（≈12%）：内容肉眼不可辨（防肩窥的核心目标），窗口轮廓仍隐约可循，
    /// 用户点回窗口即可瞬间恢复。高于此值内容可能被看出，低于此值窗口几乎消失、
    /// 容易被误认为窗口丢失——两者都损害体验。
    /// </summary>
    public const byte MaskAlphaDefault = 30;

    /// <summary>一个被监视（已开启隐私模式）窗口的运行期快照。</summary>
    /// <param name="Hwnd">目标窗口句柄。</param>
    /// <param name="ProfileAlpha">档案不透明度（WindowState.Alpha，0~255）：恢复真源。</param>
    /// <param name="IsMasked">当前是否处于遮蔽态。</param>
    public readonly record struct PrivacyWatchedWindow(IntPtr Hwnd, byte ProfileAlpha, bool IsMasked);

    /// <summary>一条遮蔽/恢复迁移指令。</summary>
    /// <param name="Hwnd">目标窗口句柄。</param>
    /// <param name="TargetAlpha">应应用的 alpha（遮蔽 = 遮蔽值，恢复 = 档案值）。</param>
    /// <param name="Masked">迁移后的遮蔽态（true = 遮蔽，false = 恢复）。</param>
    public readonly record struct PrivacyMaskTransition(IntPtr Hwnd, byte TargetAlpha, bool Masked);

    /// <summary>
    /// 计算遮蔽目标 alpha：min(<see cref="MaskAlphaDefault"/>, 档案 alpha)。
    /// </summary>
    /// <param name="profileAlpha">档案不透明度（0 全透明 ~ 255 不透明）。</param>
    /// <returns>遮蔽时应对窗口设置的 alpha。</returns>
    public static byte ComputeMaskAlpha(byte profileAlpha)
    {
        return Math.Min(MaskAlphaDefault, profileAlpha);
    }

    /// <summary>
    /// 前台窗口变化后的迁移决策：对每个被监视窗口，与前台一致的恢复、不一致的遮蔽。
    /// 输入输出皆为值快照，无副作用。
    /// </summary>
    /// <param name="foreground">新的前台窗口句柄（调用方保证非零）。</param>
    /// <param name="watched">当前全部被监视窗口（含各自档案 alpha 与遮蔽态）。</param>
    /// <returns>需要执行的迁移清单（已处于目标态的窗口不产生迁移，避免无谓 Win32 调用）。</returns>
    public static List<PrivacyMaskTransition> DecideTransitions(
        IntPtr foreground, IReadOnlyList<PrivacyWatchedWindow> watched)
    {
        var transitions = new List<PrivacyMaskTransition>(watched.Count);

        foreach (var w in watched)
        {
            if (w.Hwnd == foreground)
            {
                if (w.IsMasked)
                {
                    transitions.Add(new PrivacyMaskTransition(w.Hwnd, w.ProfileAlpha, Masked: false));
                }
            }
            else if (!w.IsMasked)
            {
                transitions.Add(new PrivacyMaskTransition(w.Hwnd, ComputeMaskAlpha(w.ProfileAlpha), Masked: true));
            }
        }

        return transitions;
    }

    /// <summary>
    /// 初始遮蔽决策（监听启动 / 列表刷新 / 开关重开时）：把所有「尚未遮蔽」的被监视窗口
    /// 一次性遮蔽。语义依据：隐私窗口的本意就是「不在我眼前聚焦的内容都不该被看清」，
    /// 初始态下这些窗口必然不在前台（前台窗口不会被遮蔽，见 DecideTransitions）。
    /// </summary>
    /// <param name="watched">当前全部被监视窗口。</param>
    /// <returns>需要执行的遮蔽迁移清单（已遮蔽的跳过）。</returns>
    public static List<PrivacyMaskTransition> DecideInitialTransitions(
        IReadOnlyList<PrivacyWatchedWindow> watched)
    {
        var transitions = new List<PrivacyMaskTransition>(watched.Count);

        foreach (var w in watched)
        {
            if (!w.IsMasked)
            {
                transitions.Add(new PrivacyMaskTransition(w.Hwnd, ComputeMaskAlpha(w.ProfileAlpha), Masked: true));
            }
        }

        return transitions;
    }
}
