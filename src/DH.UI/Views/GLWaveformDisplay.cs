using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public class GLWaveformDisplay : OpenGlControlBase
    {
        private SKPaint _paint;
        private List<(double X, double Y)> _waveformData = new List<(double X, double Y)>();
        private double _amplitude = 25;
        private double _xScale = 1.0;
        private double _yScale = 1.0;
        
        private DisplayControl2ViewModel? _viewModel;

        // 依赖属性
        public static readonly DirectProperty<GLWaveformDisplay, List<(double X, double Y)>> WaveformDataProperty =
            AvaloniaProperty.RegisterDirect<GLWaveformDisplay, List<(double X, double Y)>>(
                nameof(WaveformData),
                o => o.WaveformData,
                (o, v) => o.WaveformData = v);

        public static readonly DirectProperty<GLWaveformDisplay, double> AmplitudeProperty =
            AvaloniaProperty.RegisterDirect<GLWaveformDisplay, double>(
                nameof(Amplitude),
                o => o.Amplitude,
                (o, v) => o.Amplitude = v);

        public List<(double X, double Y)> WaveformData
        {
            get => _waveformData;
            set
            {
                SetAndRaise(WaveformDataProperty, ref _waveformData, value);
                RequestRender();
            }
        }

        public double Amplitude
        {
            get => _amplitude;
            set
            {
                SetAndRaise(AmplitudeProperty, ref _amplitude, value);
                UpdateScales();
                RequestRender();
            }
        }

        public GLWaveformDisplay()
        {
            _paint = new SKPaint
            {
                Color = SKColors.LimeGreen,
                StrokeWidth = 2,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };
        }

        private bool _initFailed = false;

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                // 先进行基础初始化
                base.OnOpenGlInit(gl);
                
                _initFailed = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenGL初始化失败: {ex.Message}");
                _initFailed = true;
            }
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                // 总是尝试清屏，无论初始化状态
                gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);  // 黑色背景
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

                // 设置视口
                gl.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);

                // 检查OpenGL功能是否可用
                if (_initFailed)
                {
                    // 如果初始化失败，尝试使用基本的绘制
                    using var fallbackSurface = SKSurface.Create(new SKImageInfo((int)Bounds.Width, (int)Bounds.Height));
                    var canvas = fallbackSurface.Canvas;
                    
                    // 绘制黑色背景
                    canvas.Clear(SKColors.Black);
                    
                    // 绘制简单的坐标轴和网格
                    using var axisPaint = new SKPaint
                    {
                        Color = SKColors.White,
                        StrokeWidth = 2,
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke
                    };
                    
                    float width = (float)Bounds.Width;
                    float height = (float)Bounds.Height;
                    
                    // X轴
                    canvas.DrawLine(0, height / 2, width, height / 2, axisPaint);
                    // Y轴
                    canvas.DrawLine(width / 2, 0, width / 2, height, axisPaint);
                    
                    fallbackSurface.Canvas.Flush();
                }
                else
                {
                    // 使用SkiaSharp进行绘制（OpenGL + SkiaSharp混合）
                    using var surface = SKSurface.Create(new SKImageInfo((int)Bounds.Width, (int)Bounds.Height));
                    
                    // 绘制坐标系和网格
                    DrawGrid(surface.Canvas);
                    
                    // 绘制波形（只有在有数据时）
                    if (WaveformData != null && WaveformData.Count >= 2)
                    {
                        DrawWaveform(surface.Canvas);
                    }
                    
                    surface.Canvas.Flush();
                }
            }
            catch (Exception ex)
            {
                // 记录错误但尝试保持显示
                System.Diagnostics.Debug.WriteLine($"OpenGL渲染失败: {ex.Message}");
                try
                {
                    // 尝试清屏以保持黑色背景
                    gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
                    gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                }
                catch { }
            }
        }

        private void DrawGrid(SKCanvas canvas)
        {
            var info = canvas.DeviceClipBounds;
            float width = info.Width;
            float height = info.Height;

            var gridPaint = new SKPaint
            {
                Color = SKColors.Gray,
                StrokeWidth = 1,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            var axisPaint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 2,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 绘制垂直网格线 (X轴)
            for (int x = 0; x <= (int)width; x += 50)
            {
                canvas.DrawLine(x, 0, x, height, gridPaint);
            }

            // 绘制水平网格线 (Y轴)
            for (int y = 0; y <= (int)height; y += 25)
            {
                canvas.DrawLine(0, y, width, y, gridPaint);
            }

            // 绘制坐标轴
            canvas.DrawLine(0, height / 2, width, height / 2, axisPaint); // X轴
            canvas.DrawLine(width / 2, 0, width / 2, height, axisPaint); // Y轴

            // 绘制标签
            using var textPaint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 1,
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                TextSize = 12
            };

            // X轴标签
            for (int x = 0; x <= (int)width; x += 100)
            {
                canvas.DrawText($"X:{x}", x, height / 2 - 5, textPaint);
            }

            // Y轴标签
            for (int y = 0; y <= (int)height; y += 50)
            {
                float actualY = height / 2 - y;
                if (actualY >= 0 && actualY <= height)
                {
                    canvas.DrawText($"Y:{y - height / 2}", width / 2 + 5, actualY, textPaint);
                }
            }
        }

        private void DrawWaveform(SKCanvas canvas)
        {
            if (WaveformData == null || WaveformData.Count < 2 || _paint == null)
                return;

            var info = canvas.DeviceClipBounds;
            float width = info.Width;
            float height = info.Height;
            
            // 计算中心点
            float centerX = width / 2;
            float centerY = height / 2;

            // 创建路径来绘制波形
            using var path = new SKPath();
            
            bool firstPoint = true;
            foreach (var point in WaveformData)
            {
                // 将数据点转换为屏幕坐标
                float x = (float)(point.X * _xScale) + centerX;
                float y = centerY - (float)(point.Y * _yScale); // Y轴翻转

                if (x >= 0 && x <= width && y >= 0 && y <= height) // 确保点在视口范围内
                {
                    if (firstPoint)
                    {
                        path.MoveTo(x, y);
                        firstPoint = false;
                    }
                    else
                    {
                        path.LineTo(x, y);
                    }
                }
            }

            if (!firstPoint) // 确保至少有一个点
            {
                canvas.DrawPath(path, _paint);
            }
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
                RequestRender();
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
                var newWaveformData = _viewModel.WaveformData ?? new List<(double X, double Y)>();
                // 直接更新WaveformData属性，这会触发重绘
                WaveformData = newWaveformData;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _paint?.Dispose();
        }

        private void RequestRender()
        {
            try
            {
                RequestNextFrameRendering();
            }
            catch
            {
                // 忽略渲染请求失败，避免在关闭阶段抛异常
            }
        }
    }
}