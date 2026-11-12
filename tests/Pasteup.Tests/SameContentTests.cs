using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>SameContent（零构键去重判定）与 ContentKey 的语义一致性：
/// 引擎 Add 的去重比较已改走此路径，此处守卫两种判定永远给出相同结论。</summary>
public class SameContentTests
{
    [Fact]
    public void 同文本不同来源_等价且与构键判定一致()
    {
        var a = ClipboardEntry.FromText("同一段文本", "Notepad");
        var b = ClipboardEntry.FromText("同一段文本", "Word");
        Assert.True(ClipboardEntry.SameContent(a, b));
        Assert.Equal(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void 不同文本_不等价()
    {
        var a = ClipboardEntry.FromText("甲", null);
        var b = ClipboardEntry.FromText("乙", null);
        Assert.False(ClipboardEntry.SameContent(a, b));
        Assert.NotEqual(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void 同文不同类_不等价()
    {
        var link = ClipboardEntry.FromText("https://x.dev", null);   // 单 URL → Link
        var file = ClipboardEntry.FromFiles(["https://x.dev"], null); // File，FullText 同串
        Assert.NotEqual(link.Kind, file.Kind);
        Assert.False(ClipboardEntry.SameContent(link, file));
    }

    [Fact]
    public void 图像同尺寸同字节_等价_文件名不入键()
    {
        var a = ClipboardEntry.FromImage("a.png", 800, 600, 1000, null);
        var b = ClipboardEntry.FromImage("b.png", 800, 600, 1000, null);
        Assert.True(ClipboardEntry.SameContent(a, b));
        Assert.False(ClipboardEntry.SameContent(a, ClipboardEntry.FromImage("c.png", 800, 601, 1000, null)));
        Assert.False(ClipboardEntry.SameContent(a, ClipboardEntry.FromImage("d.png", 800, 600, 1001, null)));
    }

    [Fact]
    public void 文件同路径集合_等价()
    {
        var a = ClipboardEntry.FromFiles([@"C:\a.txt", @"C:\b\c.png"], null);
        var b = ClipboardEntry.FromFiles([@"C:\a.txt", @"C:\b\c.png"], "Explorer");
        Assert.True(ClipboardEntry.SameContent(a, b));
        Assert.False(ClipboardEntry.SameContent(a, ClipboardEntry.FromFiles([@"C:\a.txt"], null)));
    }

    // ---- 图像内容摘要（ContentHash）：弱键「宽×高×字节」碰撞会让不同图互顶丢内容 ----

    [Fact]
    public void 图像有摘要_同尺寸同字节不同摘要_不等价()
    {
        var a = ClipboardEntry.FromImage("a.png", 800, 600, 1000, null, null, "aaaaaaaaaaaaaaaa");
        var b = ClipboardEntry.FromImage("b.png", 800, 600, 1000, null, null, "bbbbbbbbbbbbbbbb");
        Assert.False(ClipboardEntry.SameContent(a, b));
        Assert.NotEqual(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void 图像同摘要_等价()
    {
        var a = ClipboardEntry.FromImage("a.png", 800, 600, 1000, null, null, "aaaaaaaaaaaaaaaa");
        var b = ClipboardEntry.FromImage("b.png", 800, 600, 1000, null, null, "aaaaaaaaaaaaaaaa");
        Assert.True(ClipboardEntry.SameContent(a, b));
        Assert.Equal(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void 图像旧条目无摘要_弱键回退仍与同内容新条目等价()
    {
        var legacy = ClipboardEntry.FromImage("old.png", 800, 600, 1000, null); // 磁盘读回：无摘要
        var fresh = ClipboardEntry.FromImage("new.png", 800, 600, 1000, null, null, "cccccccccccccccc");
        Assert.True(ClipboardEntry.SameContent(legacy, fresh)); // 重复制即升级为带摘要条目
    }

    [Fact]
    public void 摘要前缀_稳定且十六位()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        Assert.Equal(16, ClipboardEntry.HashPngPrefix(png).Length);
        Assert.Equal(ClipboardEntry.HashPngPrefix(png), ClipboardEntry.HashPngPrefix(png));
    }
}
