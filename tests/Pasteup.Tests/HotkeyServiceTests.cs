using Pasteup.Core.Hotkeys;
using Xunit;

namespace Pasteup.Tests;

/// <summary>RegisterHotKey 要求窗口句柄所属线程与调用线程一致——曾因跨线程直调导致注册恒失败、
/// 每次启动弹两个「热键注册失败」警告（2026-10-27 踩坑）。此为走真实 Win32 注册的集成测试。</summary>
public class HotkeyServiceTests
{
    [Fact]
    public void Register_from_caller_thread_succeeds()
    {
        using var svc = new HotkeyService();
        var combo = KeyCombo.Parse("Ctrl+Alt+Shift+F12")!; // 冷门组合，避免被环境占用
        var id = svc.Register(combo);

        Assert.NotNull(id);
        Assert.False(svc.IsConflict(KeyCombo.Parse("Ctrl+Alt+Shift+F11")!));
        Assert.True(svc.Unregister(id!.Value));
    }
}
