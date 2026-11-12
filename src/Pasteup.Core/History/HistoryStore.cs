using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pasteup.Core.History;

/// <summary>历史持久化：history.json + cache/{id}.png（图像本体）+ cache/thumbs/{id}.png（入库预缩略图）。原子写，损坏文件自动备份重建。</summary>
public sealed class HistoryStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 不转义非 ASCII：默认 JavaScriptEncoder 把每个汉字写成 \uXXXX 六字符，中文为主的
        // 剪贴板历史实测 21.5→78MB（3.6×），单次序列化瞬时分配 +156MB（2026-11-02 压测）。
        // 文件只自读自写（STJ 读回两种形式等价），不进 HTML 嵌入场景，relaxed 安全。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>
    /// 预缩略图规格：入库时后台生成 640 像素宽的 PNG 落 cache/thumbs/{id}.png。
    /// 依据：面板图像瓦片可见宽约 500–640 DIP（175% DPI 物理约 900px），640 宽在瓦片上有
    /// 可接受清晰度；行图解码宽仅 132，绰绰有余。文件一般几十~几百 KB，
    /// 解码成本远低于原图（4K 照片型 PNG 首解实测 25–100ms）。
    /// 只做缩不放：小图缩略图即原尺寸（见 ImageCodec.RenderThumbnailPng）。
    /// </summary>
    public const int ThumbnailWidth = 640;

    public string DataDirectory { get; } = dataDirectory;
    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    /// <summary>预缩略图目录：cache/thumbs，与主图 cache/{id}.png 同名（同一信任边界）。</summary>
    public string ThumbnailDirectory => Path.Combine(CacheDirectory, "thumbs");

    private string HistoryPath => Path.Combine(DataDirectory, "history.json");

    /// <summary>增量日志：JSONL，一行一条记录（有序 id 列表 + 变更条目），由 SaveIncremental 追加、
    /// Load 按行重放。删除/清空/首次保存/超阈值时走全量 Save 并清空它。</summary>
    private string JournalPath => Path.Combine(DataDirectory, "history.journal");

    /// <summary>journal 字节阈值（默认 8MB）：达到后下一次 SaveIncremental 自动全量 compaction。
    /// 测试可调小以触发阈值分支。</summary>
    public long JournalMaxBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>journal 记录数阈值（默认 256 条）：达到后下一次 SaveIncremental 自动全量 compaction。
    /// 测试可调小以触发阈值分支。</summary>
    public int JournalMaxRecords { get; set; } = 256;

    // ---- Load 限额（2026-11-10 复审第4批 P2-3）：history.json / history.journal 是信任边界，
    // 磁盘档可被任意进程篡改或半写；启动期 Load 必须在反序列化前给出文件大小上限，并对
    // 字段长度/条目数封顶——否则超大档直接打爆启动内存（OOM 会连累整个应用，而单档损坏只丢历史）。----

    /// <summary>history.json 文件大小上限（默认 256MB）：反序列化前检查，超限按现有损坏语义
    /// 迁 .corrupt（覆盖旧备份）并返回空。测试可调小。</summary>
    public long MaxHistoryFileBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>history.journal 文件大小上限（默认 64MB）：重放前检查，超限整份不重放（保留基档结果、
    /// 不删除超大 journal），并标记下次保存必须全量 compaction。测试可调小。
    /// 正常 journal 由 <see cref="JournalMaxBytes"/>（默认 8MB）/<see cref="JournalMaxRecords"/>
    /// 触发 compaction，远达不到此上限。</summary>
    public long MaxJournalFileBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Load 条目总数上限（默认 10 万条）：反序列化后超限按损坏语义迁 .corrupt 并返回空
    /// （「极小条目 × 百万条」同样能撑爆启动内存）。测试可调小。</summary>
    public int MaxLoadedEntries { get; set; } = 100_000;

    // ---- Load 字段上限（信任边界）：与 App 层入库限额 IngestLimits 同口径。Core 不引用 App，
    // 故按同值集中定义在这里；入库侧正常数据不会触发，Load 侧只拦篡改/损坏档。----

    /// <summary>Preview 字符上限（与 TextUtil.MakePreview 的 max 一致）：超限截断。</summary>
    private const int MaxPreviewChars = 200;

    /// <summary>单条 FullText 上限（UTF-8 字节，= App IngestLimits.MaxTextBytes 10MB）：超限整条丢弃
    ///（截断存储会破坏回贴保真，与入库侧「超限拒收」口径一致）。</summary>
    private const int MaxFullTextBytes = 10 * 1024 * 1024;

    /// <summary>文件列表条数上限（= App IngestLimits.MaxFileCount 1000）：超限整条丢弃。</summary>
    private const int MaxFileCount = 1000;

    /// <summary>单条文件路径字符上限（Windows 扩展长度路径上限 32767；App 入库限额为 32K）：
    /// 超出即非法路径，整条丢弃。</summary>
    private const int MaxFilePathChars = 32_767;

    /// <summary>SourceApp 字符上限：超限截断（只作展示，截断不破坏回贴内容）。</summary>
    private const int MaxSourceAppChars = 1024;

    /// <summary>SourceAppPath 字符上限：超限截断（只作图标来源路径，截断后回退首字母/默认图标）。</summary>
    private const int MaxSourceAppPathChars = 4096;

    /// <summary>图像宽高上限（像素；入库限额 50MP 远小于此）：负值或超限整条丢弃。</summary>
    private const int MaxImageDimension = 100_000;

    // ---- 增量保存状态（只在 _saveGate 内读写；调用约定：Save/SaveIncremental 由 App 的 _saveGate
    // 串行化，这里再加锁兜底直接调用方的并发）----
    private readonly object _saveGate = new();

    // 最近一次成功落盘状态（全量基档，或基档 + journal 重放）的条目「拷贝」与顺序。
    // 必须存拷贝：引擎会原地修改 Pinned/Favorite/UtcTimestamp，若存引用会被跟着改而漏判变更。
    private Dictionary<Guid, ClipboardEntry>? _lastSaved;
    private List<Guid>? _lastSavedOrder;
    // 基档代号：写进 history.json 的 JournalBase；journal 每行携带同一代号，只有匹配才重放。
    // 防止「全量 compaction 写了新基档但清 journal 前崩溃」留下的旧 journal 被误重放（删条目复活/顺序回退）。
    private string? _journalGeneration;
    private int _journalRecords;        // 当前 journal 记录数（Load 重放 / append 维护，全量保存归零）
    private bool _journalNeedsCompaction; // journal 尾部损坏/清不掉等：下次保存必须全量重写

    private sealed class Dto
    {
        public int Version { get; set; } = 1;

        /// <summary>基档代号（Guid "N"）。旧版本文件无此字段（null），journal 不参与重放。</summary>
        public string? JournalBase { get; set; }

        public List<ClipboardEntry> Entries { get; set; } = [];
    }

    /// <summary>journal 一行：Base=基档代号；Ids=该记录生效后的完整有序 id 列表（决定顺序与成员）；
    /// Set=相对上一条记录新增/变更的条目（只增改、不删）；Removed=诊断字段——删除/成员减少一律
    /// 走全量 Save 并清空 journal，所以正常恒为 null。</summary>
    private sealed class JournalRecord
    {
        public string? Base { get; set; }
        public List<string>? Ids { get; set; }
        public List<ClipboardEntry>? Set { get; set; }
        public List<string>? Removed { get; set; }
    }

    /// <summary>一次增量保存的差量：完整有序 id 列表 + 需要写入的变更/新增条目。</summary>
    private sealed class JournalDelta
    {
        public required List<Guid> Ids { get; init; }
        public required List<ClipboardEntry> Changed { get; init; }
    }

    public string SaveImagePng(Guid id, byte[] png)
    {
        Directory.CreateDirectory(CacheDirectory);
        var name = $"{id:N}.png";
        File.WriteAllBytes(Path.Combine(CacheDirectory, name), png);
        return name;
    }

    /// <summary>
    /// 流式保存主图 PNG（DIB 入库路径）：调用方把编码器直接写到本方法打开的 FileStream——
    /// 避免 ToPng 先产整份 byte[]（MemoryStream + ToArray 双份）再 WriteAllBytes。fsync 与
    /// <see cref="SaveImagePng"/> 对齐（Flush(true) 后再返回）；IO/编码异常向上抛，由调用方兜底。
    /// 返回文件名（cache/{id:N}.png）；sizeBytes = 实际落盘字节数。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1021:Avoid out parameters",
        Justification = "接口约定按「返回文件名 + out 字节数」供入库路径一次取全（调用方只有 AppServices 一处）；改成元组会让「失败抛异常」与「成功返回」两条路径都多一次包装")]
    public string SavePng(Guid id, Action<Stream> writePng, out long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(writePng);
        Directory.CreateDirectory(CacheDirectory);
        var name = $"{id:N}.png";
        var path = Path.Combine(CacheDirectory, name);
        try
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            writePng(fs);
            fs.Flush(true); // 与 SaveImagePng 的「落盘即耐用」语义对齐
            sizeBytes = fs.Length;
            return name;
        }
        catch
        {
            // 编码/写盘中途失败会留下半截 PNG（引用它的条目还没入库）：当场删掉；
            // 删除也失败（被占用）留给启动期孤儿清理兜底。异常照常向上抛给调用方兜底。
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// 保存入库预缩略图（cache/thumbs/{id:N}.png，与主图同 Id）。仅写盘、无缓存态，
    /// 可后台线程并发调用（不同 id 不同文件；同 id 覆盖写为幂等）。
    /// 失败一律返回 null 吞掉：缩略图只是解码优化，ThumbConverter 缺它自动回退原图。
    /// </summary>
    public string? SaveThumbnailPng(Guid id, byte[] png)
    {
        try
        {
            Directory.CreateDirectory(ThumbnailDirectory);
            var name = $"{id:N}.png";
            File.WriteAllBytes(Path.Combine(ThumbnailDirectory, name), png);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 主图文件名 → 缩略图绝对路径。复用 <see cref="IsValidImageFileName"/> 白名单
    /// （history.json 是信任边界，文件名可被篡改；缩略图路径同样不得越出 cache/thumbs），
    /// 非法名返回 null（调用方回退原图路径）。不校验文件是否存在。
    /// </summary>
    public string? ThumbnailPathFor(string? imageFile)
    {
        if (!IsValidImageFileName(imageFile)) return null;
        return Path.Combine(ThumbnailDirectory, imageFile!);
    }

    /// <summary>
    /// 图像缓存文件名的信任边界：history.json 在磁盘上可被篡改，文件名只认
    /// 「32 位小写十六进制 + .png」（SaveImagePng 恒用 Guid "N" 生成），
    /// 其余（相对穿越 ../、绝对路径、乱名）一律拒绝，杜绝读出缓存目录之外。
    /// </summary>
    public static bool IsValidImageFileName(string? name)
    {
        if (name is not { Length: 36 } || !name.EndsWith(".png", StringComparison.Ordinal)) return false;
        foreach (var c in name.AsSpan(0, 32))
            if (c is (< '0' or > '9') and (< 'a' or > 'f')) return false;
        return true;
    }

    public byte[]? LoadImagePng(string? imageFile)
    {
        if (!IsValidImageFileName(imageFile)) return null;
        var path = Path.Combine(CacheDirectory, imageFile!);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>删除一个缓存图像文件：「删除即删盘」路径（条目已从引擎移除且 history.json
    /// 已成功落盘后调用）。文件名失范直接忽略；被占用等删除失败留待启动期孤儿清理兜底。
    /// 同名缩略图一并 best-effort 删除（主图删除失败也删缩略图：原图还在，回退显示不受影响）。</summary>
    public void DeleteImageFile(string? imageFile)
    {
        if (!IsValidImageFileName(imageFile)) return;
        try { File.Delete(Path.Combine(CacheDirectory, imageFile!)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        DeleteThumbnailFile(imageFile);
    }

    /// <summary>best-effort 删除一个缩略图文件：名字失范/不存在/被占用一律静默
    ///（孤儿清理在下次启动兜底）。</summary>
    public void DeleteThumbnailFile(string? imageFile)
    {
        if (!IsValidImageFileName(imageFile)) return;
        try { File.Delete(Path.Combine(ThumbnailDirectory, imageFile!)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 全量原子保存：写 tmp → fsync → rename 覆盖基档，并清空 journal（compaction）。
    /// 语义与旧实现一致（history.json 是唯一权威快照）；首次保存、删除/成员减少、journal 超阈值、
    /// 退出/清空路径都落到这里。调用约定：由 App 的 _saveGate 串行化（此处另有 _saveGate 兜底）。
    /// </summary>
    public void Save(HistoryEngine engine)
    {
        lock (_saveGate) SaveLocked(SnapshotEntries(engine.Snapshot()));
    }

    /// <summary>
    /// 增量保存（每次复制后的防抖自动保存走这里）：相对最近一次成功落盘状态做差量。
    /// 纯新增/更新/重排（Add/Touch/TogglePin/ToggleFavorite）→ 向 history.journal 追加一行
    /// （append + Flush(true) 落定后才更新内存快照）；首次保存/无基档/journal 超阈值/
    /// journal 曾损坏 → 自动退回全量 Save。
    /// 出现任何删除/成员减少（Remove/Clear/容量、保留期、预算淘汰/去重顶掉）→ 必须全量 Save：
    /// journal 只表达「当前完整成员 + 顺序」，不能替代「history.json 不再引用被删条目」的
    /// 删除即删盘/隐私语义。
    /// 返回 false 表示本轮未能落盘（调用方保留待删队列，等下一次自动保存重试）。
    /// </summary>
    public bool SaveIncremental(HistoryEngine engine)
    {
        lock (_saveGate)
        {
            var frozen = SnapshotEntries(engine.Snapshot());
            var savedOrder = _lastSavedOrder;
            if (_lastSaved is null || savedOrder is null || _journalGeneration is null
                || !File.Exists(HistoryPath) || _journalNeedsCompaction || JournalTooLarge()
                // 已重放过的 journal 被外部删除/清空：基档不含那部分状态，必须全量重写后再追加
                || (_journalRecords > 0 && !File.Exists(JournalPath)))
            {
                SaveLocked(frozen);
                return true;
            }

            var delta = ComputeDelta(frozen);
            if (delta is null) // 删除/成员减少：必须全量，让基档不再引用被删条目
            {
                SaveLocked(frozen);
                return true;
            }
            // 无任何变化（例如仅设置变更触发的保存）：不追加空记录，避免 journal 白涨记录数/字节数
            if (delta.Changed.Count == 0 && delta.Ids.Count == savedOrder.Count && delta.Ids.SequenceEqual(savedOrder))
                return true;

            try
            {
                AppendJournalLine(delta);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 可能留下半行：标记下次必须全量重写，否则新追加记录会被坏行截断而无法重放
                _journalNeedsCompaction = true;
                return false;
            }
            // fsync 成功之后才更新内存快照：崩溃时以 journal 为准，不出现「内存领先于盘」
            AdoptSnapshot(frozen);
            _journalRecords++;
            return true;
        }
    }

    /// <summary>全量写基档（tmp+fsync+Move）+ 清 journal + 采纳内存快照。调用方须持有 _saveGate。</summary>
    private void SaveLocked(List<ClipboardEntry> frozen)
    {
        Directory.CreateDirectory(DataDirectory);
        var generation = $"{Guid.NewGuid():N}"; // 与 SaveImagePng 的插值格式化同风格（不做 culture 相关转换）
        var tmp = HistoryPath + ".tmp";
        // 直写文件流：先 Serialize 成整串 string 再 WriteAllText 会双份瞬时分配
        //（千条中文历史实测 +156MB/次，每回复制/删除/置顶都触发）。
        // 注意转义器必须挂在 JsonWriterOptions 上：外部传入的 writer 用自己的 encoder
        //（默认全转义），JsonOpts.Encoder 在这条路径不生效（2026-11-03 实测文件仍 81.6MB 踩坑）
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
                   {
                       Encoder = JsonOpts.Encoder!,
                       Indented = JsonOpts.WriteIndented,
                   }))
                JsonSerializer.Serialize(writer, new Dto { JournalBase = generation, Entries = frozen }, JsonOpts);
            // fsync 后再 rename：断电时 MFT 已改名而数据块未落盘会把 history.json 变半文件，
            // Load 只能整档迁 .corrupt（全量历史静默丢失）；Flush(true) 把数据块压进磁盘
            fs.Flush(true);
        }
        File.Move(tmp, HistoryPath, overwrite: true);
        // 顺序承重：先写新基档（带新代号）、后清 journal。若清 journal 前崩溃，留下的旧 journal
        // 代号与基档不匹配，Load 会忽略并清掉它——不会把已删除条目「重放复活」或退回旧顺序。
        if (!DeleteJournalBestEffort()) _journalNeedsCompaction = true;
        else _journalNeedsCompaction = false;
        _journalGeneration = generation;
        _journalRecords = 0;
        AdoptSnapshot(frozen);
    }

    /// <summary>追加一行 journal 并 fsync；IO 失败向上抛（调用方决定标记全量重写与返回 false）。</summary>
    private void AppendJournalLine(JournalDelta delta)
    {
        Directory.CreateDirectory(DataDirectory);
        var record = new JournalRecord
        {
            Base = _journalGeneration,
            Ids = [.. delta.Ids.Select(id => id.ToString())], // Guid.ToString() 默认即 36 位带连字符格式
            Set = delta.Changed,
        };
        using var fs = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.None, 1 << 14);
        using (var writer = new Utf8JsonWriter(fs, new JsonWriterOptions
               {
                   Encoder = JsonOpts.Encoder!,
                   Indented = JsonOpts.WriteIndented,
               }))
            JsonSerializer.Serialize(writer, record, JsonOpts);
        fs.WriteByte((byte)'\n');
        fs.Flush(true); // 追加记录 fsync 落定后才允许更新内存快照
    }

    /// <summary>计算相对 _lastSaved 的差量；删除/成员减少（或引擎出现重复 Id）返回 null 表示必须全量。</summary>
    private JournalDelta? ComputeDelta(List<ClipboardEntry> frozen)
    {
        var previous = _lastSaved!;
        var previousOrder = _lastSavedOrder!;
        var ids = new List<Guid>(frozen.Count);
        var changed = new List<ClipboardEntry>();
        var seen = new HashSet<Guid>(frozen.Count);
        foreach (var entry in frozen)
        {
            if (!seen.Add(entry.Id)) return null; // 重复 Id 不该出现：保守全量
            ids.Add(entry.Id);
            if (!previous.TryGetValue(entry.Id, out var saved) || !SamePersistedState(saved, entry))
                changed.Add(entry);
        }
        // 成员减少 = 删除/清空/淘汰/去重顶掉：journal 不表达删除，且必须让基档不再引用被删条目。
        // 注意不能反用「数量变化」判断：纯新增（数量变多）必须走增量，只有旧快照里的 id 在当前
        // 快照缺席才是删除；数量变少但旧 id 全在是不可能的防御分支，交给下面的覆盖检查。
        foreach (var id in previousOrder)
            if (!seen.Contains(id)) return null;
        return new JournalDelta { Ids = ids, Changed = changed };
    }

    /// <summary>条目「已持久化状态」全字段比较。快照拷贝与引擎条目共享字符串/列表引用，
    /// 未变条目基本全是引用相等短路——整表判等是 O(N) 引用比较，不含任何序列化。</summary>
    private static bool SamePersistedState(ClipboardEntry a, ClipboardEntry b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Id != b.Id || a.Kind != b.Kind || a.Pinned != b.Pinned || a.Favorite != b.Favorite
            || a.UtcTimestamp != b.UtcTimestamp || a.ImageWidth != b.ImageWidth || a.ImageHeight != b.ImageHeight
            || a.SizeBytes != b.SizeBytes)
            return false;
        return StrEq(a.Preview, b.Preview) && StrEq(a.FullText, b.FullText) && StrEq(a.ImageFile, b.ImageFile)
            && StrEq(a.SourceApp, b.SourceApp) && StrEq(a.SourceAppPath, b.SourceAppPath)
            && StrEq(a.ContentHash, b.ContentHash) && FilesEqual(a.Files, b.Files);
    }

    private static bool StrEq(string? a, string? b)
        => ReferenceEquals(a, b) || string.Equals(a, b, StringComparison.Ordinal);

    private static bool FilesEqual(List<string> a, List<string> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>条目拷贝（只拷字段；字符串/文件列表共享引用）：引擎原地修改 Pinned/Favorite/
    /// UtcTimestamp 不会污染快照，未变条目靠引用相等快速判等。</summary>
    private static ClipboardEntry CopyEntry(ClipboardEntry e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Preview = e.Preview,
        FullText = e.FullText,
        ImageFile = e.ImageFile,
        ImageWidth = e.ImageWidth,
        ImageHeight = e.ImageHeight,
        Files = e.Files,
        SourceApp = e.SourceApp,
        SourceAppPath = e.SourceAppPath,
        Pinned = e.Pinned,
        Favorite = e.Favorite,
        UtcTimestamp = e.UtcTimestamp,
        SizeBytes = e.SizeBytes,
        ContentHash = e.ContentHash,
    };

    /// <summary>拷贝一批条目（SaveSnapshot 的输入：保持与引擎对象解耦，防原地修改漏判变更）。</summary>
    private static List<ClipboardEntry> SnapshotEntries(IReadOnlyList<ClipboardEntry> entries)
    {
        var frozen = new List<ClipboardEntry>(entries.Count);
        foreach (var entry in entries) frozen.Add(CopyEntry(entry));
        return frozen;
    }

    /// <summary>把一批拷贝清单再拷贝一份（Load 返回的条目与引擎共享引用，不能直接进快照）。</summary>
    private static List<ClipboardEntry> CopyEntries(List<ClipboardEntry> entries)
    {
        var copies = new List<ClipboardEntry>(entries.Count);
        foreach (var entry in entries) copies.Add(CopyEntry(entry));
        return copies;
    }

    /// <summary>采纳一批「已拷贝」条目为「最近一次成功落盘状态」（调用方保证 entries 不是引擎活对象，
    /// 否则引擎原地修改 Pinned/Favorite/UtcTimestamp 会让快照跟着变而漏判变更）。</summary>
    private void AdoptSnapshot(List<ClipboardEntry> copies)
    {
        var map = new Dictionary<Guid, ClipboardEntry>(copies.Count);
        var order = new List<Guid>(copies.Count);
        foreach (var entry in copies)
        {
            map[entry.Id] = entry;
            order.Add(entry.Id);
        }
        _lastSaved = map;
        _lastSavedOrder = order;
    }

    /// <summary>journal 是否已达阈值（记录数或字节数）：达到则下一次保存改走全量 compaction。</summary>
    private bool JournalTooLarge()
    {
        if (_journalRecords >= Math.Max(0, JournalMaxRecords)) return true;
        var limit = Math.Max(0, JournalMaxBytes);
        try
        {
            var info = new FileInfo(JournalPath);
            return info.Exists && info.Length >= limit;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true; // 读不到大小：保守全量，避免 journal 无界增长
        }
    }

    /// <summary>best-effort 删除 journal；返回是否已不存在（删除成功或本来就没有）。</summary>
    private bool DeleteJournalBestEffort()
    {
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>清掉残留的 history.json.tmp（写一半被杀的明文全量历史副本；单实例保证无并发写者）。</summary>
    private void DeleteStaleTmp()
    {
        try { if (File.Exists(HistoryPath + ".tmp")) File.Delete(HistoryPath + ".tmp"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 读取历史：先读基档（现有损坏→.corrupt 逻辑保留），再按行重放 history.journal。
    /// 重放规则：每行携带基档代号，只有与基档代号匹配才应用（compaction 崩溃残留的旧 journal
    /// 会被忽略并清掉，不会复活已删条目/退回旧顺序）；行内 ids 决定重放后的顺序与成员，
    /// set 提供新增/变更条目。尾部半行/坏行容忍——解析失败即停止重放该行及之后，不把整档变 .corrupt，
    /// 并标记下次保存必须全量重写。基档缺失/损坏但 journal 存在时增量无法独立重建（set 只是差量），
    /// 按现有语义返回空并清掉 journal。
    /// </summary>
    public List<ClipboardEntry> Load()
    {
        lock (_saveGate) return LoadLocked();
    }

    private List<ClipboardEntry> LoadLocked()
    {
        DeleteStaleTmp(); // 上次写一半被杀残留的 tmp：明文全量历史副本，不再留盘
        _lastSaved = null;
        _lastSavedOrder = null;
        _journalGeneration = null;
        _journalRecords = 0;
        _journalNeedsCompaction = false;

        if (!File.Exists(HistoryPath))
        {
            // 基档不存在时 journal 无从上溯（全量保存会清空 journal，正常不该出现）→ 清掉按空库处理
            DeleteJournalBestEffort();
            AdoptSnapshot([]);
            return [];
        }

        // 大小上限（复审 P2-3）：反序列化前先看文件大小，超限档按损坏处理——只丢历史，不炸启动。
        // 「读不到大小」交给下面打开路径的既有 catch 口径，这里不重复收尾。
        if (ExceedsFileLimit(HistoryPath, MaxHistoryFileBytes)) return QuarantineCorruptBase();

        Dto? dto;
        try
        {
            // 流式反序列化（UTF-8 直读）：ReadAllText 会先产 2× 文件大小的 UTF-16 大串、
            // STJ 字符串入口内部再转码回 UTF-8——20MB 档实测每次 Load 白付 ~80MB 托管分配；
            // 保存侧早已 Utf8JsonWriter 直写流，此为读侧镜像（2026-11-06 性能复审②）。
            using var stream = File.OpenRead(HistoryPath);
            dto = JsonSerializer.Deserialize<Dto>(stream, JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return QuarantineCorruptBase();
        }
        if (dto?.Entries is null)
        {
            // "Entries": null 会覆盖属性初始化器，直用会 NRE；可修复的异常档：返回空、原文件保留
            DeleteJournalBestEffort();
            AdoptSnapshot([]);
            return [];
        }

        // 条目总数上限（复审 P2-3）：反序列化后仍可能条数爆炸（极小条目 × 百万条）；超限按损坏档处理。
        if (dto.Entries.Count > Math.Max(0, MaxLoadedEntries)) return QuarantineCorruptBase();

        _journalGeneration = dto.JournalBase;
        var baseEntries = new List<ClipboardEntry>(dto.Entries.Count);
        var seenIds = new HashSet<Guid>(dto.Entries.Count); // 重复 Id（篡改/损坏）：保留先出现的条目
        foreach (var raw in dto.Entries)
        {
            if (NormalizeEntry(raw) is not { } normalized) continue;
            if (!seenIds.Add(normalized.Id)) continue;
            baseEntries.Add(normalized);
        }
        var result = ReplayJournal(baseEntries);
        AdoptSnapshot(CopyEntries(result)); // result 与引擎共享引用：存拷贝后再交给调用方
        return result;
    }

    /// <summary>
    /// 按损坏档语义收尾：history.json 迁 .corrupt（覆盖旧备份；被占用/权限失败只吞掉，下次启动再试）、
    /// 清 journal、采纳空快照，返回空列表。Load 不得因坏档抛出（复审 P2-3 的超限路径与既有
    /// JSON/IO 损坏路径共用同一收尾，保证「迁一次、留备份、可继续启动」语义一致）。
    /// </summary>
    private List<ClipboardEntry> QuarantineCorruptBase()
    {
        try { File.Move(HistoryPath, HistoryPath + ".corrupt", overwrite: true); } catch { }
        DeleteJournalBestEffort();
        AdoptSnapshot([]);
        return [];
    }

    /// <summary>重放 journal（基档已读入并按白名单归一化）。见 Load 注释的规则与容错语义。</summary>
    private List<ClipboardEntry> ReplayJournal(List<ClipboardEntry> baseEntries)
    {
        var map = new Dictionary<Guid, ClipboardEntry>(baseEntries.Count);
        var order = new List<Guid>(baseEntries.Count);
        foreach (var entry in baseEntries)
        {
            map[entry.Id] = entry;
            order.Add(entry.Id);
        }
        _journalRecords = 0;
        if (!File.Exists(JournalPath)) return [.. baseEntries];

        // journal 大小上限（复审 P2-3）：超限整份不重放（篡改的超大 journal 同样能在启动期撑爆内存），
        // 保留基档结果；不删超大 journal——标记下次保存必须全量 compaction，由 SaveLocked 清掉。
        if (ExceedsFileLimit(JournalPath, MaxJournalFileBytes))
        {
            _journalNeedsCompaction = true;
            return [.. baseEntries];
        }

        var applied = 0;
        var broken = false;
        var stale = false;
        try
        {
            // 共享读打开：不与其它读方互斥（写方只在本 store 的 _saveGate 内）
            using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue; // 容错：空行（正常写入不产生）直接跳过
                JournalRecord? record;
                try
                {
                    record = JsonSerializer.Deserialize<JournalRecord>(line, JsonOpts);
                }
                catch (JsonException)
                {
                    broken = true; // 尾部半行/坏行：停在最后一个完整记录，不让整档变 .corrupt
                    break;
                }
                if (record?.Base is null)
                {
                    broken = true; // 行内容不是有效记录（null / 缺 Base）：按坏行处理
                    break;
                }
                if (_journalGeneration is null
                    || !string.Equals(record.Base, _journalGeneration, StringComparison.Ordinal))
                {
                    // 基档代号不匹配 = compaction 崩溃残留的旧 journal（或被篡改）：整份不重放
                    if (applied == 0) stale = true;
                    else broken = true;
                    break;
                }
                var nextOrder = ApplyJournalRecord(record, map);
                if (nextOrder is null)
                {
                    broken = true;
                    break;
                }
                order = nextOrder;
                applied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            broken = true; // 读到一半 IO 失败：保留已重放部分，标记下次保存全量
        }

        if (stale)
        {
            // 与当前基档无关的旧 journal：清掉（清不掉则下次保存强制全量重写一次）
            if (!DeleteJournalBestEffort()) _journalNeedsCompaction = true;
            return [.. baseEntries];
        }
        // 末条记录缺结尾换行（「JSON 已落盘、换行符未落盘」的崩溃窗口）：数据有效且已在上面重放，
        // 但再 append 会与它粘成一行 → 标记下次保存全量重写，把已重放状态固化进基档。
        if (!broken && applied > 0 && !JournalEndsWithNewline()) broken = true;
        if (broken) _journalNeedsCompaction = true; // 否则后续追加的记录会被坏行截断而无法重放
        _journalRecords = applied;
        return order.Select(id => map[id]).ToList();
    }

    /// <summary>应用一条 journal 记录：set 覆盖/新增条目，ids 决定顺序与成员；
    /// 不自洽返回 null（先校验后提交，失败时不污染 map——保证结果恰为最后一个完整记录的状态）。</summary>
    private static List<Guid>? ApplyJournalRecord(JournalRecord record, Dictionary<Guid, ClipboardEntry> map)
    {
        if (record.Ids is null) return null;
        var incoming = new Dictionary<Guid, ClipboardEntry>();
        if (record.Set is not null)
            foreach (var raw in record.Set)
            {
                var entry = NormalizeEntry(raw);
                if (entry is null) return null;
                incoming[entry.Id] = entry;
            }
        var ids = new List<Guid>(record.Ids.Count);
        var seen = new HashSet<Guid>(record.Ids.Count);
        foreach (var text in record.Ids)
        {
            if (!Guid.TryParse(text, out var id) || !seen.Add(id)) return null;
            if (!map.ContainsKey(id) && !incoming.ContainsKey(id)) return null;
            ids.Add(id);
        }
        // journal 只表达「新增/更新/重排」，成员只增不减：ids 必须恰好覆盖“原成员 + 本记录新增”
        var newlyAdded = 0;
        foreach (var id in incoming.Keys)
            if (!map.ContainsKey(id)) newlyAdded++;
        if (ids.Count != map.Count + newlyAdded) return null;
        foreach (var pair in incoming) map[pair.Key] = pair.Value;
        return ids;
    }

    /// <summary>journal 末字节是否是换行：保证下一次 append 不会与「缺换行的末条记录」粘成一行。</summary>
    private bool JournalEndsWithNewline()
    {
        try
        {
            using var fs = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return true;
            _ = fs.Seek(-1, SeekOrigin.End);
            return fs.ReadByte() == '\n';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // 读不了：保守视为需要全量重写
        }
    }

    /// <summary>
    /// 磁盘条目归一化（信任边界：history.json / journal 可被篡改/半写）。不合规矩（空 Id、越界 Kind、
    /// 图像条目文件名失范、字段超上限）返回 null 丢弃；可修复的失范就地归一化：null Preview/Files 补默认值、
    /// Preview 截断到 200 字符、SourceApp/SourceAppPath 截断、SizeBytes 负值归 0（复审 P2-3）。
    /// 重复 Id 由调用方（Load 的 HashSet）丢弃后出现者——这里的空 Id 检查同时覆盖 journal 的 Set 条目。
    /// 注意：SourceApp/SourceAppPath/SizeBytes 是 init-only（条目契约不变），故返回归一化副本
    /// （字段全量浅拷贝：字符串/列表共享引用，Load 每个条目多一条小对象分配，可忽略）。
    /// </summary>
    private static ClipboardEntry? NormalizeEntry(ClipboardEntry? e)
    {
        if (e is null) return null;
        if (e.Id == Guid.Empty) return null; // 空 Id 无法命中任何条目（重复 Id 由 Load 去重）
        if ((int)e.Kind is < 0 or > 3) return null; // STJ 对数字枚举不做范围校验，越界值下游字典/switch 会炸
        if (e.Kind == EntryKind.Image && !IsValidImageFileName(e.ImageFile)) return null;

        // 字段上限：超限整条丢弃（截断存储会破坏回贴保真，与入库侧「超限拒收」口径一致）
        if (e.FullText is { } fullText
            && System.Text.Encoding.UTF8.GetByteCount(fullText) > MaxFullTextBytes) return null;
        var files = e.Files;
        if (files is not null)
        {
            if (files.Count > MaxFileCount) return null;
            foreach (var path in files)
                if (path is null || path.Length > MaxFilePathChars) return null;
        }
        if (e.ImageWidth is < 0 or > MaxImageDimension || e.ImageHeight is < 0 or > MaxImageDimension) return null;

        return new ClipboardEntry
        {
            Id = e.Id,
            Kind = e.Kind,
            Preview = Truncate(e.Preview, MaxPreviewChars) ?? string.Empty,
            FullText = e.FullText,
            ImageFile = e.ImageFile,
            ImageWidth = e.ImageWidth,
            ImageHeight = e.ImageHeight,
            Files = files ?? [],
            SourceApp = Truncate(e.SourceApp, MaxSourceAppChars),
            SourceAppPath = Truncate(e.SourceAppPath, MaxSourceAppPathChars),
            Pinned = e.Pinned,
            Favorite = e.Favorite,
            UtcTimestamp = e.UtcTimestamp,
            SizeBytes = Math.Max(0, e.SizeBytes),
            ContentHash = e.ContentHash,
        };
    }

    /// <summary>超长字符串截断到 maxChars；null 原样返回。</summary>
    private static string? Truncate(string? value, int maxChars)
        => value is not null && value.Length > maxChars ? value[..maxChars] : value;

    /// <summary>文件是否超过上限（Load 限额用）。不存在/读不到大小按 <see cref="FileBytes"/> 语义取 0
    /// （即 false，交给后续打开路径的既有 catch 口径）；负阈值按 0（任何非空文件都算超限）。</summary>
    private static bool ExceedsFileLimit(string path, long maxBytes)
        => FileBytes(path) > Math.Max(0, maxBytes);

    /// <summary>删除缓存目录中不再被引用的图像文件，返回删除数量（主图 + 缩略图合计）。
    /// 主图：cache/*.png 中未被任何条目引用者（非递归枚举，thumbs 子目录不参与主图清理）。
    /// 缩略图：thumbs/*.png 中对应主图已不存在或未被引用者；只认白名单文件名（32hex+.png），
    /// 目录里的其他文件不动——防止数据目录被塞入任意文件后借启动清理删掉目录外/无关内容。
    /// 主图被删时缩略图通常已由 DeleteImageFile 同步删除，这里是崩溃/删除失败后的兜底。</summary>
    public int CleanupOrphanImages(HistoryEngine engine)
    {
        if (!Directory.Exists(CacheDirectory)) return 0;
        var referenced = engine.Snapshot()
            .Where(e => e.Kind == EntryKind.Image && e.ImageFile is not null)
            .Select(e => e.ImageFile!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(CacheDirectory, "*.png"))
        {
            if (!referenced.Contains(Path.GetFileName(file)))
            {
                try { File.Delete(file); removed++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        if (!Directory.Exists(ThumbnailDirectory)) return removed;
        foreach (var file in Directory.EnumerateFiles(ThumbnailDirectory, "*.png"))
        {
            var name = Path.GetFileName(file);
            if (!IsValidImageFileName(name)) continue; // 白名单：只处理本应用生成的缩略图名
            if (referenced.Contains(name) && File.Exists(Path.Combine(CacheDirectory, name))) continue;
            try { File.Delete(file); removed++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>
    /// 图像缓存预算（主图 cache/*.png + 预缩略图 cache/thumbs/*.png 合计，默认 512MB）。
    /// 背景（2026-11-10 复审问题 5）：单图 PNG ≤64MB 且此前 SaveImagePng 无配额，1000 条
    /// 图像缓存理论可达数十 GB；本预算由 <see cref="EnforceImageCacheBudget"/> 在启动期执行。
    /// 负值按 0 处理。
    /// </summary>
    public long ImageCacheBudgetBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>主图 + 缩略图目录的 PNG 字节和（目录不存在/枚举或读大小异常按 0 或已得部分，不抛）。</summary>
    public long GetImageCacheBytes() => SumPngBytes(CacheDirectory) + SumPngBytes(ThumbnailDirectory);

    /// <summary>
    /// 图像缓存配额执行（启动期调用，期望 UI 线程——见下文调用约定）。步骤：
    /// ①先清理孤儿文件（无条目引用的主图/缩略图，不计入条目淘汰）；
    /// ②仍在预算内即结束；
    /// ③否则按 UtcTimestamp 从旧到新移除「非置顶/非收藏」的图像条目，并立即删除其主图+缩略图，
    ///   直到合计 ≤ 预算或候选用尽。置顶/收藏图像不删——它们可能让缓存继续超预算，属预期。
    /// 返回删除数量 = 孤儿文件数（<see cref="CleanupOrphanImages"/> 口径）+ 被移除的图像条目数。
    ///
    /// 调用约定：engine.Remove 会触发 Changed（App 已订阅防抖自动保存），故须在 UI 线程调用；
    /// 文件立即删除、history.json 由既有保存路径稍后追上——若此间崩溃，条目会指向缺失图像，
    /// 面板按「缓存已清理」占位兜底（LoadImagePng/ThumbConverter 对缺失文件返回 null），不会崩。
    /// 预算比较按删除前的文件尺寸预估：删除因被占用而失败时实际占用可能仍高于预算，
    /// 由下次启动的孤儿清理/配额执行兜底。
    /// </summary>
    public int EnforceImageCacheBudget(HistoryEngine engine, long? budgetBytes = null)
    {
        var budget = Math.Max(0, budgetBytes ?? ImageCacheBudgetBytes);
        var removed = CleanupOrphanImages(engine);
        var current = GetImageCacheBytes();
        if (current <= budget) return removed;

        var candidates = engine.Snapshot()
            .Where(e => e.Kind == EntryKind.Image && !e.Pinned && !e.Favorite && e.ImageFile is not null)
            .OrderBy(e => e.UtcTimestamp)
            .ToList();
        var evicted = 0;
        foreach (var e in candidates)
        {
            if (current <= budget) break;
            var freed = ImageFileBytes(e.ImageFile);
            if (!engine.Remove(e.Id)) continue; // 快照后被并发移除：文件删除已由那条路径/孤儿清理负责
            DeleteImageFile(e.ImageFile);
            current -= freed;
            evicted++;
        }
        return removed + evicted;
    }

    /// <summary>一个图像条目对应的主图+缩略图当前字节和（文件名失范/文件缺失/不可读按 0）。</summary>
    private long ImageFileBytes(string? imageFile)
    {
        if (!IsValidImageFileName(imageFile)) return 0;
        var total = FileBytes(Path.Combine(CacheDirectory, imageFile!));
        if (ThumbnailPathFor(imageFile) is { } thumb) total += FileBytes(thumb);
        return total;
    }

    private static long SumPngBytes(string directory)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
                total += FileBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 枚举中途失败：返回已得部分（配额执行是尽力而为，不把磁盘状态异常抛进启动路径）
        }
        return total;
    }

    /// <summary>单个文件字节数；不存在/被占用/不可读一律 0（FileInfo.Length 不存在时抛的
    /// FileNotFoundException 属 IOException，一并吞掉）。</summary>
    private static long FileBytes(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }
}
