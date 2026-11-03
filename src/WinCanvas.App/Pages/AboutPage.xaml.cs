using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using WinCanvas.App.Services;

namespace WinCanvas.App.Pages;

/// <summary>
/// 关于页。内容全是进程级只读元数据（见 <see cref="AppInfo"/>），
/// 因此不走 ViewModel 绑定而是在构造时填好：这一页在「程序还能打开但功能全坏」时
/// 才最有用，越少依赖越好。
/// </summary>
public sealed partial class AboutPage : UserControl
{
    public AboutPage()
    {
        InitializeComponent();

        VersionLine.Text = "版本 " + AppInfo.ProductVersion;
        CommitLine.Text = "文件版本 " + AppInfo.FileVersion
            + " · 架构 " + AppInfo.Architecture;
        RuntimeLine.Text = ".NET 运行时：" + AppInfo.Runtime;
        SdkLine.Text = "Windows App SDK：" + AppInfo.WindowsAppSdkVersion;
        OsLine.Text = "操作系统：" + AppInfo.OperatingSystem;
        DeployLine.Text = "分发形态：" + AppInfo.DeploymentMode;
        ElevationLine.Text = "当前权限：" + AppInfo.ElevationState;

        ConfigPathLine.Text = AppInfo.ConfigDirectory;
        LogPathLine.Text = AppInfo.LogDirectory;
        CachePathLine.Text = AppInfo.CacheDirectory;

        TryLoadIcon();
    }

    /// <summary>
    /// 图标从程序目录的 WinCanvas.ico 直接读。
    /// 不用 <c>ms-appx</c>：非打包部署没有包 URI 方案，那样只会得到一个空框。
    /// </summary>
    private void TryLoadIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "WinCanvas.ico");
            if (File.Exists(iconPath))
            {
                AppIconImage.Source = new BitmapImage(new Uri(iconPath));
            }
        }
        catch
        {
            // 图标读不出来不影响这一页的正事（版本与路径），留空即可
        }
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e) =>
        OpenInExplorer(AppInfo.ConfigDirectory);

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e) =>
        OpenInExplorer(AppInfo.LogDirectory);

    /// <summary>
    /// 在资源管理器里打开目录。目录不存在时先建出来 —— 用户点这个按钮就是想去看日志，
    /// 「目录还不存在」不是有用的答复（日志目录只在首次写入时才被创建）。
    /// </summary>
    private static void OpenInExplorer(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            App.LogFatal(ex, $"打开目录失败：{directory}");
        }
    }
}
