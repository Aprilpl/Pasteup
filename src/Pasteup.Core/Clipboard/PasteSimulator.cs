using Pasteup.Core.Interop;

namespace Pasteup.Core.Clipboard;

/// <summary>把内容写入剪贴板后模拟一次 Ctrl+V（SendInput），用于「选中即粘贴」。</summary>
public static class PasteSimulator
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;

    /// <summary>发送 Ctrl+V。delayMs 为按下与抬起之间的间隔，过小可能被目标应用吞掉。
    /// stillSafe：按下首个键前的最后复核——还原成功到实际注入有 60ms+ 窗口，期间用户点走/
    /// 目标关闭都会把剪贴板内容粘进非预期窗口；复核不过立即放弃（此时尚未按下任何键，无粘键残留）。</summary>
    public static bool SendPaste(int delayMs = 30, Func<bool>? stillSafe = null)
    {
        Thread.Sleep(60); // 给目标应用一点重获前台焦点的时间
        if (stillSafe is not null && !stillSafe()) return false;
        var ok = Key(VK_CONTROL, down: true);
        Thread.Sleep(delayMs);
        ok &= Key(VK_V, down: true);
        Thread.Sleep(delayMs);
        ok &= Key(VK_V, down: false);
        Thread.Sleep(delayMs);
        ok &= Key(VK_CONTROL, down: false);
        return ok;
    }

    private static bool Key(ushort vk, bool down)
    {
        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            U = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = down ? 0u : NativeMethods.KEYEVENTF_KEYUP,
                },
            },
        };
        return NativeMethods.SendInput(1, [input], NativeMethods.INPUT.Size) == 1;
    }

    /// <summary>合成一次单键点放（按下 → delayMs → 抬起）。间隔过近可能被目标应用吞掉。
    /// App 层前台还原的 Alt 点放解锁 / Esc 关菜单栏模式复用——键盘合成全仓只此一份
    /// （INPUT union 必须与 MOUSEINPUT 等宽的封送坑见 NativeMethods.INPUT）。</summary>
    public static void TapKey(ushort vk, int delayMs)
    {
        _ = Key(vk, down: true);
        Thread.Sleep(delayMs);
        _ = Key(vk, down: false);
    }
}

/// <summary>查询当前前台窗口所属进程（用于历史条目的来源应用：进程名 + exe 完整路径）。</summary>
public static class ForegroundApp
{
    /// <summary>来源应用信息。Name 与 ExePath 独立可空：取不到名字整个来源为空，路径失败名字仍在。</summary>
    public sealed record SourceInfo(string? Name, string? ExePath);

    public static SourceInfo GetSourceInfo()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new SourceInfo(null, null);
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return new SourceInfo(null, null);

        string? name = null;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid); // SafeHandle 须确定性释放
            name = p.ProcessName;
        }
        catch { /* 进程可能在窗口枚举与查询之间退出 */ }
        return new SourceInfo(name, ExePathOf((int)pid));
    }

    /// <summary>按 pid 查 exe 完整路径（App 图标服务按名补齐复用）。PROCESS_QUERY_LIMITED_INFORMATION
    /// 打开（比 Process.MainModule 稳：提权进程、UWP 商店应用不抛拒绝访问），失败返回 null。</summary>
    public static string? ExePathOf(int pid)
    {
        try
        {
            var h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { _ = NativeMethods.CloseHandle(h); }
        }
        catch { return null; }
    }
}
