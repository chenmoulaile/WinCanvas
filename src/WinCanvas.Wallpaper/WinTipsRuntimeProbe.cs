using System;
using System.IO;
using System.Text.Json;

namespace WinCanvas.Wallpaper;

/// <summary>
/// WinTips.exe 能否被【安全启动】的探测（v0.3.0 新增）。
///
/// 为什么必须有这一步：WinTips 是框架依赖的 net10 WPF 程序。目标机器默认位置
/// 没有 .NET 10 桌面运行时时，启动它的 apphost 会自己弹出一个
/// 【系统模态对话框】「You must install or update .NET to run this application」。
/// 那个框不是我们的异常：Process.Start 会成功返回（进程确实起来了），
/// try/catch 抓不到、也没法把它关掉 —— 用户看到的是一个和 NumLock 毫无关系的
/// 安装提示框。实测就是这么踩到的。
///
/// 所以规则是：【探测不过就绝不 spawn】，直接走托盘气泡 / InfoBar 回退。
/// 这样既保留了「装了 .NET 10 的机器上用 WinTips 原生浮窗」的体验，
/// 又不会在缺运行时的机器上制造系统对话框。
/// </summary>
internal static class WinTipsRuntimeProbe
{
    /// <summary>WinTips 需要的运行时框架名（写在其 runtimeconfig.json 里）。</summary>
    private const string RequiredFramework = "Microsoft.WindowsDesktop.App";

    /// <summary>
    /// 判断 <paramref name="exePath"/> 是否可以安全启动。
    /// </summary>
    /// <param name="exePath">WinTips.exe 的完整路径。</param>
    /// <param name="reason">不可启动时的人话原因（供诊断日志）。</param>
    /// <returns>true = 可以 spawn；false = 必须走回退通道。</returns>
    internal static bool IsLaunchable(string? exePath, out string? reason)
    {
        reason = null;

        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            reason = "WinTips.exe 不存在";
            return false;
        }

        string directory = Path.GetDirectoryName(exePath)!;

        // ① 自带运行时（self-contained）：目录里有 coreclr.dll 就一定能起，
        //    不依赖机器上装了什么
        if (File.Exists(Path.Combine(directory, "coreclr.dll")))
        {
            return true;
        }

        // ② 框架依赖：必须确认机器上存在【主版本兼容】的桌面运行时
        string requiredVersion = ReadRequiredFrameworkVersion(directory);
        int requiredMajor = ParseMajor(requiredVersion);
        if (requiredMajor <= 0)
        {
            // 读不出要求的版本就别冒险：宁可走回退，也不要弹系统对话框
            reason = $"无法从 runtimeconfig.json 判定所需运行时版本（读到 \"{requiredVersion}\"）";
            return false;
        }

        foreach (string root in CandidateDotnetRoots())
        {
            string sharedDir = Path.Combine(root, "shared", RequiredFramework);
            if (!Directory.Exists(sharedDir))
            {
                continue;
            }

            try
            {
                foreach (string versionDir in Directory.GetDirectories(sharedDir))
                {
                    if (ParseMajor(Path.GetFileName(versionDir)) == requiredMajor)
                    {
                        return true; // 同主版本的运行时存在，rollForward 会挑到合适的补丁版
                    }
                }
            }
            catch (Exception)
            {
                // 目录不可读：换下一个候选根
            }
        }

        reason = $"机器上没有 .NET {requiredMajor} 桌面运行时（{RequiredFramework}），" +
                 "启动 WinTips.exe 会触发系统的「需要安装 .NET」对话框，已改走本地提示通道";
        return false;
    }

    /// <summary>
    /// 从 WinTips.runtimeconfig.json 读要求的桌面运行时版本；读不到返回空串。
    ///
    /// 实测框架依赖的 WPF 应用写出来的是【复数 frameworks 数组】
    /// （同时列 Microsoft.NETCore.App 与 Microsoft.WindowsDesktop.App），
    /// 而单数 framework 只出现在部分场景 —— 两种形态都要认，
    /// 并且必须取 WindowsDesktop.App 那一条：WPF 缺的是桌面运行时，
    /// 只有基础运行时照样起不来。
    /// </summary>
    private static string ReadRequiredFrameworkVersion(string directory)
    {
        string configPath = Path.Combine(directory, "WinTips.runtimeconfig.json");
        if (!File.Exists(configPath))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
            {
                return string.Empty;
            }

            // ① 复数 frameworks：优先取桌面框架那条
            if (runtimeOptions.TryGetProperty("frameworks", out var frameworks)
                && frameworks.ValueKind == JsonValueKind.Array)
            {
                string? fallback = null;
                foreach (var item in frameworks.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("name", out var name)
                        || !item.TryGetProperty("version", out var version))
                    {
                        continue;
                    }

                    string n = name.GetString() ?? string.Empty;
                    string v = version.GetString() ?? string.Empty;

                    if (string.Equals(n, RequiredFramework, StringComparison.OrdinalIgnoreCase))
                    {
                        return v;
                    }

                    fallback ??= v;
                }

                if (fallback != null)
                {
                    return fallback;
                }
            }

            // ② 单数 framework
            if (runtimeOptions.TryGetProperty("framework", out var framework)
                && framework.ValueKind == JsonValueKind.Object
                && framework.TryGetProperty("version", out var singleVersion)
                && singleVersion.ValueKind == JsonValueKind.String)
            {
                return singleVersion.GetString() ?? string.Empty;
            }

            // ③ 兜底：从 tfm（"net10.0-windows"）里抠主版本
            if (runtimeOptions.TryGetProperty("tfm", out var tfm)
                && tfm.ValueKind == JsonValueKind.String)
            {
                string text = tfm.GetString() ?? string.Empty;
                int start = text.IndexOf('0');
                if (start < 0)
                {
                    return string.Empty;
                }

                int end = start;
                while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.'))
                {
                    end++;
                }

                return text[start..end];
            }
        }
        catch (Exception)
        {
            // 配置损坏：交由调用方按「不可启动」处理
        }

        return string.Empty;
    }

    /// <summary>取版本串的主版本号；解析失败返回 -1。</summary>
    private static int ParseMajor(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return -1;
        }

        string text = version.Trim();
        int dot = text.IndexOf('.');
        string head = dot > 0 ? text[..dot] : text;

        return int.TryParse(head, out int major) ? major : -1;
    }

    /// <summary>
    /// 机器上可能的 .NET 安装根。
    /// 包含 DOTNET_ROOT（有些环境把运行时装在非默认位置）与两个 Program Files 路径。
    /// </summary>
    private static string[] CandidateDotnetRoots()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        return new[]
        {
            fromEnv ?? string.Empty,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"),
        };
    }
}
