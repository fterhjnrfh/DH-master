using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NewAvalonia.Views
{
    /// <summary>
    /// GPU加速的波形绘制控件，用于高效渲染多个波形
    /// </summary>
    public class GPUAcceleratedWaveformCanvas : OpenGlControlBase
    {
        private GRContext? _gpuContext;
        private GRGlInterface? _glInterface;
        private SKPaint? _gridPaint;
        private SKPaint? _axisPaint;
        private SKPaint? _textPaint;
        private SKPaint? _titlePaint;
        private SKPaint? _labelPaint;

        // 波形数据集合
        private readonly List<(List<(double x, double y)> data, SKColor color)> _waveforms = new();
        private readonly object _waveformsLock = new object();

        // 控件参数
        private double _amplitude = 1.0;
        private double _xScale = 1.0;
        private double _yScale = 1.0;
        private bool _showGrid = true;
        private bool _showAxes = true;
        private bool _showLabels = true;

        // 定时器用于更新动画
        private readonly Avalonia.Threading.DispatcherTimer _renderTimer;

        public GPUAcceleratedWaveformCanvas()
        {
            // 初始化定时器，控制刷新率
            _renderTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(16) // 约60 FPS
            };
            _renderTimer.Tick += OnRenderTick;
            _renderTimer.Start();
        }

        #region Public Properties
        
        public double Amplitude
        {
            get => _amplitude;
            set
            {
                _amplitude = value;
                UpdateScales();
                RequestNextFrameRendering(); // 使用新的API
            }
        }

        public bool ShowGrid
        {
            get => _showGrid;
            set
            {
                _showGrid = value;
                RequestNextFrameRendering(); // 使用新的API
            }
        }

        public bool ShowAxes
        {
            get => _showAxes;
            set
            {
                _showAxes = value;
                RequestNextFrameRendering(); // 使用新的API
            }
        }

        public bool ShowLabels
        {
            get => _showLabels;
            set
            {
                _showLabels = value;
                RequestNextFrameRendering(); // 使用新的API
            }
        }

        #endregion

        #region Waveform Management

        /// <summary>
        /// 添加波形数据
        /// </summary>
        /// <param name="data">波形数据点</param>
        /// <param name="color">波形颜色</param>
        public void AddWaveform(List<(double x, double y)> data, SKColor color)
        {
            lock (_waveformsLock)
            {
                _waveforms.Add((data, color));
            }
            RequestNextFrameRendering(); // 使用新的API
        }

        /// <summary>
        /// 移除所有波形
        /// </summary>
        public void ClearWaveforms()
        {
            lock (_waveformsLock)
            {
                _waveforms.Clear();
            }
            RequestNextFrameRendering(); // 使用新的API
        }

        /// <summary>
        /// 更新指定索引的波形数据
        /// </summary>
        /// <param name="index">波形索引</param>
        /// <param name="data">新的波形数据</param>
        /// <param name="color">波形颜色（可选）</param>
        public void UpdateWaveform(int index, List<(double x, double y)> data, SKColor? color = null)
        {
            lock (_waveformsLock)
            {
                if (index >= 0 && index < _waveforms.Count)
                {
                    var current = _waveforms[index];
                    _waveforms[index] = (data, color ?? current.color);
                }
            }
            RequestNextFrameRendering(); // 使用新的API
        }

        /// <summary>
        /// 获取当前波形数量
        /// </summary>
        public int WaveformCount
        {
            get
            {
                lock (_waveformsLock)
                {
                    return _waveforms.Count;
                }
            }
        }

        #endregion

        private void OnRenderTick(object? sender, EventArgs e)
        {
            // 定期重绘以支持动画效果
            RequestNextFrameRendering(); // 使用新的API
        }

        protected override void OnOpenGlInit(GlInterface gl)
        {
            try
            {
                base.OnOpenGlInit(gl);

                // 创建 GPU 上下文
                _glInterface = GRGlInterface.Create((string procName) => gl.GetProcAddress(procName));
                if (_glInterface != null)
                {
                    _gpuContext = GRContext.CreateGl(_glInterface);
                    
                    // 验证 GPU 上下文是否成功创建
                    if (_gpuContext != null)
                    {
                        // GPU 上下文创建成功
                        System.Diagnostics.Debug.WriteLine("GPU 上下文已成功创建");
                        
                        System.Diagnostics.Debug.WriteLine("GPU 加速渲染已启用");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("ERROR: 无法创建 GPU 上下文！");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("ERROR: 无法创建 OpenGL 接口！");
                }

                // 初始化绘制工具
                InitializeDrawingTools();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GPU加速初始化失败: {ex.Message}");
            }
        }

        private void InitializeDrawingTools()
        {
            // 初始化网格线画笔
            _gridPaint = new SKPaint
            {
                Color = new SKColor(85, 85, 85), // 灰色
                StrokeWidth = 0.5f,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 初始化坐标轴画笔
            _axisPaint = new SKPaint
            {
                Color = SKColors.White,
                StrokeWidth = 2,
                IsAntialias = true,
                Style = SKPaintStyle.Stroke
            };

            // 初始化文字画笔
            _textPaint?.Dispose();
            _titlePaint?.Dispose();
            _labelPaint?.Dispose();

            _textPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true
            };

            _titlePaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                TextSize = 14
            };

            _labelPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                TextSize = 12
            };
        }

        protected override void OnOpenGlRender(GlInterface gl, int fb)
        {
            try
            {
                // 清除颜色缓冲区
                gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f); // 黑色背景
                gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);

                // 设置视口
                gl.Viewport(0, 0, (int)Bounds.Width, (int)Bounds.Height);

                // 使用 GPU 上下文创建 SkiaSharp 表面
                using var surface = SKSurface.Create(
                    _gpuContext,
                    false, // no need for MSAA
                    new SKImageInfo((int)Bounds.Width, (int)Bounds.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                );

                if (surface != null)
                {
                    var canvas = surface.Canvas;
                    
                    // 绘制所有元素
                    DrawBackground(canvas);
                    if (_showGrid) DrawGrid(canvas);
                    if (_showAxes) DrawAxes(canvas);
                    DrawWaveforms(canvas);
                    if (_showLabels) DrawLabels(canvas);

                    // 提交绘制命令到 GPU
                    canvas.Flush();
                    
                    // 在 GPU 加速上下文中，这将确保绘制命令被提交
                    _gpuContext?.Flush();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GPU渲染失败: {ex.Message}");
                
                // 如果 GPU 渲染失败，尝试使用基础清屏
                try
                {
                    gl.ClearColor(0.0f, 0.0f, 0.0f, 1.0f);
                    gl.Clear(GlConsts.GL_COLOR_BUFFER_BIT);
                }
                catch { }
            }
        }

        private void DrawBackground(SKCanvas canvas)
        {
            // 绘制黑色背景
            canvas.Clear(SKColors.Black);
        }

        private void DrawGrid(SKCanvas canvas)
        {
            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;

            if (_gridPaint == null) return;

            // 绘制垂直网格线 (X轴方向)
            for (int x = 0; x <= (int)width; x += 40)
            {
                canvas.DrawLine(x, 0, x, height, _gridPaint);
            }

            // 绘制水平网格线 (Y轴方向)
            for (int y = 0; y <= (int)height; y += 40)
            {
                canvas.DrawLine(0, y, width, y, _gridPaint);
            }
        }

        private void DrawAxes(SKCanvas canvas)
        {
            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;

            if (_axisPaint == null) return;

            // 绘制X轴
            canvas.DrawLine(0, height / 2, width, height / 2, _axisPaint);

            // 绘制Y轴
            canvas.DrawLine(width / 2, 0, width / 2, height, _axisPaint);
        }

        private void DrawWaveforms(SKCanvas canvas)
        {
            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;

            lock (_waveformsLock)
            {
                foreach (var (dataPoints, color) in _waveforms)
                {
                    if (dataPoints == null || dataPoints.Count < 2) continue;

                    using var paint = new SKPaint
                    {
                        Color = color,
                        StrokeWidth = 1.5f,
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeCap = SKStrokeCap.Round
                    };

                    // 创建波形路径
                    using var path = new SKPath();
                    bool first = true;

                    foreach (var point in dataPoints)
                    {
                        // 将数据点转换为屏幕坐标
                        var screenPoint = DataToScreenPoint(point, width, height);
                        
                        if (screenPoint.X >= 0 && screenPoint.X <= width) // 确保点在视口范围内
                        {
                            if (first)
                            {
                                path.MoveTo(screenPoint.X, screenPoint.Y);
                                first = false;
                            }
                            else
                            {
                                path.LineTo(screenPoint.X, screenPoint.Y);
                            }
                        }
                    }

                    // 只有在有有效点的情况下才绘制路径
                    if (!first)
                    {
                        canvas.DrawPath(path, paint);
                    }
                }
            }
        }

        private void DrawLabels(SKCanvas canvas)
        {
            if (_titlePaint == null || _labelPaint == null) return;

            float width = (float)Bounds.Width;
            float height = (float)Bounds.Height;

            // 绘制标题
            var titleText = "GPU 加速波形显示";
            var titleBounds = new SKRect();
            _titlePaint.MeasureText(titleText, ref titleBounds);
            var titleX = (width - titleBounds.Width) / 2;
            canvas.DrawText(titleText, titleX, 20, _titlePaint);

            // 绘制X轴标签
            var xLabelText = "X 轴";
            var xLabelBounds = new SKRect();
            _labelPaint.MeasureText(xLabelText, ref xLabelBounds);
            canvas.DrawText(xLabelText, width - xLabelBounds.Width - 10, height - 10, _labelPaint);

            // 绘制Y轴标签（旋转-90度）
            canvas.Save();
            canvas.RotateDegrees(-90, 10, height / 2);
            canvas.DrawText("Y 轴", 10, height / 2, _labelPaint);
            canvas.Restore();
        }

        private SKPoint DataToScreenPoint((double x, double y) dataPoint, float width, float height)
        {
            // 将数据坐标转换为屏幕坐标
            // 假设 X 范围是 0-200 映射到 0-width
            float screenX = (float)((dataPoint.x / 200.0) * width);

            // Y 轴：将数据的 Y 值映射到 Canvas 坐标系，0 点在中间
            // 假设 Y 值范围为 -1.0 到 1.0 (根据振幅)
            float normalizedY = (float)(dataPoint.y); // 值已标准化
            float screenY = height / 2 - (normalizedY * height / 2);

            return new SKPoint(screenX, screenY);
        }

        private void UpdateScales()
        {
            if (Bounds.Width > 0 && Bounds.Height > 0)
            {
                // 计算 X 和 Y 轴的缩放因子，确保波形适合显示区域
                _xScale = Math.Max(0.01, Bounds.Width / 200.0); // 假设 X 轴范围是 0-200
                _yScale = Math.Max(0.01, (Bounds.Height / 2) / (_amplitude * 1.5)); // Y 轴范围是振幅的 1.5 倍
            }
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == BoundsProperty)
            {
                UpdateScales();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);

            // 停止定时器
            _renderTimer.Stop();
            _renderTimer.Tick -= OnRenderTick;

            // 释放资源
            _gridPaint?.Dispose();
            _axisPaint?.Dispose();
            _textPaint?.Dispose();
            _titlePaint?.Dispose();
            _labelPaint?.Dispose();
            _gpuContext?.Dispose();
            _glInterface?.Dispose();
        }
    }
}