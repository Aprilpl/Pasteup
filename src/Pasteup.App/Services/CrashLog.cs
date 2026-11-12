using System.IO;

namespace Pasteup.App.Services;

/// <summary>
/// 崩溃/异常落盘：dataDir/logs/ 下按时间命名，一次异常一个文件，写失败静默（日志不能反噬宿主）。
/// 用户报「点几下就崩」但事件日志查无记录时，这里是唯一的现场证据来源。
/// </summary>
public static class CrashLog
{
    /// <summary>保留的最新日志份数：文件名以时间戳开头，字典序即时间序，超出裁掉最旧（无界增长会慢慢吃盘）。</summary>
    private const int MaxFiles = 20;

    private static string? _dir;

    /// <summary>初始化日志目录。未初始化时按 PASTEUP_DATA / %LocalAppData%\Pasteup 兜底。</summary>
    public static void Init(string dataDirectory)
    {
        try
        {
            _dir = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(_dir);
            Prune(_dir);
        }
        catch
        {
            _dir = null; // 数据目录不可写：放弃落盘，不阻塞主流程
        }
    }

    private static string FallbackDir()
    {
        // 空串/空白视同未设置（与 App.OnStartup 同口径）：避免 logs 落到相对 CWD。
        // 默认与 App 一致用 Local 而不是 Roaming（2026-11-10 复审 C1 落地·B）：
        // 明文日志同样不应随域漫游/OneDrive 云同步离开本机
        var raw = Environment.GetEnvironmentVariable("PASTEUP_DATA");
        var data = string.IsNullOrWhiteSpace(raw) ? DataDirectoryResolver.LocalPasteupDir : raw!;
        return Path.Combine(data, "logs");
    }

    public static void Write(string kind, Exception ex, string? context = null)
    {
        // 内容只拼一次：主路径与 exe 目录兜底写的是同一份现场
        var content =
            $"""
            time:    {DateTime.Now:O}
            kind:    {kind}
            context: {context ?? "-"}
            app:     {Environment.ProcessPath}
            os:      {Environment.OSVersion}

            {ex}
            """;
        // 文件名带毫秒：同 kind 同秒的第二条异常曾互相覆盖
        var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{kind}.log";
        if (TryWriteLog(Path.Combine(_dir ?? FallbackDir(), name), content, prune: true)) return;
        // exe 目录兜底（2026-11-12 事故复盘）：数据目录整体不可写（权限/低完整性标签/沙箱令牌）时
        // _dir 与 PASTEUP_DATA 兜底会全部失败，「崩了没证据」持续整个会话。exe 所在目录是低完整性
        // 环境下少数仍可写的位置（与 placement.log 的兜底同款）。兜底目录不做修剪——修剪规则
        // （按文件名裁最旧）只应作用于数据目录内的日志。
        _ = TryWriteLog(Path.Combine(AppContext.BaseDirectory, name), content, prune: false);
    }

    /// <summary>写入单个日志文件（含建目录）；失败返回 false 交由调用方走下一级兜底，绝不抛。</summary>
    private static bool TryWriteLog(string file, string content, bool prune)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, content);
                if (prune) Prune(dir);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按文件名降序保留最新 MaxFiles 份；每份删除各自兜底（修剪失败不能反噬写日志主流程）。</summary>
    private static void Prune(string dir)
    {
        try
        {
            var stale = Directory.GetFiles(dir, "*.log")
                .OrderDescending(StringComparer.Ordinal)
                .Skip(MaxFiles);
            foreach (var file in stale)
            {
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch
        {
            // 目录枚举失败：本轮放弃修剪
        }
    }
}
