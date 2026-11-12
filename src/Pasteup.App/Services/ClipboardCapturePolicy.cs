using System.IO;

namespace Pasteup.App.Services;

/// <summary>
/// 剪贴板入库退订策略（2026-11-10 C1-A，安全复审问题 6 / C1）：尊重操作系统与密码管理器写入的
/// 「勿记录 / 勿上云」剪贴板格式。
///
/// 来源（Windows 剪贴板历史 / 云剪贴板约定，Windows 10/11 自带 Win+V 与云剪贴板都遵守）：
/// · <see cref="CanIncludeInClipboardHistory"/>：DWORD，0 = 不要把内容放进剪贴板历史；
/// · <see cref="CanUploadToCloudClipboard"/>：DWORD，0 = 不要把内容上传到云剪贴板；
/// · <see cref="ExcludeClipboardContentFromMonitorProcessing"/>：存在即要求剪贴板监控进程忽略，
///   值被忽略（presence-only）；
/// · <see cref="ClipboardViewerIgnore"/>：旧版剪贴板查看器忽略标记（presence-only）。
/// 密码管理器（KeePassXC / 1Password / Bitwarden 等）复制密码时会同时放 <c>CF_UNICODETEXT</c> 与这些
/// 标记，表示「别记历史/别上云」——Pasteup 此前只按格式取内容，会把密码明文入库（默认保留 90 天）。
///
/// 取舍（隐私优先）：DWORD 格式存在但**读不到值**（getData 返回 null / 空流 / 短流 / 类型不可识别）
/// 时按 0 处理即跳过——宁可少记一轮正常内容，也不冒把密码记下来的风险；但检查本身抛异常
///（剪贴板被占用等）时该格式按「不跳过」处理：**可用性优先**，绝不因退订检查失败而丢用户的正常
/// 复制内容。命中任意一个标记 → 本轮整条载荷视为无内容（文本/PNG/DIB/文件列表全部不入库）。
/// </summary>
public static class ClipboardCapturePolicy
{
    /// <summary>是否允许内容进入剪贴板历史（DWORD：0=不纳入；缺失或非 0=允许）。</summary>
    public const string CanIncludeInClipboardHistory = "CanIncludeInClipboardHistory";

    /// <summary>是否允许内容上传到云剪贴板（DWORD：0=不上传；缺失或非 0=允许）。</summary>
    public const string CanUploadToCloudClipboard = "CanUploadToCloudClipboard";

    /// <summary>监控进程排除标记（存在即要求忽略，值被忽略）。</summary>
    public const string ExcludeClipboardContentFromMonitorProcessing =
        "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>旧版剪贴板查看器忽略标记（存在即忽略，值被忽略）。</summary>
    public const string ClipboardViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>判断当前剪贴板是否带「勿记录/勿上云」标记（命中即本轮不入库）。
    /// 参数做成委托以便纯函数单测（生产调用方传 <c>Clipboard.ContainsData</c> / <c>Clipboard.GetData</c>）。
    /// <paramref name="contains"/> 或 <paramref name="getData"/> 抛异常（COMException 等）时，
    /// 该格式按「不跳过」处理且异常绝不出方法（保持可用性）。</summary>
    public static bool ShouldSkip(Func<string, bool> contains, Func<string, object?> getData)
    {
        // 历史/云：存在则看值——为 0 或读不到值 → 跳过；非 0 → 不因该格式跳过
        if (ShouldSkipDwordFormat(contains, getData, CanIncludeInClipboardHistory)) return true;
        if (ShouldSkipDwordFormat(contains, getData, CanUploadToCloudClipboard)) return true;
        // 监控排除 / 旧查看器忽略：presence-only，存在即跳过（不看值，也没有可靠的值语义）
        if (ShouldSkipPresenceFormat(contains, ExcludeClipboardContentFromMonitorProcessing)) return true;
        if (ShouldSkipPresenceFormat(contains, ClipboardViewerIgnore)) return true;
        return false;
    }

    /// <summary>presence-only 格式：存在即跳过。contains 抛异常 → 视为无标记（可用性优先）。</summary>
    private static bool ShouldSkipPresenceFormat(Func<string, bool> contains, string format)
    {
        try
        {
            return contains(format);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>DWORD 格式：存在且（读不到值 或 值==0）→ 跳过；非 0 → 不跳过。
    /// contains/getData 抛异常 → 该格式不跳过（可用性优先，异常不外抛）。</summary>
    private static bool ShouldSkipDwordFormat(Func<string, bool> contains, Func<string, object?> getData,
        string format)
    {
        bool present;
        try
        {
            present = contains(format);
        }
        catch (Exception)
        {
            return false;
        }
        if (!present) return false;

        object? data;
        try
        {
            data = getData(format);
        }
        catch (Exception)
        {
            return false; // 读值失败按「该格式不跳过」：不因退订检查失败丢正常内容
        }
        // 读不到值 / 类型不可识别（TryGetDword=null）→ 保守按 0 处理：跳过（隐私优先）
        var value = TryGetDword(data);
        return value is null or 0;
    }

    /// <summary>把剪贴板自定义格式读到的值归一化成 DWORD 数值。WPF 从**跨进程 OLE 剪贴板**读自定义
    /// 格式时经常返回 <see cref="MemoryStream"/> 而不是标量（与 <c>ImageCodec.PngBytesFromClipboardData</c>
    /// 同时兼容 MemoryStream/byte[] 同因），故兼容：
    /// · int / uint / long / short（同进程 DataObject 或 WPF 归一化后的标量）；
    /// · byte[]：长度 ≥ 4 时取前 4 字节小端（长度不足视为读不到值）；
    /// · MemoryStream：从头（Position=0）读 4 字节小端，读完复位原位置（不干扰其它潜在读取方）；
    ///   空流/短流（&lt;4 字节）/ 不可读 → 视为读不到值。
    /// 其余类型（含 null、其他 Stream 子类）返回 null，由调用方按「读不到值」保守处理。</summary>
    private static long? TryGetDword(object? data) => data switch
    {
        int i => i,
        uint u => u,
        long l => l,
        short s => s,
        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
        MemoryStream stream => TryReadDwordFromStream(stream),
        _ => null,
    };

    /// <summary>从 OLE 自定义格式的 MemoryStream 读 4 字节小端 DWORD；空/短流/读失败返回 null。
    /// 按「从头读」语义先 Seek 0，读完复位原位置（MemoryStream 是 OLE 包装的视图，复位更礼貌）。
    /// 已释放/不可读的流所有访问都在 try 内，异常一律降级为「读不到值」。</summary>
    private static long? TryReadDwordFromStream(MemoryStream stream)
    {
        long position = 0; // 仅用于 finally 复位；Position 读取本身可能抛（已释放），故也放在 try 内
        try
        {
            position = stream.Position;
            if (stream.Length < 4) return null; // 空流/短流：读不到值（调用方按 0 处理 → 跳过）
            stream.Position = 0;
            Span<byte> buffer = stackalloc byte[4];
            if (stream.Read(buffer) != buffer.Length) return null;
            return BitConverter.ToUInt32(buffer);
        }
        catch (Exception)
        {
            return null; // 流已释放/不可读：视为读不到值
        }
        finally
        {
            try { stream.Position = position; }
            catch (Exception)
            {
                // 流已不可 Seek / 已释放：忽略（本次读取结果不受影响）
            }
        }
    }
}
