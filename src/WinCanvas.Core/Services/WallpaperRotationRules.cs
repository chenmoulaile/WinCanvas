namespace WinCanvas.Core.Services;

/// <summary>
/// 壁纸库轮换的选条目规则（纯函数，供轮换执行路径与单测共用）。
///
/// round-robin 语义：从可用条目里选「上一张之后的第一张还没播过的」；
/// 本轮已播过的跳过（一轮内不重复）；一轮全部播完返回 null（调用方清空
/// 「本轮已播」集合后再次调用即从头再来，轮完从头再来）。
/// 放在 Core 而不是 App：它不依赖任何 UI 类型，且选条目是轮换功能
/// 唯一真正会咬人的地方（重复 / 跳过 / 回绕），必须可进现有测试工程。
/// </summary>
public static class WallpaperRotationRules
{
    /// <summary>
    /// 选取下一条轮换条目。
    /// </summary>
    /// <param name="usablePaths">当前扫描判定可用的条目路径（按库展示顺序传入）。</param>
    /// <param name="playedThisRound">本轮已播路径集合（调用方持有并跨轮次维护）。
    /// 必须以 OrdinalIgnoreCase 比较器创建（Windows 路径大小写不敏感）。</param>
    /// <param name="lastPlayedPath">上一张的路径；null 或已不在列表中（被删除 / 重扫移位）时从头开始。</param>
    /// <returns>选中的路径；<paramref name="usablePaths"/> 为空返回 null；
    /// 本轮可用条目全部已播返回 null（一轮播完，调用方清空集合再调即重开一轮）。</returns>
    public static string? PickNextRotationPath(
        IReadOnlyList<string> usablePaths,
        ISet<string> playedThisRound,
        string? lastPlayedPath)
    {
        if (usablePaths.Count == 0)
        {
            return null;
        }

        // 起点游标：上一张的下一个位置（wrap 由取模处理）；
        // 上一张已不在列表（删除 / 重扫移位）时游标归零，从头转。
        int start = 0;
        if (lastPlayedPath is not null)
        {
            for (int i = 0; i < usablePaths.Count; i++)
            {
                if (string.Equals(usablePaths[i], lastPlayedPath, StringComparison.OrdinalIgnoreCase))
                {
                    start = i + 1;
                    break;
                }
            }
        }

        // 从游标起环形扫一圈：第一个「本轮未播过」的即入选；
        // 一整圈都播过 = 本轮播完，返回 null 让调用方重开一轮。
        for (int offset = 0; offset < usablePaths.Count; offset++)
        {
            string candidate = usablePaths[(start + offset) % usablePaths.Count];
            if (!playedThisRound.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
