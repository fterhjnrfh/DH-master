using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public class SKWaveformDisplay : Control
    {
        private List<(double X, double Y)> _waveformData = new List<(double X, double Y)>();
        private double _amplitude = 25;
        private double _xScale = 1.0;
        private double _yScale = 1.0;
        
        private DisplayControl2ViewModel? _viewModel;

        // 依赖属性
        public static readonly DirectProperty<SKWaveformDisplay, List<(double X, double Y)>> WaveformDataProperty =
            AvaloniaProperty.RegisterDirect<SKWaveformDisplay, List<(double X, double Y)>>(
                nameof(WaveformData),
                o => o.WaveformData,
                (o, v) => o.WaveformData = v);

        public static readonly DirectProperty<SKWaveformDisplay, double> AmplitudeProperty =
            AvaloniaProperty.RegisterDirect<SKWaveformDisplay, double>(
                nameof(Amplitude),
                o => o.Amplitude,
                (o, v) => o.Amplitude = v);

        public List<(double X, double Y)> WaveformData
        {
            get => _waveformData;
            set
            {
                SetAndRaise(WaveformDataProperty, ref _waveformData, value);
                InvalidateVisual();
            }
        }

        public double Amplitude
        {
            get => _amplitude;
            set
            {
                SetAndRaise(AmplitudeProperty, ref _amplitude, value);
                UpdateScales();
                InvalidateVisual();
            }
        }

        public SKWaveformDisplay()
        {
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateScales();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == DataContextProperty)
            {
                if (DataContext is DisplayControl2ViewModel newViewModel)
                {
                    _viewModel = newViewModel;
                    // 绑定数据变化事件
                    if (_viewModel != null)
                        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
                    UpdateFromViewModel();
                }
            }
            else if (change.Property == BoundsProperty)
            {
                UpdateScales();
                InvalidateVisual();
            }
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DisplayControl2ViewModel.WaveformData))
            {
                UpdateFromViewModel();
            }
            else if (e.PropertyName == nameof(DisplayControl2ViewModel.Amplitude))
            {
                Amplitude = _viewModel?.Amplitude ?? 25;
            }
        }

        private void UpdateFromViewModel()
        {
            if (_viewModel != null)
            {
                WaveformData = _viewModel.WaveformData ?? new List<(double X, double Y)>();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            // 绘制黑色背景
            context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

            // 绘制坐标系和网格
            DrawGridAvalonia(context);

            // 绘制波形
            DrawWaveformAvalonia(context);
        }

        private void DrawGridAvalonia(DrawingContext context)
        {
            double width = Bounds.Width;
            double height = Bounds.Height;

            var gridPen = new Pen(Brushes.Gray, 1);
            var axisPen = new Pen(Brushes.White, 2);

            // 绘制垂直网格线 (X轴)
            for (int x = 0; x <= (int)width; x += 50)
            {
                var start = new Point(x, 0);
                var end = new Point(x, height);
                context.DrawLine(gridPen, start, end);
            }

            // 绘制水平网格线 (Y轴)
            for (int y = 0; y <= (int)height; y += 25)
            {
                var start = new Point(0, y);
                var end = new Point(width, y);
                context.DrawLine(gridPen, start, end);
            }

            // 绘制坐标轴
            var xAxisStart = new Point(0, height / 2);
            var xAxisEnd = new Point(width, height / 2);
            context.DrawLine(axisPen, xAxisStart, xAxisEnd); // X轴

            var yAxisStart = new Point(width / 2, 0);
            var yAxisEnd = new Point(width / 2, height);
            context.DrawLine(axisPen, yAxisStart, yAxisEnd); // Y轴

            // 绘制标签
            var textBrush = Brushes.White;
            var textTypeface = new Typeface("Consolas");
            
            // X轴标签
            for (int x = 0; x <= (int)width; x += 100)
            {
                var text = new FormattedText(
                    $"X:{x}",
                    System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    textTypeface,
                    10,
                    textBrush
                );
                context.DrawText(text, new Point(x, height / 2 - 15));
            }

            // Y轴标签
            for (int y = 0; y <= (int)height; y += 50)
            {
                double actualY = height / 2 - y;
                if (actualY >= 0 && actualY <= height)
                {
                    var yValue = y - height / 2;
                    var text = new FormattedText(
                        $"Y:{yValue}",
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        textTypeface,
                        10,
                        textBrush
                    );
                    context.DrawText(text, new Point(width / 2 + 5, actualY - 6));
                }
            }
        }

        private void DrawWaveformAvalonia(DrawingContext context)
        {
            if (WaveformData == null || WaveformData.Count < 2)
                return;

            double width = Bounds.Width;
            double height = Bounds.Height;
            
            // 计算中心点
            double centerX = width / 2;
            double centerY = height / 2;

            // 创建路径来绘制波形
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                bool first = true;
                foreach (var point in WaveformData)
                {
                    // 将数据点转换为屏幕坐标
                    double x = point.X * _xScale + centerX;
                    double y = centerY - point.Y * _yScale; // Y轴翻转

                    if (x >= 0 && x <= width && y >= 0 && y <= height)
                    {
                        if (first)
                        {
                            ctx.BeginFigure(new Point(x, y), false);
                            first = false;
                        }
                        else
                        {
                            ctx.LineTo(new Point(x, y));
                        }
                    }
                }
                if (!first)
                {
                    ctx.EndFigure(false);
                }
            }

            var pen = new Pen(Brushes.LimeGreen, 2);
            context.DrawGeometry(null, pen, geometry);
        }

        private void UpdateScales()
        {
            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                // 计算X和Y轴的缩放因子，确保波形适合显示区域
                _xScale = Math.Max(0.01, Bounds.Width / 1000.0); // 假设X轴最大范围是1000
                _yScale = Math.Max(0.01, (Bounds.Height / 2) / (Amplitude * 1.5)); // Y轴范围是幅值的1.5倍
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
        }
    }
}