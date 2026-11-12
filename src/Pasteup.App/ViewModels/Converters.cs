using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Pasteup.App.ViewModels;

/// <summary>bool → TabButton 激活标记。</summary>
public sealed class BoolToActiveConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "Active" : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>null → Collapsed、非 null → Visible；参数 "Inverse" 反转。
/// 来源图标槽双层用：Image 非 null 才显，首字母层反向（图标到了盖住字母而非透出）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var inverse = string.Equals(parameter as string, "Inverse", StringComparison.OrdinalIgnoreCase);
        return (value is null) ^ inverse ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ImageFile（缓存目录相对名）→ 缩略图 BitmapSource。
/// 优先读入库预生成的 640 宽缩略图（cache/thumbs，见 HistoryStore.SaveThumbnailPng）：
/// 小 PNG 解码 &lt;1ms，不再从原图解码（4K 照片型 PNG 首解实测 25–100ms，滚动卡顿）；
/// 旧条目没有缩略图时回退原图（行为与改动前一致）。
/// 解码走 UriSource + DecodePixelWidth：WIC 原生缩放解码，不在托管堆落整文件
/// byte[]/MemoryStream（2026-11-02 压测：StreamSource 路径单遍全表滚动堆 279→729MB、
/// 12s 内 63 次 gen2 GC）。宽度经 ConverterParameter 指定：行缩略图默认 132（66px 槽 2x），
/// 图像 Tab 瓦片传 1120（640 DIP × 1.75 DPI）。命中预缩略图时解码宽封顶 ThumbnailWidth（640）：
/// 行 132 不变、瓦片 1120→640（解码后由 Image 拉伸显示，清晰度可接受）。两档独立 LRU——
/// 行图常规 ~50KB 原生帧，瓦片常规 ~3MB/张；但长截图（宽度受限、高度随纵横比）单张可到
/// 数十 MB，故 LRU 除张数外再按估算字节预算封顶。绑定只在 UI 线程求值，普通字典 + lock 足够。
/// </summary>
public sealed class ThumbConverter : IValueConverter
{
    /// <summary>
    /// 解码位图的 LRU：按「张数容量 + 估算字节预算」双条件淘汰。
    /// public 纯为可测（仓库无 InternalsVisibleTo 先例）；ClearCaches 供空闲深度清理调用
    /// （窗口全隐后释放原生 WIC 帧提交）。
    /// 为什么还要字节预算：`DecodePixelWidth` 只限宽、高度随原纵横比，长截图（如 1280×20000
    /// 解成 1120×17500≈78MB/张）会让「N 张」的张数上限放大成 GB 级；预算按 `像素数×4B`
    /// 估算（解码产物都是 32bpp，与显示无关，略偏保守），把常驻内存封顶。
    /// </summary>
    public sealed class LruCache(int capacity, long budgetBytes = long.MaxValue)
    {
        private sealed record Entry(BitmapSource Source, LinkedListNode<string> Node, long Bytes);
        private readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _lru = [];
        private long _bytes; // 当前全部条目的估算字节合计（只在锁内读写）

        public int Count
        {
            get { lock (_lru) return _map.Count; }
        }

        /// <summary>当前估算字节合计（仅测试/诊断；锁内读，保证与 Count 同一快照口径）。</summary>
        public long Bytes
        {
            get { lock (_lru) return _bytes; }
        }

        public BitmapSource? Get(string key)
        {
            lock (_lru)
            {
                if (!_map.TryGetValue(key, out var hit)) return null;
                _lru.Remove(hit.Node);
                _lru.AddFirst(hit.Node); // 命中刷新新鲜度（同一节点搬家，不新建）
                return hit.Source;
            }
        }

        public void Put(string key, BitmapSource source)
        {
            var bytes = EstimateBytes(source);
            lock (_lru)
            {
                // 重复键 = 替换：先把旧节点从链表摘下并扣掉旧字节。否则旧节点会悬空留在
                // 链表里，之后按链表尾逐出时 _map.Remove(同 key) 会把同名活条目误删。
                if (_map.TryGetValue(key, out var old))
                {
                    _lru.Remove(old.Node);
                    _map.Remove(key);
                    _bytes -= old.Bytes;
                }

                _map[key] = new Entry(source, _lru.AddFirst(key), bytes);
                _bytes += bytes;

                // 双条件从链表尾淘汰最旧，但至少保留刚放入的新条目：单张就超预算时允许它
                // 独占（否则「放入→立即逐出→下次再解」会变成抖动的死循环）。
                while (_map.Count > 1 && (_map.Count > capacity || _bytes > budgetBytes))
                {
                    var last = _lru.Last!;
                    _lru.RemoveLast();
                    if (_map.Remove(last.Value, out var evicted)) _bytes -= evicted.Bytes;
                }
            }
        }

        public void Clear()
        {
            lock (_lru)
            {
                _map.Clear();
                _lru.Clear();
                _bytes = 0;
            }
        }

        private static long EstimateBytes(BitmapSource source)
            => (long)source.PixelWidth * source.PixelHeight * 4;
    }

    private const int RowDecodeWidth = 132;

    // 行档：常规 132×74≈39KB，256 张≈10MB 在 24MB 预算内（张数仍生效）；长截图
    // 132×2062≈1.09MB 时预算先到（≈22 张），不会再按 256 张放大到 ~280MB。
    private static readonly LruCache RowCache = new(256, 24L * 1024 * 1024);

    // 瓦片档：常规 1120×630≈2.8MB，16 张≈45MB 在 64MB 预算内；单张长截图解到 ~78MB
    // 时独占预算（单张超预算允许放入），下一张进来即逐出，天花板 ~78MB 而非 16×78MB。
    private static readonly LruCache TileCache = new(16, 64L * 1024 * 1024);

    /// <summary>空闲深度清理：两档缓存全弃（下次滚动按需重解；常规行图 ~1–2ms 不值长驻提交，
    /// 但 4K 照片型 PNG 首解实测 25–100ms——复审 A5 已证「~0.5ms/张」只对小图成立）。</summary>
    public static void ClearCaches()
    {
        RowCache.Clear();
        TileCache.Clear();
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string file || !Core.History.HistoryStore.IsValidImageFileName(file)) return null;
        var width = parameter is string ps && int.TryParse(ps, out var pw) && pw > 0 ? pw : RowDecodeWidth;
        var cache = width <= 256 ? RowCache : TileCache;
        // 缓存 key 仍用 file（不是缩略图路径）：行/瓦片是两档独立缓存，同一 file 在各自档里
        // 只有一种解码结果；若缩略图晚于首次解码就绪，缓存里存的是原图解码（更清晰，不是坏值），
        // 空闲清理后下次自然改读缩略图，无需失效逻辑。
        if (cache.Get(file) is { } hit) return hit;

        // 优先读入库预缩略图；旧条目（无缩略图）或生成失败回退原图，行为与改动前一致。
        var thumb = App.Current.Store.ThumbnailPathFor(file);
        var useThumbnail = thumb is not null && File.Exists(thumb);
        var path = useThumbnail ? thumb! : Path.Combine(App.Current.Store.CacheDirectory, file);
        // 命中缩略图时解码宽封顶 ThumbnailWidth（行 132 不变、瓦片 1120→640，Image 拉伸显示）；
        // 回退原图时保持请求宽（旧条目路径零变化）。
        var decodeWidth = useThumbnail ? Math.Min(width, Core.History.HistoryStore.ThumbnailWidth) : width;
        if (!File.Exists(path)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.DecodePixelWidth = decodeWidth;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.EndInit();
            img.Freeze();
            cache.Put(file, img);
            return img;
        }
        catch
        {
            return null; // 缓存文件损坏（写一半被杀）：行显空，下次复制自然替换
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
