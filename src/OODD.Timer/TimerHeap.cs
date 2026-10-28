namespace OODD.Timer;

/// <summary>
/// 带索引的到期时间戳最小堆。调度线程用它决定"下一个要等的到底是哪个定时器"。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 <see cref="System.Collections.Generic.SortedSet{T}"/>：</b>
/// 堆只需要"取最小 / 替换最小 / 按键更新"，O(log n) 但常数远小于平衡树，
/// 而且不产生任何节点分配——调度线程每拍都要动它，微秒级节拍下的分配压力会变成 GC 停顿，
/// 直接毁掉尾部精度。
/// </para>
/// <para>
/// <b>为什么要"带索引"：</b>堆里的每一项就是一个 <see cref="HighResolutionTimer"/>，
/// 而定时器对象自己记住所在下标（<see cref="HighResolutionTimer.HeapIndex"/>）。
/// 这样 <see cref="HighResolutionTimer.Stop"/> 能精确把自己从堆里摘掉（而不是留墓碑），
/// <see cref="UpdateKey"/> 能在改周期时原地调整位置。
/// 用墓碑的写法会让同一个定时器在"停→立即启动"时出现两份堆内记录，从而双倍触发——
/// 这是本库明确避免的失效模式。
/// </para>
/// </remarks>
internal sealed class TimerHeap
{
    private HighResolutionTimer[] _items = new HighResolutionTimer[16];
    private int _count;

    /// <summary>堆内定时器数量。</summary>
    public int Count => _count;

    /// <summary>堆顶（最早到期）定时器。<see cref="Count"/> 为 0 时调用会抛 <see cref="InvalidOperationException"/>。</summary>
    public HighResolutionTimer Peek() => _count > 0 ? _items[0] : throw new InvalidOperationException("堆为空。");

    /// <summary>加入一个定时器（要求其 <see cref="HighResolutionTimer.DueTimestamp"/> 已设置，且当前不在堆内）。</summary>
    /// <param name="timer">要加入的定时器。</param>
    public void Add(HighResolutionTimer timer)
    {
        System.Diagnostics.Debug.Assert(timer.HeapIndex < 0, "定时器已经在堆里，重复加入会导致双倍触发。");
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        int index = _count++;
        _items[index] = timer;
        timer.HeapIndex = index;
        SiftUp(index);
    }

    /// <summary>取出并移除堆顶。</summary>
    public HighResolutionTimer ExtractMin()
    {
        HighResolutionTimer top = _items[0];
        RemoveAt(0);
        return top;
    }

    /// <summary>按键值变化后原地调整位置（改周期 / 改到期时间用）。定时器必须已在堆内。</summary>
    /// <param name="timer">到期时间戳已被修改的定时器。</param>
    public void UpdateKey(HighResolutionTimer timer)
    {
        int index = timer.HeapIndex;
        if (index < 0 || index >= _count || _items[index] != timer)
        {
            return;
        }

        // 先尝试上移；没动再尝试下移。两次的比较都只依赖 DueTimestamp。
        if (index > 0 && IsBefore(timer, _items[(index - 1) / 2]))
        {
            SiftUp(index);
        }
        else
        {
            SiftDown(index);
        }
    }

    /// <summary>移除指定定时器（不在堆内时静默返回）。</summary>
    /// <param name="timer">要移除的定时器。</param>
    public void Remove(HighResolutionTimer timer)
    {
        int index = timer.HeapIndex;
        if (index < 0 || index >= _count || _items[index] != timer)
        {
            return;
        }

        RemoveAt(index);
    }

    /// <summary>清空（释放调度器时用）。</summary>
    public void Clear()
    {
        for (int i = 0; i < _count; i++)
        {
            _items[i].HeapIndex = -1;
            _items[i] = null!;
        }

        _count = 0;
    }

    private void RemoveAt(int index)
    {
        int last = _count - 1;
        _items[index].HeapIndex = -1;
        _items[index] = null!;
        _count = last;
        if (index == last)
        {
            return;
        }

        _items[index] = _items[last];
        _items[last] = null!;
        _items[index].HeapIndex = index;
        UpdateKey(_items[index]);
    }

    private void SiftUp(int index)
    {
        HighResolutionTimer node = _items[index];
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!IsBefore(node, _items[parent]))
            {
                break;
            }

            _items[index] = _items[parent];
            _items[index].HeapIndex = index;
            index = parent;
        }

        _items[index] = node;
        node.HeapIndex = index;
    }

    private void SiftDown(int index)
    {
        HighResolutionTimer node = _items[index];
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= _count)
            {
                break;
            }

            int right = left + 1;
            int best = right < _count && IsBefore(_items[right], _items[left]) ? right : left;
            if (!IsBefore(_items[best], node))
            {
                break;
            }

            _items[index] = _items[best];
            _items[index].HeapIndex = index;
            index = best;
        }

        _items[index] = node;
        node.HeapIndex = index;
    }

    /// <summary>优先级：先到期者优先；同一到期点按入堆顺序（<see cref="HighResolutionTimer.Ordinal"/>）稳定排序。</summary>
    private static bool IsBefore(HighResolutionTimer left, HighResolutionTimer right)
    {
        long delta = left.DueTimestamp - right.DueTimestamp;
        return delta != 0 ? delta < 0 : left.Ordinal < right.Ordinal;
    }
}
