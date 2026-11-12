using System.IO;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>崩溃日志仪表自身必须可靠：全局钩子兜底的最后一环，写不出日志就再次「崩了没证据」。</summary>
public class CrashLogTests
{
    [Fact]
    public void Write_落盘完整异常与上下文()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-crashlog-test-" + Guid.NewGuid().ToString("N")[..8]);
        CrashLog.Init(dir);
        CrashLog.Write("test-kind", new InvalidOperationException("boom-message"), "测试上下文");

        var logsDir = Path.Combine(dir, "logs");
        var file = Assert.Single(Directory.GetFiles(logsDir, "*-test-kind.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("boom-message", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("测试上下文", text);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Init_目录不可写也不抛()
    {
        // 非法路径（盘符不存在）：初始化放弃落盘；Write 走 PASTEUP_DATA 兜底也必须不抛、不越界写别处
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-crashlog-fallback-" + Guid.NewGuid().ToString("N")[..8]);
        var old = Environment.GetEnvironmentVariable("PASTEUP_DATA");
        Environment.SetEnvironmentVariable("PASTEUP_DATA", dir);
        try
        {
            CrashLog.Init("Q:\\definitely-not-exists\\pasteup");
            CrashLog.Write("test-kind", new Exception("x"), null);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PASTEUP_DATA", old);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Write_数据目录与PASTEUP_DATA都不可写时落exe目录兜底()
    {
        // 2026-11-12 事故形态：数据目录与 PASTEUP_DATA 兜底全部不可写（低完整性进程写不了
        // AppData）——此前直接静默丢现场。现在应落到 exe 目录（与 placement.log 兜底同款）。
        var old = Environment.GetEnvironmentVariable("PASTEUP_DATA");
        Environment.SetEnvironmentVariable("PASTEUP_DATA", "Q:\\definitely-not-exists\\pasteup");
        try
        {
            CrashLog.Init("Q:\\definitely-not-exists2\\pasteup");
            CrashLog.Write("fallback-kind", new Exception("fallback-boom"), "exe 兜底");

            var file = Assert.Single(Directory.GetFiles(AppContext.BaseDirectory, "*-fallback-kind.log"));
            Assert.Contains("fallback-boom", File.ReadAllText(file));
            File.Delete(file); // 兜底产物不留在测试 bin 目录
        }
        finally
        {
            Environment.SetEnvironmentVariable("PASTEUP_DATA", old);
        }
    }

    [Fact]
    public void Init_裁掉最旧日志只留上限份数()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-crashlog-rotate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        // 手工造 25 份不同名日志（同秒写入会因时间戳同名互相覆盖——那是既有行为，与轮转无关）
        for (var i = 0; i < 25; i++)
            File.WriteAllText(Path.Combine(dir, "logs", $"20260101-0000{i + 1:D2}-kind.log"), "x");

        CrashLog.Init(dir); // Init 即修剪

        var remaining = Directory.GetFiles(Path.Combine(dir, "logs"), "*.log")
            .Select(Path.GetFileName).Order().ToList();
        Assert.Equal(20, remaining.Count);
        Assert.Equal("20260101-000025-kind.log", remaining[^1]); // 最新的留下
        Assert.DoesNotContain("20260101-000001-kind.log", remaining); // 最旧的裁掉
        Directory.Delete(dir, true);
    }
}
