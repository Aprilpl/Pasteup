using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 第4批 P2-1/P2-2：图像入库降解码成本/降峰值的新 API。
/// 纯 ImageCodec/HistoryStore + 临时目录；不碰剪贴板/App.Current。
/// P2-1 = DecodePngThumbnail（入库只解码缩略图尺寸，不再全量解码）；
/// P2-2 = EncodePngToStream + HashingWriteStream + HistoryStore.SavePng（DIB 流式落盘、边写边算摘要）。
/// </summary>
public class IngestImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>纯色 Bgra32 位图（冻结），stride = w×4。</summary>
    private static BitmapSource Solid(int w, int h) => Bitmap(w, h, alpha: 255);

    private static BitmapSource Bitmap(int w, int h, byte alpha)
    {
        var stride = w * 4;
        var pixels = new byte[stride * h];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 10;      // B
            pixels[i + 1] = 20;  // G
            pixels[i + 2] = 30;  // R
            pixels[i + 3] = alpha;
        }
        var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        img.Freeze();
        return img;
    }

    private static byte[] PngOf(int w, int h) => ImageCodec.ToPng(Solid(w, h));

    private static byte[] PixelsOf(BitmapSource src)
    {
        var bytes = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(bytes, src.PixelWidth * 4, 0);
        return bytes;
    }

    private static byte[] Rgba(BitmapSource src, int x, int y)
    {
        var px = new byte[4];
        src.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return px;
    }

    // ---- P2-1：DecodePngThumbnail（WIC 缩放解码，只解缩略尺寸） ----

    [Fact]
    public void DecodePngThumbnail_scales_down_to_target_width()
    {
        var png = PngOf(1600, 800);
        Assert.Equal((1600u, 800u), ImageCodec.TryReadPngDimensions(png)); // 宽高走 IHDR，不需要解码

        var thumb = ImageCodec.DecodePngThumbnail(png, HistoryStore.ThumbnailWidth);

        Assert.True(thumb.IsFrozen);
        Assert.Equal((640, 320), (thumb.PixelWidth, thumb.PixelHeight)); // 等比 1600×800 → 640×320
        var px = Rgba(thumb, 2, 2);
        Assert.Equal(((byte)10, (byte)20, (byte)30, (byte)255), (px[0], px[1], px[2], px[3])); // 像素内容不变
    }

    [Fact]
    public void DecodePngThumbnail_does_not_upscale_small_image()
    {
        // 目标宽（640）≥ 原宽（400）：走原尺寸解码，不放大（WPF DecodePixelWidth 会放大，必须显式挡掉）
        var thumb = ImageCodec.DecodePngThumbnail(PngOf(400, 200), HistoryStore.ThumbnailWidth);
        Assert.Equal((400, 200), (thumb.PixelWidth, thumb.PixelHeight));
    }

    [Fact]
    public void DecodePngThumbnail_throws_for_bad_bytes()
        => Assert.ThrowsAny<Exception>(() => ImageCodec.DecodePngThumbnail([1, 2, 3], HistoryStore.ThumbnailWidth));

    [Fact]
    public void DecodePngThumbnail_throws_for_truncated_body_with_valid_header()
    {
        // IHDR 合法、像素数据被截断：入库侧据此回退 DIB（BuildFromPng catch → corrupt=true）
        var png = PngOf(1600, 800);
        Assert.ThrowsAny<Exception>(() => ImageCodec.DecodePngThumbnail(png[..40], HistoryStore.ThumbnailWidth));
    }

    // ---- P2-2：EncodePngToStream / HashingWriteStream / HistoryStore.SavePng ----

    [Fact]
    public void EncodePngToStream_writes_decodable_png_and_keeps_output_open()
    {
        var src = Solid(37, 23);
        using var ms = new MemoryStream();
        ImageCodec.EncodePngToStream(src, ms);

        Assert.True(ms.CanWrite);            // 输出流不归编码器所有（HashingWriteStream 依赖此约定）
        ms.WriteByte(0);                     // 仍可继续写：没有被 Save 关闭
        var bytes = ms.ToArray()[..^1];      // 去掉测试追加的尾字节

        var decoded = ImageCodec.FromPng(bytes);
        var direct = ImageCodec.FromPng(ImageCodec.ToPng(src));
        Assert.Equal((37, 23), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal((direct.PixelWidth, direct.PixelHeight), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(PixelsOf(direct), PixelsOf(decoded)); // 与 ToPng 路径解码结果逐字节一致
    }

    [Fact]
    public void HashingWriteStream_hashes_all_writes_and_leaves_inner_open()
    {
        var data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7)).ToArray();
        using var inner = new MemoryStream();
        var hashing = new HashingWriteStream(inner);

        hashing.Write(data, 0, 100);
        hashing.Write(data.AsSpan(100, 3900));
        hashing.WriteByte(data[4000]);
        hashing.Write(data.AsSpan(4001, 999)); // 分片 + span + 单字节混合写入
        var digest = hashing.Hash;

        Assert.Equal(32, digest.Length);
        Assert.Equal(SHA256.HashData(data), digest);
        Assert.Equal(digest, hashing.Hash); // 首次访问定稿/缓存，重复访问同一数组

        hashing.Dispose();                  // 不关闭 inner
        Assert.Equal(data, inner.ToArray());
        Assert.Equal(digest, hashing.Hash); // Dispose 之后仍可读缓存摘要
    }

    [Fact]
    public void HashingWriteStream_passes_position_through_and_rejects_reads()
    {
        using var inner = new MemoryStream();
        using var hashing = new HashingWriteStream(inner);
        Assert.False(hashing.CanRead);
        Assert.True(hashing.CanWrite);
        Assert.Throws<NotSupportedException>(() => { _ = hashing.Read([0], 0, 1); });

        hashing.Write(new byte[] { 1, 2, 3 }, 0, 3);
        Assert.Equal(3L, hashing.Length);
        Assert.Equal(3L, hashing.Position);
        Assert.Equal(3L, hashing.Seek(0, SeekOrigin.End)); // 位置类 Seek 透传（纯查询不影响摘要）
        Assert.Equal(SHA256.HashData(new byte[] { 1, 2, 3 }), hashing.Hash);
    }

    [Fact]
    public void HashPngPrefix_span_overload_matches_byte_array_overload()
    {
        var png = PngOf(120, 80);
        var digest = SHA256.HashData(png);

        var fromBytes = ClipboardEntry.HashPngPrefix(png);
        var fromDigest = ClipboardEntry.HashPngPrefix(digest.AsSpan());

        Assert.Equal(fromBytes, fromDigest);              // ContentHash 口径一致（SHA-256 前 8 字节 hex）
        Assert.Equal(16, fromDigest.Length);
        Assert.Equal(fromDigest.ToLowerInvariant(), fromDigest); // 16 位小写 hex
    }

    [Fact]
    public void SavePng_writes_stream_content_and_reports_size()
    {
        var store = new HistoryStore(_dir);
        var id = Guid.NewGuid();
        var payload = PngOf(64, 32);

        var file = store.SavePng(id, s => s.Write(payload, 0, payload.Length), out var sizeBytes);

        Assert.Equal($"{id:N}.png", file);
        var path = Path.Combine(_dir, "cache", file);
        Assert.True(File.Exists(path));
        Assert.Equal(payload, File.ReadAllBytes(path)); // 写入内容逐字节一致
        Assert.Equal(payload.Length, sizeBytes);        // sizeBytes == 实际文件长度
        Assert.Equal(new FileInfo(path).Length, sizeBytes);
    }

    [Fact]
    public void SavePng_propagates_write_failure()
    {
        var store = new HistoryStore(_dir);
        Assert.Throws<IOException>(() =>
        {
            store.SavePng(Guid.NewGuid(), _ => throw new IOException("模拟编码失败"), out _);
        });
    }

    [Fact]
    public void SavePng_propagates_directory_failure()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "cache"), "占位文件：让 cache 目录无法创建");
        var store = new HistoryStore(_dir);
        Assert.Throws<IOException>(() =>
        {
            store.SavePng(Guid.NewGuid(), s => s.WriteByte(0), out _);
        });
    }

    [Fact]
    public void Dib_streaming_pipeline_writes_file_and_hash_matches_content()
    {
        // 模拟 DIB 入库：全 0 alpha（GDI 典型形态）+ RGB 有内容 → ForceOpaque 修复 →
        // 流式 PNG 落盘（边写边算 SHA-256）→ 条目内容寻址键与「对落盘 PNG 字节算摘要」一致。
        var raw = Bitmap(320, 200, alpha: 0);
        var bmp = ImageCodec.ForceOpaqueIfAllAlphaZero(raw);
        Assert.NotSame(raw, bmp); // alpha 全 0 且 RGB 有内容：确实走了修复拷贝

        var store = new HistoryStore(_dir);
        var id = Guid.NewGuid();
        byte[]? digest = null;
        var file = store.SavePng(id, output =>
        {
            using var hashing = new HashingWriteStream(output);
            ImageCodec.EncodePngToStream(bmp, hashing);
            digest = hashing.Hash;
        }, out var sizeBytes);

        var bytes = File.ReadAllBytes(Path.Combine(_dir, "cache", file));
        Assert.Equal(sizeBytes, (long)bytes.Length);
        Assert.NotNull(digest);
        Assert.Equal(32, digest!.Length);
        Assert.Equal(ClipboardEntry.HashPngPrefix(bytes), ClipboardEntry.HashPngPrefix(digest.AsSpan()));

        var decoded = ImageCodec.FromPng(bytes);
        Assert.Equal((320, 200), (decoded.PixelWidth, decoded.PixelHeight));
        Assert.Equal(255, Rgba(decoded, 10, 10)[3]); // alpha 修复写入的 PNG 仍是不透明

        var entry = ClipboardEntry.FromImage(file, decoded.PixelWidth, decoded.PixelHeight, sizeBytes,
            null, null, ClipboardEntry.HashPngPrefix(digest.AsSpan()));
        Assert.Equal(ClipboardEntry.HashPngPrefix(bytes), entry.ContentHash);
        Assert.Equal($"img:{entry.ContentHash}", entry.ContentKey);
    }
}
