using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Pasteup.App.Services;

/// <summary>
/// 「开机自启」的注册表实现（2026-11-14）：只写 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>
/// 的 <c>Pasteup</c> 值，HKCU 不需要管理员权限，也不污染 HKLM（便携版不应要求提权）。
///
/// 为什么命令要加引号：Run 值是一条命令行，exe 路径含空格（如 <c>C:\Program Files\...</c>）时
/// 不加引号会被 Windows 按空格拆成「程序 + 参数」，自启直接失败。
///
/// 为什么要 <see cref="Reconcile"/>：便携版解压到新目录、升级换 exe 后旧路径就失效了；
/// 启动期按设置项对齐注册表可自愈（启用→改写当前路径，关闭→清本程序形态的遗留值；
/// 同名但指向其他程序的手工 Run 项保留，不误删用户自己的配置），保证「设置开关 = 本程序自启状态」一致。
/// </summary>
public sealed class StartupRegistration
{
    /// <summary>HKCU 下登录自启项的标准位置。</summary>
    public const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启项值名（与产品名一致）。</summary>
    public const string DefaultValueName = "Pasteup";

    /// <summary>实机使用的默认实例；测试用自定义 keyPath 构造隔离，<b>绝不要</b>碰真实 Run 键。</summary>
    public static StartupRegistration Default { get; } = new();

    private readonly string _keyPath;
    private readonly string _valueName;

    /// <param name="keyPath">HKCU 相对键路径；测试传 <c>Software\Pasteup\Tests\...</c> 隔离。</param>
    /// <param name="valueName">值名；测试可一并自定义，避免误伤真实 <c>Pasteup</c> 值。</param>
    public StartupRegistration(string keyPath = DefaultKeyPath, string valueName = DefaultValueName)
    {
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <summary>Run 值内容：双引号包裹的绝对路径（命令行语义，见类注释）。</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\"";

    /// <summary>
    /// 当前进程可执行文件路径。<see cref="Environment.ProcessPath"/> 在个别宿主/受限环境可能为空，
    /// 退用主模块文件名；两者都取不到就抛（调用方负责可见化，不能静默写一条空路径去自启）。
    /// </summary>
    public static string CurrentExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                using var process = Process.GetCurrentProcess();
                path = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                throw new InvalidOperationException("无法确定当前进程的可执行文件路径，无法设置开机自启。", ex);
            }
            if (!string.IsNullOrWhiteSpace(path)) return path;
            throw new InvalidOperationException("无法确定当前进程的可执行文件路径，无法设置开机自启。");
        }
    }

    /// <summary>
    /// 已存在的 Run 值是否「本程序的自动启动项」（2026-11-14 复核建议 S1 的宽松归属校验）：
    /// 以引号开头、且以 <c>Pasteup.exe"</c> 结尾。关闭自启时只删这种值——避免把用户在本功能上线前
    /// 手工添加、同名但指向别的程序的 Run 项一起删掉（静默破坏用户自己的开机启动配置）。
    /// </summary>
    public static bool LooksLikePasteupAutostart(string? command)
        => command is { Length: > 2 }
        && command[0] == '"'
        && command.EndsWith("Pasteup.exe\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>读取已注册的命令行；键或值不存在返回 null（只读打开，不创建键）。</summary>
    public string? ReadCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    /// <summary>当前是否存在自启项。注意不校验路径是否仍有效——路径失效由 <see cref="Reconcile"/> 自愈。</summary>
    public bool IsEnabled => ReadCommand() is not null;

    /// <summary>
    /// 写入/删除自启项。异常<b>不吞</b>：调用方（设置页/启动对齐）负责可见化或记录，
    /// 以免「点开关没反应但设置显示已开启」。
    /// </summary>
    public void Apply(bool enabled, string exePath)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath)
                ?? throw new InvalidOperationException($"无法创建注册表键 HKCU\\{_keyPath}，开机自启未生效。");
            key.SetValue(_valueName, CommandFor(exePath), RegistryValueKind.String);
            return;
        }

        // 关闭：键/值不存在都算成功（幂等）。键存在才打开，避免「为了删值反而建出空键」
        using var open = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        open?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 按设置项对齐注册表（启动期调用，best-effort）：
    /// 启用时只在与当前 exe 不一致时改写——便携版换目录/升级后旧路径失效即自愈；
    /// 已一致则不写（避免每次启动都无谓触碰注册表）；关闭时清除遗留项。
    /// </summary>
    public void Reconcile(bool enabled, string exePath)
    {
        var current = ReadCommand();
        if (enabled)
        {
            // 大小写不敏感比较：Windows 路径大小写不改变指向，避免仅因大小写反复改写
            if (!string.Equals(current, CommandFor(exePath), StringComparison.OrdinalIgnoreCase))
                Apply(true, exePath);
        }
        else if (LooksLikePasteupAutostart(current))
        {
            // 设置关闭 = 不应有本程序自启项：清掉旧目录/上次残留的遗留值。
            // 只删「看起来是本程序」的值，同名但指向别的程序的手工项保持不动（S1）。
            Apply(false, exePath);
        }
    }
}
