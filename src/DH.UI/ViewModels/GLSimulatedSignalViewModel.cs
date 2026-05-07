using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using NewAvalonia.Services;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using System.Linq;

namespace NewAvalonia.ViewModels
{
    public class GLSimulatedSignalViewModel : ViewModelBase, IDisposable
    {
        private readonly DispatcherTimer _timer;
        private double _timeOffset = 0;
        private bool _disposed = false;
        private AlgorithmProcessor? _algorithmProcessor;

        public GLSimulatedSignalViewModel()
        {
            _algorithmProcessor = new AlgorithmProcessor();
            
            // 初始化动画定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _timer.Tick += OnTimerTick;

            // 开始动画
            _timer.Start();
        }

        // 波形参数
        private double _peakPosition = 50;
        public double PeakPosition
        {
            get => _peakPosition;
            set
            {
                if (SetField(ref _peakPosition, value))
                {
                    OnPropertyChanged(nameof(SignalData));
                }
            }
        }

        private double _peakHeight = 1.0;
        public double PeakHeight
        {
            get => _peakHeight;
            set
            {
                if (SetField(ref _peakHeight, value))
                {
                    OnPropertyChanged(nameof(SignalData));
                }
            }
        }

        private double _peakWidth = 10;
        public double PeakWidth
        {
            get => _peakWidth;
            set
            {
                if (SetField(ref _peakWidth, value))
                {
                    OnPropertyChanged(nameof(SignalData));
                }
            }
        }

        private double _noiseLevel = 0.1;
        public double NoiseLevel
        {
            get => _noiseLevel;
            set
            {
                if (SetField(ref _noiseLevel, value))
                {
                    OnPropertyChanged(nameof(SignalData));
                }
            }
        }

        private double _baselineDrift = 0.2;
        public double BaselineDrift
        {
            get => _baselineDrift;
            set
            {
                if (SetField(ref _baselineDrift, value))
                {
                    OnPropertyChanged(nameof(SignalData));
                }
            }
        }

        // 算法选择相关属性
        private int _selectedAlgorithmIndex = 0; // 默认选择"无处理"
        public int SelectedAlgorithmIndex
        {
            get => _selectedAlgorithmIndex;
            set
            {
                if (SetField(ref _selectedAlgorithmIndex, value))
                {
                    OnPropertyChanged(nameof(IsBuiltinAlgorithmSelected));
                    OnPropertyChanged(nameof(IsExternalAlgorithmSelected));
                    OnPropertyChanged(nameof(IsDynamicLibraryAlgorithmSelected));
                    OnPropertyChanged(nameof(IsMovingAverageSelected));
                    OnPropertyChanged(nameof(IsGaussianSelected));
                    OnPropertyChanged(nameof(IsNoProcessingSelected));
                    UpdateAlgorithmProcessor();
                }
            }
        }

        // 算法选项映射：
        // 0=无处理, 1=移动平均, 2=高斯滤波, 3=外部算法, 4+=动态库算法
        public bool IsNoProcessingSelected => _selectedAlgorithmIndex == 0;
        public bool IsMovingAverageSelected => _selectedAlgorithmIndex == 1;
        public bool IsGaussianSelected => _selectedAlgorithmIndex == 2;
        public bool IsExternalAlgorithmSelected => _selectedAlgorithmIndex == 3;
        public bool IsDynamicLibraryAlgorithmSelected => _selectedAlgorithmIndex >= 4;
        public bool IsBuiltinAlgorithmSelected => _selectedAlgorithmIndex >= 1 && _selectedAlgorithmIndex <= 2;

        // 移动平均算法参数
        private int _movingAverageWindowSize = 5;
        public int MovingAverageWindowSize
        {
            get => _movingAverageWindowSize;
            set
            {
                if (SetField(ref _movingAverageWindowSize, value))
                {
                    UpdateAlgorithmProcessor();
                }
            }
        }

        private double _movingAverageStrength = 1.0;
        public double MovingAverageStrength
        {
            get => _movingAverageStrength;
            set
            {
                if (SetField(ref _movingAverageStrength, value))
                {
                    UpdateAlgorithmProcessor();
                }
            }
        }

        // 高斯滤波算法参数
        private int _gaussianWindowSize = 5;
        public int GaussianWindowSize
        {
            get => _gaussianWindowSize;
            set
            {
                if (SetField(ref _gaussianWindowSize, value))
                {
                    UpdateAlgorithmProcessor();
                }
            }
        }

        private double _gaussianSigma = 1.0;
        public double GaussianSigma
        {
            get => _gaussianSigma;
            set
            {
                if (SetField(ref _gaussianSigma, value))
                {
                    UpdateAlgorithmProcessor();
                }
            }
        }

        // 外部算法路径
        private string _externalAlgorithmPath = "";
        public string ExternalAlgorithmPath
        {
            get => _externalAlgorithmPath;
            set
            {
                if (SetField(ref _externalAlgorithmPath, value))
                {
                    UpdateAlgorithmProcessor();
                }
            }
        }

        private Func<List<(double x, double y)>, List<(double x, double y)>>? _algorithmProcessorFunc;

        public void SetAlgorithmProcessor(Func<List<(double x, double y)>, List<(double x, double y)>>? processor)
        {
            _algorithmProcessorFunc = processor;
        }

        private void UpdateAlgorithmProcessor()
        {
            if (_algorithmProcessor == null)
            {
                _algorithmProcessorFunc = null;
                OnPropertyChanged(nameof(SignalData));
                return;
            }

            try
            {
                switch (_selectedAlgorithmIndex)
                {
                    case 0: // 无处理
                        _algorithmProcessorFunc = null;
                        break;

                    case 1: // 移动平均
                        var movingAvgAlgorithm = new AlgorithmProcessor.AlgorithmDefinition
                        {
                            AlgorithmType = "MovingAverage"
                        };
                        var movingAvgParams = new Dictionary<string, object>
                        {
                            ["windowSize"] = _movingAverageWindowSize,
                            ["strength"] = _movingAverageStrength
                        };
                        _algorithmProcessorFunc = (data) => 
                        {
                            var dataPoints = data.Select(p => new OxyPlot.DataPoint(p.x, p.y)).ToList();
                            var processedPoints = _algorithmProcessor.ApplyAlgorithm(dataPoints, movingAvgAlgorithm, movingAvgParams);
                            return processedPoints.Select(p => (p.X, p.Y)).ToList();
                        };
                        break;

                    case 2: // 高斯滤波
                        var gaussianAlgorithm = new AlgorithmProcessor.AlgorithmDefinition
                        {
                            AlgorithmType = "Gaussian"
                        };
                        var gaussianParams = new Dictionary<string, object>
                        {
                            ["windowSize"] = _gaussianWindowSize,
                            ["sigma"] = _gaussianSigma
                        };
                        _algorithmProcessorFunc = (data) => 
                        {
                            var dataPoints = data.Select(p => new OxyPlot.DataPoint(p.x, p.y)).ToList();
                            var processedPoints = _algorithmProcessor.ApplyAlgorithm(dataPoints, gaussianAlgorithm, gaussianParams);
                            return processedPoints.Select(p => (p.X, p.Y)).ToList();
                        };
                        break;

                    case 3: // 外部算法
                        if (!string.IsNullOrEmpty(_externalAlgorithmPath) && _algorithmProcessor != null)
                        {
                            // 加载外部算法文件
                            Task.Run(async () =>
                            {
                                try
                                {
                                    _algorithmProcessor.ClearLastError();
                                    var externalAlgorithm = await _algorithmProcessor.LoadAlgorithmAsync(_externalAlgorithmPath);
                                    if (externalAlgorithm != null)
                                    {
                                        await Dispatcher.UIThread.InvokeAsync(() =>
                                        {
                                            _algorithmProcessorFunc = (data) =>
                                            {
                                                _algorithmProcessor.ClearLastError();
                                                var dataPoints = data.Select(p => new OxyPlot.DataPoint(p.x, p.y)).ToList();
                                                var result = _algorithmProcessor.ApplyAlgorithm(dataPoints, externalAlgorithm, new Dictionary<string, object>());
                                                if (!string.IsNullOrEmpty(_algorithmProcessor.LastError))
                                                {
                                                    // 弹窗提示错误
                                                    var _ = MessageBoxManager.GetMessageBoxStandard("外部算法错误", _algorithmProcessor.LastError).ShowAsync();
                                                }
                                                return result.Select(p => (p.X, p.Y)).ToList();
                                            };
                                            OnPropertyChanged(nameof(SignalData));
                                        });
                                    }
                                    else
                                    {
                                        var err = _algorithmProcessor.LastError ?? "加载外部算法失败";
                                        await Dispatcher.UIThread.InvokeAsync(() =>
                                        {
                                            _algorithmProcessorFunc = null;
                                            var _ = MessageBoxManager.GetMessageBoxStandard("外部算法错误", err).ShowAsync();
                                            OnPropertyChanged(nameof(SignalData));
                                        });
                                    }
                                }
                                catch (Exception ex)
                                {
                                    await Dispatcher.UIThread.InvokeAsync(() =>
                                    {
                                        _algorithmProcessorFunc = null;
                                        var _ = MessageBoxManager.GetMessageBoxStandard("外部算法异常", ex.Message).ShowAsync();
                                        OnPropertyChanged(nameof(SignalData));
                                    });
                                }
                            });
                        }
                        else
                        {
                            _algorithmProcessorFunc = null;
                        }
                        break;

                    default:
                        // 处理动态库算法
                        if (_selectedAlgorithmIndex >= 4)
                        {
                            var algorithms = NewAvalonia.Services.AlgorithmManager.GetAllAlgorithms();
                            int dynamicIndex = _selectedAlgorithmIndex - 4; // 计算在动态库算法列表中的索引

                            if (dynamicIndex < algorithms.Count)
                            {
                                var selectedAlgorithm = algorithms[dynamicIndex];
                                _algorithmProcessorFunc = (data) =>
                                {
                                    // 将 (double, double) 列表转换为 double[] 数组，因为动态库算法只接受数组
                                    var inputArray = data.Select(dp => dp.y).ToArray();
                                    var result = NewAvalonia.Services.AlgorithmManager.ExecuteAlgorithm(selectedAlgorithm.Name, inputArray);
                                    
                                    if (result is double[] outputArray)
                                    {
                                        // 将处理后的数组转换回 (double, double) 列表
                                        var resultDataPoints = new List<(double x, double y)>();
                                        for (int i = 0; i < Math.Min(data.Count, outputArray.Length); i++)
                                        {
                                            resultDataPoints.Add((data[i].x, outputArray[i]));
                                        }
                                        if (resultDataPoints.Count < data.Count)
                                        {
                                            for (int i = resultDataPoints.Count; i < data.Count; i++)
                                            {
                                                resultDataPoints.Add((data[i].x, data[i].y));
                                            }
                                        }
                                        return resultDataPoints;
                                    }
                                    else
                                    {
                                        // 如果执行失败，返回原始数据
                                        return data;
                                    }
                                };
                            }
                            else
                            {
                                _algorithmProcessorFunc = null; // 索引超出范围
                            }
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"更新算法处理器时发生错误: {ex.Message}");
                _algorithmProcessorFunc = null;
            }

            OnPropertyChanged(nameof(SignalData));
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _timeOffset += 0.5; // 时间推进，控制动画速度
            OnPropertyChanged(nameof(SignalData)); // 通知数据更新
        }

        // 生成信号数据的属性
        public List<(double x, double y)> SignalData
        {
            get
            {
                var originalPoints = new List<(double x, double y)>();
                var random = new Random((int)(_timeOffset / 10)); // 使用较慢变化的随机种子

                // 心电图式滚动：固定X轴范围，信号从右边流入
                for (double x = 0; x <= 200; x += 0.5)
                {
                    double y = 0;

                    // 将当前X位置映射到时间轴上（心电图效果）
                    double timePosition = x + _timeOffset;

                    // 添加高斯峰（在时间轴上重复出现）
                    double peakCycle = 80; // 峰的周期
                    double localTime = timePosition % peakCycle;
                    double peakDistance = Math.Abs(localTime - _peakPosition);
                    y += _peakHeight * Math.Exp(-Math.Pow(peakDistance / _peakWidth, 2));

                    // 添加基线漂移（随时间变化的正弦波）
                    y += _baselineDrift * Math.Sin(timePosition * 0.02);

                    // 添加粗糙的随机噪声（模拟未处理的原始信号）
                    random = new Random((int)(timePosition * 7) % 10000); // 基于位置的随机种子
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 2; // 主要噪声
                    
                    // 添加额外的粗糙度
                    random = new Random((int)(timePosition * 13) % 10000);
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 1.5; // 次要噪声
                    
                    // 添加高频抖动（模拟电子噪声）
                    random = new Random((int)(timePosition * 23) % 10000);
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 0.8; // 高频抖动

                    // 添加偶发的尖峰噪声（模拟干扰）
                    if (random.NextDouble() < 0.02) // 2%的概率出现尖峰
                    {
                        y += (random.NextDouble() - 0.5) * _peakHeight * 0.5;
                    }

                    // 添加背景信号变化（保持一些确定性成分）
                    y += 0.05 * Math.Sin(timePosition * 0.1);

                    originalPoints.Add((x, y));
                }

                // 创建处理后的信号副本
                var processedPoints = new List<(double x, double y)>(originalPoints);

                // 根据选择的算法进行处理
                if (_algorithmProcessorFunc != null && processedPoints.Count > 0)
                {
                    processedPoints = _algorithmProcessorFunc(processedPoints);
                }
                // 如果选择"无处理"，processedPoints保持与originalPoints相同

                return processedPoints;
            }
        }

        // 获取原始信号数据（在实际应用中，你可能需要同时跟踪原始和处理后的数据）
        public List<(double x, double y)> OriginalSignalData
        {
            get
            {
                var originalPoints = new List<(double x, double y)>();
                var random = new Random((int)(_timeOffset / 10)); // 使用较慢变化的随机种子

                // 心电图式滚动：固定X轴范围，信号从右边流入
                for (double x = 0; x <= 200; x += 0.5)
                {
                    double y = 0;

                    // 将当前X位置映射到时间轴上（心电图效果）
                    double timePosition = x + _timeOffset;

                    // 添加高斯峰（在时间轴上重复出现）
                    double peakCycle = 80; // 峰的周期
                    double localTime = timePosition % peakCycle;
                    double peakDistance = Math.Abs(localTime - _peakPosition);
                    y += _peakHeight * Math.Exp(-Math.Pow(peakDistance / _peakWidth, 2));

                    // 添加基线漂移（随时间变化的正弦波）
                    y += _baselineDrift * Math.Sin(timePosition * 0.02);

                    // 添加粗糙的随机噪声（模拟未处理的原始信号）
                    random = new Random((int)(timePosition * 7) % 10000); // 基于位置的随机种子
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 2; // 主要噪声
                    
                    // 添加额外的粗糙度
                    random = new Random((int)(timePosition * 13) % 10000);
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 1.5; // 次要噪声
                    
                    // 添加高频抖动（模拟电子噪声）
                    random = new Random((int)(timePosition * 23) % 10000);
                    y += _noiseLevel * (random.NextDouble() - 0.5) * 0.8; // 高频抖动

                    // 添加偶发的尖峰噪声（模拟干扰）
                    if (random.NextDouble() < 0.02) // 2%的概率出现尖峰
                    {
                        y += (random.NextDouble() - 0.5) * _peakHeight * 0.5;
                    }

                    // 添加背景信号变化（保持一些确定性成分）
                    y += 0.05 * Math.Sin(timePosition * 0.1);

                    originalPoints.Add((x, y));
                }

                return originalPoints;
            }
        }

        public void StartAnimation()
        {
            if (!_disposed)
            {
                _timer.Start();
            }
        }

        public void StopAnimation()
        {
            _timer.Stop();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 清理托管资源
                    if (_timer != null)
                    {
                        _timer.Stop();
                        _timer.Tick -= OnTimerTick;
                    }
                }
                _disposed = true;
            }
        }

        /// <summary>
        /// 手动重新加载当前外部算法（供“测试”按钮调用）。
        /// 不改变 SelectedAlgorithmIndex，仅按当前 ExternalAlgorithmPath 重新编译/加载并刷新曲线。
        /// </summary>
        public void ReloadExternalAlgorithm()
        {
            try
            {
                // 仅在外部算法被选中时才触发加载
                if (IsExternalAlgorithmSelected)
                {
                    UpdateAlgorithmProcessor();
                }
                else
                {
                    // 若未选择外部算法，也允许触发一次刷新
                    OnPropertyChanged(nameof(SignalData));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ReloadExternalAlgorithm 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取所有可用的算法选项（包括内置算法和动态库算法）
        /// </summary>
        public List<string> GetAlgorithmOptions()
        {
            var options = new List<string>
            {
                "无处理（原始信号）",
                "简单移动平均",
                "高斯滤波",
                "外部算法（.xtj/.xtjs/.dll 文件）"
            };

            // 添加动态库中的算法
            var dynamicAlgorithms = NewAvalonia.Services.AlgorithmManager.GetAllAlgorithms();
            foreach (var algorithm in dynamicAlgorithms)
            {
                options.Add($"动态库: {algorithm.Name} - {algorithm.Description}");
            }

            return options;
        }
    }
}