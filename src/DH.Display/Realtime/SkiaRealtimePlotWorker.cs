using DH.Contracts.Abstractions;
using SkiaSharp;
using System.Collections.Concurrent;

namespace DH.Display.Skia.Realtime;

public sealed class SkiaRealtimePlotWorker : IDisposable
{
    // 离屏位图（只在本工作线程上绘制；UI 线程只读取）
    private readonly SKBitmap _plotBmp;
    private readonly SKCanvas _plotCanvas;
    private readonly object _bmpLock = new();

    // 区域与轴边距
    private readonly int _width, _height, _leftPad, _bottomPad;
    private int PlotW => _width  - _leftPad;
    private int PlotH => _height - _bottomPad;

    // 画笔
    private readonly SKPaint _bgPaint  = new() { Color = SKColors.Black };
    private readonly SKPaint _sigPaint = new() { Color = SKColors.Lime, IsAntialias = false, StrokeWidth = 1 };

    // Y 轴范围
    public double YMin { get; set; } = -1.2;
    public double YMax { get; set; } =  1.2;

    // 数据来源
    private readonly IDataBus _bus;
    private readonly int _channelId;
    private CancellationTokenSource? _cts;
    private Task? _subTask;

    // 连线用
    private bool _hasPrev;
    private double _prevY;

    public SkiaRealtimePlotWorker(IDataBus bus, int channelId, int width = 900, int height = 360, int leftPad = 48, int bottomPad = 24)
    {
        _bus = bus;
        _channelId = channelId;
        _width = width;
        _height = height; 
        _leftPad = leftPad; 
        _bottomPad = bottomPad;

        _plotBmp = new SKBitmap(new SKImageInfo(PlotW, PlotH, SKColorType.Bgra8888, SKAlphaType.Premul));
        _plotCanvas = new SKCanvas(_plotBmp);
        _plotCanvas.Clear(SKColors.Black);
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
                var arr = frame.Samples.ToArray();     // 避免 async 方法中 Span 限制
                DrawSamples(arr);
            }
        }, token);
    }

    public async Task StopAsync()
    {
        var cts = _cts; _cts = null;
        if (cts != null) cts.Cancel();
        if (_subTask != null) { try { await _subTask.ConfigureAwait(false); } catch { } _subTask = null; }
        cts?.Dispose();
    }

    public void Dispose() { _ = StopAsync(); _plotCanvas.Dispose(); _plotBmp.Dispose(); _bgPaint.Dispose(); _sigPaint.Dispose(); }

    // UI 线程使用：拷贝离屏位图像素用于呈现
    public void BlitTo(SKCanvas canvas, int viewW, int viewH)
    {
        lock (_bmpLock)
        {
            var src = new SKRect(0, 0, _plotBmp.Width, _plotBmp.Height);
            var dst = new SKRect(_leftPad, 0, viewW, viewH - _bottomPad);
            canvas.DrawBitmap(_plotBmp, src, dst);
        }
    }

    private void DrawSamples(ReadOnlySpan<float> ys)
    {
        lock (_bmpLock)
        {
            for (int i = 0; i < ys.Length; i++)
                AppendSample(ys[i]);
        }
    }

    private void AppendSample(double y)
    {
        // 左移一列
        var src = new SKRectI(1, 0, _plotBmp.Width, _plotBmp.Height);
        var dst = new SKRectI(0, 0, _plotBmp.Width - 1, _plotBmp.Height);
        _plotCanvas.DrawBitmap(_plotBmp, src, dst);

        // 清空最右列
        _plotCanvas.DrawRect(new SKRect(_plotBmp.Width - 1, 0, _plotBmp.Width, _plotBmp.Height), _bgPaint);

        // 映射到像素
        int x = _plotBmp.Width - 1;
        int yPix = MapY(y);
        yPix = Math.Clamp(yPix, 0, _plotBmp.Height - 1);

        if (_hasPrev)
        {
            int yPrev = Math.Clamp(MapY(_prevY), 0, _plotBmp.Height - 1);
            if (Math.Abs(yPrev - yPix) <= 1)
                _plotCanvas.DrawPoint(x, yPix, _sigPaint);
            else
                _plotCanvas.DrawLine(x - 1, yPrev, x, yPix, _sigPaint);
        }
        else
        {
            _plotCanvas.DrawPoint(x, yPix, _sigPaint);
        }

        _prevY = y;
        _hasPrev = true;
    }

    private int MapY(double y)
    {
        double range = Math.Max(1e-9, YMax - YMin);
        double t = 1.0 - (y - YMin) / range;
        return (int)Math.Round(t * (_plotBmp.Height - 1));
    }

    // 画坐标轴与网格（UI 线程在控件 Render 中调用）
    public static void DrawAxes(SKCanvas c, int viewW, int viewH, int leftPad, int bottomPad, double xSeconds, double yMin, double yMax)
    {
        using var axis = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1.5f, IsAntialias = true };
        using var grid = new SKPaint { Color = new SKColor(40, 40, 40), StrokeWidth = 1f, IsAntialias = false };
        using var text = new SKPaint { Color = SKColors.Gray, IsAntialias = true };
        using var font = new SKFont { Size = 12f };
        using var tick = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1f, IsAntialias = true };

        float x0 = leftPad;
        float y0 = viewH - bottomPad;
        float plotW = viewW - leftPad;
        float plotH = viewH - bottomPad;

        c.DrawLine(x0, 0, x0, y0, axis);
        c.DrawLine(x0, y0, viewW, y0, axis);

        int gx = 10, gy = 6;
        for (int i = 1; i <= gx; i++)
        {
            float x = x0 + i * (plotW / gx);
            c.DrawLine(x, 0, x, y0, grid);
            double tSec = i * (xSeconds / gx);
            c.DrawText($"{tSec:0.##}s", x - 10, y0 + 14, SKTextAlign.Left, font, text);
            c.DrawLine(x, y0 - 5, x, y0, tick);
        }

        for (int j = 1; j <= gy; j++)
        {
            float y = y0 - j * (plotH / gy);
            c.DrawLine(x0, y, viewW, y, grid);
            double v = yMin + j * (yMax - yMin) / gy;
            c.DrawText($"{v:0.##}", 4, y + 4, SKTextAlign.Left, font, text);
            c.DrawLine(x0, y, x0 + 5, y, tick);
        }
        c.DrawText("0", x0 - 10, y0 + 14, SKTextAlign.Left, font, text);
    }
}
