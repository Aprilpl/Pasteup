using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 入库预缩略图（2026-11-10 复审问题 3）：ImageCodec.RenderThumbnailPng 纯函数 +
/// HistoryStore 的 thumbs 目录/保存/路径/删除/孤儿清理。
/// 不碰系统剪贴板、不依赖 App.Current（ThumbConverter 的缩略图优先分支属 UI 绑定路径，
/// 靠构建 + 实机冒烟；这里只锁 Store/ImageCodec 纯逻辑）。
/// </summary>
public class ThumbnailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private HistoryStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>纯色 Bgra32 位图（冻结）；stride = w×4。</summary>
    private static BitmapSource Solid(int w, int h)
    {
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, new byte[w * h * 4], w * 4);
        img.Freeze();
        return img;
    }

    // ---- ImageCodec.RenderThumbnailPng（纯函数） ----

    [Fact]
    public void RenderThumbnailPng_scales_down_to_640_keeping_aspect()
    {
        Assert.Equal(640, HistoryStore.ThumbnailWidth); // 规格常量：640 宽（瓦片可见宽 500–640 DIP）

        var png = ImageCodec.RenderThumbnailPng(Solid(800, 400), HistoryStore.ThumbnailWidth);
        Assert.NotEmpty(png);

        var decoded = ImageCodec.FromPng(png);
        Assert.Equal((640, 320), (decoded.PixelWidth, decoded.PixelHeight)); // 等比：800×400 → 640×320
    }

    [Fact]
    public void RenderThumbnailPng_does_not_upscale_small_source()
    {
        // 目标宽（640）≥ 原宽（400）：不放大，原尺寸编码
        var decoded = ImageCodec.FromPng(ImageCodec.RenderThumbnailPng(Solid(400, 200), HistoryStore.ThumbnailWidth));
        Assert.Equal((400, 200), (decoded.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void RenderThumbnailPng_output_is_decodable_png()
    {
        var png = ImageCodec.RenderThumbnailPng(Solid(800, 400), 640);
        var dims = ImageCodec.TryReadPngDimensions(png); // 魔数 + IHDR 可解析
        Assert.True(dims.HasValue);
        Assert.Equal(640u, dims.Value.Width);
        Assert.Equal(320u, dims.Value.Height);
    }

    // ---- HistoryStore.SaveThumbnailPng / ThumbnailPathFor ----

    [Fact]
    public void SaveThumbnailPng_writes_under_thumbs_and_ThumbnailPathFor_resolves_it()
    {
        var store = Store;
        var id = Guid.NewGuid();
        var name = store.SaveThumbnailPng(id, [1, 2, 3]);

        Assert.NotNull(name);
        Assert.Equal($"{id:N}.png", name);
        var path = store.ThumbnailPathFor(name);
        Assert.NotNull(path);
        Assert.Equal(Path.Combine(_dir, "cache", "thumbs", name!), path);
        Assert.True(File.Exists(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path!));
    }

    [Fact]
    public void SaveThumbnailPng_same_id_overwrites_without_throw()
    {
        var store = Store;
        var id = Guid.NewGuid();
        Assert.NotNull(store.SaveThumbnailPng(id, [1]));
        Assert.NotNull(store.SaveThumbnailPng(id, [2, 3])); // 幂等覆盖（后台任务与重试可能撞同 id）
        Assert.Equal(new byte[] { 2, 3 }, File.ReadAllBytes(store.ThumbnailPathFor($"{id:N}.png")!));
    }

    [Fact]
    public void SaveThumbnailPng_returns_null_when_thumbs_dir_blocked()
    {
        var store = Store;
        Directory.CreateDirectory(store.CacheDirectory);
        File.WriteAllText(Path.Combine(store.CacheDirectory, "thumbs"), "占位文件：让 thumbs 目录无法创建");
        Assert.Null(store.SaveThumbnailPng(Guid.NewGuid(), [1])); // 失败只返回 null，不抛
    }

    [Theory]
    [InlineData(@"..\..\evil.png")]                     // 相对穿越
    [InlineData(@"C:\evil.png")]                        // 绝对路径
    [InlineData("evil.png")]                            // 非 Guid 名
    [InlineData("ABCDEF0123456789ABCDEF0123456789.png")] // 大写不认：SaveThumbnailPng 恒小写
    [InlineData(null)]
    public void ThumbnailPathFor_rejects_unsafe_or_null_names(string? imageFile)
        => Assert.Null(Store.ThumbnailPathFor(imageFile));

    [Fact]
    public void DeleteThumbnailFile_ignores_unsafe_names_and_missing_files()
    {
        var store = Store;
        store.DeleteThumbnailFile(@"..\..\evil.png"); // 不得抛、不得越出 thumbs
        store.DeleteThumbnailFile("0123456789abcdef0123456789abcdef.png"); // 合法名但不存在：静默
    }

    // ---- 删除 / 孤儿清理：主图与缩略图同生共死 ----

    [Fact]
    public void DeleteImageFile_removes_master_and_thumbnail()
    {
        var store = Store;
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, [9]);
        Assert.NotNull(store.SaveThumbnailPng(id, [1]));
        var mainPath = Path.Combine(_dir, "cache", name);
        var thumbPath = store.ThumbnailPathFor(name);
        Assert.True(File.Exists(mainPath));
        Assert.True(File.Exists(thumbPath));

        store.DeleteImageFile(name);

        Assert.False(File.Exists(mainPath));
        Assert.False(File.Exists(thumbPath));
    }

    [Fact]
    public void CleanupOrphanImages_removes_orphan_master_and_its_thumbnail()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var keptId = Guid.NewGuid();
        var droppedId = Guid.NewGuid();
        var keptName = store.SaveImagePng(keptId, [1]);
        var droppedName = store.SaveImagePng(droppedId, [2]);
        Assert.NotNull(store.SaveThumbnailPng(keptId, [3]));
        Assert.NotNull(store.SaveThumbnailPng(droppedId, [4]));
        engine.Add(ClipboardEntry.FromImage(keptName, 10, 10, 1, null)); // 只引用 kept

        var removed = store.CleanupOrphanImages(engine);

        Assert.Equal(2, removed); // 孤儿主图 1 + 其缩略图 1
        Assert.True(File.Exists(Path.Combine(_dir, "cache", keptName)));
        Assert.True(File.Exists(store.ThumbnailPathFor(keptName)));
        Assert.False(File.Exists(Path.Combine(_dir, "cache", droppedName)));
        Assert.False(File.Exists(store.ThumbnailPathFor(droppedName)));
    }

    [Fact]
    public void CleanupOrphanImages_removes_thumbnail_when_master_file_missing()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, [1]);
        Assert.NotNull(store.SaveThumbnailPng(id, [2]));
        engine.Add(ClipboardEntry.FromImage(name, 4, 4, 1, null));
        File.Delete(Path.Combine(_dir, "cache", name)); // 主图被外部删除/半写丢失

        store.CleanupOrphanImages(engine);

        Assert.False(File.Exists(store.ThumbnailPathFor(name)));
    }

    [Fact]
    public void CleanupOrphanImages_leaves_unrelated_files_in_thumbs_dir()
    {
        var store = Store;
        Directory.CreateDirectory(store.ThumbnailDirectory);
        var junk = Path.Combine(store.ThumbnailDirectory, "not-a-thumb.png"); // *.png 但非 32hex 白名单名
        File.WriteAllText(junk, "not ours");

        store.CleanupOrphanImages(new HistoryEngine());

        Assert.True(File.Exists(junk)); // 不认识的命名一律不动（信任边界：目录内容不可信）
    }
}
