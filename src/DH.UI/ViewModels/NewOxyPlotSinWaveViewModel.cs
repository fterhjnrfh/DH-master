using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using NewAvalonia.Commands;

namespace NewAvalonia.ViewModels
{
    public class NewOxyPlotSinWaveViewModel : ViewModelBase
    {
        private const double PhaseStep = 0.2;
        private const double FrequencyScalingFactor = 100.0;

        private readonly DispatcherTimer _timer;
        private readonly LineSeries _waveSeries;
        private double _phase = 0;
        private bool _isRunning = false;

        public PlotModel PlotModel { get; }
        public PlotController PlotController { get; }

        private double _amplitude = 25; // 默认波幅
        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetField(ref _amplitude, Math.Max(1, value))) // 确保波幅至少为1
                {
                    UpdateYAxisRange();
                    if (_isRunning) DrawWaveform(); // 实时更新波形
                }
            }
        }

        private double _frequency = 1.0; // 默认频率
        public double Frequency
        {
            get => _frequency;
            set
            {
                if (SetField(ref _frequency, Math.Max(0.1, value))) // 确保频率至少为0.1
                {
                    if (_isRunning) DrawWaveform(); // 实时更新波形
                }
            }
        }

        private double _speed = 1.0; // 默认速度
        public double Speed
        {
            get => _speed;
            set
            {
                if (SetField(ref _speed, value) && value > 0)
                {
                    _timer.Interval = TimeSpan.FromMilliseconds(30 / value);
                }
            }
        }

        private int _selectedColorIndex = 2; // 默认蓝色
        public int SelectedColorIndex
        {
            get => _selectedColorIndex;
            set
            {
                if (SetField(ref _selectedColorIndex, value))
                {
                    UpdateWaveColor();
                }
            }
        }

        public ICommand StartCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }

        public NewOxyPlotSinWaveViewModel()
        {
            PlotController = new PlotController();
            PlotController.UnbindMouseWheel();

            PlotModel = new PlotModel
            {
                Title = "正弦波形显示",
                Background = OxyColors.Black,
                TextColor = OxyColors.White,
                TitleColor = OxyColors.White,
                PlotAreaBorderColor = OxyColors.Gray
            };

            // 创建波形数据系列
            _waveSeries = new LineSeries
            {
                Color = OxyColors.Blue,
                StrokeThickness = 2,
                MarkerType = MarkerType.None
            };
            PlotModel.Series.Add(_waveSeries);

            // 设置X轴
            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "距离 (x)",
                IsAxisVisible = true,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                TitleColor = OxyColors.White,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.Gray,
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColors.DarkGray,
                MajorStep = 50,
                MinorStep = 10
            });

            // 设置Y轴
            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "幅值 (A)",
                IsAxisVisible = true,
                AxislineColor = OxyColors.White,
                TicklineColor = OxyColors.White,
                TextColor = OxyColors.White,
                TitleColor = OxyColors.White,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColors.Gray,
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColors.DarkGray
            });

            // 初始化定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += OnTimerTick;

            // 初始化命令
            StartCommand = new RelayCommand(Start, () => !_isRunning);
            PauseCommand = new RelayCommand(Pause, () => _isRunning);
            StopCommand = new RelayCommand(Stop);

            // 初始化Y轴范围和绘制波形，然后自动开始动画
            UpdateYAxisRange();
            DrawWaveform();
            Start(); // 自动开始动画
        }

        private void Start()
        {
            _isRunning = true;
            _timer.Start();
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
        }

        private void Pause()
        {
            _isRunning = false;
            _timer.Stop();
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
        }

        private void Stop()
        {
            _isRunning = false;
            _timer.Stop();
            _phase = 0;
            _waveSeries.ItemsSource = null;
            PlotModel.InvalidatePlot(true);
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _phase += PhaseStep * Speed;
            DrawWaveform();
        }

        private void DrawWaveform()
        {
            var xAxis = PlotModel.Axes.First(ax => ax.Position == AxisPosition.Bottom);
            
            // 设置更大的X轴范围以显示更多波形
            double width = 600;
            xAxis.Minimum = 0;
            xAxis.Maximum = width;
            
            // 确保Y轴范围正确
            UpdateYAxisRange();
            
            var points = new List<DataPoint>();

            // 生成高密度的波形数据点
            for (double x = 0; x <= width; x += 0.5)
            {
                // 修正频率计算：频率应该影响波长，而不是直接乘以x
                double y = Amplitude * Math.Sin(_phase + x * Frequency * 2 * Math.PI / FrequencyScalingFactor);
                points.Add(new DataPoint(x, y));
            }

            _waveSeries.ItemsSource = points;
            PlotModel.InvalidatePlot(true);
        }

        private void UpdateYAxisRange()
        {
            var yAxis = PlotModel.Axes.FirstOrDefault(ax => ax.Position == AxisPosition.Left);
            if (yAxis != null)
            {
                // 设置Y轴范围，添加适当的边距
                double margin = Math.Max(Amplitude * 0.1, 5);
                yAxis.Minimum = -Amplitude - margin;
                yAxis.Maximum = Amplitude + margin;
                PlotModel.InvalidatePlot(false);
            }
        }

        private void UpdateWaveColor()
        {
            OxyColor color = _selectedColorIndex switch
            {
                0 => OxyColors.Red,
                1 => OxyColors.Green,
                2 => OxyColors.Blue,
                3 => OxyColors.Yellow,
                4 => OxyColors.Magenta,
                5 => OxyColors.Cyan,
                6 => OxyColors.White,
                _ => OxyColors.Blue
            };
            _waveSeries.Color = color;
            PlotModel.InvalidatePlot(false);
        }
    }
}