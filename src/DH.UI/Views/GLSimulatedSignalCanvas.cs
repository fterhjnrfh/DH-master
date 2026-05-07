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
    public class GLSimulatedSignalCanvas : Control
    {
        private readonly DispatcherTimer _timer;

        // 当前使用的是来自 ViewModel 的处理后信号数据
        private List<(double x, double y)> _processedSignalData = new();
        private List<(double x, double y)> _originalSignalData = new();
        
        // 为兼容性保留参数属性
        public static readonly StyledProperty<double> PeakPositionProperty =
            AvaloniaProperty.Register<GLSimulatedSignalCanvas, double>(nameof(PeakPosition), 50);
        
        public static readonly StyledProperty<double> PeakHeightProperty =
            AvaloniaProperty.Register<GLSimulatedSignalCanvas, double>(nameof(PeakHeight), 1.0);
        
        public static readonly StyledProperty<double> PeakWidthProperty =
            AvaloniaProperty.Register<GLSimulatedSignalCanvas, double>(nameof(PeakWidth), 10);
        
        public static readonly StyledProperty<double> NoiseLevelProperty =
            AvaloniaProperty.Register<GLSimulatedSignalCanvas, double>(nameof(NoiseLevel), 0.1);
        
        public static readonly StyledProperty<double> BaselineDriftProperty =
            AvaloniaProperty.Register<GLSimulatedSignalCanvas, double>(nameof(BaselineDrift), 0.2);

        // 用于接收 ViewModel
        public GLSimulatedSignalViewModel? ViewModel { get; set; }

        public double PeakPosition
        {
            get => GetValue(PeakPositionProperty);
            set => SetValue(PeakPositionProperty, value);
        }

        public double PeakHeight
        {
            get => GetValue(PeakHeightProperty);
            set => SetValue(PeakHeightProperty, value);
        }

        public double PeakWidth
        {
            get => GetValue(PeakWidthProperty);
            set => SetValue(PeakWidthProperty, value);
        }

        public double NoiseLevel
        {
            get => GetValue(NoiseLevelProperty);
            set => SetValue(NoiseLevelProperty, value);
        }

        public double BaselineDrift
        {
            get => GetValue(BaselineDriftProperty);
            set => SetValue(BaselineDriftProperty, value);
        }

        public GLSimulatedSignalCanvas()
        {
            // 初始化定时器用于更新信号数据
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // 每50ms更新一次
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            // 从 ViewModel 更新信号数据
            if (DataContext is GLSimulatedSignalViewModel vm)
            {
                _processedSignalData = vm.SignalData;
                _originalSignalData = vm.OriginalSignalData;
                
                // 添加调试信息
                System.Diagnostics.Debug.WriteLine($"定时器更新: 原始信号点数 = {_originalSignalData.Count}, 处理后信号点数 = {_processedSignalData.Count}");
            }
            
            // 请求重绘（使用InvalidateVisual替代OpenGL方法）
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // 从 ViewModel 更新信号数据（确保使用最新数据）
            if (DataContext is GLSimulatedSignalViewModel vm)
            {
                _processedSignalData = vm.SignalData;
                _originalSignalData = vm.OriginalSignalData;
                
                // 添加调试信息
                System.Diagnostics.Debug.WriteLine($"Render 调用: 画布大小 = ({Bounds.Width}, {Bounds.Height}), 原始信号点数 = {_originalSignalData.Count}, 处理后信号点数 = {_processedSignalData.Count}");
            }

            // 绘制背景
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            // 绘制坐标轴
            DrawAxesAvalonia(context, new Rect(Bounds.Size));

            // 从ViewModel获取信号数据并绘制
            if (_originalSignalData.Count > 1)
            {
                DrawSignalAvalonia(context, _originalSignalData, Brushes.Red, new Rect(Bounds.Size));
            }

            if (_processedSignalData.Count > 1)
            {
                DrawSignalAvalonia(context, _processedSignalData, Brushes.Cyan, new Rect(Bounds.Size));
            }

            // 绘制标签 - 暂时留空以避免可能的错误
        }

        private void DrawAxesAvalonia(DrawingContext context, Rect bounds)
        {
            // 绘制X轴
            var xAxisPen = new Pen(Brushes.White, 1);
            context.DrawLine(xAxisPen, new Point(0, bounds.Height / 2), new Point(bounds.Width, bounds.Height / 2));

            // 绘制Y轴
            var yAxisPen = new Pen(Brushes.White, 1);
            context.DrawLine(yAxisPen, new Point(bounds.Width / 2, 0), new Point(bounds.Width / 2, bounds.Height));

            // 绘制网格线
            DrawGridLinesAvalonia(context, bounds);
        }

        private void DrawGridLinesAvalonia(DrawingContext context, Rect bounds)
        {
            var gridPen = new Pen(Brushes.Gray, 0.5);
            
            // 绘制垂直网格线（X轴方向）
            for (int x = 0; x <= bounds.Width; x += 40)
            {
                context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
            }

            // 绘制水平网格线（Y轴方向）
            for (int y = 0; y <= bounds.Height; y += 40)
            {
                context.DrawLine(gridPen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        private void DrawSignalAvalonia(DrawingContext context, List<(double x, double y)> dataPoints, IBrush brush, Rect bounds)
        {
            if (dataPoints.Count < 2) return;

            var pen = new Pen(brush, 1.5) { LineCap = PenLineCap.Round };
            
            // 创建路径几何
            var pathGeometry = new StreamGeometry();
            using (var ctx = pathGeometry.Open())
            {
                bool first = true;
                foreach (var point in dataPoints)
                {
                    var screenPoint = DataToScreenPointAvalonia(point, bounds);
                    
                    if (screenPoint.X >= 0 && screenPoint.X <= bounds.Width) // 只绘制在可见范围内的点
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
                if (!first) // 确保至少有一个点
                {
                    ctx.EndFigure(false);
                }
            }

            // 绘制路径
            context.DrawGeometry(null, pen, pathGeometry);
        }

        private Point DataToScreenPointAvalonia((double x, double y) dataPoint, Rect bounds)
        {
            // 将数据坐标转换为屏幕坐标
            // X轴：0-200 映射到 0-width
            double screenX = (dataPoint.x / 200.0) * bounds.Width;
            
            // Y轴：将数据的Y值映射到Canvas坐标系，0点在中间
            // 假设Y值范围为 -0.5 到 1.5 (根据原始代码的图表范围)
            double valueRange = 2.0; // -0.5 到 1.5 的范围
            double normalizedY = (dataPoint.y - 0.5) / valueRange; // 将值中心化到 0，然后标准化到 [-0.5, 0.5]
            double screenY = bounds.Height / 2 - (normalizedY * bounds.Height); // 映射到屏幕高度
            
            return new Point(screenX, screenY);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == PeakPositionProperty ||
                e.Property == PeakHeightProperty ||
                e.Property == PeakWidthProperty ||
                e.Property == NoiseLevelProperty ||
                e.Property == BaselineDriftProperty)
            {
                // 如果直接修改参数属性（用于兼容旧代码），也更新数据
                InvalidateVisual();
            }
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            
            if (DataContext is GLSimulatedSignalViewModel vm)
            {
                ViewModel = vm;
                
                // 监听 ViewModel 属性变化
                vm.PropertyChanged += OnViewModelPropertyChanged;
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GLSimulatedSignalViewModel.SignalData) || 
                e.PropertyName == nameof(GLSimulatedSignalViewModel.OriginalSignalData))
            {
                // 当信号数据变化时更新本地副本
                if (DataContext is GLSimulatedSignalViewModel vm)
                {
                    _processedSignalData = vm.SignalData;
                    _originalSignalData = vm.OriginalSignalData;
                }
                
                InvalidateVisual();
            }
        }

        // 重写OnDetachedFromVisualTree以清理资源
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // 停止定时器
            _timer.Stop();
            _timer.Tick -= OnTimerTick;

            if (DataContext is GLSimulatedSignalViewModel vm)
            {
                vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }
    }
}