// src/DH.Display/Realtime/EcgSignalRenderer.cs
using DH.Configmanage.MockConfig;
using DH.Contracts.Abstractions;
using ScottPlot.Plottables;
namespace DH.Display.Realtime;

public sealed class EcgSignalRenderer : IDisposable
{
    private readonly IDataBus _bus;
    private readonly int _channelId;
    private readonly int _sampleRate;
    private readonly double _period;         // 秒/点
    private readonly double _windowSeconds;
    private IDHPlotHost? _host;
    private Signal? _signal;

    private double[] _buffer = Array.Empty<double>();
    private int _count = 0;                   // 当前有效点数
    private double _tStart = 0;               // _buffer[0] 的绝对时间(秒)

    private CancellationTokenSource? _cts;
    private Task? _subTask;

    public EcgSignalRenderer(IDataBus bus, int channelId, MockConfig? cfg = null)
    {
        _bus = bus;
        _channelId = channelId;
        cfg ??= MockConfig.Instance;

        _sampleRate   = cfg.SampleRate;
        _period       = 1.0 / _sampleRate;
        _windowSeconds= cfg.WindowSeconds;
    }

    public void AttachHost(IDHPlotHost host)
    {
        _host = host;

        // 在 UI 线程上创建 Signal
        _host.InvokeOnUi(() =>
        {
            var plt = _host.Plot;
            
            int capacity = (int)Math.Ceiling(_windowSeconds * _sampleRate);
            _buffer = new double[capacity];
            _count = 0;
            _tStart = 0;

            _signal = plt.Add.Signal(_buffer, _period);
            _signal.LineWidth = 1;

            // 初始坐标范围
            plt.Axes.SetLimits(0, _windowSeconds, -1.5, 1.5);
            _host.Refresh();
        });
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _subTask = Task.Run(async () =>
        {
            await foreach (var frame in _bus.SubscribeChannel(_channelId, token))
            {
                // 取出样本（避免在 async 方法里用 Span）
                var samples = frame.Samples.ToArray();
                AppendAndRender(samples);
            }
        }, token);
    }

    public async Task StopAsync()
    {
        var cts = _cts; _cts = null;
        if (cts != null) cts.Cancel();
        if (_subTask != null)
        {
            try { await _subTask.ConfigureAwait(false); } catch { }
            _subTask = null;
        }
        cts?.Dispose();
    }

    public void Dispose() => _ = StopAsync();

    private void AppendAndRender(float[] yNew)
    {
        if (_host is null) return;

        // 累加到滑动窗口（顺序数组 + 左移）
        int k = yNew.Length;
        if (_buffer.Length == 0 || k == 0) return;

        if (_count + k > _buffer.Length)
        {
            int shift  = _count + k - _buffer.Length;
            int remain = _count - shift;
            if (remain > 0)
                Array.Copy(_buffer, shift, _buffer, 0, remain);
            _count = Math.Max(0, remain);
            _tStart += shift * _period; // 左移 => 起点时间后移
        }

        Array.Copy(yNew, 0, _buffer, _count, k);
        _count += k;

        // 在 UI 线程更新 Plot
        _host.InvokeOnUi(() =>
        {
            var plt = _host.Plot;
            if (_signal is null) return;

            // v5 最通用：移除旧 plottable，用最新数组重建
            plt.Remove(_signal);
            _signal = plt.Add.Signal(_buffer, _period);
            _signal.LineWidth = 1;
            _signal.Data.XOffset = _tStart; //更新起点

            double nowSec = _tStart + (_count > 0 ? (_count - 1) * _period : 0);
            double xmin   = Math.Max(0, nowSec - _windowSeconds);
            double xmax   = xmin + _windowSeconds;

            plt.Axes.SetLimits(xmin, xmax, -1.5, 1.5);
            _host.Refresh();
        });
    }
}
