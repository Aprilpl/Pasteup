using System.IO;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>app.ico 资产约束：七档尺寸齐全、容器结构合法（小尺寸 32bpp BMP 帧 / 大尺寸 PNG 帧、
/// 帧数据不越界）、FromIcoStream 产出 32px 自持句柄且透明角完整的图标（托盘交付路径）。</summary>
public class AppIconTests
{
    private static string LocateIco()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Pasteup.App", "Assets", "app.ico")))
            dir = dir.Parent;
        Assert.True(dir is not null, "未找到仓库根（app.ico 缺失？先跑 scripts/make_icon.py）");
        return Path.Combine(dir.FullName, "src", "Pasteup.App", "Assets", "app.ico");
    }

    [Fact]
    public void Ico_contains_all_required_sizes_with_valid_frames()
    {
        var bytes = File.ReadAllBytes(LocateIco());
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 0)); // reserved
        Assert.Equal(1, BitConverter.ToUInt16(bytes, 2)); // type = icon
        var count = BitConverter.ToUInt16(bytes, 4);
        Assert.Equal(7, count);

        var sizes = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var dim = bytes[entry] == 0 ? 256 : bytes[entry];
            Assert.Equal(dim, bytes[entry + 1] == 0 ? 256 : bytes[entry + 1]);
            sizes.Add(dim);

            var dataLen = BitConverter.ToUInt32(bytes, entry + 8);
            var offset = BitConverter.ToUInt32(bytes, entry + 12);
            Assert.InRange(offset + dataLen, 0u, (uint)bytes.Length);

            if (dim <= 48)
            {
                // BMP 帧：BITMAPINFOHEADER(40) / biWidth=dim / biHeight=2*dim / 32bpp
                Assert.Equal(40u, BitConverter.ToUInt32(bytes, (int)offset));
                Assert.Equal(dim, BitConverter.ToInt32(bytes, (int)offset + 4));
                Assert.Equal(dim * 2, BitConverter.ToInt32(bytes, (int)offset + 8));
                Assert.Equal(32, BitConverter.ToUInt16(bytes, (int)offset + 14));
            }
            else
            {
                Assert.Equal(0x89, bytes[(int)offset]);
                Assert.Equal((byte)'P', bytes[(int)offset + 1]);
                Assert.Equal((byte)'N', bytes[(int)offset + 2]);
                Assert.Equal((byte)'G', bytes[(int)offset + 3]);
            }
        }
        Assert.Equal(new HashSet<int> { 16, 24, 32, 48, 64, 128, 256 }, sizes);
    }

    [Fact]
    public void FromIcoStream_returns_owned_32px_icon_with_transparent_corners()
    {
        using var fs = File.OpenRead(LocateIco());
        using var icon = AppIcon.FromIcoStream(fs);
        Assert.Equal(32, icon.Size.Width);
        Assert.Equal(32, icon.Size.Height);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
        using var bmp = icon.ToBitmap();
        Assert.Equal(0, bmp.GetPixel(0, 0).A);
        Assert.Equal(0, bmp.GetPixel(31, 31).A);
    }
}
