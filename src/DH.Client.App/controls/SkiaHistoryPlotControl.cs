using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using DH.Display.Realtime;

namespace DH.Client.App.Controls;

public class SkiaHistoryPlotControl : Control
{
    public HistoryWorker? Worker { get; private set; }
    public int SampleRate { get; set; } = 1000;
    public double YMin { get; set; } = -1.2;
    public double YMax { get; set; } =  1.2;

    // 视口：以全局样本索引为单位
    private bool _followLive = true;     // 是否跟随最新（启动默认开启）
    private long _viewLeft = 0;
    private int  _viewSamples;             // = seconds * SampleRate
    private bool _isDragging = false;
    private Point _lastPt;

    // 左右与下方边距（轴）
    private const int LeftPad = 48;
    private const int BottomPad = 24;

    public bool FollowLive
    {
        get => _followLive;
        set { _followLive = value; InvalidateVisual(); }
    }

    public void AttachWorker(HistoryWorker worker, double initialSeconds = 5.0)
    {
        Worker = worker;
        _viewSamples = (int)Math.Max(1, initialSeconds * SampleRate);
        InvalidateVisual();
    }

    public void GoLive()
    {
         // 直接把视口对齐到最新，开启跟随
        if (Worker is null) 
            return;

        long total = Worker.Ring.TotalCount;
        _viewLeft = Math.Max(0, total - _viewSamples);
        _followLive = true;
        InvalidateVisual();
    }

    public SkiaHistoryPlotControl()
    {
        // 支持鼠标操作
        this.PointerPressed += (s, e) =>
        {
            _isDragging = true; 
            _lastPt = e.GetPosition(this);
            e.Pointer.Capture(this);

            _followLive = false;  // 用户拖动就退出跟随
        };
        this.PointerReleased += (s, e) =>
        {
            _isDragging = false; 
            _followLive = true;
            e.Pointer.Capture(null);
        };
        this.PointerMoved += (s, e) =>
        {
            if (!_isDragging) return;
            var p = e.GetPosition(this);
            double dx = p.X - _lastPt.X;
            _lastPt = p;

            // 像素→样本：每像素代表的样本数 = 视口样本 / 绘图区宽度
            int plotW = Math.Max(1, (int)Bounds.Width - LeftPad);
            double samplesPerPixel = (double)_viewSamples / plotW;
            long dSamples = (long)Math.Round(-dx * samplesPerPixel); // 向右拖=看更早 => 左边索引增加
            Pan(dSamples);
        };
        this.PointerWheelChanged += (s, e) =>
        {
            // 滚轮缩放 X（视口样本数）：滚轮上=放大（看更短时间）
            double factor = e.Delta.Y > 0 ? 0.8 : 1.25;
            ZoomX(factor, (int)e.GetPosition(this).X);

            _followLive = false; // 缩放也退出跟随
        };
    }

    private void Pan(long deltaSamples)
    {
        if (Worker is null) return;
        long total = Worker.Ring.TotalCount;
        long maxLeft = Math.Max(0, total - _viewSamples); // 允许看到的最右位置（贴近最新）
        _viewLeft = Math.Clamp(_viewLeft + deltaSamples, 0, maxLeft);
        InvalidateVisual();
    }

    private void ZoomX(double factor, int mouseX)
    {
        // 围绕当前鼠标点进行缩放，保持鼠标所指时间不变
        int plotW = Math.Max(1, (int)Bounds.Width - LeftPad);
        int xInPlot = Math.Clamp(mouseX - LeftPad, 0, plotW - 1);
        long anchorSample = _viewLeft + (long)Math.Round((double)xInPlot / plotW * _viewSamples);

        int newView = (int)Math.Clamp(Math.Round(_viewSamples * factor), SampleRate * 0.1, SampleRate * 3600);
        long newLeft = anchorSample - (long)Math.Round((double)xInPlot / plotW * newView);

        if (Worker != null)
        {
            long total = Worker.Ring.TotalCount;
            long maxLeft = Math.Max(0, total - newView);
            _viewSamples = newView;
            _viewLeft = Math.Clamp(newLeft, 0, maxLeft);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.Custom(new DrawOp(new Rect(Bounds.Size), this, Worker, SampleRate, _viewLeft, _viewSamples, YMin, YMax));
    }

    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly SkiaHistoryPlotControl _owner;
        private readonly HistoryWorker? _worker;
        private readonly int _fs;
        private readonly long _viewLeft;
        private readonly int _viewSamples;
        private readonly double _ymin, _ymax;

        public DrawOp(Rect bounds, SkiaHistoryPlotControl owner, HistoryWorker? worker, int fs, long viewLeft, int viewSamples, double ymin, double ymax)
        {
            _bounds = bounds; 
            _owner = owner;
            _worker = worker; 
            _fs = fs;
            _viewLeft = viewLeft; 
            _viewSamples = Math.Max(1, viewSamples);
            _ymin = ymin; 
            _ymax = ymax;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            using var lease  = leaseFeature?.Lease();
            var canvas       = lease?.SkCanvas;
            if (canvas is null) return;

            int w = (int)_bounds.Width, h = (int)_bounds.Height;
            canvas.Clear(SKColors.Black);

            int plotW = Math.Max(1, w - LeftPad);
            int plotH = Math.Max(1, h - BottomPad);
            float x0 = LeftPad;
            float y0 = h - BottomPad;

            // 取本视口的样本（尽量一次性批量拉取）
            var tmp = new double[_viewSamples];
            int got = _worker?.Ring.Read(_viewLeft, _viewSamples, tmp) ?? 0;

            // 画波形（按像素步进采样；如需更高保真可做每像素min/max包络）
            using var pen = new SKPaint { Color = SKColors.Lime, IsAntialias = false, StrokeWidth = 1f };

            double yrange = Math.Max(1e-9, _ymax - _ymin);
            if (got > 0)
            {
                // 像素→样本索引映射：s = viewLeft + x / plotW * viewSamples
                int lastX = -1, lastY = -1;
                for (int px = 0; px < plotW; px++)
                {
                    double sRel = (double)px / (plotW - 1) * (got - 1);
                    int si = (int)Math.Round(sRel);
                    double v = tmp[si];
                    int yy = (int)Math.Round(y0 - (v - _ymin) / yrange * plotH);
                    int xx = (int)(x0 + px);

                    yy = Math.Clamp(yy, 0, h - BottomPad - 1);
                    if (lastX >= 0)
                    {
                        if (Math.Abs(yy - lastY) <= 1) canvas.DrawPoint(xx, yy, pen);
                        else canvas.DrawLine(lastX, lastY, xx, yy, pen);
                    }
                    lastX = xx; lastY = yy;
                }
            }

            // 画坐标轴与网格 + X轴刻度（起点时间 = viewLeft / fs）
            DrawAxes(canvas, w, h, _fs, _viewLeft, _viewSamples, _ymin, _ymax);
        }

        private static void DrawAxes(SKCanvas c, int viewW, int viewH, int fs, long viewLeft, int viewSamples, double yMin, double yMax)
        {
            using var axis = new SKPaint { Color = SKColors.Gray, IsAntialias = true, StrokeWidth = 1.5f };
            using var grid = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = false, StrokeWidth = 1f };
            using var text = new SKPaint { Color = SKColors.Gray, IsAntialias = true, TextSize = 12f };
            using var tick = new SKPaint { Color = SKColors.Gray, IsAntialias = true, StrokeWidth = 1f };

            float x0 = LeftPad;
            float y0 = viewH - BottomPad;
            float plotW = viewW - LeftPad;
            float plotH = viewH - BottomPad;

            c.DrawLine(x0, 0, x0, y0, axis);
            c.DrawLine(x0, y0, viewW, y0, axis);

            // X轴：把左边界对应的时间（秒）作为 t0
            double t0 = (double)viewLeft / fs;
            int gx = 10, gy = 6;

            for (int i = 1; i <= gx; i++)
            {
                float x = x0 + i * (plotW / gx);
                c.DrawLine(x, 0, x, y0, grid);

                double tSec = t0 + (i / (double)gx) * (viewSamples / (double)fs);
                c.DrawText($"{tSec:0.##}s", x - 14, y0 + 14, text);
                c.DrawLine(x, y0 - 5, x, y0, tick);
            }

            for (int j = 1; j <= gy; j++)
            {
                float y = y0 - j * (plotH / gy);
                c.DrawLine(x0, y, viewW, y, grid);
                double v = yMin + j * (yMax - yMin) / gy;
                c.DrawText($"{v:0.##}", 4, y + 4, text);
                c.DrawLine(x0, y, x0 + 5, y, tick);
            }
            c.DrawText("0", x0 - 10, y0 + 14, text);
        }
    }

    public void LiveFollowAndInvalidate()
    {
        if (Worker is null) return;
        // if (_isDragging) { InvalidateVisual(); return; }

        // long total = Worker.Ring.TotalCount;
        // long maxLeft = Math.Max(0, total - _viewSamples); // 钉在最新

        // 如果当前几乎贴近末尾（误差≤一个像素对应的样本数），就追到底
        // int plotW = Math.Max(1, (int)Bounds.Width - LeftPad);
        // double samplesPerPixel = (double)_viewSamples / Math.Max(1, plotW);
        // if (Math.Abs((_viewLeft - maxLeft)) <= samplesPerPixel * 2)
        //     _viewLeft = maxLeft;

        if (_followLive && !_isDragging)
        {
            long total = Worker.Ring.TotalCount;
            _viewLeft = Math.Max(0, total - _viewSamples);   // 视口一直保持在最新
        }

        InvalidateVisual();
    }
}
