using System.Text.Json.Serialization;
using Pasteup.Core.Text;

namespace Pasteup.Core.History;

public enum EntryKind
{
    Text = 0,
    Image = 1,
    Link = 2,
    File = 3,
}

/// <summary>剪贴板历史条目。图像本体以 PNG 文件形式存于缓存目录，条目内只保留元数据。</summary>
public sealed class ClipboardEntry
{
    public required Guid Id { get; set; }
    public required EntryKind Kind { get; init; }
    public string Preview { get; set; } = string.Empty;       // set：磁盘条目归一化需补默认值（HistoryStore.NormalizeEntry）
    public string? FullText { get; init; }
    public string? ImageFile { get; init; }      // 相对缓存目录的文件名
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }
    public List<string> Files { get; set; } = []; // set：同 Preview，防 "Files": null 炸搜索
    public string? SourceApp { get; init; }
    public string? SourceAppPath { get; init; }  // exe 完整路径（应用图标的来源）；旧条目为 null，回退首字母
    public bool Pinned { get; set; }
    public bool Favorite { get; set; }          // 收藏：独立双态标志，只打标记不动顺序
    public DateTime UtcTimestamp { get; set; } = DateTime.UtcNow;
    public long SizeBytes { get; init; }

    /// <summary>图像内容摘要（SHA-256 前 8 字节的 16 位小写 hex，入库时随 PNG 字节一次算好）。
    /// 旧条目（磁盘读回）为 null，去重回退「宽×高×字节数」弱键——弱键碰撞会把同尺寸
    /// 同字节数的不同图互相顶掉（旧条目静默丢失、PNG 成孤儿文件），摘要使键真正内容寻址。</summary>
    public string? ContentHash { get; set; }

    /// <summary>内容等价键：同类型同内容视为同一条（去重与置顶更新用）。
    /// 计算属性不入档：文本键内嵌整段 FullText，序列化等于把大文本在文件里存两遍
    /// （实测 21.5MB 种子存成 42.6MB；读回时无 setter 本就被跳过，纯属浪费）。</summary>
    [JsonIgnore]
    public string ContentKey => Kind switch
    {
        // 不含 ImageFile：那是每次复制随机生成的文件名，掺进键里图像就永远去不了重。
        // 新条目按 ContentHash 内容寻址；旧条目无摘要保持弱键（与同内容新条目仍可去重升级）
        EntryKind.Image => ContentHash is { } h ? $"img:{h}" : $"img:{ImageWidth}x{ImageHeight}:{SizeBytes}",
        EntryKind.File => "file:" + string.Join("|", Files.Order(StringComparer.Ordinal)),
        _ => $"{Kind}:{FullText ?? Preview}",
    };

    /// <summary>去重等价判定：语义与 ContentKey 相同但不构造键字符串。
    /// 引擎 Add 的去重循环逐条与现存条目比较——文本键内嵌整段 FullText，
    /// 逐条构键 = 大文本入库时对全库做整段拷贝（千条量级最重可至上千次）。</summary>
    public static bool SameContent(ClipboardEntry a, ClipboardEntry b)
    {
        if (a.Kind != b.Kind) return false;
        return a.Kind switch
        {
            // 双方都有摘要 → 内容寻址；任一方是旧条目（无摘要）→ 弱键回退（保住旧条目的去重升级路径）
            EntryKind.Image when a.ContentHash is not null && b.ContentHash is not null =>
                string.Equals(a.ContentHash, b.ContentHash, StringComparison.Ordinal),
            EntryKind.Image => a.ImageWidth == b.ImageWidth && a.ImageHeight == b.ImageHeight
                                                                && a.SizeBytes == b.SizeBytes,
            EntryKind.File => a.ContentKey == b.ContentKey, // 罕见路径：文件列表小，构键无碍
            _ => string.Equals(a.FullText ?? a.Preview, b.FullText ?? b.Preview, StringComparison.Ordinal),
        };
    }

    public static ClipboardEntry FromText(string text, string? sourceApp, string? sourceAppPath = null)
    {
        var isLink = TextUtil.IsSingleUrl(text);
        return new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = isLink ? EntryKind.Link : EntryKind.Text,
            Preview = TextUtil.MakePreview(text),
            FullText = text,
            SourceApp = sourceApp,
            SourceAppPath = sourceAppPath,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text),
        };
    }

    public static ClipboardEntry FromImage(string imageFileName, int width, int height, long pngBytes,
        string? sourceApp, string? sourceAppPath = null, string? contentHash = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Image,
        Preview = $"图像 {width}×{height} · {TextUtil.FormatSize(pngBytes)}",
        ImageFile = imageFileName,
        ImageWidth = width,
        ImageHeight = height,
        SourceApp = sourceApp,
        SourceAppPath = sourceAppPath,
        SizeBytes = pngBytes,
        ContentHash = contentHash,
    };

    /// <summary>PNG 内容摘要前 8 字节（16 位小写 hex）。入库后台段调用一次，存入 ContentHash。</summary>
    public static string HashPngPrefix(byte[] png) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png), 0, 8).ToLowerInvariant();

    /// <summary>由完整 SHA-256 摘要（32 字节）取前 8 字节转 16 位小写 hex：DIB 入库路径
    /// 「边写盘边增量算摘要」，不再需要整份 PNG 字节；与对整份字节哈希的
    /// <see cref="HashPngPrefix(byte[])"/> 结果一致（同一种 ContentHash 口径）。</summary>
    public static string HashPngPrefix(ReadOnlySpan<byte> sha256Digest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sha256Digest.Length, 8);
        return Convert.ToHexString(sha256Digest[..8]).ToLowerInvariant();
    }

    public static ClipboardEntry FromFiles(IReadOnlyList<string> files, string? sourceApp, string? sourceAppPath = null)
    {
        var joined = string.Join("\r\n", files);
        return new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.File,
            Preview = TextUtil.MakePreview(joined),
            FullText = joined,
            Files = [.. files],
            SourceApp = sourceApp,
            SourceAppPath = sourceAppPath,
            SizeBytes = System.Text.Encoding.UTF8.GetByteCount(joined), // 口径与 FromText 一致（曾用字符数×2 估算）
        };
    }
}
