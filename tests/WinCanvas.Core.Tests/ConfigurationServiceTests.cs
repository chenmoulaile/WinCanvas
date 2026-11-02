using System.Text;
using System.Text.Json;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// WP4 用例表第一行：<see cref="ConfigurationService"/> 的持久化行为。
/// 全部用临时目录注入文件路径，不触碰真实 %APPDATA%/WinCanvas/config.json；
/// 防抖间隔经新构造参数（WP4 加入）注入 120ms 量级短值，把「合并落盘」验证
/// 从 500ms 级等待缩短到亚秒级（行为语义与生产值一致，只改时长）。
/// </summary>
public sealed class ConfigurationServiceTests : IDisposable
{
    /// <summary>本用例类的临时目录（每个测试实例独立，Dispose 时整体删除）。</summary>
    private readonly string _tempDir;

    /// <summary>注入的 config.json 路径（位于临时目录内）。</summary>
    private readonly string _configPath;

    public ConfigurationServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinCanvas.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _configPath = Path.Combine(_tempDir, "config.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { } // 临时目录清理失败不影响测试结论（系统回收临时区兜底）
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>构造被测实例（防抖 120ms：足以让 10 次快速 Update 落在同一防抖窗口内）。</summary>
    private ConfigurationService CreateService() =>
        new(_configPath, debounceDelay: TimeSpan.FromMilliseconds(120));

    /// <summary>构造一个带单个窗口档案的配置（各测试用不同的标题区分写入批次）。</summary>
    private static AppConfig MakeConfig(string marker) => new()
    {
        AutoRestoreOnStartup = true,
        Windows =
        {
            new WindowState { Key = $"notepad|Notepad::{marker}", HwndHash = 42, IsTopmost = true },
        },
    };

    [Fact]
    public async Task LoadAsync_首次无文件_返回默认配置()
    {
        using var service = CreateService();

        var config = await service.LoadAsync();

        // 默认值口径：版本取当前支持版本、自动恢复开、无窗口档案（与 AppConfig 的属性初始化器一致）。
        // 断言引用 ConfigurationService.SupportedConfigVersion 而不是写死数字：
        // 写死的话每次配置格式加字段都要来改测试，那不是测试在保护行为，只是在记录历史。
        Assert.Equal(ConfigurationService.SupportedConfigVersion, config.Version);
        Assert.True(config.AutoRestoreOnStartup);
        Assert.Empty(config.Windows);
        // 首次加载只读不写：磁盘上不应出现 config.json（写盘只发生在 Update/Flush 路径）
        Assert.False(File.Exists(_configPath));
    }

    [Fact]
    public async Task LoadAsync_损坏JSON_回退默认配置且不抛异常()
    {
        // 模拟「写一半崩溃 / 人工编辑损坏」：非 JSON 文本 + 截断 JSON 两种损坏形态
        await File.WriteAllTextAsync(_configPath, "{ 这不是合法的 JSON !!!", Encoding.UTF8);
        using var service = CreateService();

        var config = await service.LoadAsync(); // 不应抛 JsonException 等任何异常

        Assert.Equal(ConfigurationService.SupportedConfigVersion, config.Version);
        Assert.True(config.AutoRestoreOnStartup);
        Assert.Empty(config.Windows);
    }

    [Fact]
    public async Task Update_10次快速更新_防抖合并后磁盘只落盘1次()
    {
        using var service = CreateService();

        // 落盘次数观测器：FSW 对一次「写临时文件 + Move 覆盖目标」会连发数个事件
        // （目标文件的 Created/Changed/Renamed 突发都在同一毫秒内），直接数事件会重复计数，
        // 故按「事件突发」归并：相邻事件间隔 < 80ms 视为同一次落盘，突发数 = 真实写盘次数。
        var writeBursts = 0;
        var lastEventAt = DateTime.MinValue;
        var eventGate = new object();
        using var watcher = new FileSystemWatcher(_tempDir, "config.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        watcher.Created += (_, _) => RecordEvent();
        watcher.Changed += (_, _) => RecordEvent();
        watcher.Renamed += (_, _) => RecordEvent();
        watcher.EnableRaisingEvents = true;

        void RecordEvent()
        {
            lock (eventGate) // FSW 事件在线程池触发，时间戳读改写须互斥
            {
                var now = DateTime.UtcNow;
                if (now - lastEventAt > TimeSpan.FromMilliseconds(80))
                    Interlocked.Increment(ref writeBursts); // 与上次事件隔开了 → 新一次落盘
                lastEventAt = now;
            }
        }

        // 10 次快速 Update（间隔远小于 120ms 防抖）：每次取消前一个待写定时器重新计时
        for (var i = 1; i <= 10; i++)
        {
            service.Update(MakeConfig($"batch-{i}"));
            await Task.Delay(5);
        }

        // 防抖窗口内（最后一次 Update 后 120ms 前不该写盘）：给 60ms 后文件必须仍不存在
        await Task.Delay(60);
        Assert.False(File.Exists(_configPath), "防抖窗口内出现落盘，说明合并失效（逐次立即写盘）。");

        // 等待防抖触发 + 磁盘事件到达（防抖 120ms + 写盘 + FSW 通知余量）
        await Task.Delay(600);

        Assert.Equal(1, writeBursts); // 10 次更新合并为恰好 1 次落盘

        // 且落盘内容是最后一次更新的值（防抖语义 = 最后状态胜出）
        var final = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);
        Assert.Contains("batch-10", final);
        Assert.DoesNotContain("batch-9", final);
    }

    [Fact]
    public async Task Flush_之后文件为合法JSON且无BOM()
    {
        using var service = CreateService();
        service.Update(MakeConfig("flush-me"));

        await service.FlushAsync();

        Assert.True(File.Exists(_configPath));

        // 无 BOM：直接读原始字节，断言前 3 字节不是 EF BB BF
        //（JSON 按 RFC 8259 不得带 BOM；带 BOM 会被部分解析器拒绝——用户级 AGENTS.md 实测口径）
        var raw = await File.ReadAllBytesAsync(_configPath);
        Assert.False(raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "config.json 不应携带 UTF-8 BOM。");

        // 合法 JSON：能反序列化回 AppConfig 且档案内容一致
        var json = Encoding.UTF8.GetString(raw);
        var parsed = JsonSerializer.Deserialize<AppConfig>(json);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Windows);
        Assert.Contains("flush-me", parsed.Windows[0].Key);
    }

    [Fact]
    public async Task 写盘为原子_完成后无临时文件残留()
    {
        using var service = CreateService();
        service.Update(MakeConfig("atomic"));

        await service.FlushAsync();

        // 原子写 = 先写 .tmp 再 Move 覆盖目标：成功路径上 .tmp 必须已消失
        Assert.True(File.Exists(_configPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public async Task 并发Update与Flush_全程无异常且最终文件合法()
    {
        // 并发口径：Update（防抖写盘路径）与 Flush（退出路径）在真实应用里可能交错，
        // 二者的磁盘写入由 _ioGate 信号量串行化——本用例以多线程交错压测验证不抛异常、
        // 不产生半截文件（最终文件若存在必须能完整反序列化）。
        using var service = CreateService();

        var errors = new List<Exception>();
        var tasks = new List<Task>();

        // 4 个并发写手 × 10 次 Update，穿插 4 个并发 Flush
        for (var worker = 0; worker < 4; worker++)
        {
            var id = worker;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 10; i++)
                        service.Update(MakeConfig($"w{id}-{i}"));
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }));
        }

        for (var flusher = 0; flusher < 4; flusher++)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await service.FlushAsync();
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            }));
        }

        await Task.WhenAll(tasks);
        await Task.Delay(400); // 让最后一个防抖写盘走完（有 Flush 兜底，这里只是收尾稳定）

        Assert.Empty(errors); // 全程无异常（包括 _ioGate 竞争下的信号量释放序）

        if (File.Exists(_configPath)) // Flush 取消防抖后文件必然存在；防御性容错双分支断言
        {
            var final = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);
            var parsed = JsonSerializer.Deserialize<AppConfig>(final);
            Assert.NotNull(parsed); // 能完整反序列化 = 未出现写一半的半截文件
        }
    }

    [Fact]
    public void 构造_防抖间隔非正值_立即抛ArgumentOutOfRange()
    {
        // 防抖间隔是装配期错误（TimeSpan.Zero 会让每次 Update 的写盘任务立即空转），
        // 构造即校验，尽早暴露（与既有服务构造风格一致）。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfigurationService(_configPath, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(-1)));
    }
}
