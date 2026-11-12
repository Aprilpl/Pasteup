using System.IO;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 数据目录解析与一次性 Roaming→Local 迁移（2026-11-10 复审 C1 落地·B）：
/// TryMigrate 全部用临时目录注入，不碰真实 %APPDATA%/%LOCALAPPDATA%；覆盖 Move 优先路径、
/// 目标已有数据不覆盖、旧目录缺失/为空、目标目录已存在（Move 失败）时的复制降级、
/// 复制中途失败（FileShare.None 占住源文件，稳定复现）时清理新目录半成品保证下次重试。
/// ResolveWithMigration 用注入重载（env/新旧目录）测完整分支：成功切 Local、失败沿用旧目录、
/// 已有 Local 数据不迁移、PASTEUP_DATA 覆盖不迁移、旧目录为空不迁移；生产无参重载只读进程级
/// 环境变量与真实特殊文件夹（同进程 CrashLogTests 也会临时改写 PASTEUP_DATA），不做断言。
/// </summary>
public class DataDirectoryResolverTests
{
    private static string NewTempBase() =>
        Path.Combine(Path.GetTempPath(), "pasteup-datadir-" + Guid.NewGuid().ToString("N")[..8]);

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void IsWritable_可写目录建logs子目录且探针即写即删()
    {
        var root = NewTempBase();
        try
        {
            Assert.True(DataDirectoryResolver.IsWritable(root, out var failure));
            Assert.Null(failure); // 成功路径不交还原因
            var logs = Path.Combine(root, "logs");
            Assert.True(Directory.Exists(logs));
            Assert.Empty(Directory.GetFiles(logs)); // 探针文件即写即删，不留痕
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IsWritable_路径被同名文件占据时判不可写且不抛()
    {
        var root = NewTempBase();
        try
        {
            Directory.CreateDirectory(root);
            var blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x"); // 目录路径被同名文件占据：任何建目录/写文件都会失败

            Assert.False(DataDirectoryResolver.IsWritable(blocker, out var failure));
            Assert.NotNull(failure); // 原因交还调用方（启动弹窗/CrashLog 展示用）
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryMigrate_moves_whole_tree_when_dest_absent()
    {
        var root = NewTempBase();
        var oldDir = Path.Combine(root, "roaming");
        var newDir = Path.Combine(root, "nested", "local"); // 父目录不存在：迁移应先建父级再 Move
        try
        {
            WriteFile(Path.Combine(oldDir, "history.json"), "H");
            WriteFile(Path.Combine(oldDir, "settings.json"), "S");
            WriteFile(Path.Combine(oldDir, "cache", "img.png"), "PNG");

            Assert.True(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));

            Assert.Equal("moved", note); // Move 优先路径（同卷改名）
            Assert.False(Directory.Exists(oldDir)); // 旧目录消失
            Assert.Equal("H", File.ReadAllText(Path.Combine(newDir, "history.json")));
            Assert.Equal("S", File.ReadAllText(Path.Combine(newDir, "settings.json")));
            Assert.Equal("PNG", File.ReadAllText(Path.Combine(newDir, "cache", "img.png")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryMigrate_never_overwrites_dest_that_already_has_data()
    {
        var root = NewTempBase();
        var oldDir = Path.Combine(root, "roaming");
        var newDir = Path.Combine(root, "local");
        try
        {
            WriteFile(Path.Combine(oldDir, "history.json"), "OLD");
            WriteFile(Path.Combine(newDir, "history.json"), "NEW");

            Assert.False(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));

            Assert.NotNull(note); // 跳过原因（skipped-dest-exists）供诊断/启动日志
            Assert.Equal("OLD", File.ReadAllText(Path.Combine(oldDir, "history.json"))); // 旧目录原样
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(newDir, "history.json"))); // 新数据不被覆盖
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryMigrate_returns_false_when_old_dir_missing()
    {
        var root = NewTempBase();
        try
        {
            var oldDir = Path.Combine(root, "nope");
            var newDir = Path.Combine(root, "local");

            Assert.False(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));
            Assert.Null(note); // 无迁移动作：不打迁移日志
            Assert.False(Directory.Exists(newDir)); // 也不顺手创建目标目录（调用方自建）
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryMigrate_returns_false_for_empty_old_dir()
    {
        var root = NewTempBase();
        var oldDir = Path.Combine(root, "roaming");
        var newDir = Path.Combine(root, "local");
        Directory.CreateDirectory(oldDir);
        try
        {
            Assert.False(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));
            Assert.Null(note);
            Assert.True(Directory.Exists(oldDir)); // 空旧目录不白迁/不删除
            Assert.False(Directory.Exists(newDir));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryMigrate_falls_back_to_copy_when_move_blocked_by_existing_dest_dir()
    {
        // 目标目录已存在（但无 history/settings）：Directory.Move 必抛 IOException → 复制降级路径。
        // 这是「旧实例运行中目录被占」的可稳定复现近似（真实锁文件场景另按代码审查）。
        var root = NewTempBase();
        var oldDir = Path.Combine(root, "roaming");
        var newDir = Path.Combine(root, "local");
        try
        {
            WriteFile(Path.Combine(oldDir, "history.json"), "H");
            WriteFile(Path.Combine(oldDir, "cloud", "deep", "note.txt"), "DEEP");
            Directory.CreateDirectory(newDir);

            Assert.True(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));

            Assert.Equal("copied", note); // 复制成功且旧目录可删
            Assert.False(Directory.Exists(oldDir));
            Assert.Equal("H", File.ReadAllText(Path.Combine(newDir, "history.json")));
            Assert.Equal("DEEP", File.ReadAllText(Path.Combine(newDir, "cloud", "deep", "note.txt"))); // 保结构
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Default_dirs_are_rooted_and_named_pasteup()
    {
        // 不依赖真实目录内容，只锁「两个默认目录 = 各自特殊文件夹 + Pasteup」的基本形状
        Assert.True(Path.IsPathRooted(DataDirectoryResolver.RoamingPasteupDir));
        Assert.True(Path.IsPathRooted(DataDirectoryResolver.LocalPasteupDir));
        Assert.Equal("Pasteup", Path.GetFileName(DataDirectoryResolver.RoamingPasteupDir));
        Assert.Equal("Pasteup", Path.GetFileName(DataDirectoryResolver.LocalPasteupDir));
    }

    [Fact]
    public void TryMigrate_cleans_partial_dest_when_copy_fails()
    {
        // 目标目录已存在 → Move 必失败 → 走复制；源文件被 FileShare.None 占住 → File.Copy 必失败。
        // 失败语义：旧目录原样保留 + 本次写出的新目录半成品被清理（否则半份 history 会让下次启动
        // HasExistingData 误判而跳过迁移，旧历史被永久搁置）。
        var root = NewTempBase();
        var oldDir = Path.Combine(root, "roaming");
        var newDir = Path.Combine(root, "local");
        var locked = Path.Combine(oldDir, "cache", "locked.bin");
        try
        {
            WriteFile(Path.Combine(oldDir, "history.json"), "H");
            WriteFile(locked, "LOCKED");
            Directory.CreateDirectory(newDir);

            using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(DataDirectoryResolver.TryMigrate(oldDir, newDir, out var note));

                Assert.NotNull(note);
                Assert.StartsWith("failed:", note);
            }

            Assert.False(Directory.Exists(newDir)); // 半成品已清理 → 下次启动可重试迁移
            Assert.True(File.Exists(Path.Combine(oldDir, "history.json"))); // 旧目录原样
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ---- ResolveWithMigration 分支（注入临时的 roaming/local，不碰真实特殊文件夹） ----

    [Fact]
    public void ResolveWithMigration_switches_to_local_after_successful_move()
    {
        var root = NewTempBase();
        var roaming = Path.Combine(root, "roaming");
        var local = Path.Combine(root, "local");
        try
        {
            WriteFile(Path.Combine(roaming, "history.json"), "H");

            var (dataDir, note) = DataDirectoryResolver.ResolveWithMigration(null, roaming, local);

            Assert.Equal(local, dataDir); // 成功：本次起切到 Local
            Assert.Equal("moved", note);
            Assert.False(Directory.Exists(roaming));
            Assert.Equal("H", File.ReadAllText(Path.Combine(local, "history.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveWithMigration_falls_back_to_roaming_when_migration_fails()
    {
        // 造一个必然失败的 local：父路径是文件 → Directory.CreateDirectory/Move 抛异常（非锁文件，
        // 可稳定复现）。失败语义：本次沿用旧 Roaming 目录（不空历史），半成品清理，下次启动重试。
        var root = NewTempBase();
        var roaming = Path.Combine(root, "roaming");
        WriteFile(Path.Combine(root, "blocked-parent"), "i-am-a-file"); // 父路径是文件
        var local = Path.Combine(root, "blocked-parent", "local");
        try
        {
            WriteFile(Path.Combine(roaming, "history.json"), "OLD-HISTORY");

            var (dataDir, note) = DataDirectoryResolver.ResolveWithMigration(null, roaming, local);

            Assert.Equal(roaming, dataDir); // 失败：本次仍用旧目录，历史不丢
            Assert.NotNull(note);
            Assert.StartsWith("failed:", note);
            Assert.True(File.Exists(Path.Combine(roaming, "history.json"))); // 旧数据原样
            Assert.False(Directory.Exists(local)); // 半成品已清理（保证下次启动重试）
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveWithMigration_keeps_local_when_it_already_has_data()
    {
        var root = NewTempBase();
        var roaming = Path.Combine(root, "roaming");
        var local = Path.Combine(root, "local");
        try
        {
            WriteFile(Path.Combine(roaming, "history.json"), "ROAMING");
            WriteFile(Path.Combine(local, "history.json"), "LOCAL");

            var (dataDir, note) = DataDirectoryResolver.ResolveWithMigration(null, roaming, local);

            Assert.Equal(local, dataDir); // 已在新位置：不重复迁移
            Assert.Null(note);
            Assert.Equal("ROAMING", File.ReadAllText(Path.Combine(roaming, "history.json"))); // 旧目录不动
            Assert.Equal("LOCAL", File.ReadAllText(Path.Combine(local, "history.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveWithMigration_env_override_skips_migration()
    {
        var root = NewTempBase();
        var roaming = Path.Combine(root, "roaming");
        var sandbox = Path.Combine(root, "sandbox-data");
        try
        {
            WriteFile(Path.Combine(roaming, "history.json"), "ROAMING");

            var (dataDir, note) = DataDirectoryResolver.ResolveWithMigration(sandbox, roaming,
                Path.Combine(root, "local"));

            Assert.Equal(sandbox, dataDir); // PASTEUP_DATA 语义完全不变（沙箱依赖）
            Assert.Null(note);
            Assert.True(File.Exists(Path.Combine(roaming, "history.json"))); // 有旧目录也不迁移
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResolveWithMigration_uses_local_without_migration_when_old_dir_empty()
    {
        var root = NewTempBase();
        var roaming = Path.Combine(root, "roaming");
        var local = Path.Combine(root, "local");
        try
        {
            Directory.CreateDirectory(roaming); // 空旧目录：没有内容可迁

            var (dataDir, note) = DataDirectoryResolver.ResolveWithMigration(null, roaming, local);

            Assert.Equal(local, dataDir);
            Assert.Null(note);
            Assert.False(Directory.Exists(local)); // 目标目录仍由各调用方自建，不在这里创建
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
