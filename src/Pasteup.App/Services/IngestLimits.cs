using System.Text;

namespace Pasteup.App.Services;

/// <summary>剪贴板内容入库限额（纯函数，便于单测）。剪贴板来自任意进程，超大负载直接拒收。</summary>
public static class IngestLimits
{
    /// <summary>单条文本上限（UTF-8 字节）：超限拒收——截断存储会破坏回贴保真，宁可不记。</summary>
    public const int MaxTextBytes = 10 * 1024 * 1024;

    /// <summary>文件列表条数上限：CF_HDROP 可被塞任意多条路径（一次「全选大目录复制」就够大），
    /// 且该通道此前零限额，10MB 文本上限可被「换格式重投」绕过。</summary>
    public const int MaxFileCount = 1000;

    /// <summary>单条路径字符数上限（32K 字符，正常路径哪怕极长 UNC 也用不到）。</summary>
    public const int MaxFilePathChars = 32 * 1024;

    public static bool TextTooLarge(string text)
        => Encoding.UTF8.GetByteCount(text) > MaxTextBytes;

    /// <summary>CF_HDROP 文件列表限额：条数 / 单路径长度 / 总字节（Σ UTF8，与文本同口径的上限），
    /// 超限整条拒收。空列表不在此拦截（大小为零不算超限）——由调用方另行拒绝。</summary>
    public static bool FileListTooLarge(IReadOnlyList<string> files)
        => files.Count > MaxFileCount
        || files.Any(f => f.Length > MaxFilePathChars)
        || files.Sum(f => (long)Encoding.UTF8.GetByteCount(f)) > MaxTextBytes;
}
