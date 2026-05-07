namespace DH.Display.Realtime;

public sealed class SampleRing
{
    private float[] _buf;
    private long _write;     // 下一个写入位置（全局样本序号 mod 容量）
    private long _count;     // 历史总样本数（单调递增）
    private readonly object _lock = new();

    public int Capacity { get; }
    public long TotalCount { get { lock (_lock) return _count; } }

    public SampleRing(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _buf = new float[Capacity];
        _write = 0; _count = 0;
    }

    public void Append(ReadOnlySpan<float> src)
    {
        lock (_lock)
        {
            for (int i = 0; i < src.Length; i++)
            {
                _buf[_write] = src[i];
                _write = (_write + 1) % Capacity;
                _count++;
            }
        }
    }

    /// <summary>
    /// 读取从全局样本索引 globalStart 开始的 count 个样本（不足则用可用部分）。
    /// 返回真实读取数量。globalStart 以 0 为最早样本；超过窗口会裁剪。
    /// </summary>
    public int Read(long globalStart, int count, Span<double> dst)
    {
        lock (_lock)
        {
            if (_count == 0 || count <= 0) return 0;

            // 环里当前有效窗口：最近 min(_count, Capacity) 个样本
            long windowSize = Math.Min(_count, (long)Capacity);
            long firstIndex = _count - windowSize;           // 此窗口的最左全局索引
            long lastIndex = _count - 1;                    // 最右全局索引

            // 与请求区间求交集
            long reqStart = Math.Max(globalStart, firstIndex);
            long reqEnd = Math.Min(globalStart + count - 1, lastIndex);
            if (reqStart > reqEnd) return 0;

            int n = (int)(reqEnd - reqStart + 1);

            // 把全局索引映射到环缓冲物理索引
            for (long g = reqStart, i = 0; i < n; g++, i++)
            {
                long offset = g - firstIndex;                // 在窗口内的偏移
                int phys = (int)(((_write - windowSize + offset) % Capacity + Capacity) % Capacity);
                dst[(int)i] = _buf[phys];
            }
            return n;
        }
    }
}
