using System.Text;
using System.Text.Json;
using WinCanvas.Core.Models;
using WinCanvas.Core.Services;
using Xunit;

namespace WinCanvas.Core.Tests;

/// <summary>
/// ConfigurationService 的并发与数据安全回归用例（v0.2.0 引入）。
///
/// 针对两条实测确认的缺陷：
/// ① 撕裂写盘：旧实现缓存调用方的【活对象引用】，防抖任务在线程池上序列化同一实例，
///    而 UI 线程同时在改集合 → 偶发 Collection was modified；Task 被 `_ =` 丢弃，
///    异常无人观察，配置静默丢失。
/// ② CTS 世代竞态：旧实现的防抖任务收尾时【无条件】Dispose/null 共享 _pendingSaveCts，
///    可能释放掉下一次 Update 新建的 CTS，使新任务抛 ObjectDisposedException
///    （旧代码只 catch OperationCanceledException）→ 最新改动永不落盘。
/// 这两条在修复前都没有用例覆盖，所以缺陷能长期存在。
/// </summary>
public sealed class ConfigurationServiceConcurrencyTests : IDisposable
{
    private readonly string _configPath;

    public ConfigurationServiceConcurrencyTests()
    {
        _configPath = Path.Combine(
            Path.GetTempPath(),
            "WinCanvasTests",
            $"concurrency-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
    }

    public void Dispose()
    {
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(_configPath)!,
                   Path.GetFileNameWithoutExtension(_configPath) + "*"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 临时文件清不掉不影响测试结论
            }
        }
    }

    private static AppConfig MakeConfig(string marker, int profileCount)
    {
        var config = new AppConfig();
        for (var i = 0; i < profileCount; i++)
        {
            config.Windows.Add(new WindowState
            {
                Key = $"{marker}|proc-{i}|class-{i}",
                IsTopmost = true,
                Alpha = 200,
            });
        }

        return config;
    }

    /// <summary>
    /// 缺陷①的回归：Update 之后继续从别的线程猛改同一个 AppConfig 实例，
    /// 防抖落盘必须既【不抛】也【不丢】——快照在锁内生成，之后与活对象无关。
    /// </summary>
    [Fact]
    public async Task Update之后并发修改活对象_落盘不抛异常且内容完整()
    {
        using var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(80));

        var live = MakeConfig("live", 40);
        service.Update(live);

        // 在防抖窗口内持续改活对象：这正是旧实现被撕裂的时机
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        var mutator = Task.Run(() =>
        {
            var round = 0;
            while (!cts.IsCancellationRequested)
            {
                live.Windows.Add(new WindowState { Key = $"mut-{round}", Alpha = (byte)(round % 256) });
                live.AutoRestoreOnStartup = round % 2 == 0;
                if (live.Windows.Count > 200)
                {
                    live.Windows.RemoveRange(0, 50);
                }

                round++;
            }
        });

        await mutator;
        await service.FlushAsync();

        // 落盘失败会被记入 LastSaveError（旧实现是整批吞掉，无从得知）
        Assert.Null(service.LastSaveError);

        var json = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);

        // 关键断言：文件是【完整合法】的 JSON，不是半截内容
        var reloaded = JsonSerializer.Deserialize<AppConfig>(json)
            ?? throw new InvalidOperationException("落盘内容反序列化失败。");
        Assert.NotEmpty(reloaded.Windows);
    }

    /// <summary>
    /// 缺陷②的回归：连续快速 Update（每次都换掉 CTS），最后一次的值必须落盘。
    /// 旧实现里前一个任务可能释放后一个任务的 CTS，导致最后几次改动永不写盘。
    /// </summary>
    [Fact]
    public async Task 连续快速Update_最后一个值必定落盘()
    {
        using var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(30));

        const int rounds = 60;
        for (var i = 1; i <= rounds; i++)
        {
            service.Update(MakeConfig($"gen-{i}", 3));
            // 刻意小于防抖间隔：每次都打断上一个定时器，制造世代交替
            await Task.Delay(8);
        }

        // 等最后一个防抖任务真正跑完
        await Task.Delay(300);

        Assert.Null(service.LastSaveError);
        Assert.True(File.Exists(_configPath), "连续 Update 后没有任何落盘发生。");

        var json = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);
        var reloaded = JsonSerializer.Deserialize<AppConfig>(json)!;

        // 最后一次的标记必须在，且倒数第二次的不能残留
        Assert.Contains($"gen-{rounds}", json);
        Assert.DoesNotContain($"gen-{rounds - 1}|", json);
        Assert.Equal(3, reloaded.Windows.Count);
    }

    /// <summary>
    /// Flush 必须取消防抖并把当前快照同步写完；随后的防抖任务不得再写一次旧内容。
    /// </summary>
    [Fact]
    public async Task Flush之后在途防抖任务不得覆盖()
    {
        using var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(120));

        service.Update(MakeConfig("before-flush", 2));
        await service.FlushAsync();

        service.Update(MakeConfig("after-flush", 5));
        await service.FlushAsync();

        // 等第一个防抖窗口过去：它应当因世代校验失败而让位，不写任何东西
        await Task.Delay(300);

        var json = await File.ReadAllTextAsync(_configPath, Encoding.UTF8);
        Assert.Contains("after-flush", json);
        Assert.DoesNotContain("before-flush", json);
    }

    /// <summary>
    /// 主文件损坏时应从 .bak 恢复，而不是直接回退默认值把用户档案清空。
    /// </summary>
    [Fact]
    public async Task 主文件损坏_从bak备份恢复()
    {
        using (var first = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(20)))
        {
            first.Update(MakeConfig("saved", 7));
            await first.FlushAsync();

            // 再写一次才会留下 .bak（File.Replace 顺带产出上一版）
            first.Update(MakeConfig("latest", 9));
            await first.FlushAsync();
        }

        Assert.True(File.Exists(_configPath + ".bak"), "覆盖写盘未留下 .bak 备份。");

        // 人为损坏主文件
        await File.WriteAllTextAsync(_configPath, "{ 半截的 JSO");

        using var second = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(20));
        var recovered = await second.LoadAsync();

        // .bak 是 File.Replace 替换下来的【上一版】内容，所以恢复得到的是「saved」那一份
        // （7 条）而不是当前版（9 条）。这正是备份应有的语义：主文件坏了，
        // 退回到最近一次已知可用的旧版本，而不是回退默认值把档案清零。
        Assert.Equal(7, recovered.Windows.Count);
        Assert.Contains(recovered.Windows, w => w.Key!.Contains("saved"));
        Assert.DoesNotContain(recovered.Windows, w => w.Key!.Contains("latest"));
    }

    /// <summary>
    /// 主文件与备份都不可用时：坏文件必须被【隔离保留】而不是留着被下次写盘覆盖。
    /// </summary>
    [Fact]
    public async Task 主文件与备份都损坏_隔离坏文件并回退默认()
    {
        await File.WriteAllTextAsync(_configPath, " totally not json ", Encoding.UTF8);
        await File.WriteAllTextAsync(_configPath + ".bak", " also not json ", Encoding.UTF8);

        using var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(20));
        var config = await service.LoadAsync();

        Assert.Equal(ConfigurationService.SupportedConfigVersion, config.Version);
        Assert.Empty(config.Windows);

        // 原坏文件被改名保留，用户仍有机会手工找回内容
        var quarantined = Directory.GetFiles(
            Path.GetDirectoryName(_configPath)!,
            Path.GetFileName(_configPath) + ".corrupt-*");
        Assert.NotEmpty(quarantined);
    }

    /// <summary>
    /// Dispose 是兜底路径：仍应把防抖窗口内未写的快照落盘（旧实现直接丢弃）。
    /// </summary>
    [Fact]
    public void Dispose会落盘剩余快照()
    {
        var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(400));
        service.Update(MakeConfig("on-dispose", 4));

        // 不 Flush、不等防抖，直接 Dispose
        service.Dispose();

        Assert.True(File.Exists(_configPath), "Dispose 未兜底落盘。");
        var json = File.ReadAllText(_configPath, Encoding.UTF8);
        Assert.Contains("on-dispose", json);
    }

    /// <summary>
    /// 写盘必须是【无 BOM】的合法 JSON，且原子替换不留临时文件残渣。
    /// </summary>
    [Fact]
    public async Task 落盘无BOM且不留临时文件()
    {
        using var service = new ConfigurationService(_configPath, TimeSpan.FromMilliseconds(20));
        service.Update(MakeConfig("atomic", 3));
        await service.FlushAsync();

        var bytes = await File.ReadAllBytesAsync(_configPath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "JSON 文件带 BOM（RFC 8259 不允许）。");

        // 随机名 .tmp 必须已被 File.Replace 消耗掉
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_configPath)!, "*.tmp"));
    }
}
