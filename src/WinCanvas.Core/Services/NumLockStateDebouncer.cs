namespace WinCanvas.Core.Services;

/// <summary>
/// NumLock 状态去抖器（WP4 自 <see cref="KeyboardIndicatorService"/> 抽离的纯逻辑）：
/// 对同一开关状态的连续采样只放行一次翻转上报。
///
/// 为什么要抽成独立类型（而不是留在钩子回调内联实现）：
/// - 去抖语义（同值吞掉 / 异值放行 / 跨周期共享基线）是「NumLock 提示不风暴」的唯一防线，
///   值得被无 Win32、无钩子线程的单元测试直接钉住（总提示词 WP4 用例表：
///   「状态去抖逻辑：同状态连续事件只上报一次（钩子线程 mocked/抽离纯逻辑）」）；
/// - 抽离后钩子回调只负责「读真实开关位 → 喂给去抖器」，输入输出关系一目了然，
///   回调热路径仅多一次对象字段访问，无性能影响（替代原 Interlocked.Exchange 内联序列）。
///
/// 并发口径：原 <c>_lastReportedState</c> 的共享语义原样保留——
/// <see cref="_lastReportedState"/> 用 Interlocked 原子交换 / Volatile 读写，无锁；
/// 各钩子周期共享同一去抖器实例（跨周期也不重复上报同一状态），与抽取前完全一致。
/// </summary>
internal sealed class NumLockStateDebouncer
{
    /// <summary>
    /// 已上报的 NumLock 状态：-1 = 未知（未播种），0 = 未锁定，1 = 锁定。
    /// Interlocked 原子交换读写，无锁；见类型注释的并发口径。
    /// </summary>
    private int _lastReportedState = -1;

    /// <summary>当前基线状态（-1 = 未播种；0 = 未锁定；1 = 锁定）。诊断与测试断言用。</summary>
    public int LastReportedState => Volatile.Read(ref _lastReportedState);

    /// <summary>
    /// 播种基线（不产生上报语义）：钩子装好前读一次全局开关位写入基线，
    /// 使开启后的第一次真实翻转必然上报、且不会把「开启前就处于的旧状态」误报为变化。
    /// 对应原 <c>HookThreadProc</c> 中的 <c>Interlocked.Exchange</c> 播种语句。
    /// </summary>
    /// <param name="locked">当前全局 NumLock 开关位（true = 锁定）。</param>
    public void Seed(bool locked)
    {
        Volatile.Write(ref _lastReportedState, locked ? 1 : 0);
    }

    /// <summary>
    /// 喂入一次状态采样，返回是否为「状态翻转」（应上报）。
    /// 同状态连续事件（按住自动重复 / 同一翻转的另一时机读到同值）返回 false，由调用方抑制。
    /// </summary>
    /// <param name="locked">本次采样读到的 NumLock 开关位（true = 锁定）。</param>
    /// <returns>true = 与上次上报值不同，应上报新状态；false = 重复状态，应抑制。</returns>
    public bool OnSample(bool locked)
    {
        var now = locked ? 1 : 0;
        var previous = Interlocked.Exchange(ref _lastReportedState, now);
        return previous != now;
    }
}
