// DH.Client.App/Controls/SkiaMultiGridControl.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using DH.Display.Realtime;
using System;
using System.Collections.Generic;

namespace DH.Client.App.Controls;


public class SkiaMultiGridControl : Control
{
    private GridHub? _hub;
    public bool AutoLayoutByActivity { get; set; } = true;  // true=按活动面板（有数据）动态显示
    public int FixedPanelCount { get; set; } = 0;           // >0 时强制显示前 N 个面板（覆盖 Auto）
    public IReadOnlyList<int>? IncludedPanels { get; set; } // 显示指定面板集合（覆盖前两者）
    private List<int> _visiblePanels = new();

    // 面板状态
    private readonly long[] _viewLeft = new long[GridHub.Panels];
    private readonly int[] _viewSamples = new int[GridHub.Panels];
    private readonly double[] _yMin = new double[GridHub.Panels];
    private readonly double[] _yMax = new double[GridHub.Panels];
    private readonly bool[] _followLive = new bool[GridHub.Panels];

    // 交互状态
    private bool _drag;
    private int _activePanel = -1;
    private Point _lastPt;

    // 布局
    private const int AxisLeftPad = 36;
    private const int AxisBottomPad = 18;
    private const int Gap = 8; // 面板间距

    private void RebuildVisiblePanels()
    {
        var list = new List<int>();
        if (_hub is null)
        {
            _visiblePanels = list;
            return;
        }

        if (IncludedPanels is { Count: > 0 })
        {
            foreach (var p in IncludedPanels)
                if (p >= 0 && p < GridHub.Panels) list.Add(p);
        }
        else if (FixedPanelCount > 0)
        {
            for (int p = 0; p < Math.Min(FixedPanelCount, GridHub.Panels); p++)
                list.Add(p);
        }
        else if (AutoLayoutByActivity)
        {
            for (int p = 0; p < GridHub.Panels; p++)
            {
                // 认为“有数据”：任一曲线 TotalCount>0
                bool active = false;
                for (int c = 0; c < GridHub.CurvesPerPanel; c++)
                {
                    if (_hub.GetRing(p, c).TotalCount > 0) { active = true; break; }
                }
                if (active) list.Add(p);
            }
            // 若一个都没有（刚启动），至少显示 1 个，避免空白
            if (list.Count == 0) list.Add(0);
        }
        else
        {
            for (int p = 0; p < GridHub.Panels; p++) list.Add(p);
        }

        _visiblePanels = list;
    }

    // 计算“可见索引 vIndex 对应的面板矩形”
    private (Rect rect, int width, int height, int panelId) VisiblePanelRect(int vIndex)
    {
        int count = _visiblePanels.Count;
        if (vIndex < 0 || vIndex >= count) return (new Rect(0, 0, 0, 0), 0, 0, -1);

        var (rows, cols) = ComputeGrid(count, Bounds.Width, Bounds.Height);
        double W = Bounds.Width, H = Bounds.Height;

        const int Gap = 8;
        double cellW = (W - (cols + 1) * Gap) / cols;
        double cellH = (H - (rows + 1) * Gap) / rows;

        int row = vIndex / cols;
        int col = vIndex % cols;

        double x = Gap + col * (cellW + Gap);
        double y = Gap + row * (cellH + Gap);

        int panelId = _visiblePanels[vIndex];
        return (new Rect(x, y, cellW, cellH), (int)cellW, (int)cellH, panelId);
    }

    // 命中测试：返回实际 panelId
    private int HitPanel(Point p)
    {
        for (int i = 0; i < _visiblePanels.Count; i++)
        {
            var (rect, _, _, pid) = VisiblePanelRect(i);
            if (rect.Contains(p)) return pid;
        }
        return -1;
    }

    // 根据可见面板数，生成“近似正方形”的网格（考虑窗口宽高比）
    private static (int rows, int cols) ComputeGrid(int count, double W, double H)
    {
        if (count <= 0) return (0, 0);
        // 基于窗口宽高比做一点权衡：宽屏 → 多列；竖屏 → 多行
        double targetCols = Math.Sqrt(count * (W / Math.Max(1, H)));
        int cols = Math.Clamp((int)Math.Ceiling(targetCols), 1, count);
        int rows = (int)Math.Ceiling(count / (double)cols);
        return (rows, cols);
    }

    public void AttachHub(GridHub hub, double initSeconds = 5.0, double yMin = -1.2, double yMax = 1.2)
    {
        _hub = hub;
        for (int p = 0; p < GridHub.Panels; p++)
        {
            _viewSamples[p] = (int)Math.Max(1, initSeconds * hub.SampleRate);
            _yMin[p] = yMin; _yMax[p] = yMax;
            _followLive[p] = true; // 默认跟随最新
        }
        InvalidateVisual();
    }

    public SkiaMultiGridControl()
    {
        // 限制绘制到自身区域，避免越界导致整窗闪烁
        ClipToBounds = true;
        PointerPressed += (s, e) =>
        {
            _drag = true;
            _lastPt = e.GetPosition(this);
            _activePanel = HitPanel(_lastPt);
            if (_activePanel >= 0)
                _followLive[_activePanel] = false;
            e.Pointer.Capture(this);
        };
        PointerReleased += (s, e) => { _drag = false; e.Pointer.Capture(null); };
        PointerMoved += (s, e) =>
        {
            if (!_drag || _activePanel < 0 || _hub is null) return;
            var p = e.GetPosition(this);
            var (rect, plotW, _) = PanelRects(_activePanel);
            double dx = p.X - _lastPt.X; _lastPt = p;

            double spp = (double)_viewSamples[_activePanel] / Math.Max(1, plotW - AxisLeftPad);
            long dSamples = (long)Math.Round(-dx * spp);
            Pan(_activePanel, dSamples);
        };
        PointerWheelChanged += (s, e) =>
        {
            // 命中哪个面板就缩放哪个
            int panel = HitPanel(e.GetPosition(this));
            if (panel < 0 || _hub is null) return;

            _followLive[panel] = false;
            double factor = e.Delta.Y > 0 ? 0.8 : 1.25; // 上滚放大，下滚缩小
            ZoomX(panel, factor, e.GetPosition(this));
        };
    }

    // 回到最新（单面板/全部）
    public void GoLive(int panel)
    {
        if (_hub is null) return;
        long total = _hub.GetRing(panel, 0).TotalCount; // 任一曲线的总数用于估算末端
        _viewLeft[panel] = Math.Max(0, total - _viewSamples[panel]);
        _followLive[panel] = true;
        InvalidateVisual();
    }
    public void GoLiveAll()
    {
        if (_hub is null) return;
        for (int p = 0; p < GridHub.Panels; p++) GoLive(p);
    }

    // 每帧调用：处于跟随模式的面板钉住末端
    public void LiveFollowAndInvalidate()
    {
        if (_hub is null) return;
        for (int p = 0; p < GridHub.Panels; p++)
        {
            if (_followLive[p])
            {
                long total = _hub.GetRing(p, 0).TotalCount;
                _viewLeft[p] = Math.Max(0, total - _viewSamples[p]);
            }
        }
        InvalidateVisual();
    }

    private void Pan(int panel, long deltaSamples)
    {
        if (_hub is null) return;
        long total = _hub.GetRing(panel, 0).TotalCount;
        long maxLeft = Math.Max(0, total - _viewSamples[panel]);
        _viewLeft[panel] = Math.Clamp(_viewLeft[panel] + deltaSamples, 0, maxLeft);
        InvalidateVisual();
    }

    private void ZoomX(int panel, double factor, Point mouse)
    {
        if (_hub is null) return;
        var (rect, plotW, _) = PanelRects(panel);
        int x0 = (int)(rect.X + AxisLeftPad);
        int w = Math.Max(1, plotW - AxisLeftPad);
        int xInPlot = Math.Clamp((int)mouse.X - x0, 0, w - 1);

        long anchor = _viewLeft[panel] + (long)Math.Round((double)xInPlot / w * _viewSamples[panel]);
        int newView = (int)Math.Clamp(Math.Round(_viewSamples[panel] * factor), _hub.SampleRate * 0.1, _hub.SampleRate * 3600);
        long newLeft = anchor - (long)Math.Round((double)xInPlot / w * newView);

        long total = _hub.GetRing(panel, 0).TotalCount;
        long maxLeft = Math.Max(0, total - newView);
        _viewSamples[panel] = newView;
        _viewLeft[panel] = Math.Clamp(newLeft, 0, maxLeft);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        RebuildVisiblePanels();

        context.Custom(new DrawOp(new Rect(Bounds.Size), this, _hub, _visiblePanels,
                                 _viewLeft, _viewSamples, _yMin, _yMax));

        // context.Custom(new DrawOp(new Rect(Bounds.Size), this, _hub, _viewLeft, _viewSamples, _yMin, _yMax));
    }

    // 计算第 k 个面板的矩形与 plot 区域尺寸
    private (Rect rect, int width, int height) PanelRects(int k)
    {
        int row = k / GridHub.Cols;
        int col = k % GridHub.Cols;
        double W = Bounds.Width, H = Bounds.Height;

        double cellW = (W - (GridHub.Cols + 1) * Gap) / GridHub.Cols;
        double cellH = (H - (GridHub.Rows + 1) * Gap) / GridHub.Rows;
        double x = Gap + col * (cellW + Gap);
        double y = Gap + row * (cellH + Gap);
        return (new Rect(x, y, cellW, cellH), (int)cellW, (int)cellH);
    }

    // ==== 绘制操作 ====
    private sealed class DrawOp : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly SkiaMultiGridControl _owner;
        private readonly GridHub? _hub;
        private readonly List<int> _visible;
        private readonly long[] _viewLeft;
        private readonly int[] _viewSamples;
        private readonly double[] _yMin, _yMax;

        public DrawOp(Rect bounds, SkiaMultiGridControl owner, GridHub? hub, List<int> visible,
                     long[] viewLeft, int[] viewSamples, double[] yMin, double[] yMax)
        {
            _bounds = bounds; _owner = owner; _hub = hub; _visible = visible;
            _viewLeft = viewLeft; _viewSamples = viewSamples; _yMin = yMin; _yMax = yMax;
        }

        public Rect Bounds => _bounds;
        public void Dispose() { }
        public bool HitTest(Point p) => _bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
            using (lease)
            {
                var canvas = lease?.SkCanvas;
                if (canvas is null) return;

                // 仅清除/填充本控件的区域，避免影响外层导航栏等元素
                var boundsRect = new SKRect((float)_bounds.X, (float)_bounds.Y, (float)_bounds.Right, (float)_bounds.Bottom);
                using (var bg = new SKPaint { Color = SKColors.Black })
                {
                    canvas.Save();
                    canvas.ClipRect(boundsRect);
                    canvas.DrawRect(boundsRect, bg);
                }

                for (int vi = 0; vi < _visible.Count; vi++)
                    DrawPanel(canvas, vi);
                canvas.Restore();
            }
        }

        private void DrawPanel(SKCanvas c, int k)
        {
            if (_hub is null) return;

            var (rect, w, h, panelId) = _owner.VisiblePanelRect(k);// 面板背景
            if (panelId < 0) return;

            using var bg = new SKPaint { Color = new SKColor(18, 18, 18) };
            c.DrawRect(new SKRect((float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom), bg);

            // 轴与网格笔
            using var axis = new SKPaint { Color = SKColors.Gray, StrokeWidth = 1f, IsAntialias = true };
            using var grid = new SKPaint { Color = new SKColor(40, 40, 40), StrokeWidth = 1f, IsAntialias = false };
            using var text = new SKPaint { Color = SKColors.Gray, IsAntialias = true, TextSize = 10f };

            int x0 = (int)rect.X + AxisLeftPad;
            int y0 = (int)rect.Bottom - AxisBottomPad;
            int plotW = Math.Max(1, w - AxisLeftPad);
            int plotH = Math.Max(1, h - AxisBottomPad);

            // 坐标轴
            c.DrawLine(x0, (int)rect.Y, x0, y0, axis);
            c.DrawLine(x0, y0, (int)rect.Right, y0, axis);

            // 网格+刻度
            int gx = 4, gy = 3;
            double yrange = Math.Max(1e-9, _yMax[k] - _yMin[k]);
            double t0 = (double)_viewLeft[k] / _hub.SampleRate;
            double dt = (double)_viewSamples[k] / _hub.SampleRate;

            for (int i = 1; i <= gx; i++)
            {
                float x = x0 + i * (plotW / (float)gx);
                c.DrawLine(x, (float)rect.Y, x, y0, grid);
                c.DrawText($"{t0 + dt * i / gx:0.##}s", x - 12, y0 + 12, text);
            }
            for (int j = 1; j <= gy; j++)
            {
                float y = y0 - j * (plotH / (float)gy);
                c.DrawLine(x0, y, (float)rect.Right, y, grid);
                double v = _yMin[k] + yrange * j / gy;
                c.DrawText($"{v:0.##}", (float)rect.X + 2, y + 4, text);
            }

            // 画曲线：为简单演示，这里画“该面板的前 N 条曲线”
            // 若要画满 64 根，直接循环 0..63；颜色可来自调色板
            int curvesToDraw = Math.Min(GridHub.CurvesPerPanel, 64);
            for (int cIdx = 0; cIdx < curvesToDraw; cIdx++)
            {
                var ring = _hub.GetRing(k, cIdx);
                int got = Math.Min(_viewSamples[k], plotW); // 每像素取一个样本（可改 min/max 包络）
                var tmp = new double[got];

                got = ring.Read(_viewLeft[k], got, tmp);
                if (got <= 1)
                {
                    if (cIdx == 0)
                        Console.WriteLine($"Panel {k}, Curve {cIdx}: Not enough data");
                    continue;
                }
                else
                    Console.WriteLine($"Panel {k}, Curve {cIdx}: Got {got} samples");


                using var pen = new SKPaint { Color = CurveColor(cIdx), IsAntialias = false, StrokeWidth = 1f };
                int lastX = -1, lastY = -1;
                for (int px = 0; px < got; px++)
                {
                    double v = tmp[px];
                    int xx = x0 + px;
                    int yy = (int)Math.Round(y0 - (v - _yMin[k]) / yrange * plotH);
                    yy = Math.Clamp(yy, (int)rect.Y, y0 - 1);
                    if (lastX >= 0)
                    {
                        if (Math.Abs(yy - lastY) <= 1) c.DrawPoint(xx, yy, pen);
                        else c.DrawLine(lastX, lastY, xx, yy, pen);
                    }
                    lastX = xx; lastY = yy;
                }
            }
        }

        private static SKColor CurveColor(int i)
        {
            // 简单可区分的循环色表（可替换为固定调色板）
            unchecked
            {
                int r = 80 + (37 * i) % 175;
                int g = 80 + (59 * i) % 175;
                int b = 80 + (83 * i) % 175;
                return new SKColor((byte)r, (byte)g, (byte)b);
            }
        }
    }
}
