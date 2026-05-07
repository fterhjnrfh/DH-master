using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;

namespace NewAvalonia.Views
{
    public class TcpSquareWaveGlCanvas : Control
    {
        private const double PacketIntervalMs = 50d;
        private const double PulseWidthMs = 12d;
        private const double WindowDurationMs = 2000d;

        private readonly List<Point> _points = new();
        private readonly DispatcherTimer _timer;
        private double _timeInWindow;
        private double _windowStartMs;

        public event Action<double, double>? WindowRangeChanged;
        public double CurrentWindowStart => _windowStartMs;
        public double CurrentWindowEnd => _windowStartMs + WindowDurationMs;

        public TcpSquareWaveGlCanvas()
        {
            ClipToBounds = true;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PacketIntervalMs)
            };
            _timer.Tick += (_, _) => UpdateSignal();
            ResetWindow(true);
        }

        private void ResetWindow(bool raiseEvent)
        {
            _timeInWindow = 0;
            if (_windowStartMs < 0)
            {
                _windowStartMs = 0;
            }
            _points.Clear();
            AppendPoint(0, 0);
            if (raiseEvent)
            {
                WindowRangeChanged?.Invoke(CurrentWindowStart, CurrentWindowEnd);
            }
        }

        private void UpdateSignal()
        {
            var intervalStart = _timeInWindow;
            _timeInWindow += PacketIntervalMs;

            if (_timeInWindow > WindowDurationMs)
            {
                _windowStartMs += WindowDurationMs;
                _timeInWindow = PacketIntervalMs;
                intervalStart = 0;
                _points.Clear();
                AppendPoint(0, 0);
                WindowRangeChanged?.Invoke(CurrentWindowStart, CurrentWindowEnd);
            }

            var intervalEnd = Math.Min(WindowDurationMs, _timeInWindow);
            AppendPulse(intervalStart, intervalEnd);
            InvalidateVisual();
        }

        private void AppendPulse(double intervalStart, double intervalEnd)
        {
            if (_points.Count == 0)
            {
                AppendPoint(0, 0);
            }

            AppendPoint(intervalStart, 0);

            var pulseEnd = Math.Min(intervalEnd, intervalStart + PulseWidthMs);
            if (pulseEnd <= intervalStart)
            {
                AppendPoint(intervalEnd, 0);
                return;
            }

            AppendPoint(intervalStart, 1);
            AppendPoint(pulseEnd, 1);
            AppendPoint(pulseEnd, 0);

            if (intervalEnd > pulseEnd)
            {
                AppendPoint(intervalEnd, 0);
            }
        }

        private void AppendPoint(double timeMs, double level)
        {
            var clamped = Math.Clamp(level, 0, 1);
            var pt = new Point(Math.Clamp(timeMs, 0, WindowDurationMs), clamped);
            if (_points.Count > 0 && Math.Abs(_points[^1].X - pt.X) < 0.01 && Math.Abs(_points[^1].Y - pt.Y) < 0.01)
            {
                return;
            }
            _points.Add(pt);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            DrawBackground(context, bounds);
            DrawGrid(context, bounds);
            DrawAxes(context, bounds);
            DrawWave(context, bounds);
        }

        private void DrawBackground(DrawingContext context, Rect bounds)
        {
            var bg = new LinearGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#0F1C2D"), 0),
                    new GradientStop(Color.Parse("#050A12"), 1)
                }
            };
            context.FillRectangle(bg, bounds);
        }

        private void DrawGrid(DrawingContext context, Rect bounds)
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse("#1E3A5F")), 1);
            for (int i = 0; i <= 12; i++)
            {
                double x = bounds.Width * i / 12d;
                context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
            }
            for (int i = 0; i <= 6; i++)
            {
                double y = bounds.Height * i / 6d;
                context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        private void DrawAxes(DrawingContext context, Rect bounds)
        {
            var axisPen = new Pen(new SolidColorBrush(Color.Parse("#4CD7F6")), 1.5);
            double baseline = bounds.Height * 0.8;
            context.DrawLine(axisPen, new Point(0, baseline), new Point(bounds.Width, baseline));
        }

        private void DrawWave(DrawingContext context, Rect bounds)
        {
            if (_points.Count < 2)
                return;

            var pen = new Pen(new SolidColorBrush(Color.Parse("#00FFC6")), 2.5)
            {
                LineJoin = PenLineJoin.Bevel
            };

            var geo = new StreamGeometry();
            using (var gc = geo.Open())
            {
                var start = TransformPoint(_points[0], bounds);
                gc.BeginFigure(start, false);
                for (int i = 1; i < _points.Count; i++)
                {
                    gc.LineTo(TransformPoint(_points[i], bounds));
                }
            }
            context.DrawGeometry(null, pen, geo);
        }

        private Point TransformPoint(Point point, Rect bounds)
        {
            double x = (point.X / WindowDurationMs) * bounds.Width;
            double highY = bounds.Height * 0.2;
            double lowY = bounds.Height * 0.8;
            double y = lowY - point.Y * (lowY - highY);
            return new Point(x, y);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
            base.OnDetachedFromVisualTree(e);
        }
    }
}
