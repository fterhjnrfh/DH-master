using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System.Collections.Generic;
using SkiaSharp;
using Avalonia.Skia;
using Avalonia.Platform;

namespace NewAvalonia.Views
{
    public class OpenGLSinWaveControl : Control
    {
        // 波形参数
        public static readonly StyledProperty<double> AmplitudeProperty =
            AvaloniaProperty.Register<OpenGLSinWaveControl, double>(nameof(Amplitude), 25.0);

        public static readonly StyledProperty<double> FrequencyProperty =
            AvaloniaProperty.Register<OpenGLSinWaveControl, double>(nameof(Frequency), 1.0);

        public static readonly StyledProperty<double> SpeedProperty =
            AvaloniaProperty.Register<OpenGLSinWaveControl, double>(nameof(Speed), 1.0);

        public static readonly StyledProperty<int> SelectedColorIndexProperty =
            AvaloniaProperty.Register<OpenGLSinWaveControl, int>(nameof(SelectedColorIndex), 2);

        public static readonly StyledProperty<double> PhaseProperty =
            AvaloniaProperty.Register<OpenGLSinWaveControl, double>(nameof(Phase), 0.0);

        private readonly DispatcherTimer _timer;
        private double _phase = 0;

        public double Phase
        {
            get => _phase;
            set
            {
                if (Math.Abs(_phase - value) > double.Epsilon)
                {
                    _phase = value;
                    SetValue(PhaseProperty, value);
                    InvalidateVisual();
                }
            }
        }

        public double Amplitude
        {
            get => GetValue(AmplitudeProperty);
            set => SetValue(AmplitudeProperty, Math.Max(1, value)); // 确保振幅至少为1
        }

        public double Frequency
        {
            get => GetValue(FrequencyProperty);
            set => SetValue(FrequencyProperty, Math.Max(0.1, value)); // 确保频率至少为0.1
        }

        public double Speed
        {
            get => GetValue(SpeedProperty);
            set 
            {
                if (value > 0)
                {
                    SetValue(SpeedProperty, value);
                    _timer.Interval = TimeSpan.FromMilliseconds(30 / value); // 调整动画速度
                }
            }
        }

        public int SelectedColorIndex
        {
            get => GetValue(SelectedColorIndexProperty);
            set => SetValue(SelectedColorIndexProperty, value);
        }

        public OpenGLSinWaveControl()
        {
            // 初始化定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += OnTimerTick;
            _timer.Start();

            // 监听属性变化
            PropertyChanged += OnPropertyChanged;
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _phase += 0.2 * Speed; // 与OxyPlot正弦波控件一样的相位步长
            SetValue(PhaseProperty, _phase); // 通过属性系统更新Phase
            InvalidateVisual(); // 请求重绘
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == AmplitudeProperty || 
                e.Property == FrequencyProperty || 
                e.Property == SpeedProperty || 
                e.Property == SelectedColorIndexProperty ||
                e.Property == PhaseProperty)
            {
                // 请求重绘
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            try 
            {
                // 使用Avalonia的SkiaSharp渲染
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    bool first = true;
                    // 使用更密集的点以获得平滑的曲线
                    for (double x = 0; x <= Bounds.Width; x += 0.5)
                    {
                        // 计算Y值 - 使用与OxyPlot正弦波控件相同的公式
                        double y = Amplitude * Math.Sin(_phase + x * Frequency * 2 * Math.PI / 100.0);
                        
                        // 转换为屏幕坐标
                        var screenPoint = DataToScreenPoint(new Point(x, y));
                        var screenX = screenPoint.X; // 获取转换后的屏幕X坐标
                        if (screenX >= 0 && screenX <= Bounds.Width) // 确保点在可见范围内
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

                // 获取画笔颜色
                var strokeBrush = GetColorByIndex(SelectedColorIndex);
                
                // 绘制波形
                var pen = new Pen(strokeBrush, 2);
                context.DrawGeometry(null, pen, geometry);
            }
            catch
            {
                // 原始错误的绘制方法作为后备
                base.Render(context);

                // 绘制黑色背景
                context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

                // 绘制坐标轴
                DrawAxes(context);

                // 绘制波形
                DrawWaveform(context);
                
                // 绘制标题和标签
                DrawLabels(context);
            }
        }

        private void DrawAxes(DrawingContext context)
        {
            // 绘制X轴
            var xAxisStart = new Point(0, Bounds.Height / 2);
            var xAxisEnd = new Point(Bounds.Width, Bounds.Height / 2);
            context.DrawLine(new Pen(Brushes.White, 1), xAxisStart, xAxisEnd);

            // 绘制Y轴
            var yAxisStart = new Point(Bounds.Width / 2, 0);
            var yAxisEnd = new Point(Bounds.Width / 2, Bounds.Height);
            context.DrawLine(new Pen(Brushes.White, 1), yAxisStart, yAxisEnd);

            // 绘制网格线
            DrawGridLines(context);
        }

        private void DrawGridLines(DrawingContext context)
        {
            var gridPen = new Pen(new SolidColorBrush(new Color(255, 85, 85, 85)), 0.5);

            // 绘制垂直网格线（X轴方向）
            for (int x = 0; x <= Bounds.Width; x += 40)
            {
                var gridStart = new Point(x, 0);
                var gridEnd = new Point(x, Bounds.Height);
                context.DrawLine(gridPen, gridStart, gridEnd);
            }

            // 绘制水平网格线（Y轴方向）
            for (int y = 0; y <= Bounds.Height; y += 40)
            {
                var gridStart = new Point(0, y);
                var gridEnd = new Point(Bounds.Width, y);
                context.DrawLine(gridPen, gridStart, gridEnd);
            }
        }

        private void DrawWaveform(DrawingContext context)
        {
            // 生成波形数据点
            var points = GenerateWaveformData(Bounds.Width, Bounds.Height);

            if (points.Count < 2)
                return;

            // 获取画笔颜色
            var strokeBrush = GetColorByIndex(SelectedColorIndex);

            // 创建路径几何
            var pathGeometry = new StreamGeometry();
            using (var ctx = pathGeometry.Open())
            {
                bool first = true;
                foreach (var point in points)
                {
                    var screenPoint = DataToScreenPoint(point);
                    if (screenPoint.X >= 0 && screenPoint.X <= Bounds.Width) // 只绘制在可见范围内的点
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

            // 绘制波形
            context.DrawGeometry(null, new Pen(strokeBrush, 2), pathGeometry);
        }

        private List<Point> GenerateWaveformData(double width, double height)
        {
            var points = new List<Point>();

            // 生成高密度的波形数据点，与OxyPlot正弦波控件保持一致
            for (double x = 0; x <= width; x += 0.5)
            {
                // 修正频率计算：频率应该影响波长，而不是直接乘以x
                double y = Amplitude * Math.Sin(_phase + x * Frequency * 2 * Math.PI / 100.0);
                points.Add(new Point(x, y));
            }

            return points;
        }

        private Point DataToScreenPoint(Point dataPoint)
        {
            // 将数据坐标转换为屏幕坐标
            // 数据的Y值映射到Canvas坐标系，0点在中间
            double screenY = Bounds.Height / 2 - dataPoint.Y;
            return new Point(dataPoint.X, screenY);
        }

        private IBrush GetColorByIndex(int index)
        {
            var color = index switch
            {
                0 => Colors.Red,      // 红色
                1 => Colors.Green,    // 绿色
                2 => Colors.Blue,     // 蓝色
                3 => Colors.Yellow,   // 黄色
                4 => Colors.Magenta,  // 紫色
                5 => Colors.Cyan,     // 青色
                6 => Colors.White,    // 白色
                _ => Colors.Blue      // 默认蓝色
            };

            return new SolidColorBrush(color);
        }

        private void DrawLabels(DrawingContext context)
        {
            // 绘制标题
            var titleBrush = new SolidColorBrush(Colors.White);
            var titleTypeface = new Typeface("Consolas");
            var titleText = new FormattedText(
                "正弦波形显示",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                titleTypeface,
                14,
                titleBrush
            );
            context.DrawText(titleText, new Point(Bounds.Width / 2 - titleText.Width / 2, 5));

            // 绘制X轴标签
            var xLabelBrush = new SolidColorBrush(Colors.White);
            var xLabelTypeface = new Typeface("Consolas");
            var xLabelText = new FormattedText(
                "距离 (x)",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                xLabelTypeface,
                12,
                xLabelBrush
            );
            context.DrawText(xLabelText, new Point(Bounds.Width - xLabelText.Width - 10, Bounds.Height - 25));

            // 绘制Y轴标签 - draw normally first
            var yLabelBrush = new SolidColorBrush(Colors.White);
            var yLabelTypeface = new Typeface("Consolas");
            var yLabelText = new FormattedText(
                "幅值 (A)",
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                yLabelTypeface,
                12,
                yLabelBrush
            );
            // Calculate the rotated text position - centering it vertically
            var centerY = Bounds.Height / 2;
            var offsetX = 10 + yLabelText.Height / 2;  // Adjust for rotated text dimensions
            
            // Create a rotation transformation for -90 degrees
            var radians = -Math.PI / 2; // -90 degrees in radians
            
            // Create the rotation transformation around the appropriate point
            var transform = Matrix.CreateRotation(radians);
            var translation = Matrix.CreateTranslation(new Vector(offsetX, centerY));
            var finalTransform = transform * translation;
            
            // Apply the transformation and draw the text
            using (context.PushTransform(finalTransform))
            {
                context.DrawText(yLabelText, new Point(0, 0));
            }
        }

        private void RenderWithSkiaSharp(SkiaSharp.SKCanvas canvas)
        {
            var bounds = Bounds;

            // 清除画布并绘制黑色背景
            canvas.Clear(SkiaSharp.SKColors.Black);

            // 绘制坐标轴
            DrawAxes(canvas, bounds);

            // 绘制波形
            DrawWaveform(canvas, bounds);
            
            // 绘制标题和标签
            DrawLabels(canvas, bounds);
        }

        private void DrawAxes(SkiaSharp.SKCanvas canvas, Rect bounds)
        {
            // 绘制X轴
            using (var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, StrokeWidth = 1 })
            {
                canvas.DrawLine(0, (float)(bounds.Height / 2), (float)bounds.Width, (float)(bounds.Height / 2), paint);
            }

            // 绘制Y轴
            using (var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, StrokeWidth = 1 })
            {
                canvas.DrawLine((float)(bounds.Width / 2), 0, (float)(bounds.Width / 2), (float)bounds.Height, paint);
            }

            // 绘制网格线
            DrawGridLines(canvas, bounds);
        }

        private void DrawGridLines(SkiaSharp.SKCanvas canvas, Rect bounds)
        {
            using (var gridPaint = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(85, 85, 85), StrokeWidth = 0.5f })
            {
                // 绘制垂直网格线（X轴方向）
                for (int x = 0; x <= bounds.Width; x += 40)
                {
                    canvas.DrawLine(x, 0, x, (float)bounds.Height, gridPaint);
                }

                // 绘制水平网格线（Y轴方向）
                for (int y = 0; y <= bounds.Height; y += 40)
                {
                    canvas.DrawLine(0, y, (float)bounds.Width, y, gridPaint);
                }
            }
        }

        private void DrawWaveform(SkiaSharp.SKCanvas canvas, Rect bounds)
        {
            // 获取波形颜色
            var strokeColor = GetSKColorByIndex(SelectedColorIndex);

            using (var paint = new SkiaSharp.SKPaint 
            { 
                Color = strokeColor, 
                StrokeWidth = 2, 
                IsStroke = true, 
                IsAntialias = true,
                StrokeCap = SkiaSharp.SKStrokeCap.Round
            })
            {
                // 生成波形路径
                var path = new SkiaSharp.SKPath();
                
                bool first = true;
                // 使用更密集的点以获得平滑的曲线
                for (double x = 0; x <= bounds.Width; x += 0.5)
                {
                    // 计算Y值 - 使用与OxyPlot正弦波控件相同的公式
                    double y = Amplitude * Math.Sin(_phase + x * Frequency * 2 * Math.PI / 100.0);
                    
                    // 转换为屏幕坐标
                    var screenPoint = DataToScreenPoint(new Point(x, y), bounds);
                    
                    if (screenPoint.X >= 0 && screenPoint.X <= bounds.Width) // 确保点在可见范围内
                    {
                        if (first)
                        {
                            path.MoveTo((float)screenPoint.X, (float)screenPoint.Y);
                            first = false;
                        }
                        else
                        {
                            path.LineTo((float)screenPoint.X, (float)screenPoint.Y);
                        }
                    }
                }

                // 绘制路径
                canvas.DrawPath(path, paint);

                // 释放路径资源
                path.Dispose();
            }
        }

        private SkiaSharp.SKColor GetSKColorByIndex(int index)
        {
            return index switch
            {
                0 => SkiaSharp.SKColors.Red,      // 红色
                1 => SkiaSharp.SKColors.Green,    // 绿色
                2 => SkiaSharp.SKColors.Blue,     // 蓝色
                3 => SkiaSharp.SKColors.Yellow,   // 黄色
                4 => SkiaSharp.SKColors.Magenta,  // 紫色
                5 => SkiaSharp.SKColors.Cyan,     // 青色
                6 => SkiaSharp.SKColors.White,    // 白色
                _ => SkiaSharp.SKColors.Blue      // 默认蓝色
            };
        }

        private Point DataToScreenPoint(Point dataPoint, Rect bounds)
        {
            // 将数据坐标转换为屏幕坐标
            double screenX = dataPoint.X;
            // 将数据的Y值映射到Canvas坐标系，0点在中间
            double screenY = bounds.Height / 2 - dataPoint.Y;

            return new Point(screenX, screenY);
        }

        private void DrawLabels(SkiaSharp.SKCanvas canvas, Rect bounds)
        {
            // 绘制标题
            using var titlePaint = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.White,
                IsAntialias = true,
                TextSize = 14
            };
            var titleText = "正弦波形显示";
            var titleBounds = new SkiaSharp.SKRect();
            titlePaint.MeasureText(titleText, ref titleBounds);
            var titleX = (float)(bounds.Width / 2 - titleBounds.Width / 2);
            canvas.DrawText(titleText, titleX, 15, titlePaint);

            // 绘制X轴标签
            using var labelPaint = new SkiaSharp.SKPaint
            {
                Color = SkiaSharp.SKColors.White,
                IsAntialias = true,
                TextSize = 12
            };
            var xLabelText = "距离 (x)";
            var xLabelBounds = new SkiaSharp.SKRect();
            labelPaint.MeasureText(xLabelText, ref xLabelBounds);
            canvas.DrawText(xLabelText, (float)(bounds.Width - xLabelBounds.Width - 10), (float)(bounds.Height - 10), labelPaint);

            // 绘制Y轴标签
            var yLabelText = "幅值 (A)";
            canvas.Save();
            canvas.RotateDegrees(-90, 10, (float)(bounds.Height / 2));
            canvas.DrawText(yLabelText, 10, (float)(bounds.Height / 2), labelPaint);
            canvas.Restore();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            InvalidateVisual();
        }

        // 重写OnDetachedFromVisualTree以清理资源
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
        }
    }
}