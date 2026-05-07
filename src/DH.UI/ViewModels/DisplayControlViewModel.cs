using Avalonia.Threading;
using NewAvalonia.Commands;
using NewAvalonia.Interfaces;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace NewAvalonia.ViewModels
{
    public class DisplayControlViewModel : ViewModelBase, IWaveformDisplay
    {
        private readonly LineSeries _lineSeries;

        public PlotModel PlotModel { get; }
        public PlotController PlotController { get; }

        private double _amplitude = 25;
        public double Amplitude
        {
            get => _amplitude;
            set
            {
                if (SetField(ref _amplitude, value))
                {
                    UpdateYAxis();
                }
            }
        }

        private double _frequency = 0.1;
        public double Frequency
        {
            get => _frequency;
            set => SetField(ref _frequency, value);
        }
        
        public DisplayControlViewModel()
        {
            PlotController = new PlotController();
            PlotController.UnbindMouseWheel(); // 禁用鼠标滚轮缩放

            PlotModel = new PlotModel
            {
                Title = "波形显示",
                TitleColor = OxyColors.White,
                TextColor = OxyColors.White,
                Background = OxyColors.Black,
                PlotAreaBorderColor = OxyColors.Gray
            };

            _lineSeries = new LineSeries
            {
                Color = OxyColors.Green, // 默认颜色
                StrokeThickness = 2,
                MarkerType = MarkerType.None
            };
            PlotModel.Series.Add(_lineSeries);

            // 配置X轴
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

            // 配置Y轴
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
                MinorGridlineColor = OxyColors.DarkGray,
                MajorStep = 25,
                MinorStep = 12.5
            });
        }

        /// <summary>
        /// 使用新的数据点更新波形。
        private void UpdateYAxis()
        {
            var yAxis = PlotModel.Axes.First(ax => ax.Position == AxisPosition.Left);
            double padding = Amplitude > 0 ? Amplitude * 0.5 : 10;
            yAxis.Maximum = Amplitude + padding;
            yAxis.Minimum = -Amplitude - padding;
            PlotModel.InvalidatePlot(false);
        }

        public void UpdateWaveform(IEnumerable<DataPoint> points)
        {
            _lineSeries.ItemsSource = points;

            // 设置X轴范围，与OxyPlot正弦波控件保持一致
            var xAxis = PlotModel.Axes.FirstOrDefault(ax => ax.Position == AxisPosition.Bottom);
            if (xAxis != null)
            {
                double width = 600;
                xAxis.Minimum = 0;
                xAxis.Maximum = width;
            }

            // 自动调整Y轴范围
            if (points.Any())
            {
                double maxAmplitude = points.Max(p => Math.Abs(p.Y));
                var yAxis = PlotModel.Axes.FirstOrDefault(ax => ax.Position == AxisPosition.Left);
                if (yAxis != null)
                {
                    double margin = Math.Max(maxAmplitude * 0.1, 5);
                    yAxis.Minimum = -maxAmplitude - margin;
                    yAxis.Maximum = maxAmplitude + margin;
                }
            }
            
            PlotModel.InvalidatePlot(true);
        }

        /// <summary>
        /// 内部方法用于更新波形
        private void UpdateWaveformInternal(IEnumerable<DataPoint> points)
        {
            UpdateWaveform(points);
        }

        public void ClearWaveform()
        {
            _lineSeries.ItemsSource = null;
            PlotModel.InvalidatePlot(true);
        }

        public void UpdateWaveColor(OxyColor color)
        {
            _lineSeries.Color = color;
            PlotModel.InvalidatePlot(false);
        }

        // 显式实现IWaveformDisplay接口方法
        void IWaveformDisplay.UpdateWaveform(IEnumerable<(double X, double Y)> points)
        {
            // 转换数据格式
            var dataPoints = points.Select(p => new DataPoint(p.X, p.Y)).ToList();
            UpdateWaveformInternal(dataPoints);
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