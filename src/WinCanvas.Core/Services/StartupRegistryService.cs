using Microsoft.Win32;

namespace WinCanvas.Core.Services;

/// <summary>
/// 开机自启动的注册表管理（HKCU Run 键，注册表是单一真源）。
///
/// 设计口径：
/// - 用 <c>HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run</c>：
///   标准权限即可写（不需要管理员、不建服务、不涉账号），登录时由 explorer 代为启动，
///   与本工具「本地工具、无账号无云」的定位完全一致；
/// - 只读写值名 <see cref="ValueName"/>（"WinCanvas"），绝不触碰同一键下的其它值——
///   关闭开关只删自己名下的值，其它程序的自启动条目不受影响；
/// - 不写 config.json：开关状态每次都从注册表现读，注册表外被手工删除时界面如实显示为关
///   （避免「配置说开着、实际没生效」的两份真源漂移）。
/// </summary>
public sealed class StartupRegistryService
{
    /// <summary>本程序在 Run 键下的值名。改名会让旧条目失效，视为破坏性变更。</summary>
    public const string ValueName = "WinCanvas";

    /// <summary>自启动值所在的 HKCU 子键路径（Run 键，登录时系统按值逐条启动）。</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly RegistryKey _rootKey;
    private readonly string _subKeyPath;

    /// <summary>生产构造：读写 HKCU 下的标准 Run 键。</summary>
    public StartupRegistryService()
        : this(Registry.CurrentUser, RunKeyPath)
    {
    }

    /// <summary>
    /// 测试构造：注入根键与子键路径，让冒烟用例在隔离子键上验证行为，
    /// 不触碰用户真实的 Run 键。
    /// </summary>
    internal StartupRegistryService(RegistryKey rootKey, string subKeyPath)
    {
        _rootKey = rootKey;
        _subKeyPath = subKeyPath;
    }

    /// <summary>
    /// 构造登录启动命令（纯函数，供执行路径与单测共用）：<c>"exe 全路径"</c>。
    ///
    /// 为什么无条件加引号：Run 键的值按「可执行行」解析，路径含空格且不带引号时，
    /// 登录启动会把第一个空格前当成程序名、其余当成参数——
    /// 装在 <c>C:\Program Files\WinCanvas\</c> 之类目录时直接启动失败。带引号任何路径都合法。
    /// </summary>
    /// <param name="exePath">当前 exe 的全路径（Environment.ProcessPath）。</param>
    /// <returns>写入注册表的命令行值；路径为空白串时原样返回（调用方应先行校验）。</returns>
    public static string BuildRunCommand(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return exePath;
        }

        return $"\"{exePath}\"";
    }

    /// <summary>
    /// 查询当前是否已设置开机自启动（每次现读注册表，注册表是单一真源）。
    /// </summary>
    /// <returns>true = Run 键下存在本程序值名的条目（无论内容）。</returns>
    public bool IsEnabled()
    {
        return GetCommand() is not null;
    }

    /// <summary>
    /// 读取本程序的启动命令原文；不存在返回 null。
    /// </summary>
    public string? GetCommand()
    {
        using RegistryKey? key = _rootKey.OpenSubKey(_subKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// 开启开机自启动：把 <paramref name="exePath"/> 的带引号全路径写入 Run 键。
    /// 已有旧条目时用当前路径覆盖（exe 移动 / 重装后重开一次开关即完成迁移）。
    /// </summary>
    /// <param name="exePath">当前 exe 的全路径。</param>
    /// <exception cref="ArgumentException">exePath 为空。</exception>
    /// <exception cref="System.Exception">注册表写入失败（键被策略限制 / 权限不足等）原样上抛，
    /// 由调用方负责把「失败 + 原因 + 怎么办」呈现给用户。</exception>
    public void Enable(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("exe 全路径为空，无法写入开机自启动。", nameof(exePath));
        }

        using RegistryKey key = _rootKey.CreateSubKey(_subKeyPath, writable: true)
            ?? throw new InvalidOperationException($"打开注册表键 HKCU\\{_subKeyPath} 失败。");

        // 值类型固定 REG_SZ（字符串）：explorer 按 REG_SZ 解析 Run 值
        key.SetValue(ValueName, BuildRunCommand(exePath), RegistryValueKind.String);
    }

    /// <summary>
    /// 关闭开机自启动：删除本程序值名的条目。
    /// 幂等语义：条目不存在时不抛异常（关闭一个已经关着的开关应当安静成功）；
    /// 只删 <see cref="ValueName"/> 一个值，同键下其它程序的条目不受影响。
    /// </summary>
    public void Disable()
    {
        using RegistryKey? key = _rootKey.OpenSubKey(_subKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
