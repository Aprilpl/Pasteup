using System.IO;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 第2批-4（2026-11-10 复审问题 5）：引擎聚合字节预算 + 图像缓存配额。
/// 纯内存引擎 + 临时目录 HistoryStore，不碰 %APPDATA%/剪贴板。
/// </summary>
public class HistoryBudgetTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private HistoryStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ClipboardEntry Text(string preview, long sizeBytes, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = preview,
        FullText = preview,
        SizeBytes = sizeBytes,
        UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static ClipboardEntry ImageEntry(string imageFile, int width, long sizeBytes, int minutesAgo = 0)
    {
        var e = ClipboardEntry.FromImage(imageFile, width, width, sizeBytes, null);
        e.UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo);
        return e;
    }

    // ---- 引擎聚合字节预算 ----

    [Fact]
    public void Budget_defaults_match_review_targets()
    {
        Assert.Equal(128L * 1024 * 1024, new HistoryEngine().TotalBytesBudget);
        Assert.Equal(512L * 1024 * 1024, Store.ImageCacheBudgetBytes);
    }

    [Fact]
    public void Add_trims_oldest_until_total_within_budget()
    {
        var engine = new HistoryEngine { Capacity = 100, TotalBytesBudget = 100 };
        engine.Add(Text("最旧", 40, minutesAgo: 30));
        engine.Add(Text("中间", 40, minutesAgo: 20));
        engine.Add(Text("最新", 40, minutesAgo: 10)); // 合计 120 > 100 → 淘汰最旧 → 80

        Assert.Equal(["最新", "中间"], engine.Snapshot().Select(e => e.Preview).ToArray());
        Assert.Equal(80L, engine.TotalSizeBytes);
    }

    [Fact]
    public void Load_trims_over_budget_entries_by_budget()
    {
        var engine = new HistoryEngine { Capacity = 100, TotalBytesBudget = 150 };
        engine.Load([Text("最旧", 100, 30), Text("中间", 100, 20), Text("最新", 100, 10)]);
        // 300 > 150：淘汰最旧 → 200 > 150 → 再淘汰中间 → 100 ≤ 150
        var snap = engine.Snapshot();
        Assert.Single(snap);
        Assert.Equal("最新", snap[0].Preview);
        Assert.Equal(100L, engine.TotalSizeBytes);
    }

    [Fact]
    public void Budget_keeps_pinned_and_favorite_even_when_over_budget()
    {
        var engine = new HistoryEngine { Capacity = 100, TotalBytesBudget = 100 };
        var pinned = Text("置顶", 60, 30);
        pinned.Pinned = true;
        var favorite = Text("收藏", 60, 20);
        favorite.Favorite = true;
        var normal = Text("普通最旧", 40, 40);
        engine.Load([pinned, favorite, normal]); // 160 > 100：普通条目被裁，置顶/收藏保留

        Assert.Equal(2, engine.Count);
        Assert.Equal(120L, engine.TotalSizeBytes); // 置顶+收藏仍超预算：显式保护优先，属预期
        Assert.Contains(engine.Snapshot(), e => e.Pinned);
        Assert.Contains(engine.Snapshot(), e => e.Favorite);
    }

    [Fact]
    public void Single_entry_over_budget_is_kept()
    {
        var engine = new HistoryEngine { TotalBytesBudget = 100 };
        engine.Add(Text("单条大文本", 10_000));

        Assert.Equal(1, engine.Count);
        Assert.Equal(10_000L, engine.TotalSizeBytes);
        engine.Trim(); // 显式再裁剪也不应把唯一一条清掉
        Assert.Equal(1, engine.Count);
    }

    [Fact]
    public void Negative_size_counts_as_zero_for_budget()
    {
        var engine = new HistoryEngine { TotalBytesBudget = 100 };
        engine.Load([Text("负数", -500, 20), Text("正常", 60, 10)]);

        Assert.Equal(60L, engine.TotalSizeBytes); // 负值按 0
        Assert.Equal(2, engine.Count);             // 合计 60 ≤ 100，不裁
    }

    [Fact]
    public void Trim_applies_new_budget_and_broadcasts_once()
    {
        var engine = new HistoryEngine();
        engine.Load([Text("最旧", 40, 30), Text("中间", 40, 20), Text("最新", 40, 10)]); // 默认 128MB 不裁
        Assert.Equal(3, engine.Count);
        var fired = 0;
        engine.Changed += () => fired++;
        var version = engine.Version;

        engine.TotalBytesBudget = 80;
        engine.Trim(); // 120 > 80：淘汰最旧

        Assert.Equal(2, engine.Count);
        Assert.Equal(80L, engine.TotalSizeBytes);
        Assert.Equal(1, fired);                  // 确有条目被裁才广播（自动保存/面板刷新语义不变）
        Assert.Equal(version + 1, engine.Version);

        engine.Trim(); // 无事可裁：不广播、版本不涨
        Assert.Equal(1, fired);
        Assert.Equal(version + 1, engine.Version);
    }

    [Fact]
    public void Budget_evicted_image_goes_to_pending_delete_queue()
    {
        var engine = new HistoryEngine { TotalBytesBudget = 100 };
        engine.Add(ImageEntry("old.png", 10, 100, minutesAgo: 30));
        engine.Add(Text("新", 100, 10)); // 200 > 100：最旧图像被预算淘汰

        Assert.DoesNotContain(engine.Snapshot(), e => e.ImageFile == "old.png");
        Assert.Equal(["old.png"], engine.DrainPendingImageDeletes()); // 删除即删盘队列照旧
    }

    // ---- 图像缓存配额（HistoryStore，临时目录） ----

    [Fact]
    public void GetImageCacheBytes_sums_main_and_thumbnail_files()
    {
        var store = Store;
        Assert.Equal(0L, store.GetImageCacheBytes()); // 目录都不存在

        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, new byte[100]);
        Assert.NotNull(store.SaveThumbnailPng(id, new byte[50]));
        Assert.Equal(150L, store.GetImageCacheBytes());

        store.DeleteImageFile(name); // 主图 + 缩略图双删
        Assert.Equal(0L, store.GetImageCacheBytes());
    }

    [Fact]
    public void EnforceImageCacheBudget_evicts_oldest_images_and_cleans_orphans()
    {
        var store = Store;
        var engine = new HistoryEngine();

        // 旧图（非置顶/收藏、最旧 → 配额淘汰）
        var oldId = Guid.NewGuid();
        var oldName = store.SaveImagePng(oldId, new byte[40]);
        Assert.NotNull(store.SaveThumbnailPng(oldId, new byte[20]));
        var oldEntry = ImageEntry(oldName, 10, 40, minutesAgo: 30);
        oldEntry.Id = oldId;
        engine.Add(oldEntry);

        // 中图（置顶 → 豁免）
        var pinnedId = Guid.NewGuid();
        var pinnedName = store.SaveImagePng(pinnedId, new byte[30]);
        Assert.NotNull(store.SaveThumbnailPng(pinnedId, new byte[10]));
        var pinnedEntry = ImageEntry(pinnedName, 11, 30, minutesAgo: 20);
        pinnedEntry.Id = pinnedId;
        engine.Add(pinnedEntry);
        Assert.True(engine.TogglePin(pinnedEntry.Id));

        // 新图（最新 → 保留）
        var newId = Guid.NewGuid();
        var newName = store.SaveImagePng(newId, new byte[20]);
        Assert.NotNull(store.SaveThumbnailPng(newId, new byte[10]));
        var newEntry = ImageEntry(newName, 12, 20, minutesAgo: 10);
        newEntry.Id = newId;
        engine.Add(newEntry);

        // 孤儿（未入引擎）：主图 + 缩略图，同批清理
        var orphanId = Guid.NewGuid();
        store.SaveImagePng(orphanId, new byte[15]);
        Assert.NotNull(store.SaveThumbnailPng(orphanId, new byte[5]));

        // 被引用文件：旧 60 + 置顶 40 + 新 30 = 130；预算 70 恰好只够置顶+新
        var removed = store.EnforceImageCacheBudget(engine, budgetBytes: 70);

        Assert.Equal(3, removed); // 孤儿文件 2（主图+缩略图）+ 被淘汰的旧图条目 1
        Assert.Equal(2, engine.Count); // 只剩置顶 + 新图
        Assert.DoesNotContain(engine.Snapshot(), e => e.Id == oldId);
        Assert.False(File.Exists(Path.Combine(store.CacheDirectory, oldName)));
        Assert.False(File.Exists(store.ThumbnailPathFor(oldName)!));
        // 被引用者保留：置顶与新图条目不删，主图+缩略图都在
        Assert.Contains(engine.Snapshot(), e => e.Id == pinnedId);
        Assert.Contains(engine.Snapshot(), e => e.Id == newId);
        Assert.True(File.Exists(Path.Combine(store.CacheDirectory, pinnedName)));
        Assert.True(File.Exists(store.ThumbnailPathFor(pinnedName)!));
        Assert.True(File.Exists(Path.Combine(store.CacheDirectory, newName)));
        Assert.True(File.Exists(store.ThumbnailPathFor(newName)!));
        Assert.True(store.GetImageCacheBytes() <= 70);
    }

    [Fact]
    public void EnforceImageCacheBudget_evicts_in_oldest_first_order()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var ids = new Guid[3];
        for (var i = 0; i < 3; i++)
        {
            ids[i] = Guid.NewGuid();
            var name = store.SaveImagePng(ids[i], new byte[30]);
            var e = ImageEntry(name, 10 + i, 30, minutesAgo: 30 - i * 10); // i=0 最旧
            e.Id = ids[i];
            engine.Add(e);
        }

        var removed = store.EnforceImageCacheBudget(engine, 35);

        Assert.Equal(2, removed); // 最旧两张被淘汰，只剩最新
        var snap = Assert.Single(engine.Snapshot());
        Assert.Equal(ids[2], snap.Id);
    }

    [Fact]
    public void EnforceImageCacheBudget_noop_when_under_budget_keeps_images()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, new byte[10]);
        Assert.NotNull(store.SaveThumbnailPng(id, new byte[5]));
        var entry = ImageEntry(name, 10, 10);
        entry.Id = id;
        engine.Add(entry);
        var orphanId = Guid.NewGuid();
        store.SaveImagePng(orphanId, new byte[7]);

        var removed = store.EnforceImageCacheBudget(engine); // 默认 512MB，远未超

        Assert.Equal(1, removed); // 只清孤儿主图
        Assert.Contains(engine.Snapshot(), e => e.Id == id);
        Assert.True(File.Exists(Path.Combine(store.CacheDirectory, name)));
        Assert.True(File.Exists(store.ThumbnailPathFor(name)!));
    }

    [Fact]
    public void EnforceImageCacheBudget_keeps_pinned_images_even_over_budget()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, new byte[100]);
        var entry = ImageEntry(name, 10, 100);
        entry.Id = id;
        entry.Pinned = true;
        engine.Add(entry);

        var removed = store.EnforceImageCacheBudget(engine, budgetBytes: 1);

        Assert.Equal(0, removed); // 置顶图像不删，允许超配额
        var snap = Assert.Single(engine.Snapshot());
        Assert.Equal(id, snap.Id);
        Assert.True(File.Exists(Path.Combine(store.CacheDirectory, name)));
    }

    [Fact]
    public void Negative_image_budget_treated_as_zero()
    {
        var store = Store;
        store.ImageCacheBudgetBytes = -1;
        var engine = new HistoryEngine();
        var imageId = Guid.NewGuid();
        var imageName = store.SaveImagePng(imageId, new byte[10]);
        var imageEntry = ImageEntry(imageName, 10, 10);
        imageEntry.Id = imageId;
        engine.Add(imageEntry);
        engine.Add(Text("文本", 1, minutesAgo: 1));

        var removed = store.EnforceImageCacheBudget(engine); // 负预算按 0：非豁免图像被移除

        Assert.Equal(1, removed);
        Assert.DoesNotContain(engine.Snapshot(), e => e.Kind == EntryKind.Image);
        Assert.False(File.Exists(Path.Combine(store.CacheDirectory, imageName)));
    }
}
