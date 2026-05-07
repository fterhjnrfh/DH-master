using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Reflection;
using NewAvalonia.ViewModels;
using Avalonia.Platform;
using Avalonia.Rendering;

namespace NewAvalonia.Views
{
    public class SKSimulatedSignalCanvas : Control
    {
        private readonly DispatcherTimer _renderTimer;
        private object? _viewModel;
        
        public SKSimulatedSignalCanvas()
        {
            Console.WriteLine("SKSimulatedSignalCanvas constructor called");
            // 创建并启动渲染定时器
            _renderTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // 每50ms更新一次，保持动画流畅度
            };
            _renderTimer.Tick += OnRenderTick;
            _renderTimer.Start();
        }

        private void OnRenderTick(object? sender, EventArgs e)
        {
            Console.WriteLine("OnRenderTick called");
            // 每次定时器触发时，重绘控件
            InvalidateVisual();
        }
        
        public void SetViewModel(object viewModel)
        {
            _viewModel = viewModel;
        }

        // 重写Render方法以使用Avalonia原生绘图
        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Console.WriteLine($"Render called. ViewModel is {( _viewModel == null ? "null" : "not null" )}, Bounds: {Bounds}");

            if (_viewModel == null)
                return;

            // 回退到Avalonia绘图API绘制波形
            DrawWaveformWithAvalonia(context, new Rect(Bounds.Size));
        }

        private void DrawWaveformWithAvalonia(DrawingContext context, Rect bounds)
        {
            Console.WriteLine($"DrawWaveformWithAvalonia called. Bounds: {bounds}");

            // 绘制黑色背景
            context.FillRectangle(Brushes.Black, bounds);
            
            // 绘制坐标轴
            DrawAxesAvalonia(context, bounds);
            
            // 生成信号数据点 (这里需要根据实际ViewModel获取数据)
            var originalPoints = GenerateOriginalSignal(bounds, _viewModel);
            var processedPoints = GenerateProcessedSignal(bounds, _viewModel);
            
            Console.WriteLine($"Generated points - Original: {originalPoints.Count}, Processed: {processedPoints.Count}");

            // 绘制原始信号（红色）
            DrawSignalAvalonia(context, originalPoints, Brushes.Red, bounds);
            
            // 绘制处理后信号（青色）
            DrawSignalAvalonia(context, processedPoints, Brushes.Cyan, bounds);
            
            // 绘制图例
            DrawLegendAvalonia(context, bounds);
        }

        private void DrawAxesAvalonia(DrawingContext context, Rect bounds)
        {
            // 绘制X轴
            context.DrawLine(new Pen(Brushes.White, 1), new Point(0, bounds.Height / 2), new Point(bounds.Width, bounds.Height / 2));

            // 绘制Y轴
            context.DrawLine(new Pen(Brushes.White, 1), new Point(bounds.Width / 2, 0), new Point(bounds.Width / 2, bounds.Height));

            // 绘制网格线
            DrawGridLinesAvalonia(context, bounds);
        }

        private void DrawGridLinesAvalonia(DrawingContext context, Rect bounds)
        {
            // 绘制垂直网格线（X轴方向）
            for (int x = 0; x <= bounds.Width; x += 40)
            {
                var gridPen = new Pen(Brushes.Gray, 0.5);
                context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
            }

            // 绘制水平网格线（Y轴方向）
            for (int y = 0; y <= bounds.Height; y += 40)
            {
                var gridPen = new Pen(Brushes.Gray, 0.5);
                context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        private void DrawLegendAvalonia(Avalonia.Media.DrawingContext context, Rect bounds)
        {
            // 绘制图例背景
            var legendRect = new Rect(bounds.Width - 150, 10, 140, 45);
            context.FillRectangle(new SolidColorBrush(Color.Parse("#323232")), legendRect);
            context.DrawRectangle(new Pen(Brushes.Gray, 1), legendRect);

            // 绘制原始信号图例 - 红色线
            var originalLegendLine = new Pen(Brushes.Red, 2);
            context.DrawLine(originalLegendLine, new Point(bounds.Width - 140, 25), new Point(bounds.Width - 100, 25));

            // 绘制处理后信号图例 - 青色线
            var processedLegendLine = new Pen(Brushes.Cyan, 2);
            context.DrawLine(processedLegendLine, new Point(bounds.Width - 140, 42), new Point(bounds.Width - 100, 42));
        }

        private void DrawSignalAvalonia(Avalonia.Media.DrawingContext context, List<(double x, double y)> points, IBrush brush, Rect bounds)
        {
            if (points.Count == 0) return;

            // 创建路径几何
            var pathGeometry = new StreamGeometry();
            using (var ctx = pathGeometry.Open())
            {
                bool first = true;
                foreach (var (x, y) in points)
                {
                    // 将数据坐标转换为屏幕坐标
                    var screenX = x;
                    var screenY = bounds.Height / 2 - (y) * (bounds.Height / 2); // 根据实际信号范围调整映射
                    
                    var screenPoint = new Point(screenX, screenY);
                    if (screenX >= 0 && screenX <= bounds.Width) // 只绘制在可见范围内的点
                    {
                        if (first)
                        {
                            ctx.BeginFigure(screenPoint, false);
                            first = false;
                        }
                        else
                        {
                            ctx.LineTo(screenPoint);
                        }
                    }
                }
                if (!first)
                {
                    ctx.EndFigure(false);
                }
            }

            // 绘制路径
            var pen = new Pen(brush, 1.5);
            context.DrawGeometry(null, pen, pathGeometry);
        }

        private List<(double x, double y)> GenerateOriginalSignal(Rect bounds, object? viewModelObj)
        {
            Console.WriteLine($"GenerateOriginalSignal called. Bounds: {bounds}, ViewModel is {( viewModelObj == null ? "null" : "not null" )}");

            if (viewModelObj == null) return new List<(double x, double y)>();

            var points = new List<(double x, double y)>();
            
            // 使用反射获取ViewModel中的时间偏移值和其他参数
            var vmType = viewModelObj.GetType();
            double timeOffset = 0;
            var timeField = vmType.GetField("_timeOffset", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (timeField?.GetValue(viewModelObj) is double offset)
            {
                timeOffset = offset;
            }

            // 获取其他参数
            double peakPosition = 50; // 默认值
            double peakHeight = 1.0;
            double peakWidth = 10;
            double noiseLevel = 0.1;
            double baselineDrift = 0.2;
            
            var peakPositionProp = vmType.GetProperty("PeakPosition");
            var peakHeightProp = vmType.GetProperty("PeakHeight");
            var peakWidthProp = vmType.GetProperty("PeakWidth");
            var noiseLevelProp = vmType.GetProperty("NoiseLevel");
            var baselineDriftProp = vmType.GetProperty("BaselineDrift");
            
            if (peakPositionProp?.GetValue(viewModelObj) is double pos) peakPosition = pos;
            if (peakHeightProp?.GetValue(viewModelObj) is double height) peakHeight = height;
            if (peakWidthProp?.GetValue(viewModelObj) is double width) peakWidth = width;
            if (noiseLevelProp?.GetValue(viewModelObj) is double noise) noiseLevel = noise;
            if (baselineDriftProp?.GetValue(viewModelObj) is double drift) baselineDrift = drift;

            Console.WriteLine($"Parameters - TimeOffset: {timeOffset}, PeakPos: {peakPosition}, PeakHeight: {peakHeight}, PeakWidth: {peakWidth}, Noise: {noiseLevel}, Baseline: {baselineDrift}");

            // 生成信号数据点
            for (double x = 0; x <= bounds.Width; x += 1.0) // 更密集的点以获得更平滑的曲线
            {
                double y = 0;

                // 将当前X位置映射到时间轴上
                double timePosition = (x * 200 / bounds.Width) + timeOffset; // 将屏幕宽度映射到200的信号范围

                // 添加高斯峰
                double peakCycle = 80; // 峰的周期
                double localTime = timePosition % peakCycle;
                double peakDistance = Math.Abs(localTime - peakPosition);
                y += peakHeight * Math.Exp(-Math.Pow(peakDistance / peakWidth, 2));

                // 添加基线漂移
                y += baselineDrift * Math.Sin(timePosition * 0.02);

                // 添加粗糙的随机噪声（模拟未处理的原始信号）
                var random = new Random((int)(timePosition * 7) % 10000); // 基于位置的随机种子
                y += noiseLevel * (random.NextDouble() - 0.5) * 2; // 主要噪声
                
                // 添加额外的粗糙度
                random = new Random((int)(timePosition * 13) % 10000);
                y += noiseLevel * (random.NextDouble() - 0.5) * 1.5; // 次要噪声
                
                // 添加高频抖动（模拟电子噪声）
                random = new Random((int)(timePosition * 23) % 10000);
                y += noiseLevel * (random.NextDouble() - 0.5) * 0.8; // 高频抖动

                // 添加偶发的尖峰噪声（模拟干扰）
                if (random.NextDouble() < 0.02) // 2%的概率出现尖峰
                {
                    y += (random.NextDouble() - 0.5) * peakHeight * 0.5;
                }

                // 添加背景信号变化（保持一些确定性成分）
                y += 0.05 * Math.Sin(timePosition * 0.1);

                points.Add((x, y));
            }

            Console.WriteLine($"Generated {points.Count} points");
            return points;
        }

        private List<(double x, double y)> GenerateProcessedSignal(Rect bounds, object? viewModelObj)
        {
            if (viewModelObj == null) return new List<(double x, double y)>();
            
            var originalPoints = GenerateOriginalSignal(bounds, viewModelObj);
            var processedPoints = new List<(double x, double y)>(originalPoints);

            // 这里需要实现根据算法处理信号的逻辑
            // 由于缺乏完整的算法处理器，我们暂时返回原始数据
            return processedPoints;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            
            // 不需要特殊的GPU初始化，Avalonia的Skia后端会自动处理
        }

        // 重写OnDetachedFromVisualTree以清理资源
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _renderTimer.Stop();
            _renderTimer.Tick -= OnRenderTick;
        }
    }
}