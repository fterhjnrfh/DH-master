using DH.Contracts.Abstractions;
using DH.Display.Realtime;

public sealed class GridHub : IDisposable
{
    public const int Rows = 8;
    public const int Cols = 8;
    public const int Panels = Rows * Cols;   // 64
    public const int CurvesPerPanel = 64;    // 每面板 64 根
    private readonly IDataBus _bus;
    private readonly int _sampleRate;
    private readonly SampleRing[][] _rings;   // [panel][curve]
    private CancellationTokenSource? _cts;
    private Task? _task;

    public int SampleRate => _sampleRate;
    public SampleRing GetRing(int panel, int curve) => _rings[panel][curve];

    public GridHub(IDataBus bus, int sampleRate, int historySeconds = 20)
    {
        // 预先分配64个样本环，可以优化从而减少内存
        _bus = bus;
        _sampleRate = sampleRate; //note: 不应该使用原始的采样频率
        int cap = Math.Max(1, sampleRate * historySeconds);

        _rings = new SampleRing[Panels][];
        for (int p = 0; p < Panels; p++)
        {
            _rings[p] = new SampleRing[CurvesPerPanel];
            for (int c = 0; c < CurvesPerPanel; c++)
                _rings[p][c] = new SampleRing(cap);
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var tk = _cts.Token;

        _task = Task.Run(async () =>
        {
            Console.WriteLine("GridHub started");
            await foreach (var f in _bus.SubscribeChannel(1, tk))
            {
                //使用单个mockthread，所有的panel都显示相同曲线
                for (int panel = 0; panel < Panels; panel++)
                {
                    _rings[panel][0].Append(f.Samples.ToArray());
                }
            }
        }, tk);
    }

    public async Task StopAsync()
    {
        var cts = _cts; _cts = null;
        if (cts != null) cts.Cancel();
        if (_task != null) { try { await _task.ConfigureAwait(false); } catch { } _task = null; }
        cts?.Dispose();
    }

    public void Dispose() => _ = StopAsync();
}
