namespace WinCanvas.Core.Interfaces;

/// <summary>
/// 壁纸 / 动态背景渲染服务（本轮只定义接口，不实现；由后续工作包提供
/// 基于 Win2D / MediaPlayer 的具体实现）。
/// </summary>
public interface IWallpaperService
{
    /// <summary>
    /// 为指定窗口初始化壁纸渲染并加载背景资源。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="path">背景资源路径（图片 / 视频文件或 pkg 包路径）。</param>
    Task InitializeWallpaperAsync(IntPtr hwnd, string path);

    /// <summary>
    /// 运行期切换指定窗口的背景资源。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="path">新的背景资源路径。</param>
    Task UpdateWallpaperAsync(IntPtr hwnd, string path);

    /// <summary>
    /// 移除指定窗口的背景，销毁宿主与会话，让目标窗口恢复自己的绘制内容（v0.2.0 引入）。
    /// 之前只有「设置 / 切换」没有「清除」——用户挂上背景后只能靠关掉目标窗口来摆脱它。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    Task RemoveWallpaperAsync(IntPtr hwnd);

    /// <summary>
    /// 查询指定窗口当前是否已挂着「同一背景文件」的活动壁纸会话（WP3 引入）。
    /// 供启动 / 刷新时的档案恢复管线判重：会话已存在则跳过重复加载
    /// （图片重复解码、pkg 重复解包都是纯浪费）。实现直接查询服务内部的
    /// 会话字典（<c>_sessions</c>），不引入新的锁体系。
    /// </summary>
    /// <param name="hwnd">承载壁纸的窗口句柄。</param>
    /// <param name="path">背景资源路径（与档案里保存的一致）。</param>
    /// <returns>true = 该窗口已有同一背景文件的活动会话（恢复可跳过）。</returns>
    bool IsBackgroundActive(IntPtr hwnd, string path);

    /// <summary>
    /// 暂停渲染（例如系统进入省电模式或窗口不可见时）。
    /// </summary>
    void PauseRendering();

    /// <summary>
    /// 恢复渲染。
    /// </summary>
    void ResumeRendering();

    /// <summary>
    /// 销毁全部壁纸会话与宿主窗口（应用退出时调用，WP1-A 引入）。
    /// 同步语义：返回时全部 WinEvent 钩子已卸载、全部宿主窗口已销毁（内部经宿主专用
    /// 消息线程往返完成销毁，跨线程 DestroyWindow 非法）。
    /// </summary>
    void ShutdownAll();
}
