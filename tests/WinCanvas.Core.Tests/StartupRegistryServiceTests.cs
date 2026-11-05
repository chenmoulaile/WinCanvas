using Microsoft.Win32;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// 开机自启动工作包的用例：
/// ① Run 值构造纯函数 <see cref="StartupRegistryService.BuildRunCommand"/> ——
///    路径含空格不带引号时登录启动会把第一个空格前当程序名（路径被截断），必须带引号；
/// ② 读 / 开 / 关的真实注册表冒烟 —— 全部在 HKCU 下本测试专用的隔离子键上进行，
///    不触碰用户真实的 Run 键；用例结束整体删除隔离键。
/// </summary>
public sealed class StartupRegistryServiceTests : IDisposable
{
    /// <summary>本用例类的隔离子键（HKCU\Software\WinCanvas.Core.Tests\&lt;随机&gt;）。</summary>
    private readonly string _subKeyPath;

    private readonly StartupRegistryService _service;

    public StartupRegistryServiceTests()
    {
        _subKeyPath = $@"Software\WinCanvas.Core.Tests\{Guid.NewGuid():N}";
        _service = new StartupRegistryService(Registry.CurrentUser, _subKeyPath);
    }

    public void Dispose()
    {
        try
        {
            // 整键删除（含子值），测试残留零痕迹
            Registry.CurrentUser.DeleteSubKeyTree(_subKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论（隔离子键不干扰任何真实功能）
        }
    }

    // ---------- BuildRunCommand：Run 值构造纯函数 ----------

    [Fact]
    public void 含空格路径_必须带引号()
    {
        // 验收钉死项：Run 值按「可执行行」解析，含空格不带引号时登录启动路径被截断，
        // 表现为「开机自启动开着，但登录后程序没起来」
        string command = StartupRegistryService.BuildRunCommand(@"C:\Program Files\WinCanvas\WinCanvas.App.exe");

        Assert.StartsWith("\"", command);
        Assert.EndsWith("\"", command);
        Assert.Equal("\"C:\\Program Files\\WinCanvas\\WinCanvas.App.exe\"", command);
    }

    [Fact]
    public void 不含空格路径_同样带引号_路径原样保留()
    {
        // 无空格路径也统一加引号：引号对任何路径都合法，统一口径避免「有时带有时不带」的两套解析
        string command = StartupRegistryService.BuildRunCommand(@"C:\WinCanvas\WinCanvas.App.exe");

        Assert.Equal("\"C:\\WinCanvas\\WinCanvas.App.exe\"", command);
    }

    [Fact]
    public void 空路径_原样返回不抛()
    {
        // 执行路径有先行校验（Enable 抛 ArgumentException），纯函数只保证自身不炸
        Assert.Equal(string.Empty, StartupRegistryService.BuildRunCommand(string.Empty));
        Assert.Equal("  ", StartupRegistryService.BuildRunCommand("  "));
    }

    // ---------- 读 / 开 / 关：隔离注册表冒烟 ----------

    [Fact]
    public void 初始状态_隔离键不存在_如实显示为关()
    {
        // 「手工删除注册表值后界面如实显示为关」的口径：键/值不存在 = 关，不抛异常
        Assert.False(_service.IsEnabled());
        Assert.Null(_service.GetCommand());
    }

    [Fact]
    public void Enable_写入带引号全路径_IsEnabled为真()
    {
        _service.Enable(@"C:\Program Files\WinCanvas\WinCanvas.App.exe");

        Assert.True(_service.IsEnabled());
        Assert.Equal("\"C:\\Program Files\\WinCanvas\\WinCanvas.App.exe\"", _service.GetCommand());
    }

    [Fact]
    public void Enable_exe路径变化_重开开关以当前路径覆盖旧条目()
    {
        _service.Enable(@"C:\old\WinCanvas.App.exe");
        _service.Enable(@"C:\new\WinCanvas.App.exe");

        // 覆盖而不是追加/失败：exe 移动后重开一次开关即完成迁移
        Assert.Equal("\"C:\\new\\WinCanvas.App.exe\"", _service.GetCommand());
    }

    [Fact]
    public void Enable_空路径_抛ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => _service.Enable(string.Empty));
        Assert.False(_service.IsEnabled()); // 失败不留下半条目
    }

    [Fact]
    public void Disable_删除条目_重复关闭幂等不抛()
    {
        _service.Enable(@"C:\WinCanvas\WinCanvas.App.exe");

        _service.Disable();
        Assert.False(_service.IsEnabled());

        _service.Disable(); // 再关一次（键已无该值）必须安静成功
        Assert.False(_service.IsEnabled());
    }

    [Fact]
    public void EnableDisable_不触碰同键下其它值名()
    {
        // 「只管理自己名下的值名」：先放一个第三方模拟条目，开关全程它必须原样保留
        using (RegistryKey key = Registry.CurrentUser.CreateSubKey(_subKeyPath, writable: true))
        {
            key.SetValue("SomeOtherApp", "\"C:\\other\\app.exe\"", RegistryValueKind.String);
        }

        _service.Enable(@"C:\WinCanvas\WinCanvas.App.exe");
        _service.Disable();

        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(_subKeyPath)!)
        {
            Assert.NotNull(key);
            Assert.Equal("\"C:\\other\\app.exe\"", key!.GetValue("SomeOtherApp"));
            Assert.Null(key.GetValue(StartupRegistryService.ValueName));
        }
    }
}
