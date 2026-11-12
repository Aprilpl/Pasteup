using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Pasteup.Core.Clipboard;
using Pasteup.Core.History;

namespace Pasteup.App.Services;

/// <summary>来源应用图标：exe 路径 → 48px 图标（面板屏普遍 ≥150% DPI，32px 拉到物理 56px 会糊）。
/// 主路 SHGetImageList(SHIL_EXTRALARGE) 取 shell 系统图标列表（覆盖 UWP/资源重定向），
/// 备胎 PrivateExtractIcons 直读 exe 图标资源；两者皆败返回 null（UI 侧回退首字母）。
/// 全内存缓存：路径正/负缓存（提不到不再重试）+ 进程名→路径正缓存
/// （「应用没开着」不缓存——下次唤起重试，应用随后启动仍能补上图）。</summary>
public sealed class SourceAppIconService
{
    /// <summary>进程名 → exe 路径（按名补齐旧条目）；可注入替身以便单测。</summary>
    private readonly Func<string, string?> _resolveByName;

    // 路径 → 图标；值为 null = 提取失败（负缓存，不再重试）
    private readonly ConcurrentDictionary<string, BitmapSource?> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    // 进程名 → exe 路径；只缓存解析成功的正结果
    private readonly ConcurrentDictionary<string, string> _nameToPath = new(StringComparer.OrdinalIgnoreCase);
    // 并发去重（搜索打字会连刷重建，同一来源只解析一次）
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inflight = new(StringComparer.OrdinalIgnoreCase);

    public SourceAppIconService() : this(DefaultResolveByName) { }

    /// <param name="resolveByName">进程名 → exe 路径（按名补齐旧条目）；测试注入替身。</param>
    public SourceAppIconService(Func<string, string?> resolveByName) => _resolveByName = resolveByName;

    /// <summary>行的图标分组键：path:xxx / name:xxx / ""（无来源，跳过加载）。</summary>
    public static string KeyOf(ClipboardEntry e)
        => !string.IsNullOrEmpty(e.SourceAppPath) ? "path:" + e.SourceAppPath
         : !string.IsNullOrEmpty(e.SourceApp) ? "name:" + e.SourceApp!.ToLowerInvariant()
         : "";

    /// <summary>解析条目来源图标。缓存命中同步完成（await 不让出、预览无闪烁）；
    /// 未命中走后台线程（进程快照 + 磁盘读资源），务必 await。</summary>
    public Task<BitmapSource?> GetAsync(ClipboardEntry entry)
    {
        if (entry.SourceAppPath is { Length: > 0 } p && _pathCache.TryGetValue(p, out var hit))
            return Task.FromResult(hit);
        if (entry.SourceApp is { Length: > 0 } n
            && _nameToPath.TryGetValue(n, out var resolved) && _pathCache.TryGetValue(resolved, out var hit2))
            return Task.FromResult(hit2);
        var key = KeyOf(entry);
        if (key.Length == 0) return Task.FromResult<BitmapSource?>(null);
        return _inflight.GetOrAdd(key, _ => ResolveAsync(key, entry));
    }

    private async Task<BitmapSource?> ResolveAsync(string key, ClipboardEntry entry)
    {
        try
        {
            if (entry.SourceAppPath is { Length: > 0 } path)
                return await Task.Run(() => FromPath(path));

            var name = entry.SourceApp!;
            if (!_nameToPath.TryGetValue(name, out var resolved))
            {
                resolved = await Task.Run(() => _resolveByName(name));
                if (string.IsNullOrEmpty(resolved)) return null; // 没在运行：不缓存，下次唤起再试
                _nameToPath[name] = resolved;
            }
            return await Task.Run(() => FromPath(resolved));
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    /// <summary>按路径取图标（缓存含负结果）。冻结后可跨线程赋给任意行 VM。</summary>
    public BitmapSource? FromPath(string path)
    {
        if (_pathCache.TryGetValue(path, out var cached)) return cached;
        var icon = Extract(path);
        _pathCache[path] = icon;
        return icon;
    }

    private static string? DefaultResolveByName(string processName)
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(processName))
                using (p)
                {
                    var path = ForegroundApp.ExePathOf(p.Id);
                    if (!string.IsNullOrEmpty(path)) return path;
                }
        }
        catch { /* 进程快照竞争（目标恰好退出）：视作没找到 */ }
        return null;
    }

    // ===== 提取：shell 系统图标列表 48px 主路 + exe 资源备胎 =====

    private static BitmapSource? Extract(string exePath)
    {
        var hIcon = ShellIcon48(exePath) ?? ResourceIcon48(exePath);
        if (hIcon is not { } h || h == IntPtr.Zero) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(h, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch { return null; }
        finally { _ = DestroyIcon(h); }
    }

    private static IntPtr? ShellIcon48(string path)
    {
        try
        {
            var shfi = new SHFILEINFOW();
            if (SHGetFileInfo(path, 0, ref shfi, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_SYSICONINDEX) == IntPtr.Zero)
                return null;
            var iid = typeof(IImageList).GUID;
            if (SHGetImageList(SHIL_EXTRALARGE, ref iid, out IImageList list) != 0)
                return null;
            try
            {
                var hr = list.GetIcon(shfi.iIcon, ILD_TRANSPARENT, out var hIcon);
                return hr == 0 && hIcon != IntPtr.Zero ? hIcon : null;
            }
            finally { Marshal.ReleaseComObject(list); }
        }
        catch { return null; }
    }

    private static IntPtr? ResourceIcon48(string path)
    {
        try
        {
            var icons = new IntPtr[1];
            return PrivateExtractIcons(path, 0, 48, 48, icons, null, 1, 0) >= 1 && icons[0] != IntPtr.Zero
                ? icons[0] : null;
        }
        catch { return null; }
    }

    private const int SHIL_EXTRALARGE = 0x2;    // 48×48 系统图标列表
    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int ILD_TRANSPARENT = 0x0001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    /// <summary>shell 系统图像列表接口；vtable 顺序按 commoncontrols.h，只声明到用到的 GetIcon。</summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int AddMasked(IntPtr hbmImage, uint crMask, out int pi);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int fStyle, out IntPtr picon);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        [Out] IntPtr[] phicon, [Out] int[]? piconid, uint nIcons, int flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
