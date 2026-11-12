using Pasteup.Core.Interop;

namespace Pasteup.Core.Clipboard;

/// <summary>系统剪贴板变更监听（AddClipboardFormatListener）。事件在内部消息线程触发。</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private readonly MessageWindow _window = new();
    private bool _listening;
    private volatile bool _suppressNext; // UI 线程置位、消息窗口线程读取，必须 volatile

    public event EventHandler? Changed;
    public event EventHandler? SystemSettingsChanged;

    public ClipboardMonitor()
    {
        _window.Message += (msg, _, _) =>
        {
            switch (msg)
            {
                case NativeMethods.WM_CLIPBOARDUPDATE:
                    if (_suppressNext) _suppressNext = false;
                    else Changed?.Invoke(this, EventArgs.Empty);
                    break;
                case NativeMethods.WM_SETTINGCHANGE:
                    SystemSettingsChanged?.Invoke(this, EventArgs.Empty);
                    break;
            }
            return IntPtr.Zero;
        };
    }

    public bool Start()
    {
        if (_listening) return true;
        _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);
        return _listening;
    }

    public void Stop()
    {
        if (!_listening) return;
        NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
    }

    /// <summary>调用方在向剪贴板写入前调用，吞掉由此产生的一次变更通知。</summary>
    public void SuppressNextChange() => _suppressNext = true;

    /// <summary>写回失败（剪贴板被占用等）时调用：复位抑制标志。标志不会自行过期，
    /// 不复位会把下一次真实的外部复制当自家回声吞掉（该条目静默丢失入库）。</summary>
    public void ResetSuppression() => _suppressNext = false;

    public void Dispose()
    {
        Stop();
        _window.Dispose();
    }
}
