using System.Reflection;
using System.Runtime.InteropServices;

namespace WinCanvas.App.Services;

/// <summary>
/// 关于页要展示的版本与路径信息（纯读取，无副作用）。
///
/// 为什么单独一个类而不是塞进 MainViewModel：这些值全是【进程级只读元数据】，
/// 与界面状态无关，也不该被配置持久化拖着。放 VM 里会让 MainViewModel 再多一项
/// 与它 11 项职责无关的活。
///
/// 版本口径：ProductVersion 带 git 提交哈希（由 Directory.Build.props 的
/// <c>WinCanvasVersion</c> 派生，全仓库单一来源），FileVersion 是四段数值 ——
/// 两个都显示，因为排查「用户装的到底是哪一版」时哈希才是确定的。
/// </summary>
internal static class AppInfo
{
    /// <summary>产品版本 + 提交哈希，如 <c>0.3.0+0bb2de9…</c>。</summary>
    public static string ProductVersion { get; } = Read(() =>
    {
        var attr = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? FileVersion ?? "未知";
    });

    /// <summary>四段文件版本，如 <c>0.3.0.0</c>。</summary>
    public static string FileVersion { get; } = Read(() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知");

    /// <summary>.NET 运行时描述，如 <c>.NET 10.0.x</c>。</summary>
    public static string Runtime { get; } = Read(() => RuntimeInformation.FrameworkDescription);

    /// <summary>Windows App SDK（WinUI 3）程序集版本；取不到就说明运行时没加载起来。</summary>
    public static string WindowsAppSdkVersion { get; } = Read(() =>
    {
        var asm = typeof(Microsoft.UI.Xaml.Application).Assembly.GetName().Version;
        return asm?.ToString() ?? "未知";
    });

    /// <summary>操作系统与内部版本号（圆角需要 22621+，这行是用户自查的第一站）。</summary>
    public static string OperatingSystem { get; } = Read(() =>
    {
        var v = Environment.OSVersion.Version;
        return $"Windows {v.Major}.{v.Minor} (build {v.Build})";
    });

    /// <summary>进程架构。</summary>
    public static string Architecture { get; } = Read(() => RuntimeInformation.ProcessArchitecture.ToString());

    /// <summary>
    /// 分发形态：产物目录里有 coreclr.dll 就是 self-contained（目标机无需装 .NET）。
    /// 这一行直接回答「我电脑上没装 .NET 能不能跑」。
    /// </summary>
    public static string DeploymentMode { get; } = Read(() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"))
            ? "Self-contained（运行时随包分发，目标机器无需安装 .NET / Windows App Runtime）"
            : "Framework-dependent（需要机器上已安装对应运行时）");

    /// <summary>配置文件目录。</summary>
    public static string ConfigDirectory { get; } = Read(() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinCanvas"));

    /// <summary>诊断日志目录。</summary>
    public static string LogDirectory { get; } = Read(() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCanvas", "logs"));

    /// <summary>壁纸解包缓存目录。</summary>
    public static string CacheDirectory { get; } = Read(() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCanvas", "wallpaper-cache"));

    /// <summary>仓库地址。</summary>
    public const string RepositoryUrl = "https://github.com/chenmoulaile/WinCanvas";

    /// <summary>第三方组件与许可证声明文件名（仓库根）。</summary>
    public const string ThirdPartyNoticesFile = "THIRD-PARTY-NOTICES.md";

    /// <summary>是否以管理员权限运行（UIPI 引导要看的状态）。</summary>
    public static string ElevationState { get; } = Read(
        () => WinCanvas.Core.Services.ProcessElevation.IsElevated ? "管理员" : "标准用户");

    /// <summary>
    /// 元数据读取兜底：任何一项取不到都不允许把关于页整个搞崩，
    /// 显示「未知」就够了 —— 这一页的作用正是「程序还能打开时告诉你它是什么版本」。
    /// </summary>
    private static string Read(Func<string> reader)
    {
        try
        {
            return reader() ?? "未知";
        }
        catch
        {
            return "未知";
        }
    }
}
