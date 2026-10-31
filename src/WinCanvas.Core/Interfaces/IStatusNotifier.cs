namespace WinCanvas.Core.Interfaces;

/// <summary>
/// 状态提示服务（本轮只定义接口，不实现；具体 UI 呈现由后续工作包提供，
/// 例如托盘气泡 / Toast 通知）。
/// </summary>
public interface IStatusNotifier
{
    /// <summary>
    /// 提示 NumLock 键的锁定状态（供键盘指示器功能使用）。
    /// </summary>
    /// <param name="isLocked">true = NumLock 已锁定，false = 未锁定。</param>
    void ShowNumLockStatus(bool isLocked);
}
