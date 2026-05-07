using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Rendering.SceneGraph;
using SkiaSharp;
using DH.Display.Skia.Realtime;
using System;

namespace DH.Client.App.Controls;

public class SkiaPlotControl : Control
{
    public SkiaRealtimePlotWorker? Worker { get; private set; }

    /// <summary>X 轴刻度上显示的秒数（视觉用，不影响滚动速度）</summary>
    public double XSeconds { get; set; } = 5.0;
    public double YMin     { get; set; } = -1.2;
    public double YMax     { get; set; } =  1.2;
    public bool Logged = false;
    public void AttachWorker(SkiaRealtimePlotWorker worker)
    {
        Worker = worker;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // 把需要用到的东西打包进自定义绘制操作（ICustomDrawOperation）
        context.Custom(new SkiaDrawOp(new Rect(Bounds.Size), Worker, XSeconds, YMin, YMax, Logged));

        Logged = true;
    }

    private sealed class SkiaDrawOp : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly SkiaRealtimePlotWorker? _worker;
        private readonly double _xSeconds, _yMin, _yMax;
        private bool _logged;

        public SkiaDrawOp(Rect bounds, SkiaRealtimePlotWorker? worker, double xSeconds, double yMin, double yMax, bool Logged)
        {
            _bounds   = bounds;
            _worker   = worker;
            _xSeconds = xSeconds;
            _yMin     = yMin;
            _yMax     = yMax;
            _logged   = Logged;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool HitTest(Point p) => _bounds.Contains(p);

        // 为了保证每帧都重绘，简单返回 false（也可根据需要实现更精细的 Equals）
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            // 从 ImmediateDrawingContext 获取 Skia 画布
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            using var lease  = leaseFeature?.Lease();
            var canvas       = lease?.SkCanvas;
            if (canvas is null) return;

            if (!_logged)
            {
                bool gpu = lease?.GrContext is not null;
                string backend = lease?.GrContext?.Backend.ToString() ?? "Software";
                Console.WriteLine($"[Skia] Rendering: {(gpu ? "GPU" : "CPU")}  Backend={backend}");
            }
                

            int viewW = (int)_bounds.Width;
            int viewH = (int)_bounds.Height;

            // 背景清屏
            canvas.Clear(SKColors.Black);

            // 先把后台线程绘制好的离屏位图贴到绘图区
            _worker?.BlitTo(canvas, viewW, viewH);

            // 再叠加 X/Y 轴与刻度（固定不滚动）
            SkiaRealtimePlotWorker.DrawAxes(
                canvas,
                viewW, viewH,
                leftPad: 48,
                bottomPad: 24,
                xSeconds: _xSeconds,
                yMin: _yMin,
                yMax: _yMax
            );
        }
    }
}
