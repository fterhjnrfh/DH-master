using Avalonia.Controls;
using Avalonia;
using Avalonia.Markup.Xaml;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NewAvalonia.Views
{
    public partial class GPUAcceleratedWaveformControl : UserControl
    {
        private GPUAcceleratedWaveformCanvas? _waveformCanvas;
        private Button? _addWaveformBtn;
        private Button? _clearWaveformsBtn;
        private Button? _addManyWaveformsBtn;
        private Button? _testPerformanceBtn;
        private TextBlock? _waveformCountText;
        private TextBlock? _gpuStatusText;
        private TextBlock? _performanceText;
        private int _waveformCounter = 0;
        
        // 预设的颜色列表
        private static readonly SKColor[] WaveformColors = {
            SKColors.Red,
            SKColors.Cyan,
            SKColors.Lime,
            SKColors.Yellow,
            SKColors.Magenta,
            SKColors.Orange,
            SKColors.Pink,
            SKColors.Aqua,
            SKColors.Chartreuse,
            SKColors.Turquoise
        };

        public GPUAcceleratedWaveformControl()
        {
            InitializeComponent();
            SetupControls();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _waveformCanvas = this.FindControl<GPUAcceleratedWaveformCanvas>("WaveformCanvas");
            _addWaveformBtn = this.FindControl<Button>("AddWaveformBtn");
            _clearWaveformsBtn = this.FindControl<Button>("ClearWaveformsBtn");
            _addManyWaveformsBtn = this.FindControl<Button>("AddManyWaveformsBtn");
            _testPerformanceBtn = this.FindControl<Button>("TestPerformanceBtn");
            _waveformCountText = this.FindControl<TextBlock>("WaveformCountText");
            _gpuStatusText = this.FindControl<TextBlock>("GpuStatusText");
            _performanceText = this.FindControl<TextBlock>("PerformanceText");
        }

        private void SetupControls()
        {
            if (_addWaveformBtn != null)
            {
                _addWaveformBtn.Click += AddWaveformBtn_Click;
            }

            if (_clearWaveformsBtn != null)
            {
                _clearWaveformsBtn.Click += ClearWaveformsBtn_Click;
            }

            if (_addManyWaveformsBtn != null)
            {
                _addManyWaveformsBtn.Click += AddManyWaveformsBtn_Click;
            }

            if (_testPerformanceBtn != null)
            {
                _testPerformanceBtn.Click += TestPerformanceBtn_Click;
            }

            if (_waveformCountText != null)
            {
                _waveformCountText.Text = "波形数量: 0";
            }

            if (_gpuStatusText != null)
            {
                _gpuStatusText.Text = "GPU 状态: 尚未初始化";
            }

            if (_performanceText != null)
            {
                _performanceText.Text = "性能: 等待测试";
            }
        }

        private void AddWaveformBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_waveformCanvas != null)
            {
                // 生成随机参数的波形数据
                var waveformData = GenerateWaveformData(200, 50 + _waveformCounter * 0.5, 0.02 * _waveformCounter, 0.1);
                var color = WaveformColors[_waveformCounter % WaveformColors.Length];
                
                // 添加到 GPU 加速画布
                _waveformCanvas.AddWaveform(waveformData, color);
                _waveformCounter++;
                
                // 更新计数显示
                if (_waveformCountText != null)
                {
                    _waveformCountText.Text = $"波形数量: {_waveformCanvas.WaveformCount}";
                }
            }
        }

        private void AddManyWaveformsBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_waveformCanvas != null)
            {
                // 添加 50 个波形进行压力测试
                for (int i = 0; i < 50; i++)
                {
                    var waveformData = GenerateWaveformData(200, 50 + _waveformCounter * 0.5, 0.02 * _waveformCounter, 0.1);
                    var color = WaveformColors[_waveformCounter % WaveformColors.Length];
                    
                    _waveformCanvas.AddWaveform(waveformData, color);
                    _waveformCounter++;
                }
                
                // 更新计数显示
                if (_waveformCountText != null)
                {
                    _waveformCountText.Text = $"波形数量: {_waveformCanvas.WaveformCount}";
                }
            }
        }

        private void ClearWaveformsBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_waveformCanvas != null)
            {
                _waveformCanvas.ClearWaveforms();
                _waveformCounter = 0;
                
                // 更新计数显示
                if (_waveformCountText != null)
                {
                    _waveformCountText.Text = "波形数量: 0";
                }
                
                if (_performanceText != null)
                {
                    _performanceText.Text = "性能: 清空完成";
                }
            }
        }

        private void TestPerformanceBtn_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_waveformCanvas != null)
            {
                TestRenderPerformance();
            }
        }

        private void TestRenderPerformance()
        {
            if (_waveformCanvas == null) return;

            var stopwatch = Stopwatch.StartNew();
            
            // 立即触发渲染，不使用计时器
            int testIterations = 100; // 测试100次重绘
            for (int i = 0; i < testIterations; i++)
            {
                // 触发重绘
                _waveformCanvas.RequestNextFrameRendering();
            }
            
            stopwatch.Stop();
            double avgTime = stopwatch.ElapsedMilliseconds / (double)testIterations;
            
            System.Diagnostics.Debug.WriteLine($"100次重绘总耗时: {stopwatch.ElapsedMilliseconds}ms, 平均每次: {avgTime:F3}ms");
            
            if (_performanceText != null)
            {
                _performanceText.Text = $"性能: 平均 {avgTime:F3}ms/帧 (共{testIterations}次)";
            }

            // 基于平均渲染时间判断GPU加速状态
            if (avgTime < 10.0) // 如果平均渲染时间小于10ms，很可能使用了GPU加速
            {
                if (_gpuStatusText != null)
                {
                    _gpuStatusText.Text = "GPU 状态: 正在使用 GPU 加速 (性能良好)";
                }
            }
            else if (avgTime < 50.0) // 如果在10-50ms之间，可能使用GPU，但性能一般
            {
                if (_gpuStatusText != null)
                {
                    _gpuStatusText.Text = "GPU 状态: 可能使用 GPU 加速或 CPU 渲染 (中等性能)";
                }
            }
            else // 如果超过50ms，很可能是CPU渲染
            {
                if (_gpuStatusText != null)
                {
                    _gpuStatusText.Text = "GPU 状态: 可能未使用 GPU 加速 (性能较低)";
                }
            }
        }

        /// <summary>
        /// 生成波形数据
        /// </summary>
        /// <param name="length">数据点数量</param>
        /// <param name="amplitude">振幅</param>
        /// <param name="frequency">频率</param>
        /// <param name="phase">相位</param>
        /// <returns>波形数据点列表</returns>
        private List<(double x, double y)> GenerateWaveformData(int length, double amplitude = 1.0, double frequency = 0.1, double phase = 0.0)
        {
            var data = new List<(double x, double y)>();
            
            for (int i = 0; i < length; i++)
            {
                double x = i; // X 值范围 0-199
                // 生成复合波形：正弦波 + 一些噪声
                double y = amplitude * Math.Sin(frequency * x + phase) + 
                          0.1 * Math.Sin(3 * frequency * x + phase) +  // 谐波
                          0.05 * (new Random((int)(x + phase * 1000)).NextDouble() - 0.5); // 轻微噪声
                
                data.Add((x, y));
            }
            
            return data;
        }
    }
}