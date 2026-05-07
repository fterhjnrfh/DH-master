using Avalonia.Threading;
using NewAvalonia.Commands;
using NewAvalonia.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace NewAvalonia.ViewModels
{
    public class DisplayControl2ViewModel : ViewModelBase, IWaveformDisplay
    {
        private List<(double X, double Y)> _waveformData = new List<(double X, double Y)>();
        private double _amplitude = 25;
        private double _frequency = 0.1;

        public List<(double X, double Y)> WaveformData
        {
            get => _waveformData;
            set => SetField(ref _waveformData, value);
        }

        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetField(ref _amplitude, value))
                {
                    OnPropertyChanged(nameof(Amplitude));
                }
            }
        }

        public double Frequency
        {
            get => _frequency;
            set => SetField(ref _frequency, value);
        }

        public DisplayControl2ViewModel()
        {
            // 初始化默认波形数据
            UpdateWaveform(new List<(double X, double Y)>());
        }

        public void UpdateWaveform(IEnumerable<(double X, double Y)> points)
        {
            WaveformData = points.ToList();
            OnPropertyChanged(nameof(WaveformData));
        }

        public void ClearWaveform()
        {
            WaveformData = new List<(double X, double Y)>();
            OnPropertyChanged(nameof(WaveformData));
        }

        // 显式实现IWaveformDisplay接口方法
        void IWaveformDisplay.UpdateWaveform(IEnumerable<(double X, double Y)> points)
        {
            UpdateWaveform(points);
        }

        void IWaveformDisplay.ClearWaveform()
        {
            ClearWaveform();
        }

        double IWaveformDisplay.Amplitude 
        { 
            get => this.Amplitude; 
            set => this.Amplitude = value; 
        }
    }
}