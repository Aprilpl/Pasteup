using Pasteup.Core.Interop;

namespace Pasteup.Core.Hotkeys;

public sealed class HotkeyEventArgs : EventArgs
{
    public required int Id { get; init; }
    public required KeyCombo Combo { get; init; }
}

/// <summary>全局热键注册（RegisterHotKey）。事件在内部消息线程触发，UI 侧需自行调度。</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly MessageWindow _window = new();
    private readonly Dictionary<int, KeyCombo> _registered = new();
    private int _nextId = 1;

    public event EventHandler<HotkeyEventArgs>? HotkeyPressed;

    public HotkeyService()
    {
        _window.Message += (msg, wParam, lParam) =>
        {
            if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;
            var id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var combo))
                HotkeyPressed?.Invoke(this, new HotkeyEventArgs { Id = id, Combo = combo });
            return IntPtr.Zero;
        };
    }

    /// <summary>注册全局热键，返回分配的 Id；失败（组合非法或被占用）返回 null。
    /// RegisterHotKey 不接受其他线程创建的窗口句柄，故封送到窗口线程执行（2026-10-27 踩坑）。</summary>
    public int? Register(KeyCombo combo)
    {
        if (!combo.IsValid) return null;
        return _window.Invoke<int?>(() =>
        {
            if (_window.Handle == IntPtr.Zero) return null;
            var id = _nextId++;
            if (!NativeMethods.RegisterHotKey(_window.Handle, id,
                    (uint)combo.Modifiers, (uint)combo.VirtualKey))
                return null;
            _registered[id] = combo;
            return id;
        });
    }

    public bool Unregister(int id) => _window.Invoke(() => UnregisterCore(id));

    private bool UnregisterCore(int id)
    {
        if (!_registered.Remove(id)) return false;
        return NativeMethods.UnregisterHotKey(_window.Handle, id);
    }

    public void UnregisterAll() => _window.Invoke<object?>(() =>
    {
        foreach (var id in _registered.Keys.ToList())
            UnregisterCore(id);
        return null;
    });

    /// <summary>试探性注册以检测冲突，立即注销。</summary>
    public bool IsConflict(KeyCombo combo)
    {
        var id = Register(combo);
        if (id is null) return true;
        Unregister(id.Value);
        return false;
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.Dispose();
    }
}
