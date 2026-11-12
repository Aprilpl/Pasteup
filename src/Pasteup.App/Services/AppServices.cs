using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using Pasteup.App.Views;
using Pasteup.Core.Clipboard;
using Pasteup.Core.History;

namespace Pasteup.App.Services;

/// <summary>读取系统剪贴板 → 归一化为历史条目 → 入库（图像落盘为 PNG）。
/// 分工：载荷读取在专用 STA 读取线程（<see cref="ClipboardStaReader"/>，等待有上限、忙/超时降级，
/// 不冻结 UI）；解码校验/alpha 修复/编码/落盘在后台线程；引擎入库回 UI 线程
///（Changed → 面板刷新全程 UI 线程）。</summary>
public sealed class ClipboardIngestService(HistoryEngine engine, HistoryStore store) : IDisposable
{
    // 后台段串行：快速连 copy 时保住「载荷读取序 = 入库序」（MRU 正确性）
    private readonly SemaphoreSlim _ingestGate = new(1, 1);

    // 剪贴板读取专用 STA 线程（复审问题4 第2批）：跨进程 OLE 读取自身没有超时，剪贴板 owner 僵死时
    // 放在 UI 线程上就是整窗冻结。DI 在 App.OnExit 的 _services.Dispose() 里经本类 Dispose 释放它。
    private readonly ClipboardStaReader _reader = new();
    /// <summary>写回条目到系统剪贴板（粘贴准备）。须在 UI 线程调用；图像解码在后台完成，
    /// 只有真正要写剪贴板时才置抑制标志（避免"没写入"分支吞掉下一次真实复制）。
    /// 写回失败（剪贴板被占用、图像缓存损坏等）一律复位抑制并吞掉——不影响主流程。</summary>
    public async Task WriteEntryAsync(ClipboardEntry entry)
    {
        try
        {
            switch (entry.Kind)
            {
                case EntryKind.Text:
                case EntryKind.Link:
                    if (entry.FullText is null) return; // 无内容可写：不置抑制标志
                    App.Current.ClipboardMonitor.SuppressNextChange();
                    Clipboard.SetText(entry.FullText);
                    break;
                case EntryKind.Image:
                    {
                        // 缓存 PNG 可能损坏（进程被杀时写了一半）或已被清理：解码失败/null 就跳过
                        // 写回，不让坏图把整次激活变成错误弹窗。解码放后台（4K 全解码 UI 实测 ~68ms），
                        // FromPng 产物已冻结、跨线程安全；Clipboard 写入仍在 UI 上下文（STA 要求）。
                        var bmp = await Task.Run(() =>
                        {
                            var png = store.LoadImagePng(entry.ImageFile);
                            return png is null ? null : ImageCodec.FromPng(png);
                        });
                        if (bmp is null) return; // 缓存缺失/损坏：没写入，不置抑制
                        App.Current.ClipboardMonitor.SuppressNextChange();
                        Clipboard.SetImage(bmp);
                        break;
                    }
                case EntryKind.File:
                    {
                        // 空列表：没内容可写，不置抑制（也避开 SetFileDropList 对空集合可能抛 ArgumentException 的路径）
                        if (entry.Files.Count == 0) return;
                        var list = new System.Collections.Specialized.StringCollection();
                        list.AddRange(entry.Files.ToArray());
                        App.Current.ClipboardMonitor.SuppressNextChange();
                        Clipboard.SetFileDropList(list);
                        break;
                    }
            }
        }
        catch (Exception)
        {
            // 写回失败不影响主流程：剪贴板被占用（COMException）、图像解码失败/OOM、其他写剪贴板失败，
            // 下次复制自动重来。抑制标志随之复位：本次没有产生变更通知，留着只会吞掉下一次真实的外部复制
            App.Current.ClipboardMonitor.ResetSuppression();
        }
    }

    /// <summary>释放专用 STA 读取线程（由 DI 在 App.OnExit 的 _services.Dispose() 调用）。
    /// 内部限时 Join(300ms) 且不抛：读取线程卡在 OLE 调用里也只会多等这一拍，不会挂住退出。</summary>
    public void Dispose() => _reader.Dispose();

    /// <summary>读取当前剪贴板内容并入历史；无有效内容/读取失败/读取超时返回 null。
    /// 须在 UI 线程调用：来源应用要在「复制瞬间」取（前台稍后可能已易主）；载荷读取本身在专用
    /// STA 读取线程上做（不占 UI 线程），重活后台化后经 await 续回 UI 入库。</summary>
    public async Task<ClipboardEntry?> IngestClipboardAsync()
    {
        // 来源应用必须在复制瞬间的 UI 线程上取；载荷读取交给专用 STA 线程（OLE 读取无超时，不能占 UI）
        var source = ForegroundApp.GetSourceInfo();
        var payload = await _reader.ReadAsync();
        if (payload is null) return null;

        await _ingestGate.WaitAsync();
        try
        {
            if (payload is byte[] png)
            {
                var (entry, corrupt) = await Task.Run(() => BuildFromPng(png, source));
                if (entry is not null) return engine.Add(entry);
                if (!corrupt) return null; // 拒收（超大像素等守卫）
                // PNG 字节损坏：回读取线程再取 DIB 载荷走位图分支（原同步实现的回退语义）
                payload = await _reader.ReadDibAsync();
                if (payload is null) return null;
            }
            var built = await Task.Run(() => BuildEntry(payload, source));
            return built is null ? null : engine.Add(built);
        }
        finally { _ingestGate.Release(); }
    }

    /// <summary>后台线程：载荷 → 历史条目（图像含落盘）。不含 engine.Add（入库由 UI 侧调用）。</summary>
    private ClipboardEntry? BuildEntry(object payload, ForegroundApp.SourceInfo source) => payload switch
    {
        string text => ClipboardEntry.FromText(text, source.Name, source.ExePath),
        System.Windows.Media.Imaging.BitmapSource dib => BuildFromDib(dib, source),
        List<string> files => ClipboardEntry.FromFiles(files, source.Name, source.ExePath),
        _ => null,
    };

    /// <summary>PNG 路径：只做 IHDR 头校验 +（宽 &gt; 缩略图规格时）WIC 缩放解码缩略图，
    /// **不再全量解码**。原实现 FromPng 全解码只为拿宽高/生成缩略图：4K PNG 首解 25–100ms
    /// 且峰值保留 33MB 全尺寸像素；主图字节本就不用改，原样落盘即可，宽高直接取 IHDR，
    /// 缩略图走 DecodePngThumbnail（峰值只留 640 宽像素）。
    /// corrupt=true 表示 PNG 字节损坏（调用方回退 DIB）；像素超限是明确拒收（不回退）。</summary>
    private (ClipboardEntry? entry, bool corrupt) BuildFromPng(byte[] png, ForegroundApp.SourceInfo source)
    {
        try
        {
            if (ImageCodec.TryReadPngDimensions(png) is not { } dims) return (null, true); // 无有效 IHDR：坏 PNG → 回退 DIB
            // 解码炸弹防线：先保证宽高能放进 int 再乘（uint×uint 直接相乘会溢出 long 绕过上限），
            // 超限明确拒收（不回退 DIB——DIB 路径有自己的 50MP 守卫，回退只会白付一次位图解码）
            if (dims.Width == 0 || dims.Height == 0 || dims.Width > int.MaxValue || dims.Height > int.MaxValue
                || (long)dims.Width * dims.Height > ImageCodec.MaxImagePixels)
                return (null, false);
            var width = (int)dims.Width;
            var height = (int)dims.Height;
            // 只有宽于缩略图规格时才解码缩略图；窄图不生成缩略图（ThumbConverter 直接回退原图，
            // 这类小图解码成本本来就低）。IHDR 与实际像素不一致的极端伪造图由这里解码抛异常兜底。
            System.Windows.Media.Imaging.BitmapSource? thumb = null;
            if (width > HistoryStore.ThumbnailWidth)
                thumb = ImageCodec.DecodePngThumbnail(png, HistoryStore.ThumbnailWidth);
            return (BuildImage(png, width, height, thumb, source), false);
        }
        catch (Exception ex) when (ex is not COMException)
        {
            return (null, true); // PNG 字节损坏（解码失败）：调用方回退 DIB
        }
    }

    /// <summary>DIB 路径：alpha 修复 → 流式 PNG 落盘（边写边算 SHA-256）→ 缩略图。
    /// 原实现 ToPng 先产出整份 PNG byte[]（MemoryStream + ToArray 双份，50MP 图瞬时数百 MB），
    /// 现在编码器直写文件流，只剩编码器内部缓冲。编码/落盘失败返回 null（跳过本次入库，
    /// 不让异常穿出 Task.Run）；DIB 解码炸弹防线仍在大分配之前。</summary>
    private ClipboardEntry? BuildFromDib(System.Windows.Media.Imaging.BitmapSource raw,
        ForegroundApp.SourceInfo source)
    {
        try
        {
            // DIB 解码炸弹防线：像素拷贝/编码等大分配之前按声明尺寸拒收（与 PNG 路径同限；
            // 读取线程已按 CF_DIB 头预检，这里是后台段的最后兜底）
            if ((long)raw.PixelWidth * raw.PixelHeight > ImageCodec.MaxImagePixels) return null;
            // GDI 来源的 32bpp DIB alpha 字节全 0 会被 WPF 当真成全透明，先修复再编码
            var bmp = ImageCodec.ForceOpaqueIfAllAlphaZero(raw);
            var id = Guid.NewGuid();
            byte[]? digest = null;
            var file = store.SavePng(id, output =>
            {
                using var hashing = new HashingWriteStream(output);
                ImageCodec.EncodePngToStream(bmp, hashing);
                digest = hashing.Hash; // 编码完成后取增量摘要，与 HashPngPrefix(PNG 字节) 语义一致
            }, out var sizeBytes);
            // 缩略图：用修正后的位图缩放编码（宽 > 640 才做，与 PNG 路径同规则）
            if (bmp.PixelWidth > HistoryStore.ThumbnailWidth)
            {
                try
                {
                    store.SaveThumbnailPng(id, ImageCodec.RenderThumbnailPng(bmp, HistoryStore.ThumbnailWidth));
                }
                catch (Exception)
                {
                    // best-effort：缩略图缺失只影响列表解码成本，不影响入库正确性
                }
            }
            var entry = ClipboardEntry.FromImage(file, bmp.PixelWidth, bmp.PixelHeight, sizeBytes, source.Name,
                source.ExePath, digest is { Length: >= 8 } ? ClipboardEntry.HashPngPrefix(digest.AsSpan()) : null);
            entry.Id = id;
            return entry;
        }
        catch (Exception)
        {
            return null; // 编码/落盘失败（磁盘满、目录被占、编码器异常）：跳过本次入库，历史里不留半截条目
        }
    }

    /// <summary>主图落盘 + 入库预缩略图（PNG 路径）。thumbSource 已按目标宽解码/缩放
    ///（null = 小图不生成缩略图）；主图 PNG 字节原样保存、宽高来自 IHDR、
    /// ContentHash 仍取 SHA-256(PNG 字节) 前 8 字节（口径不变）。
    /// 缩略图只是面板解码优化：生成/落盘失败一律忽略，绝不让入库失败。</summary>
    private ClipboardEntry BuildImage(byte[] png, int width, int height,
        System.Windows.Media.Imaging.BitmapSource? thumbSource, ForegroundApp.SourceInfo source)
    {
        var id = Guid.NewGuid();
        var file = store.SaveImagePng(id, png);
        if (thumbSource is not null)
        {
            try
            {
                store.SaveThumbnailPng(id, ImageCodec.ToPng(thumbSource));
            }
            catch (Exception)
            {
                // best-effort：坏图/OOM/编码器异常等都可能到此，缩略图缺失只影响解码成本，不影响正确性
            }
        }
        var entry = ClipboardEntry.FromImage(file, width, height, png.Length, source.Name,
            source.ExePath, ClipboardEntry.HashPngPrefix(png)); // 内容寻址键：同尺寸同字节的不同图不再互顶
        entry.Id = id;
        return entry;
    }
}

/// <summary>贴图窗口协调：App 层贴图窗口实例的创建与关闭。</summary>
public sealed class PinCoordinator
{
    private readonly ObservableCollection<PinWindow> _windows = [];
    public int Count => _windows.Count;

    public event Action? Changed;

    public void Open(byte[] png, int width, int height, double opacity,
        System.Windows.Media.ImageSource? display = null)
    {
        var win = new PinWindow(png, width, height, opacity, display);
        _windows.Add(win);
        win.Closed += (_, _) =>
        {
            _windows.Remove(win);
            Changed?.Invoke();
        };
        win.Show();
        Changed?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var w in _windows.ToArray()) w.Close();
        _windows.Clear();
    }
}
