using Microsoft.Win32;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>开机自启注册（2026-11-14）：只操作 HKCU 下自定义测试键
/// （<c>Software\Pasteup\Tests\StartupRegistration\&lt;guid&gt;</c>），每个用例独立键、Dispose 兜底删树。
/// <b>绝不触碰真实 Run 键的 Pasteup 值</b>——那是实机自启项，测试误改会污染开发机开机行为。</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string ExePath = @"C:\Program Files\Pasteup\Pasteup.exe";

    private readonly string _keyPath =
        @"Software\Pasteup\Tests\StartupRegistration\" + Guid.NewGuid().ToString("N");

    private StartupRegistration NewSut() => new(_keyPath);

    public void Dispose()
    {
        // 逐级向上清理：App/测试只会在 HKCU\Software\Pasteup 下留下空壳父键，
        // 能删则删（非空时 DeleteSubKey 抛 ArgumentException，说明还有别的用例在用，忽略）
        TryDeleteEmptyKey(_keyPath);
        TryDeleteEmptyKey(@"Software\Pasteup\Tests\StartupRegistration");
        TryDeleteEmptyKey(@"Software\Pasteup\Tests");
        TryDeleteEmptyKey(@"Software\Pasteup");
    }

    private static void TryDeleteEmptyKey(string path)
    {
        try { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
        catch
        {
            // 仍有子键/无权限：不影响用例结论（键名带 guid，不会与真实自启项冲突）
        }
    }

    [Fact]
    public void Defaults_target_hkcu_run_value_named_pasteup()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", StartupRegistration.DefaultKeyPath);
        Assert.Equal("Pasteup", StartupRegistration.DefaultValueName);
    }

    [Fact]
    public void Apply_true_writes_quoted_exe_path_and_enables()
    {
        var sut = NewSut();

        sut.Apply(true, ExePath);

        Assert.Equal("\"C:\\Program Files\\Pasteup\\Pasteup.exe\"", sut.ReadCommand());
        Assert.True(sut.IsEnabled);
    }

    [Fact]
    public void Apply_false_removes_value_and_is_idempotent()
    {
        var sut = NewSut();
        sut.Apply(true, ExePath);

        sut.Apply(false, ExePath);

        Assert.Null(sut.ReadCommand());
        Assert.False(sut.IsEnabled);
        // 值已不存在（甚至键可能已无值）时再关一次：静默幂等，不抛
        sut.Apply(false, ExePath);
        Assert.False(sut.IsEnabled);
    }

    [Fact]
    public void Reconcile_true_writes_when_missing_and_rewrites_stale_path()
    {
        var sut = NewSut();
        var newPath = @"D:\Tools\Pasteup\Pasteup.exe";

        sut.Reconcile(true, newPath); // 缺失 → 写入
        Assert.Equal(StartupRegistration.CommandFor(newPath), sut.ReadCommand());

        sut.Apply(true, ExePath); // 模拟便携版换目录前的旧值
        sut.Reconcile(true, newPath); // 旧路径 → 改写为新路径（自愈）
        Assert.Equal(StartupRegistration.CommandFor(newPath), sut.ReadCommand());

        sut.Reconcile(true, newPath); // 已一致 → 不重复写（值保持不变）
        Assert.Equal(StartupRegistration.CommandFor(newPath), sut.ReadCommand());
    }

    [Fact]
    public void Reconcile_false_removes_leftover_value()
    {
        var sut = NewSut();
        sut.Apply(true, ExePath);
        Assert.True(sut.IsEnabled);

        sut.Reconcile(false, ExePath);

        Assert.Null(sut.ReadCommand());
        Assert.False(sut.IsEnabled);
    }

    [Fact]
    public void Reconcile_false_keeps_foreign_value_under_same_name()
    {
        var sut = NewSut();
        // 模拟用户在本功能上线前手工添加的同名 Run 值，但指向别的程序（或非引号形态）：
        // 关闭自启只清「看起来是本程序」的值，不能静默删掉用户自己的开机启动配置（复核建议 S1）
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath)!)
        {
            key.SetValue(StartupRegistration.DefaultValueName, @"""C:\Other\Tool.exe""", RegistryValueKind.String);
        }
        sut.Reconcile(false, ExePath);
        Assert.Equal(@"""C:\Other\Tool.exe""", sut.ReadCommand());

        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath)!)
        {
            key.SetValue(StartupRegistration.DefaultValueName, @"C:\Pasteup\Pasteup.exe", RegistryValueKind.String); // 无引号形态
        }
        sut.Reconcile(false, ExePath);
        Assert.Equal(@"C:\Pasteup\Pasteup.exe", sut.ReadCommand());
    }

    [Fact]
    public void LooksLikePasteupAutostart_matches_only_quoted_pasteup_exe()
    {
        Assert.True(StartupRegistration.LooksLikePasteupAutostart("\"C:\\Tools\\Pasteup.exe\""));
        Assert.True(StartupRegistration.LooksLikePasteupAutostart("\"D:\\PASTEUP.EXE\"")); // 大小写不敏感
        Assert.False(StartupRegistration.LooksLikePasteupAutostart(null));
        Assert.False(StartupRegistration.LooksLikePasteupAutostart(""));
        Assert.False(StartupRegistration.LooksLikePasteupAutostart("Pasteup.exe"));
        Assert.False(StartupRegistration.LooksLikePasteupAutostart("\"C:\\Other\\Tool.exe\""));
        Assert.False(StartupRegistration.LooksLikePasteupAutostart("\"C:\\Old\\Pasteup.exe\" --show")); // 带参数形态不匹配本程序写入值
    }

    [Fact]
    public void CommandFor_quotes_paths_with_and_without_spaces()
    {
        Assert.Equal("\"C:\\Program Files\\Pasteup\\Pasteup.exe\"", StartupRegistration.CommandFor(ExePath));
        Assert.Equal("\"C:\\Pasteup\\Pasteup.exe\"", StartupRegistration.CommandFor(@"C:\Pasteup\Pasteup.exe"));
    }
}
