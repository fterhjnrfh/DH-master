using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace NewAvalonia.Controls
{
    public class WaveformCanvas : Control
    {
        public static readonly StyledProperty<IBrush?> BorderBrushProperty =
            Border.BorderBrushProperty.AddOwner<WaveformCanvas>();
        
        public static readonly StyledProperty<Thickness> BorderThicknessProperty =
            Border.BorderThicknessProperty.AddOwner<WaveformCanvas>();
            
        private readonly Random _random = new Random();
        private double[]? _originalWaveformData;  // 原始信号数据
        private double[]? _processedWaveformData; // 处理后的信号数据
        
        public IBrush? BorderBrush
        {
            get => GetValue(BorderBrushProperty);
            set => SetValue(BorderBrushProperty, value);
        }
        
        public Thickness BorderThickness
        {
            get => GetValue(BorderThicknessProperty);
            set => SetValue(BorderThicknessProperty, value);
        }
        
        public WaveformCanvas()
        {
            // Initialize with random waveform data
            GenerateRandomWaveform();
        }
        
        public void UpdateWaveform()
        {
            GenerateRandomWaveform();
            // 当更新波形时，处理后的数据应该基于新的原始数据
            _processedWaveformData = _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : null;
            InvalidateVisual();
        }
        
        private void GenerateRandomWaveform()
        {
            // Create an array of random values for the original waveform
            int pointCount = 200;
            _originalWaveformData = new double[pointCount];
            
            // Generate random values with some continuity to make it look more like a signal
            double currentValue = _random.NextDouble() * 2 - 1; // Start with a value between -1 and 1
            
            for (int i = 0; i < pointCount; i++)
            {
                // Add some randomness but maintain some continuity
                double change = (_random.NextDouble() - 0.5) * 0.4; // Small change
                currentValue += change;
                
                // Keep values within reasonable bounds but allow some outliers for "noise"
                if (currentValue > 1.5)
                    currentValue = 1.5 - _random.NextDouble() * 0.3;
                if (currentValue < -1.5)
                    currentValue = -1.5 + _random.NextDouble() * 0.3;
                
                _originalWaveformData[i] = currentValue;
            }
            
            // 初始化处理后数据为原始数据的副本
            _processedWaveformData = _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : null;
        }
        
        public void SetProcessedData(double[]? processedData)
        {
            _processedWaveformData = processedData != null ? (double[])processedData.Clone() : null;
            InvalidateVisual();
        }
        
        public void SetOriginalData(double[]? originalData)
        {
            _originalWaveformData = originalData != null ? (double[])originalData.Clone() : null;
            // 如果处理后的数据为空，初始化为原始数据的副本
            if (_processedWaveformData == null && _originalWaveformData != null)
            {
                _processedWaveformData = (double[])_originalWaveformData.Clone();
            }
            InvalidateVisual();
        }
        
        public double[]? GetOriginalData()
        {
            return _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : null;
        }
        
        public double[]? GetProcessedData()
        {
            return _processedWaveformData != null ? (double[])_processedWaveformData.Clone() : null;
        }
        
        public double[]? GenerateRandomWaveformData()
        {
            GenerateRandomWaveform();
            return _originalWaveformData != null ? (double[])_originalWaveformData.Clone() : null;
        }
        
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            
            var size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;
                
            // Draw the background
            context.FillRectangle(Brushes.Black, new Rect(0, 0, size.Width, size.Height));
            
            // Draw grid lines for better visualization
            DrawGrid(context, size);
            
            // Draw the original waveform (green)
            if (_originalWaveformData != null && _originalWaveformData.Length > 0)
            {
                var originalPen = new ImmutablePen(Brushes.LimeGreen, 1.5); // Green for original
                var originalGeometry = new StreamGeometry();
                
                using (var ctx = originalGeometry.Open())
                {
                    // Start at the first point
                    double x = 0;
                    double y = CalculateY(_originalWaveformData[0], size.Height);
                    ctx.BeginFigure(new Point(x, y), false);
                    
                    // Draw lines to subsequent points
                    for (int i = 1; i < _originalWaveformData.Length; i++)
                    {
                        x = (i * size.Width) / (_originalWaveformData.Length - 1);
                        y = CalculateY(_originalWaveformData[i], size.Height);
                        ctx.LineTo(new Point(x, y));
                    }
                    
                    ctx.EndFigure(false);
                }
                
                context.DrawGeometry(null, originalPen, originalGeometry);
            }
            
            // Draw the processed waveform (red) if it exists and is different from original
            if (_processedWaveformData != null && _processedWaveformData.Length > 0)
            {
                // Only draw if it's different from original or if we want to always show processed data
                var processedPen = new ImmutablePen(Brushes.Red, 2); // Red for processed
                var processedGeometry = new StreamGeometry();
                
                using (var ctx = processedGeometry.Open())
                {
                    // Start at the first point
                    double x = 0;
                    double y = CalculateY(_processedWaveformData[0], size.Height);
                    ctx.BeginFigure(new Point(x, y), false);
                    
                    // Draw lines to subsequent points
                    for (int i = 1; i < _processedWaveformData.Length; i++)
                    {
                        x = (i * size.Width) / (_processedWaveformData.Length - 1);
                        y = CalculateY(_processedWaveformData[i], size.Height);
                        ctx.LineTo(new Point(x, y));
                    }
                    
                    ctx.EndFigure(false);
                }
                
                context.DrawGeometry(null, processedPen, processedGeometry);
            }
            
            // Draw border
            if (BorderBrush is { } brush && BorderThickness.Left > 0)
            {
                var borderPen = new Pen(brush, BorderThickness.Left);
                context.DrawRectangle(null, borderPen, new Rect(0, 0, size.Width, size.Height));
            }
        }
        
        private double CalculateY(double value, double height)
        {
            // Map value from [-1.5, 1.5] to [height, 0] (inverted Y-axis)
            return height * (0.5 - value / 3.0);
        }
        
        private void DrawGrid(DrawingContext context, Size size)
        {
            // Create a pen with dashed style
            var dashStyle = new DashStyle(new double[] { 4, 4 }, 0);
            var gridPen = new Pen(Brushes.DarkGray, 1, dashStyle);
            
            // Horizontal center line
            context.DrawLine(gridPen, new Point(0, size.Height / 2), new Point(size.Width, size.Height / 2));
            
            // Vertical grid lines
            for (int i = 1; i < 10; i++)
            {
                double x = (i * size.Width) / 10;
                context.DrawLine(gridPen, new Point(x, 0), new Point(x, size.Height));
            }
            
            // Horizontal top and bottom lines
            context.DrawLine(gridPen, new Point(0, 0), new Point(size.Width, 0));
            context.DrawLine(gridPen, new Point(0, size.Height), new Point(size.Width, size.Height));
        }
    }
}