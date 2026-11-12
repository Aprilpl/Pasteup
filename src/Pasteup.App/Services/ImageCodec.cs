using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pasteup.App.Services;

/// <summary>PNG 字节与冻结的 BitmapSource 互转（冻结以便跨线程使用与降低渲染开销）。</summary>
public static class ImageCodec
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>入库像素上限（50 MP ≈ 8K 全屏 33 MP 留余量）：剪贴板内容来自任意进程，
    /// 超大声明尺寸的「解码炸弹」会在解码/像素拷贝时撑爆内存，须在解码前拒绝。</summary>
    public const long MaxImagePixels = 50_000_000;

    /// <summary>"PNG" 格式字节上限：截图场景远用不到 64 MB，超限视为异常负载不吞入。</summary>
    public const int MaxPngBytes = 64 * 1024 * 1024;

    public static BitmapSource FromPng(byte[] png)
    {
        // 解码前守卫：读 IHDR 的声明尺寸，超限直接抛（调用方已有非 COMException catch，
        // 表现为坏图/跳过，而不是多 GB 分配后的 OOM/挂起）
        if (TryReadPngDimensions(png) is { } dims && (long)dims.Width * dims.Height > MaxImagePixels)
            throw new InvalidOperationException($"图像尺寸 {dims.Width}×{dims.Height} 超出上限 {MaxImagePixels} 像素");
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// 只按目标宽度缩放解码 PNG（WIC 原生缩放解码），入库缩略图专用：
    /// 原入库路径 FromPng 全量解码只为拿宽高/生成缩略图——4K PNG 首解 25–100ms、峰值保留
    /// 33MB 全尺寸像素；DecodePixelWidth 让解码器只产出缩略尺寸像素（640 宽约 1.6MB 峰值）。
    /// 目标宽 ≥ 原宽时不放大（显式回退 FromPng 原尺寸解码，仍带解码炸弹守卫）。
    /// 坏 PNG/IHDR 与实际像素不一致的极端伪造图由解码异常向上抛，调用方按坏图处理（回退 DIB）。
    /// </summary>
    public static BitmapSource DecodePngThumbnail(byte[] png, int targetWidth)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetWidth);
        if (TryReadPngDimensions(png) is { } dims)
        {
            // 解码炸弹守卫：先保证宽高能放进 int 再乘（uint×uint 直接相乘会溢出 long 绕过上限）
            if (dims.Width == 0 || dims.Height == 0 || dims.Width > int.MaxValue || dims.Height > int.MaxValue
                || (long)dims.Width * dims.Height > MaxImagePixels)
                throw new InvalidOperationException(
                    $"图像尺寸 {dims.Width}×{dims.Height} 超出上限 {MaxImagePixels} 像素");
            if (dims.Width <= (uint)targetWidth) return FromPng(png); // 小图不放大：放大只增体积不增信息
        }
        var img = new BitmapImage();
        using var ms = new MemoryStream(png);
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = targetWidth;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>
    /// GDI+ Bitmap → 冻结 BitmapSource：LockBits 单次像素拷贝，无 PNG 编码/解码往返
    /// （截图链路原在 UI 线程整屏编码再解码，0.3-1s）。GDI+ 32bppArgb 字节序即 Bgra32；
    /// 输入是自家 BitBlt 产物（非任意进程内容），不走 FromPng 的解码炸弹守卫。
    /// LockBits 请求 32bppArgb 时 GDI+ 自行做格式转换，源位图任何深度都安全。
    /// </summary>
    public static BitmapSource FromGdiBitmap(System.Drawing.Bitmap bmp)
    {
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            // Create 构造期即拷贝缓冲，Unlock 后源位图可自由处置（含 Dispose）
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96,
                PixelFormats.Bgra32, null, data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// 读 PNG 头 IHDR 块的宽高（偏移 16/20，大端 uint）。字节太短/魔数不符返回 null
    /// （解码交给 BitmapImage 按原路径报错）。
    /// </summary>
    public static (uint Width, uint Height)? TryReadPngDimensions(byte[] png)
    {
        if (png is not { Length: >= 24 }) return null;
        if (!png.AsSpan(0, 8).SequenceEqual(PngSignature)) return null;
        return (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
    }

    public static byte[] ToPng(BitmapSource source)
    {
        using var ms = new MemoryStream();
        EncodePngToStream(source, ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 位图 → PNG 直写目标流（不建中间 MemoryStream、不 ToArray）：DIB 入库峰值原为
    /// 「ToPng 的 MemoryStream + ToArray 双份 PNG 字节」，流式编码只留编码器内部缓冲。
    /// 输出流不归本方法所有（不关闭/不 Flush 到磁盘；调用方负责 fsync）。
    /// </summary>
    public static void EncodePngToStream(BitmapSource source, Stream output)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(output);
    }

    /// <summary>
    /// 生成入库预缩略图（PNG）：目标宽度小于原宽时按比例缩到目标宽，否则原样编码（不放大）。
    /// 质量/成本权衡：缩放走 <see cref="TransformedBitmap"/> + <see cref="ScaleTransform"/> 的
    /// WPF 原生像素缩放（非整数比例时双线性采样，肉眼在 132/640 像素的解码显示上不可辨），
    /// 不做「先缩小再放大」的多重往返；成本 ≈ 源图像素缩放 + 目标宽 PNG 压缩，
    /// 4K 图实测 10–30ms，故调用方（入库后台段/截图后台任务）放在后台线程；
    /// 收益是单张缩略图一般几十~几百 KB，ThumbConverter 此后解码一份小 PNG（&lt;1ms）而非原图。
    /// 返回的字节不可解码等异常由调用方兜底（缩略图只是优化，失败就回退原图）。
    /// </summary>
    public static byte[] RenderThumbnailPng(BitmapSource source, int targetPixelWidth)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetPixelWidth);
        if (source.PixelWidth <= targetPixelWidth) return ToPng(source); // 小图不放大：放大只增体积不增信息
        var scale = (double)targetPixelWidth / source.PixelWidth;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze(); // 冻结后才可安全跨线程交给编码器（入库后台段 / 截图 Task.Run）
        return ToPng(scaled);
    }

    /// <summary>把绘制内容渲染为 PNG 字节（96 DPI、Pbgra32）——托盘回退图标 / POC / 演示图共用。</summary>
    public static byte[] RenderToPng(int width, int height, Action<System.Windows.Media.DrawingContext> draw)
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        return ToPng(rtb);
    }

    /// <summary>
    /// 剪贴板 "PNG" 注册格式的数据（MemoryStream / byte[]）→ PNG 字节。
    /// 内容不带 PNG 魔数（个别程序往该格式塞非 PNG 数据）或类型不认识时返回 null，
    /// 调用方落回 DIB 路径。
    /// </summary>
    public static byte[]? PngBytesFromClipboardData(object? data) => data switch
    {
        MemoryStream ms => ReadIfPng(ms.ToArray()),
        byte[] bytes => ReadIfPng(bytes),
        _ => null,
    };

    private static byte[]? ReadIfPng(byte[] bytes)
        => bytes.Length is >= 8 and <= MaxPngBytes
           && bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
            ? bytes
            : null;

    /// <summary>
    /// 修复 GDI 程序的 32bpp DIB：BitBlt/CopyImage 产出的位图 alpha 字节是未初始化的 0，
    /// WPF Clipboard.GetImage 会把它当有效透明度保留 → 整图全透明（白底上看似「没加载」）。
    /// 判据：alpha 全 0 且 RGB 有内容 → 强制置不透明（置 255 后预乘/非预乘数值等价，两种格式都安全）；
    /// RGB 也全 0（真空白图）或存在任何非 0 alpha（真半透明）时原样返回。
    /// </summary>
    public static BitmapSource ForceOpaqueIfAllAlphaZero(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32) return source;
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        if (w == 0 || h == 0) return source;

        var stride = w * 4;
        var pixels = new byte[stride * h];
        source.CopyPixels(pixels, stride, 0);

        var allAlphaZero = true;
        var anyRgb = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] != 0)
            {
                allAlphaZero = false;
                break;
            }
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) anyRgb = true;
        }
        if (!allAlphaZero || !anyRgb) return source;

        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var fixedSource = BitmapSource.Create(w, h, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        fixedSource.Freeze();
        return fixedSource;
    }
}

/// <summary>
/// 只写装饰流：字节原样转发给 inner，同时增量计算 SHA-256——DIB 入库「边写盘边算内容摘要」，
/// 不再先 ToPng 出整份 PNG byte[] 再算哈希/再写第二次（那会同时持有 2 份 PNG 字节）。
/// 不支持读；Seek/Position/Length 透传给 inner（WPF PNG 编码器实测只查一次 Length、不 Seek），
/// 摘要按写入顺序累计（顺序写时等于最终文件摘要；PNG 编码器是顺序写）。
/// **不拥有 inner**：Dispose 只释放哈希器，不关闭目标流。
/// Hash 在编码完成后访问：首次访问即定稿并缓存（之后再写入不再计入摘要）。
/// </summary>
public sealed class HashingWriteStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private byte[]? _digest;

    /// <summary>已写入全部字节的 SHA-256（32 字节）。首次访问时定稿；重复访问返回同一数组。</summary>
    public byte[] Hash => _digest ??= _hash.GetHashAndReset();

    public override bool CanRead => false;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => true;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        _hash.AppendData(buffer, offset, count);
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _hash.AppendData(buffer);
        inner.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Span<byte> one = stackalloc byte[1];
        one[0] = value;
        _hash.AppendData(one);
        inner.WriteByte(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _digest ??= _hash.GetHashAndReset(); // Dispose 前没取过也定稿，之后仍可安全读 Hash
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }
}
