using System.IO;
using Pasteup.Core.History;

namespace Pasteup.App.Services;

/// <summary>--sample：空库时注入演示条目（截图/走查用），不覆盖真实数据。</summary>
public static class SampleSeeder
{
    public static void SeedIfEmpty(HistoryEngine engine, HistoryStore store)
    {
        if (engine.Count > 0) return;
        var now = DateTime.UtcNow;
        // 第 4 元 = exe 路径（Notepad 带 System32 路径：沙箱冒烟有确定性可断言的图标行）
        var texts = new (string text, int minutesAgo, string app, string? path)[]
        {
            ("Alt+V 唤起面板 · ↑↓ 选择 · Enter 粘贴 · Esc 关闭", 2, "Pasteup", null),
            ("https://learn.microsoft.com/dotnet/desktop/wpf/", 12, "Edge", null),
            ("var app = new Application();\napp.Run(new MainWindow());", 26, "Code", null),
            ("第三季度评审会议纪要：\n1. 贴图窗口内存基线通过\n2. 冷启动 178ms，达标\n3. 下一阶段做标注工具条", 47, "Word", null),
            ("订单号 PU-20261026-0142，金额 ¥1,280.00", 95, "Wexin", null),
            ("C:\\Users\\Asuka\\Pictures\\参考图-拼版台.png", 130, "Explorer", null),
            ("快捷键备忘：Win+Shift+S 系统截图 / Alt+V Pasteup", 260, "Notepad",
                Path.Combine(Environment.SystemDirectory, "notepad.exe")),
        };
        foreach (var (text, minutesAgo, app, path) in texts)
            engine.Add(new ClipboardEntry { Id = Guid.NewGuid(), Kind = Core.Text.TextUtil.IsSingleUrl(text) ? EntryKind.Link : EntryKind.Text, Preview = Core.Text.TextUtil.MakePreview(text), FullText = text, SourceApp = app, SourceAppPath = path, UtcTimestamp = now.AddMinutes(-minutesAgo), SizeBytes = System.Text.Encoding.UTF8.GetByteCount(text) });

        // 两张生成图像（渐变 + 网格），验证图像链路
        for (var k = 0; k < 2; k++)
        {
            var png = PaintSamplePng(360 + k * 40, 240);
            var id = Guid.NewGuid();
            var file = store.SaveImagePng(id, png);
            var e = ClipboardEntry.FromImage(file, 360 + k * 40, 240, png.Length, "Pasteup");
            e.Id = id;
            e.UtcTimestamp = now.AddMinutes(-18 - k * 6);
            engine.Add(e);
        }

        var files = new[] { @"C:\Syncthing\共享\设计令牌-v3.css", @"C:\Syncthing\共享\评审截图-深色.png", @"C:\Syncthing\共享\POC-数据.xlsx" };
        engine.Add(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.File,
            Preview = string.Join("  ", files.Select(Path.GetFileName)),
            FullText = string.Join("\r\n", files),
            Files = files.ToList(),
            SourceApp = "Explorer",
            UtcTimestamp = now.AddMinutes(-70),
            SizeBytes = 200,
        });
    }

    private static byte[] PaintSamplePng(int w, int h) => ImageCodec.RenderToPng(w, h, dc =>
    {
        dc.DrawRectangle(System.Windows.Media.Brushes.White, null, new System.Windows.Rect(0, 0, w, h));
        for (int x = 0; x <= w; x += 24)
            dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.LightGray, 1), new System.Windows.Point(x, 0), new System.Windows.Point(x, h));
        for (int y = 0; y <= h; y += 24)
            dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.LightGray, 1), new System.Windows.Point(0, y), new System.Windows.Point(w, y));
        var brush = new System.Windows.Media.LinearGradientBrush(
            System.Windows.Media.Color.FromRgb(27, 108, 128), System.Windows.Media.Color.FromRgb(74, 138, 214), 45);
        dc.DrawRectangle(brush, null, new System.Windows.Rect(24, 24, w - 48, h - 48));
    });
}
