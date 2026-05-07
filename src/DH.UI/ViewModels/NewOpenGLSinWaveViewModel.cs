using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using NewAvalonia.Commands;

namespace NewAvalonia.ViewModels
{
    public class NewOpenGLSinWaveViewModel : ViewModelBase
    {
        private const double PhaseStep = 0.2;
        private readonly DispatcherTimer _timer;
        private double _phase = 0;
        private bool _isRunning = false;

        private double _amplitude = 25; // 默认波幅
        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetField(ref _amplitude, Math.Max(1, value))) // 确保波幅至少为1
                {
                    OnPropertyChanged(nameof(Amplitude));
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
                    OnPropertyChanged(nameof(Frequency));
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
                    OnPropertyChanged(nameof(Speed));
                    _timer.Interval = TimeSpan.FromMilliseconds(30 / value); // 调整动画速度
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
                    OnPropertyChanged(nameof(SelectedColorIndex));
                }
            }
        }

        public double Phase
        {
            get => _phase;
            private set
            {
                if (SetField(ref _phase, value))
                {
                    OnPropertyChanged(nameof(Phase));
                }
            }
        }

        public ICommand StartCommand { get; }
        public ICommand PauseCommand { get; }
        public ICommand StopCommand { get; }

        public NewOpenGLSinWaveViewModel()
        {
            // 初始化定时器
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _timer.Tick += OnTimerTick;

            // 初始化命令
            StartCommand = new RelayCommand(StartExecute, () => !_isRunning);
            PauseCommand = new RelayCommand(PauseExecute, () => _isRunning);
            StopCommand = new RelayCommand(StopExecute);

            // 不自动开始动画 - 让用户手动点击开始
            // 这样可以确保所有UI绑定都已正确设置
        }

        private void StartExecute()
        {
            _isRunning = true;
            _timer.Start();
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
            OnPropertyChanged(nameof(IsRunning));
        }

        private void PauseExecute()
        {
            _isRunning = false;
            _timer.Stop();
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
            OnPropertyChanged(nameof(IsRunning));
        }

        private void StopExecute()
        {
            _isRunning = false;
            _timer.Stop();
            _phase = 0;
            Phase = 0; // Update the property as well
            ((RelayCommand)StartCommand).ChangeCanExecute();
            ((RelayCommand)PauseCommand).ChangeCanExecute();
            OnPropertyChanged(nameof(IsRunning));
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            _phase += PhaseStep * Speed;
            Phase = _phase; // Update the property to trigger UI updates
        }

        public bool IsRunning => _isRunning;
    }
}