using System.IO;

namespace Pasteup.App.Services;

/// <summary>
/// 数据目录解析与一次性迁移（2026-11-10 复审 C1 落地·B）：默认目录从 Roaming（%APPDATA%）迁到
/// Local（%LOCALAPPDATA%）。原因：Roaming 会被域漫游/OneDrive 等同步到其他设备与云端，而这里落的是
/// **明文剪贴板内容**（history.json/history.journal、cache PNG、logs）——迁到 Local 才保证不随漫游
/// 离开本机；DPAPI 加密维持暂缓（同为 C1 的另一半，未做前仍只假设「本机账户边界」）。
/// PASTEUP_DATA 非空时（沙箱/自定义数据目录）语义完全不变，绝不迁移。
/// </summary>
public static class DataDirectoryResolver
{
    private const string HistoryFile = "history.json";
    private const string SettingsFile = "settings.json";

    /// <summary>旧默认目录：Roaming %APPDATA%\Pasteup（仅用于一次性迁移读取，不再作为默认写入位置）。</summary>
    public static string RoamingPasteupDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pasteup");

    /// <summary>新默认目录：Local %LOCALAPPDATA%\Pasteup（不随漫游/云同步）。</summary>
    public static string LocalPasteupDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasteup");

    /// <summary>本次进程已解析/迁移后的实际数据目录（启动路径设置一次）。用于 <see cref="Resolve"/>
    /// 让诊断日志（placement.log/CrashLog 兜底）跟随真实目录——迁移失败本次沿用旧 Roaming 时
    /// 日志也写旧目录，不出现「数据在旧、日志在新」的割裂。</summary>
    private static string? _activeDir;

    /// <summary>解析当前数据目录：PASTEUP_DATA 非空白优先（沙箱/自定义路径原样返回）；否则 Local 默认目录。
    /// 不触发迁移（迁移只在启动路径经 <see cref="ResolveWithMigration"/> 做一次）。
    /// 启动路径已解析过（含迁移失败沿用旧目录）则返回那个实际目录。</summary>
    public static string Resolve()
    {
        if (_activeDir is not null) return _activeDir;
        var raw = Environment.GetEnvironmentVariable("PASTEUP_DATA");
        // 空串/空白视同未设置：?? 不滤空串，Path.Combine("",…) 会把数据落成相对 CWD 的路径
        return string.IsNullOrWhiteSpace(raw) ? LocalPasteupDir : raw!;
    }

    /// <summary>
    /// 启动路径用：解析数据目录并做一次性 Roaming→Local 迁移。
    /// MigrationNote 语义：null = 无需迁移（PASTEUP_DATA / 新目录已有数据 / 旧目录无内容）；
    /// "moved"/"copied"/"copied-old-kept" = 迁移成功（本次及以后切到 Local，copied-old-kept 表示旧目录
    /// 因占用暂留、可重启后清理）；"failed: X" = 迁移失败——**本次沿用旧 Roaming 目录**（用户不会突然
    /// 变成空历史；旧实例运行中时单实例守卫还能据旧目录键识别它），半成品已清理，下次启动自动重试。
    /// 任何情况下都不抛、不阻塞启动；PASTEUP_DATA 场景语义完全不变。
    /// </summary>
    public static (string DataDir, string? MigrationNote) ResolveWithMigration()
    {
        var result = ResolveWithMigrationCore(Environment.GetEnvironmentVariable("PASTEUP_DATA"),
            RoamingPasteupDir, LocalPasteupDir);
        _activeDir = result.DataDir; // 诊断日志跟随实际目录（含迁移失败沿用旧 Roaming）
        return result;
    }

    /// <summary>测试/诊断注入版：显式给出 PASTEUP_DATA 语义（envOverride=null/空白视为未设置）与新旧目录，
    /// 让「成功切 Local / 失败沿用旧目录」的完整分支可在临时目录上单测（生产启动用无参重载）。</summary>
    public static (string DataDir, string? MigrationNote) ResolveWithMigration(
        string? envOverride, string roamingDir, string localDir) =>
        ResolveWithMigrationCore(envOverride, roamingDir, localDir);

    private static (string DataDir, string? MigrationNote) ResolveWithMigrationCore(
        string? envOverride, string roamingDir, string localDir)
    {
        if (!string.IsNullOrWhiteSpace(envOverride)) return (envOverride!, null); // 沙箱/自定义路径：语义完全不变
        if (HasExistingData(localDir)) return (localDir, null);  // 已在新位置：不重复迁移
        if (!HasAnyContent(roamingDir)) return (localDir, null); // 无旧数据：首次安装/已清理

        var migrated = TryMigrate(roamingDir, localDir, out var note);
        if (migrated) return (localDir, note);
        // 迁移失败：本次**沿用旧目录**（否则 App 会在空 Local 上跑并写出新 settings/history，
        // 下次启动 HasExistingData(Local)=true 就再也不迁移——旧历史被永久搁置）；半成品已清理，
        // 下次启动自动重试。两个例外仍用 Local：①目标在检查后出现真实数据（并发实例抢先迁移/
        // 写入）→ 以目标为准，避免两个实例各写一处；②旧目录在检查后消失/被清空 → 没东西可沿用。
        return HasExistingData(localDir) || !HasAnyContent(roamingDir)
            ? (localDir, note)
            : (roamingDir, note);
    }

    /// <summary>
    /// 把 oldDir 迁移到 newDir（public 供测试注入临时目录）。返回是否已把数据放到新目录；
    /// note 失败/跳过原因（诊断用）。策略：
    /// ①旧目录不存在/为空 → false（没东西可迁）；②新目录已有 history.json/settings.json → false（不覆盖）；
    /// ③优先 Directory.Move（同卷改名，快且原子）；Move 抛 IOException/UnauthorizedAccess（旧实例运行中
    /// 占用文件、权限、跨卷/重定向）→ 降级递归复制到新目录，复制成功后尝试删旧目录；
    /// 删不掉则保留旧目录（note=copied-old-kept，数据已在新目录、旧目录待重启后再清）；
    /// 复制失败 → note=failed: X、false、不删旧，并清理本次写出的新目录半成品（保证下次启动能重试）。
    /// 任何异常都不向外抛（启动路径不能崩）。
    /// </summary>
    public static bool TryMigrate(string oldDir, string newDir, out string? note)
    {
        note = null;
        var destExistedBefore = Directory.Exists(newDir); // 失败清理判断用（不接受抛异常的怪路径）
        try
        {
            if (!Directory.Exists(oldDir)) return false;      // 旧目录不存在：无需迁移
            if (!HasAnyContent(oldDir)) return false;         // 空目录：不白迁
            if (HasExistingData(newDir))                      // 新目录已有数据：绝不覆盖
            {
                note = "skipped-dest-exists";
                return false;
            }

            // Move 要求目标不存在、父目录存在：先建父级（Local 目录本身不预建——各调用方自建）
            var parent = Path.GetDirectoryName(newDir);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            try
            {
                Directory.Move(oldDir, newDir); // 同卷原子改名：最快路径
                note = "moved";
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Move 失败（旧实例仍运行占用文件/权限/跨卷重定向）：降级复制，不删旧（下面视删除结果定 note）
            }

            try
            {
                CopyTree(oldDir, newDir); // 新目录已部分存在时按同名覆盖合并（历史半份复制可重试）
            }
            catch (Exception copyEx)
            {
                note = $"failed: {copyEx.GetType().Name}";
                // 清掉本次写出的半成品：否则一旦 history.json 已复制一半，下次启动会误判「已有数据」
                // 而跳过迁移，旧历史被永久搁置。旧目录原样保留不动。
                CleanupPartialDestination(newDir, destExistedBefore);
                return false;
            }

            // 复制成功：旧目录能删就删；删不掉（旧实例仍占用）保留并提示重启
            note = TryDeleteTree(oldDir) ? "copied" : "copied-old-kept";
            return true;
        }
        catch (Exception ex)
        {
            // 兜底：路径非法/重定向异常等非 IO 类意外也不得让启动崩——按失败返回，旧目录保留
            note = $"failed: {ex.GetType().Name}";
            CleanupPartialDestination(newDir, destExistedBefore);
            return false;
        }
    }

    /// <summary>新目录是否已有「真实数据」标记（history.json/settings.json）——有则不迁移，防覆盖新数据。
    /// 只看两个入口文件：Partial 复制（未写到标记文件）不算已有数据，下次启动会重试迁移。</summary>
    private static bool HasExistingData(string dir) =>
        File.Exists(Path.Combine(dir, HistoryFile)) || File.Exists(Path.Combine(dir, SettingsFile));

    /// <summary>旧目录是否存在且可枚举到任意文件/子目录（枚举失败视为无内容：宁可少迁也不阻塞启动）。</summary>
    private static bool HasAnyContent(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 递归复制目录树（保留结构、同名文件覆盖）。history.json/settings.json **最后**复制：
    /// 中途失败时新目录还不算「已有数据」，下次启动会重试迁移而不是留下缺图/缺设置的半份数据。
    /// depth 上限只是防御异常目录结构（junction 环）导致无限递归。
    /// </summary>
    private static void CopyTree(string source, string dest, int depth = 0)
    {
        Directory.CreateDirectory(dest);
        if (depth > 32) return;

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsDataMarker(name)) continue; // 标记文件最后写
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)), depth + 1);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            if (!IsDataMarker(name)) continue;
            File.Copy(file, Path.Combine(dest, name), overwrite: true);
        }
    }

    private static bool IsDataMarker(string fileName) =>
        string.Equals(fileName, HistoryFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, SettingsFile, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 迁移失败时清掉本次写出的新目录半成品（best-effort、绝不抛），保证下次启动能重试迁移。
    /// 判据与上层一致：本次之前不存在的新目录 → 整删（半份 history/settings 一并清掉，避免下次
    /// HasExistingData 误判而跳过迁移）；本次之前就已存在且现在已有真实数据的目录 → 绝不删。
    /// 「之前已存在但没有数据」的目录也整删：里面只可能是我们写出的半成品或本应用的诊断日志。
    /// 清理本身失败（占用/权限）不影响启动——history/settings 是最后写的标记，只要它们没写进去，
    /// 下次启动 HasExistingData 仍为 false 会继续重试。
    /// </summary>
    private static void CleanupPartialDestination(string newDir, bool destExistedBefore)
    {
        try
        {
            if (!Directory.Exists(newDir)) return;
            if (destExistedBefore && HasExistingData(newDir)) return; // 原有真实数据：不动
            Directory.Delete(newDir, recursive: true);
        }
        catch
        {
            // 清理失败只影响下次启动的重试效率，不影响数据安全（旧目录始终保留）
        }
    }

    /// <summary>
    /// 探测数据目录是否可写：建 logs 子目录 + 探针文件写入即删（与运行期真实写入路径一致）。
    /// 供启动路径「数据目录不可写可见化」使用——任何异常都判不可写并交还原因，绝不抛
    ///（历史上数据目录整体不可写时应用以「空历史+零日志」形态静默降级，排障成本极高，
    /// 见 2026-11-12 低完整性标签事故）。
    /// </summary>
    public static bool IsWritable(string dataDirectory, out Exception? failure)
    {
        try
        {
            var logs = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(logs);
            var probe = Path.Combine(logs, ".write-probe");
            File.WriteAllText(probe, "p");
            File.Delete(probe);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            return false;
        }
    }

    /// <summary>尝试删旧目录；失败（文件被旧实例占用/权限）返回 false 且保留——删除失败只影响清理。</summary>
    private static bool TryDeleteTree(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
