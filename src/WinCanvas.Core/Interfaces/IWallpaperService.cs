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
