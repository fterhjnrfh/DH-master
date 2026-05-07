using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace NewAvalonia.ViewModels
{
    public class NewOpenGLWaveViewModel : INotifyPropertyChanged
    {
        private const double PhaseStep = 0.2;
        private const double FrequencyScalingFactor = 100.0;

        private readonly DispatcherTimer _timer;
        private double _phase = 0;
        private bool _isRunning = false;

        private double _amplitude = 25; // 默认波幅
        private double _frequency = 1.0; // 默认频率
        private double _speed = 1.0; // 默认速度
        private int _selectedColorIndex = 2; // 默认蓝色

        public event EventHandler? ParametersChanged;

        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetField(ref _amplitude, Math.Max(1, value))) // 确保波幅至少为1
                {
                    OnPropertyChanged(nameof(Amplitude));
                    OnParametersChanged();
                }
            }
        }

        public double Frequency
        {
            get => _frequency;
            set
            {
                if (SetField(ref _frequency, Math.Max(0.1, value))) // 确保频率至少为0.1
                {
                    OnPropertyChanged(nameof(Frequency));
                    OnParametersChanged();
                }
            }
        }

        public double Speed
        {
            get => _speed;
            set
            {
                if (SetField(ref _speed, value) && value > 0)
                {
                    OnPropertyChanged(nameof(Speed));
                    _timer.Interval = TimeSpan.FromMilliseconds(30 / value);
                }
            }
        }

        public int SelectedColorIndex
        {
            get => _selectedColorIndex;
            set
            {
                if (SetField(ref _selectedColorIndex, value))
                {
                    OnPropertyChanged(nameof(SelectedColorIndex));
                    OnParametersChanged();
                }
            }
        }

        // 当参数改变时触发事件，让视图更新
        private void OnParametersChanged()
        {
            ParametersChanged?.Invoke(this, EventArgs.Empty);
        }

        public NewOpenGLWaveViewModel()
        {
            // 初始化定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += OnTimerTick;

            // 初始化后自动开始动画
            Start(); // 自动开始动画
        }

        private void Start()
        {
            _isRunning = true;
            _timer.Start();
            OnPropertyChanged(nameof(IsRunning));
        }

        private void Pause()
        {
            _isRunning = false;
            _timer.Stop();
            OnPropertyChanged(nameof(IsRunning));
        }

        private void Stop()
        {
            _isRunning = false;
            _timer.Stop();
            _phase = 0;
            OnPropertyChanged(nameof(IsRunning));
            OnParametersChanged();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _phase += PhaseStep * Speed;
            OnParametersChanged();
        }

        public bool IsRunning => _isRunning;

        public ICommand StartCommand => new RelayCommand(Start, () => !_isRunning);
        public ICommand PauseCommand => new RelayCommand(Pause, () => _isRunning);
        public ICommand StopCommand => new RelayCommand(Stop);

        public double Phase => _phase;

        // INotifyPropertyChanged implementation
        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    // 简单的命令实现
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public void ChangeCanExecute() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}