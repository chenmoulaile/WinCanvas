using System.IO;
using WinCanvas.App.Native;

namespace WinCanvas.App.Services;

/// <summary>
/// 系统托盘图标（v0.2.0 新增）。
///
/// 为什么这个产品必须有托盘：WinCanvas 是【长期驻留】的窗口辅助工具——
/// 用户设好置顶/透明/壁纸之后就不再看主界面，但需要随时能调。
/// 迁移前它只有一个大主窗口：要么占着任务栏和 Z 序，要么干脆不能退出界面，
/// 「常驻」这个基本使用形态在 UI 上是缺失的。托盘补的就是这一环。
///
/// 实现要点：
/// · 复用 <see cref="MessagePumpWindow"/> 的隐藏窗口收回调，不额外开线程；
/// · 未调用 NIM_SETVERSION，走经典 lParam=鼠标消息号 的语义，Win10/11 一致；
/// · NOTIFYICONDATA.cbSize 必须用 Marshal.SizeOf 取，填错的表现是
///   「Shell_NotifyIcon 返回 true 但图标根本不出现」，最难查；
/// · 一切失败都只降级不抛：托盘起不来时主窗口照常可用。
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    /// <summary>菜单项 Id 基址（避开 0，0 是 TrackPopupMenu 的「未选中」）。</summary>
    private const int MenuIdBase = 0x1000;

    private enum MenuCommand
    {
        ShowHide = MenuIdBase,
        Refresh = MenuIdBase + 1,
        TogglePause = MenuIdBase + 2,
        ToggleWinTips = MenuIdBase + 3,
        Separator = MenuIdBase + 4,
        Exit = MenuIdBase + 5,
    }

    private readonly MessagePumpWindow _window;
    private readonly uint _iconId;

    private IntPtr _hIcon;
    private bool _added;
    private bool _disposed;

    /// <summary>图标是否已成功上托盘。</summary>
    internal bool IsActive => _added;

    /// <summary>「显示 / 隐藏主窗口」（左键单击、双击、菜单项都会触发）。在泵线程触发。</summary>
    internal event Action? ShowHideRequested;

    /// <summary>「刷新窗口列表」。在泵线程触发。</summary>
    internal event Action? RefreshRequested;

    /// <summary>「暂停 / 恢复壁纸渲染」。在泵线程触发。</summary>
    internal event Action? TogglePauseRequested;

    /// <summary>「WinTips NumLock 提示」开关。在泵线程触发。</summary>
    internal event Action? ToggleWinTipsRequested;

    /// <summary>「退出」。在泵线程触发。</summary>
    internal event Action? ExitRequested;

    /// <summary>暂停项的勾选态由外部提供（菜单要显示 ✓）。</summary>
    internal Func<bool>? IsWallpaperPaused;

    /// <summary>WinTips 项的勾选态由外部提供。</summary>
    internal Func<bool>? IsWinTipsEnabled;

    /// <summary>主窗口当前是否可见（决定菜单项写「显示」还是「隐藏」）。</summary>
    internal Func<bool>? IsMainWindowVisible;

    /// <summary>
    /// 在 <paramref name="window"/> 上挂一个托盘图标。
    /// </summary>
    /// <param name="window">共享的隐藏消息窗口。</param>
    /// <param name="iconPath">.ico 文件完整路径。</param>
    /// <param name="tooltip">悬停文本。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    internal TrayIconService(MessagePumpWindow window, string iconPath, string tooltip, out string? error)
    {
        _window = window;
        _iconId = 1;
        error = null;

        _hIcon = LoadIconHandle(iconPath);
        if (_hIcon == IntPtr.Zero)
        {
            error = $"托盘图标加载失败（找不到或无法解析 {Path.GetFileName(iconPath)}）";
            return;
        }

        // 右键 / 中键 → 弹菜单；左键抬起与双击 → 显示/隐藏主窗口
        _window.TrayMenuRequested += OnTrayMenuRequested;
        _window.TrayMouseMessage += OnTrayMouseMessage;

        if (!Send(_iconId, ShellNativeMethods.NIM_ADD, tooltip, withIcon: true))
        {
            error = "Shell_NotifyIcon(NIM_ADD) 被系统拒绝";
            _window.TrayMenuRequested -= OnTrayMenuRequested;
            _window.TrayMouseMessage -= OnTrayMouseMessage;
            return;
        }

        _added = true;
    }

    /// <summary>按系统小图标尺寸从 .ico 取 HICON；取不到再退回默认尺寸。</summary>
    private static IntPtr LoadIconHandle(string iconPath)
    {
        if (!File.Exists(iconPath))
        {
            return IntPtr.Zero;
        }

        int cx = ShellNativeMethods.GetSystemMetrics(ShellNativeMethods.SM_CXSMICON);
        if (cx <= 0)
        {
            cx = 16;
        }

        IntPtr h = ShellNativeMethods.LoadImageW(
            IntPtr.Zero, iconPath, ShellNativeMethods.IMAGE_ICON, cx, cx,
            ShellNativeMethods.LR_LOADFROMFILE);

        if (h == IntPtr.Zero)
        {
            // 指定尺寸拿不到（比如 .ico 里没这个尺寸）时让系统自己挑
            h = ShellNativeMethods.LoadImageW(
                IntPtr.Zero, iconPath, ShellNativeMethods.IMAGE_ICON, 0, 0,
                ShellNativeMethods.LR_LOADFROMFILE | ShellNativeMethods.LR_DEFAULTSIZE);
        }

        return h;
    }

    /// <summary>发一条 Shell_NotifyIcon 命令。cbSize 每次现算，避免结构变更后大小不符。</summary>
    private bool Send(uint id, uint message, string? tip, bool withIcon)
    {
        var data = new ShellNativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ShellNativeMethods.NOTIFYICONDATA>(),
            hWnd = _window.Hwnd,
            uID = id,
            uCallbackMessage = ShellNativeMethods.WM_TRAYICON,
            hIcon = _hIcon,
            szTip = tip ?? string.Empty,
        };

        uint flags = ShellNativeMethods.NIF_MESSAGE;
        if (withIcon)
        {
            flags |= ShellNativeMethods.NIF_ICON;
        }

        if (!string.IsNullOrEmpty(tip))
        {
            flags |= ShellNativeMethods.NIF_TIP;
        }

        data.uFlags = flags;

        try
        {
            return ShellNativeMethods.Shell_NotifyIconW(message, ref data);
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, $"Shell_NotifyIcon(0x{message:X}) 调用异常");
            return false;
        }
    }

    /// <summary>更新悬停文本（NIM_MODIFY）。</summary>
    internal void SetToolTip(string tip)
    {
        if (!_added)
        {
            return;
        }

        // 截到 127 字符：szTip 是 128 的定长缓冲区，超长会被静默截断或整条失败
        Send(_iconId, ShellNativeMethods.NIM_MODIFY,
            tip.Length > 127 ? tip[..127] : tip, withIcon: false);
    }

    /// <summary>左键抬起 / 双击 → 显示或隐藏主窗口。</summary>
    private void OnTrayMouseMessage(uint mouseMessage)
    {
        if (mouseMessage is ShellNativeMethods.WM_LBUTTONUP or ShellNativeMethods.WM_LBUTTONDBLCLK)
        {
            ShowHideRequested?.Invoke();
        }
    }

    /// <summary>
    /// 构建并跟踪托盘菜单。必须在泵线程同步执行（TrackPopupMenuEx 是模态的）。
    /// </summary>
    private void OnTrayMenuRequested(ShellNativeMethods.POINT at)
    {
        IntPtr menu = ShellNativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            bool visible = IsMainWindowVisible?.Invoke() ?? true;
            Append(menu, MenuCommand.ShowHide, visible ? "显示 / 隐藏主窗口" : "显示主窗口（已最小化到托盘）");
            Append(menu, MenuCommand.Refresh, "刷新窗口列表");
            Append(menu, MenuCommand.TogglePause,
                (IsWallpaperPaused?.Invoke() ?? false) ? "恢复壁纸渲染" : "暂停壁纸渲染");
            Append(menu, MenuCommand.ToggleWinTips,
                (IsWinTipsEnabled?.Invoke() ?? false) ? "关闭 NumLock 提示" : "开启 NumLock 提示");

            ShellNativeMethods.AppendMenuW(menu, ShellNativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
            Append(menu, MenuCommand.Exit, "退出 WinCanvas");

            // 置前后才能正确跟踪鼠标；菜单结束后补一条 WM_NULL 是社区公认的必要收尾
            ShellNativeMethods.SetForegroundWindow(_window.Hwnd);

            int chosen = ShellNativeMethods.TrackPopupMenuEx(
                menu,
                ShellNativeMethods.TPM_LEFTALIGN
                    | ShellNativeMethods.TPM_BOTTOMALIGN
                    | ShellNativeMethods.TPM_RETURNCMD,
                at.X, at.Y, _window.Hwnd, IntPtr.Zero);

            ShellNativeMethods.PostMessageW(_window.Hwnd, ShellNativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            Invoke(chosen);
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, "托盘菜单处理异常");
        }
        finally
        {
            ShellNativeMethods.DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, MenuCommand command, string text)
    {
        ShellNativeMethods.AppendMenuW(menu, ShellNativeMethods.MF_STRING,
            new IntPtr((int)command), text);
    }

    /// <summary>把菜单返回的 Id 转成对应动作。</summary>
    private void Invoke(int commandId)
    {
        switch ((MenuCommand)commandId)
        {
            case MenuCommand.ShowHide:
                ShowHideRequested?.Invoke();
                break;
            case MenuCommand.Refresh:
                RefreshRequested?.Invoke();
                break;
            case MenuCommand.TogglePause:
                TogglePauseRequested?.Invoke();
                break;
            case MenuCommand.ToggleWinTips:
                ToggleWinTipsRequested?.Invoke();
                break;
            case MenuCommand.Exit:
                ExitRequested?.Invoke();
                break;
            default:
                break; // 0 = 用户按 Esc 取消
        }
    }

    /// <summary>摘图标、退订、释放 HICON。幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_added)
        {
            Send(_iconId, ShellNativeMethods.NIM_DELETE, null, withIcon: false);
            _added = false;
        }

        _window.TrayMenuRequested -= OnTrayMenuRequested;
        _window.TrayMouseMessage -= OnTrayMouseMessage;

        if (_hIcon != IntPtr.Zero)
        {
            ShellNativeMethods.DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
