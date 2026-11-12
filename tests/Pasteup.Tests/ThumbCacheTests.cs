using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pasteup.App.ViewModels;
using Xunit;

namespace Pasteup.Tests;

/// <summary>缩略图 LRU（空闲深度清理的 ClearCaches 依赖，2026-11-03 内存优化二期；
/// 2026-11-10 复审补「容量 + 估算字节预算」双条件淘汰与重复键替换语义）。</summary>
public class ThumbCacheTests
{
    /// <summary>默认 4×4；估算字节按 像素数×4B 计（4×4=64B，与实际像素深度无关）。</summary>
    private static BitmapSource Black(int w = 4, int h = 4)
    {
        var pf = PixelFormats.Bgr24;
        var stride = (w * pf.BitsPerPixel + 7) / 8;
        var img = BitmapSource.Create(w, h, 96, 96, pf, null, new byte[stride * h], stride);
        img.Freeze();
        return img;
    }

    [Fact]
    public void 超容量逐出最旧_命中刷新新鲜度()
    {
        var cache = new ThumbConverter.LruCache(2);
        cache.Put("a", Black());
        cache.Put("b", Black());
        Assert.Equal(2, cache.Count);

        _ = cache.Get("a");   // a 变新鲜，b 成最旧
        cache.Put("c", Black()); // 逐出 b
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("b"));
        Assert.NotNull(cache.Get("a"));
        Assert.NotNull(cache.Get("c"));
    }

    [Fact]
    public void 重复键Put为替换且清掉旧节点()
    {
        var cache = new ThumbConverter.LruCache(2);
        cache.Put("a", Black());
        cache.Put("b", Black());
        var a2 = Black(5, 5); // 新实例（估算体积也不同），替换后必须命中它
        cache.Put("a", a2);
        Assert.Equal(2, cache.Count);
        Assert.Same(a2, cache.Get("a")!);

        // 旧 a 节点若悬空留在链表，逐出时会按同名 key 把活条目误删（这里会丢 a）
        cache.Put("c", Black());
        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("b"));
        Assert.Same(a2, cache.Get("a")!);
        Assert.NotNull(cache.Get("c"));
    }

    [Fact]
    public void 字节预算逐出最旧()
    {
        // 4×4=64B/张：cap=100 永不撞张数，budget=130 只容 2 张
        var cache = new ThumbConverter.LruCache(100, 130);
        cache.Put("a", Black());
        cache.Put("b", Black());
        Assert.Equal(2, cache.Count);
        Assert.Equal(128L, cache.Bytes);

        cache.Put("c", Black()); // 128+64 > 130 → 从最旧逐出 a，b/c 留下
        Assert.Equal(2, cache.Count);
        Assert.Equal(128L, cache.Bytes);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
        Assert.NotNull(cache.Get("c"));
    }

    [Fact]
    public void 单张超预算仍允许放入_不抛不死循环()
    {
        var cache = new ThumbConverter.LruCache(100, 32); // 预算 < 单张 64B
        cache.Put("a", Black()); // 超预算也必须放得进（否则每次解码都白解）
        Assert.Equal(1, cache.Count);
        Assert.Equal(64L, cache.Bytes);
        Assert.NotNull(cache.Get("a"));

        cache.Put("b", Black()); // 第二张进来：逐出旧的仍超预算，但只留新条目、不空转
        Assert.Equal(1, cache.Count);
        Assert.Equal(64L, cache.Bytes);
        Assert.Null(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
    }

    [Fact]
    public void Clear清空后Miss_再Put恢复()
    {
        var cache = new ThumbConverter.LruCache(8);
        cache.Put("a", Black());
        Assert.Equal(1, cache.Count);
        Assert.NotEqual(0L, cache.Bytes);
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0L, cache.Bytes); // 字节计数不残留（下一次 Put 不会把旧账带入预算）
        Assert.Null(cache.Get("a"));
        cache.Put("b", Black());
        Assert.Equal(1, cache.Count);
        Assert.Equal(64L, cache.Bytes);
        Assert.NotNull(cache.Get("b"));
    }

    [Fact]
    public void ClearCaches空缓存与有缓存都不抛()
    {
        ThumbConverter.ClearCaches(); // 空缓存（启动即空闲清理的路径）
        ThumbConverter.ClearCaches();
    }
}
