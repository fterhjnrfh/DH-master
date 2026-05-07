using DH.Contracts.Abstractions;

namespace DH.Display.Realtime;

public sealed class HistoryWorker : IDisposable
{
    private readonly IDataBus _bus;
    private readonly int _channelId;
    private readonly SampleRing _ring;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SampleRing Ring => _ring;

    public HistoryWorker(IDataBus bus, int channelId, int sampleRate, int historySeconds = 120)
    {
        _bus = bus; _channelId = channelId;
        int capacity = Math.Max(1, sampleRate * historySeconds);
        _ring = new SampleRing(capacity);
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var tk = _cts.Token;

        _task = Task.Run(async () =>
        {
            await foreach (var f in _bus.SubscribeChannel(_channelId, tk))
                _ring.Append(f.Samples.ToArray()); // 转成 double 也行：Array.ConvertAll
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
