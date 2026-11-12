namespace Pasteup.Core.History;

/// <summary>面板 8 个 Tab 的全部计数（<c>HistoryEngine.Counts</c> 一次加锁单遍得出）。</summary>
public readonly record struct HistoryCounts(
    int Total, int Favorite, int Pinned, int Today,
    int Text, int Image, int Link, int File);

/// <summary>
/// 剪贴板历史的内存引擎：新增去重、容量/保留期/字节预算裁剪（置顶/收藏豁免）、按类型/关键词查询、计数。
/// 线程安全：数据变更在 lock 内完成；Changed 事件统一在锁外触发——订阅者的面板整表重建
/// （面板隐藏时也照跑）若在锁内执行，会把后台保存的 Snapshot 等所有持锁方拖到 UI 重建时长
///（Monitor 可重入免死锁，争用延迟却实打实放大；2026-11-06 性能复审⑦）。
/// </summary>
public sealed class HistoryEngine
{
    private readonly object _gate = new();
    private readonly List<ClipboardEntry> _entries = []; // 最新在前
    // 「删除即删盘」待删队列：条目被移除（删除/清空/淘汰/去重顶掉）时其图像文件名入队，
    // 调用方在 history 成功落盘后 DrainPendingImageDeletes 拿走并删文件。隐私语义：
    // 删除/清空后图像本体不能留在 cache/ 等下次启动才清（常驻会话可残留数天、退出清空可永久残留）
    private readonly Queue<string> _pendingImageDeletes = new();

    public int Capacity { get; set; } = 1000;
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// 历史聚合字节预算（按条目的 SizeBytes 合计，文本/图像/文件列表都计入）。
    /// 背景（2026-11-10 复审问题 5）：Capacity 只数条数、单条文本上限 10MB，理论驻留可达
    /// GB 级；预算把它封顶（默认 128MB）。只淘汰非置顶/非收藏条目，置顶/收藏超预算时保留。
    /// </summary>
    public long TotalBytesBudget { get; set; } = 128L * 1024 * 1024;

    public event Action? Changed;

    /// <summary>数据版本号：每次内容变更 +1（与 Changed 同步）。消费者可对比版本跳过无谓的整表刷新
    ///（面板每次唤起都重建列表会在瞬态布局期吃掉用户点击——2026-11-03 点击丢选中根因）。</summary>
    public long Version { get; private set; }

    public IReadOnlyList<ClipboardEntry> Snapshot()
    { lock (_gate) return _entries.ToArray(); }

    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>全部条目 SizeBytes 合计（负值按 0；O(N) 全表求和，供诊断/测试与预算参考）。</summary>
    public long TotalSizeBytes
    {
        get { lock (_gate) return TotalBytesLocked(); }
    }

    /// <summary>估算总字节：SizeBytes 负值按 0，累加饱和到 long.MaxValue（不做减法/溢出回绕）。
    /// 不用 Enumerable.Sum(long)（checked）：history.json 是信任边界，被篡改塞入接近
    /// long.MaxValue 的 SizeBytes 会让 Sum 抛 OverflowException，把 Load/Add 路径炸掉
    /// （2026-11-10 复审问题 5 落地时顺手加固）。</summary>
    private long TotalBytesLocked()
    {
        long total = 0;
        foreach (var e in _entries)
        {
            var size = SizeBytesOf(e);
            if (long.MaxValue - total < size) return long.MaxValue;
            total += size;
        }
        return total;
    }

    private static long SizeBytesOf(ClipboardEntry e) => Math.Max(0, e.SizeBytes);

    private void TrackImageDelete(ClipboardEntry e)
    {
        if (e.Kind == EntryKind.Image && e.ImageFile is not null) _pendingImageDeletes.Enqueue(e.ImageFile);
    }

    /// <summary>排空待删盘队列：返回自上次排空以来被移除条目的图像文件名。
    /// 调用时机 = history.json 成功落盘之后：落盘失败时文件仍被 json 引用，删了会坏缩略图，
    /// 不排空留在队列里待下次成功保存再删。</summary>
    public List<string> DrainPendingImageDeletes()
    {
        lock (_gate)
        {
            if (_pendingImageDeletes.Count == 0) return [];
            var files = new HashSet<string>(_pendingImageDeletes.Count, StringComparer.OrdinalIgnoreCase);
            while (_pendingImageDeletes.Count > 0) files.Add(_pendingImageDeletes.Dequeue());
            return [.. files];
        }
    }

    /// <summary>加入条目：与最新一条内容相同则只更新时间戳与来源；否则插入头部。返回生效后的条目。</summary>
    public ClipboardEntry Add(ClipboardEntry entry)
    {
        lock (_gate)
        {
            var head = _entries.Count > 0 ? _entries[0] : null;
            if (head is not null && ClipboardEntry.SameContent(head, entry))
            {
                var wasPinned = head.Pinned; // 同内容再次复制：保留原条目的置顶状态
                var wasFavorite = head.Favorite; // 与收藏同理
                TrackImageDelete(head); // 图像同内容重复制：旧文件被新随机名文件取代，删盘
                _entries.RemoveAt(0);
                entry.Pinned = wasPinned;
                entry.Favorite = wasFavorite;
                _entries.Insert(0, entry);
            }
            else
            {
                // 移除更旧位置的完全重复项（SameContent：零构键比较，见 ClipboardEntry.SameContent）
                for (var i = 1; i < _entries.Count; i++)
                {
                    if (ClipboardEntry.SameContent(_entries[i], entry))
                    {
                        TrackImageDelete(_entries[i]);
                        _entries.RemoveAt(i);
                        break;
                    }
                }
                _entries.Insert(0, entry);
            }
            TrimLocked();
            Version++;
        }
        Changed?.Invoke(); // 锁外触发，见类注释
        return entry;
    }

    private void TrimLocked()
    {
        // 容量裁剪：从最旧端移除，置顶/收藏条目豁免（单遍计数 + 尾端删除；
        // 旧实现 while+每轮 Count 全扫为 O(n·excess)，批量装载超容时平方级）。
        var allowed = Math.Max(0, Capacity);
        var excess = 0;
        for (var i = 0; i < _entries.Count; i++)
            if (!_entries[i].Pinned && !_entries[i].Favorite) excess++;
        excess -= allowed;
        for (var i = _entries.Count - 1; i >= 0 && excess > 0; i--)
        {
            if (_entries[i].Pinned || _entries[i].Favorite) continue;
            TrackImageDelete(_entries[i]);
            _entries.RemoveAt(i);
            excess--;
        }
        // 保留期裁剪：过期且非置顶/非收藏条目移除
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(0, RetentionDays));
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            if (!_entries[i].Pinned && !_entries[i].Favorite && _entries[i].UtcTimestamp < cutoff)
            {
                TrackImageDelete(_entries[i]);
                _entries.RemoveAt(i);
            }
        }
        // 字节预算裁剪（2026-11-10 复审问题 5）：在条数/保留期之后按 SizeBytes 合计封顶，
        // 防止「容量 × 单条 10MB」放大成 GB 级驻留。从最旧端（列表尾）向前只淘汰
        // 非置顶/非收藏条目，直到合计 ≤ 预算。
        // 边界（刻意保留，不追求硬性达标）：①只剩 1 条时即使超预算也保留——单条大文本
        // 不能把历史清空，新插入的唯一一条也不会被自己删掉；②剩余条目全是置顶/收藏时
        // 保留（用户显式保护优先于预算），此时总量可能仍超预算，属预期。SizeBytes 负值按 0。
        var budgetBytes = Math.Max(0, TotalBytesBudget);
        var totalBytes = TotalBytesLocked();
        for (var i = _entries.Count - 1; i >= 0 && totalBytes > budgetBytes; i--)
        {
            if (_entries.Count <= 1) break;
            var e = _entries[i];
            if (e.Pinned || e.Favorite) continue;
            totalBytes -= SizeBytesOf(e);
            TrackImageDelete(e);
            _entries.RemoveAt(i);
        }
    }

    /// <summary>按当前容量/保留期/字节预算裁剪历史（设置变更后与测试用的显式入口）。
    /// 与其它变更器口径一致：确有条目被裁掉时锁内 Version++、锁外 Changed（面板刷新与
    /// 自动保存的既有语义不变）；无事可裁时不广播，避免无谓整表重建与落盘。</summary>
    public void Trim()
    {
        var changed = false;
        lock (_gate)
        {
            var before = _entries.Count;
            TrimLocked();
            changed = _entries.Count != before;
            if (changed) Version++;
        }
        if (changed) Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            TrackImageDelete(_entries[idx]);
            _entries.RemoveAt(idx);
            Version++;
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>清空历史。includePinned=false 时保留置顶与收藏条目（收藏 = 长期珍藏，同置顶待遇）。</summary>
    public void Clear(bool includePinned = false)
    {
        lock (_gate)
        {
            if (includePinned)
            {
                foreach (var e in _entries) TrackImageDelete(e);
                _entries.Clear();
            }
            else
                for (var i = _entries.Count - 1; i >= 0; i--)
                {
                    if (_entries[i].Pinned || _entries[i].Favorite) continue;
                    TrackImageDelete(_entries[i]);
                    _entries.RemoveAt(i);
                }
            Version++;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// 取用条目（面板回车即 MRU）：刷新时间戳并移到置顶区之后的第一位，触发 Changed。
    /// Id 不变（图像缓存文件仍有效）、置顶状态与来源应用保持原值。
    /// </summary>
    public bool Touch(Guid id)
    {
        lock (_gate)
        {
            var idx = _entries.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            var e = _entries[idx];
            e.UtcTimestamp = DateTime.UtcNow;
            _entries.RemoveAt(idx);
            var insert = 0; // 置顶区恒在最前：跳过前导置顶段
            while (insert < _entries.Count && _entries[insert].Pinned) insert++;
            _entries.Insert(insert, e);
            Version++;
        }
        Changed?.Invoke();
        return true;
    }

    public bool TogglePin(Guid id)
    {
        bool result; // 锁内取结果：锁外再读 e.Pinned 可能被并发调用翻转
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            e.Pinned = !e.Pinned;
            if (e.Pinned)
            {
                _entries.Remove(e);
                _entries.Insert(0, e); // 置顶 = 移到最前
            }
            result = e.Pinned;
            Version++;
        }
        Changed?.Invoke();
        return result;
    }

    /// <summary>收藏切换：只翻转标志，不动顺序（收藏 ≠ 置顶，列表位置不变）。</summary>
    public bool ToggleFavorite(Guid id)
    {
        bool result;
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e is null) return false;
            e.Favorite = !e.Favorite;
            result = e.Favorite;
            Version++;
        }
        Changed?.Invoke();
        return result;
    }

    public IReadOnlyList<ClipboardEntry> Query(EntryKind? kind = null, string? search = null,
        Func<ClipboardEntry, bool>? filter = null)
    {
        lock (_gate)
        {
            IEnumerable<ClipboardEntry> q = _entries;
            if (kind is not null) q = q.Where(e => e.Kind == kind.Value);
            if (filter is not null) q = q.Where(filter);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var needle = search.Trim();
                q = q.Where(e => e.Preview.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || ContainsHead(e.FullText, needle)
                              || e.Files.Any(f => f.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            }
            return q.ToArray();
        }
    }

    /// <summary>大文本只搜头部 64K 字符：截断命中已足够定位（预览本就只显示 200 字符），
    /// 多条 MB 级文本的全文扫描会让每次击键扫数十 MB（2026-11-04 审计 C9）。
    /// 尾部内容照常取用粘贴，只是不进搜索索引。</summary>
    private const int SearchHeadChars = 64 * 1024;

    private static bool ContainsHead(string? text, string needle)
    {
        if (text is null) return false;
        var head = text.Length <= SearchHeadChars ? text.AsSpan() : text.AsSpan(0, SearchHeadChars);
        return head.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    public Dictionary<EntryKind, int> CountsByKind()
    {
        lock (_gate)
        {
            var counts = new Dictionary<EntryKind, int>
            {
                [EntryKind.Text] = 0, [EntryKind.Image] = 0, [EntryKind.Link] = 0, [EntryKind.File] = 0,
            };
            foreach (var e in _entries) counts[e.Kind]++;
            return counts;
        }
    }

    /// <summary>
    /// 面板 8 个 Tab 的全部计数，**一次加锁单遍**循环得出（原实现 = 3×Query + 4×CountsByKind
    /// 共 7 趟全表扫描，搜索防抖的每个版本刷新都白跑；2026-11-11 第 3 批 P1-2）。
    /// todayLocalStart = 本地「今天」的起点，口径与 PanelViewModel 的「今天」筛选一致
    ///（e.UtcTimestamp.ToLocalTime() &gt;= 调用方传入的 DateTime.Now.Date）。
    /// </summary>
    public HistoryCounts Counts(DateTime todayLocalStart)
    {
        lock (_gate)
        {
            int total = 0, favorite = 0, pinned = 0, today = 0;
            int text = 0, image = 0, link = 0, file = 0;
            foreach (var e in _entries)
            {
                total++;
                if (e.Favorite) favorite++;
                if (e.Pinned) pinned++;
                if (e.UtcTimestamp.ToLocalTime() >= todayLocalStart) today++;
                switch (e.Kind)
                {
                    case EntryKind.Text: text++; break;
                    case EntryKind.Image: image++; break;
                    case EntryKind.Link: link++; break;
                    case EntryKind.File: file++; break;
                }
            }
            return new HistoryCounts(total, favorite, pinned, today, text, image, link, file);
        }
    }

    /// <summary>装载持久化数据（替换全部内容，不触发 Changed）。</summary>
    public void Load(IEnumerable<ClipboardEntry> entries)
    {
        lock (_gate)
        {
            _entries.Clear();
            // 置顶前缀不变量：Touch 跳过前导置顶段、分组渲染按连续段判组界，都假设置顶条目
            // 连续居首；只按时间排会把较旧的置顶条目散落中部——重启后「今天→置顶→今天」组头交错
            _entries.AddRange(entries.OrderByDescending(e => e.Pinned).ThenByDescending(e => e.UtcTimestamp));
            TrimLocked();
        }
    }
}
