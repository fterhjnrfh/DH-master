using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using System.Runtime.InteropServices;

namespace NewAvalonia.Views
{
    /// <summary>
    /// 模拟 TCP 50ms 方波发包的 Skia 控件（参考现有 Skia 控件实现方式）。
    /// </summary>
    public class TcpSquareWaveSkiaCanvas : Control
    {
        private const double PacketIntervalMs = 50d;
        private const double PulseWidthMs = 12d;
        private const double WindowDurationMs = 4000d;
        private const double BaselineLevel = 0d;
        private const double HighLevel = 1d;

        private readonly List<Point> _points = new();
        private readonly DispatcherTimer _timer;
        private double _timeInWindow;
        private double _windowStartMs;

        public event Action<double, double>? WindowRangeChanged;

        public double CurrentWindowStart => _windowStartMs;
        public double CurrentWindowEnd => _windowStartMs + WindowDurationMs;

        public TcpSquareWaveSkiaCanvas()
        {
            ClipToBounds = true;
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(PacketIntervalMs)
            };
            _timer.Tick += (_, _) => UpdateSignal();
            ResetWindowState();
        }

        private void ResetWindowState()
        {
            _timeInWindow = 0;
            _windowStartMs = Math.Max(0, _windowStartMs);
            _points.Clear();
            AppendPoint(0, BaselineLevel);
            WindowRangeChanged?.Invoke(CurrentWindowStart, CurrentWindowEnd);
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
                AppendPoint(0, BaselineLevel);
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
                AppendPoint(0, BaselineLevel);
            }

            AppendPoint(intervalStart, BaselineLevel);

            var pulseEnd = Math.Min(intervalEnd, intervalStart + PulseWidthMs);
            if (pulseEnd <= intervalStart)
            {
                AppendPoint(intervalEnd, BaselineLevel);
                return;
            }

            AppendPoint(intervalStart, HighLevel);
            AppendPoint(pulseEnd, HighLevel);
            AppendPoint(pulseEnd, BaselineLevel);

            if (intervalEnd > pulseEnd)
            {
                AppendPoint(intervalEnd, BaselineLevel);
            }
        }

        private void AppendPoint(double timeMs, double level)
        {
            var clamped = Math.Clamp(level, 0, 1);
            if (_points.Count > 0)
            {
                var last = _points[^1];
                if (Math.Abs(last.X - timeMs) < 0.01 && Math.Abs(last.Y - clamped) < 0.01)
                {
                    return;
                }
            }
            _points.Add(new Point(Math.Clamp(timeMs, 0, WindowDurationMs), clamped));
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            context.FillRectangle(new SolidColorBrush(Color.Parse("#101418")), bounds);
            DrawGrid(context, bounds);
            DrawAxes(context, bounds);
            DrawWaveform(context, bounds);
            DrawInfo(context, bounds);
        }

        private void DrawGrid(DrawingContext context, Rect bounds)
        {
            var pen = new Pen(new SolidColorBrush(Color.Parse("#222933")), 1);
            const int columns = 10;
            for (int i = 0; i <= columns; i++)
            {
                double x = bounds.Width * i / columns;
                context.DrawLine(pen, new Point(x, 0), new Point(x, bounds.Height));
            }

            const int rows = 6;
            for (int i = 0; i <= rows; i++)
            {
                double y = bounds.Height * i / rows;
                context.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            }
        }

        private void DrawAxes(DrawingContext context, Rect bounds)
        {
            var axisPen = new Pen(Brushes.DimGray, 1.5);
            double baseline = bounds.Height * 0.8;
            context.DrawLine(axisPen, new Point(0, baseline), new Point(bounds.Width, baseline));
        }

        private void DrawWaveform(DrawingContext context, Rect bounds)
        {
            if (_points.Count < 2)
                return;

            var geometry = new StreamGeometry();
            using (var gc = geometry.Open())
            {
                var start = TransformPoint(_points[0], bounds);
                gc.BeginFigure(start, false);
                for (int i = 1; i < _points.Count; i++)
                {
                    gc.LineTo(TransformPoint(_points[i], bounds));
                }
            }
            context.DrawGeometry(null, new Pen(Brushes.Lime, 2), geometry);
        }

        private void DrawInfo(DrawingContext context, Rect bounds)
        {
            var info = $"时间段：{CurrentWindowStart:0}ms - {CurrentWindowEnd:0}ms";
            var text = new FormattedText(
                info,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                12,
                Brushes.White);
            context.DrawText(text, new Point(12, 10));

            var sub = $"刷新周期 50ms · 方波模拟 TCP 包";
            var text2 = new FormattedText(
                sub,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                Brushes.Gray);
            context.DrawText(text2, new Point(12, 28));
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
    public class TcpRealtimeWaveformCanvas : Control
    {
        private const double WindowDurationMs = 4000d;

        private readonly object _gate = new();
        private readonly List<double> _times = new();
        private readonly List<float> _values = new();

        private double _windowStartMs;
        private double _currentTimeMs;
        private double _lastValue;
        private double? _fixedMin;
        private double? _fixedMax;
        private bool _boundsLocked;
        private bool _autoCalibrating = true;

        public event Action<double, double>? WindowRangeChanged;

        public void AppendSamples(IReadOnlyList<float> samples, double sampleIntervalMs)
        {
            if (samples == null || samples.Count == 0) return;
            if (sampleIntervalMs <= 0) sampleIntervalMs = 1;

            lock (_gate)
            {
                double t = _currentTimeMs;

                if (_times.Count == 0)
                {
                    StartNewWindow(t, samples[0]);
                }

                for (int i = 0; i < samples.Count; i++)
                {
                    double elapsed = t - _windowStartMs;
                    if (elapsed >= WindowDurationMs)
                    {
                        if (!_boundsLocked && _values.Count > 0)
                        {
                            _fixedMin = _values.Min();
                            _fixedMax = _values.Max();
                            _boundsLocked = true;
                            _autoCalibrating = false;
                        }

                        // 严格按窗口长度翻页，避免累积误差
                        double nextWindowStart = _windowStartMs + WindowDurationMs;
                        StartNewWindow(nextWindowStart, (float)_lastValue);
                        // 因为 t 可能比 nextWindowStart 大（采样间隔累加），重新计算 elapsed
                        elapsed = t - _windowStartMs;
                    }

                    _times.Add(t); // 绝对时间
                    _values.Add(samples[i]);

                    _lastValue = samples[i];
                    t += sampleIntervalMs;
                }

                _currentTimeMs = t;
            }

            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
        }

        public void Reset()
        {
            lock (_gate)
            {
                _times.Clear();
                _values.Clear();
                _windowStartMs = 0;
                _currentTimeMs = 0;
                _lastValue = 0;
            }
            _fixedMin = null;
            _fixedMax = null;
            _boundsLocked = false;
            _autoCalibrating = true;
            WindowRangeChanged?.Invoke(0, WindowDurationMs);
            InvalidateVisual();
        }

        private void StartNewWindow(double startTimeMs, float seedValue)
        {
            _windowStartMs = startTimeMs;
            _times.Clear();
            _values.Clear();
            _times.Add(startTimeMs); // 绝对时间，用于后续绘制比例
            _values.Add(seedValue);
            WindowRangeChanged?.Invoke(_windowStartMs, _windowStartMs + WindowDurationMs);
        }

        private (double[] times, float[] values) Snapshot()
        {
            lock (_gate)
            {
                return (_times.ToArray(), _values.ToArray());
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            var bounds = new Rect(Bounds.Size);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            var (times, values) = Snapshot();
            context.Custom(new WaveformDrawOp(bounds, _windowStartMs, WindowDurationMs, times, values, _lastValue, _fixedMin, _fixedMax, FixBoundsOnce, _autoCalibrating));
        }

        private void FixBoundsOnce(double min, double max)
        {
            lock (_gate)
            {
                if (_fixedMin is null || _fixedMax is null)
                {
                    _fixedMin = min;
                    _fixedMax = max;
                }
            }
        }

        private sealed class WaveformDrawOp : ICustomDrawOperation
        {
            private readonly Rect _bounds;
            private readonly double _windowStart;
            private readonly double _windowDuration;
            private readonly double[] _times;
            private readonly float[] _values;
            private readonly double _last;
            private readonly double? _fixedMin;
            private readonly double? _fixedMax;
            private readonly Action<double, double>? _fixBounds;
            private readonly bool _autoCalibrating;

            public WaveformDrawOp(Rect bounds, double windowStart, double windowDuration, double[] times, float[] values, double last, double? fixedMin, double? fixedMax, Action<double, double>? fixBounds, bool autoCalibrating)
            {
                _bounds = bounds;
                _windowStart = windowStart;
                _windowDuration = windowDuration;
                _times = times;
                _values = values;
                _last = last;
                _fixedMin = fixedMin;
                _fixedMax = fixedMax;
                _fixBounds = fixBounds;
                _autoCalibrating = autoCalibrating;
            }

            public void Render(ImmediateDrawingContext context)
            {
                var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                using var lease = leaseFeature?.Lease();
                var canvas = lease?.SkCanvas;
                if (canvas is null) return;

                var rect = new SKRect((float)_bounds.X, (float)_bounds.Y, (float)(_bounds.X + _bounds.Width), (float)(_bounds.Y + _bounds.Height));
                canvas.Save();
                canvas.ClipRect(rect);
                DrawBackground(canvas, rect);
                DrawGrid(canvas, rect);
                DrawWave(canvas, rect);
                DrawOverlay(canvas, rect);
                canvas.Restore();
            }

            private void DrawBackground(SKCanvas canvas, SKRect rect)
            {
                using var paint = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(
                        new SKPoint(rect.Left, rect.Top),
                        new SKPoint(rect.Left, rect.Bottom),
                        new[] { new SKColor(0x10, 0x16, 0x22), new SKColor(0x05, 0x0A, 0x12) },
                        null,
                        SKShaderTileMode.Clamp)
                };
                canvas.DrawRect(rect, paint);
            }

            private void DrawGrid(SKCanvas canvas, SKRect rect)
            {
                using var paint = new SKPaint
                {
                    Color = new SKColor(0x1F, 0x2E, 0x45),
                    StrokeWidth = 1,
                    Style = SKPaintStyle.Stroke,
                    PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
                };

                for (int i = 0; i <= 12; i++)
                {
                    float x = rect.Left + (float)(rect.Width * i / 12d);
                    canvas.DrawLine(x, rect.Top, x, rect.Bottom, paint);
                }

                for (int i = 0; i <= 6; i++)
                {
                    float y = rect.Top + (float)(rect.Height * i / 6d);
                    canvas.DrawLine(rect.Left, y, rect.Right, y, paint);
                }
            }

            private void DrawWave(SKCanvas canvas, SKRect rect)
            {
                if (_times.Length < 2) return;

                float min = _values.Length > 0 ? _values.Min() : 0;
                float max = _values.Length > 0 ? _values.Max() : 0;
                if (_fixedMin is not null && _fixedMax is not null)
                {
                    min = (float)_fixedMin.Value;
                    max = (float)_fixedMax.Value;
                }
                else
                {
                    if (Math.Abs(max - min) < 1e-6f)
                    {
                        max = min + 1e-3f;
                    }
                    _fixBounds?.Invoke(min, max);
                }

                using var paint = new SKPaint
                {
                    Color = new SKColor(0x00, 0xFF, 0xC6),
                    StrokeWidth = 2.5f,
                    Style = SKPaintStyle.Stroke,
                    StrokeJoin = SKStrokeJoin.Bevel,
                    IsAntialias = true
                };

                using var path = new SKPath();
                bool started = false;
                for (int i = 0; i < _times.Length; i++)
                {
                    double ratio = (_times[i] - _windowStart) / _windowDuration;
                    if (ratio < 0) continue;
                    if (ratio > 1 && started) break;

                    float x = rect.Left + (float)(ratio * rect.Width);
                    float value = _values[i];
                    float high = rect.Top + rect.Height * 0.2f;
                    float low = rect.Top + rect.Height * 0.8f;
                    float y = low - (value - min) / Math.Max(1e-6f, (max - min)) * (low - high);

                    if (!started)
                    {
                        path.MoveTo(x, y);
                        started = true;
                    }
                    else
                    {
                        path.LineTo(x, y);
                    }
                }

                if (started)
                {
                    canvas.DrawPath(path, paint);
                }
            }

            private void DrawOverlay(SKCanvas canvas, SKRect rect)
            {
                using var paint = new SKPaint
                {
                    Color = SKColors.White,
                    IsAntialias = true,
                    TextSize = 12
                };
                float min = _values.Length > 0 ? _values.Min() : 0;
                float max = _values.Length > 0 ? _values.Max() : 0;
                if (_fixedMin is not null && _fixedMax is not null)
                {
                    min = (float)_fixedMin.Value;
                    max = (float)_fixedMax.Value;
                }
                string info = $"Latest: {_last:F3} mV   Min: {min:F3} mV   Max: {max:F3} mV";
                canvas.DrawText(info, rect.Left + 12, rect.Top + 18, paint);

                if (_autoCalibrating)
                {
                    using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 180), IsAntialias = true };
                    var overlay = new SKRect(rect.Left + 6, rect.Top + 6, rect.Right - 6, rect.Bottom - 6);
                    canvas.DrawRect(overlay, bg);
                    using var alertPaint = new SKPaint
                    {
                        Color = SKColors.White,
                        IsAntialias = true,
                        TextSize = 20,
                        FakeBoldText = true
                    };
                    // 文本居中
                    var message = "Calibrating...";
                    var bounds = new SKRect();
                    alertPaint.MeasureText(message, ref bounds);
                    float textX = overlay.Left + (overlay.Width - bounds.Width) / 2;
                    float textY = overlay.Top + (overlay.Height + bounds.Height) / 2 - bounds.Bottom;
                    canvas.DrawText(message, textX, textY, alertPaint);
                }
            }

            public Rect Bounds => _bounds;
            public bool HitTest(Point p) => _bounds.Contains(p);
            public void Dispose() { }
            public bool Equals(ICustomDrawOperation? other) => false;
        }
    }

    public class TcpRealtimeWaveformControl : UserControl
    {
        private sealed class ChannelDescriptor
        {
            public ChannelDescriptor(string key, string display)
            {
                Key = key;
                Display = display;
            }

            public string Key { get; }
            public string Display { get; }
            public override string ToString() => Display;
        }

        private readonly ObservableCollection<ChannelDescriptor> _channels = new();
        private TcpRealtimeWaveformCanvas _canvas = null!;
        private TextBox _ipBox = null!;
        private TextBox _portBox = null!;
        private ComboBox _channelCombo = null!;
        private TextBlock _statusText = null!;
        private TextBlock _lastPacketText = null!;
        private TextBlock _windowInfoText = null!;
        private Button _connectButton = null!;
        private Button _disconnectButton = null!;

        private TcpRealtimeIngestor? _ingestor;
        private string? _selectedChannelKey;

        public TcpRealtimeWaveformControl()
        {
            Content = BuildLayout();

            _connectButton.Click += async (_, _) => await ConnectAsync();
            _disconnectButton.Click += async (_, _) => await DisconnectAsync();
            _channelCombo.SelectionChanged += (_, _) =>
            {
                if (_channelCombo.SelectedItem is ChannelDescriptor descriptor)
                {
                    _selectedChannelKey = descriptor.Key;
                }
                else
                {
                    _selectedChannelKey = null;
                }
                _canvas.Reset();
            };
        }

        private Control BuildLayout()
        {
            _ipBox = new TextBox { Width = 140, Text = "127.0.0.1" };
            _portBox = new TextBox { Width = 80, Text = "4008" };
            _channelCombo = new ComboBox
            {
                Width = 210,
                PlaceholderText = "等待数据..."
            };
            _channelCombo.ItemsSource = _channels;
            _statusText = new TextBlock
            {
                Text = "未连接",
                Foreground = Brushes.OrangeRed,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.Bold
            };
            _lastPacketText = new TextBlock
            {
                Text = "暂无数据",
                Foreground = Brushes.LightGreen,
                VerticalAlignment = VerticalAlignment.Center
            };
            _windowInfoText = new TextBlock
            {
                Text = "当前窗口：0ms - 4000ms",
                Foreground = Brushes.LightGray,
                VerticalAlignment = VerticalAlignment.Center
            };
            _connectButton = new Button
            {
                Content = "连接",
                Padding = new Thickness(16, 5),
                Background = new SolidColorBrush(Color.Parse("#28A745")),
                Foreground = Brushes.White
            };
            _disconnectButton = new Button
            {
                Content = "断开",
                Padding = new Thickness(16, 5),
                Background = new SolidColorBrush(Color.Parse("#AA3333")),
                Foreground = Brushes.White,
                IsEnabled = false
            };

            var topRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock{ Text = "TCP服务器:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    _ipBox,
                    new TextBlock{ Text = "端口:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    _portBox,
                    _connectButton,
                    _disconnectButton,
                    _statusText
                }
            };

            var secondRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock{ Text = "通道:", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center },
                    _channelCombo,
                    _lastPacketText,
                    _windowInfoText
                }
            };

            _canvas = new TcpRealtimeWaveformCanvas();
            _canvas.WindowRangeChanged += (start, end) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _windowInfoText.Text = $"当前窗口：{start:0}ms - {end:0}ms";
                });
            };
            _canvas.Reset();

            var grid = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                },
                Margin = new Thickness(0, 0, 0, 12),
            };
            grid.Children.Add(topRow);
            Grid.SetRow(secondRow, 1);
            grid.Children.Add(secondRow);
            Grid.SetRow(_canvas, 2);
            grid.Children.Add(_canvas);

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1C1F26")),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16),
                Child = grid,
                Height = 420,
                MinWidth = 900
            };
        }

        private async Task ConnectAsync()
        {
            if (_ingestor != null)
            {
                await DisconnectAsync();
            }

            if (!int.TryParse(_portBox.Text, out int port))
            {
                UpdateStatus("端口无效", false);
                return;
            }

            string host = string.IsNullOrWhiteSpace(_ipBox.Text) ? "127.0.0.1" : _ipBox.Text.Trim();

            _ingestor = new TcpRealtimeIngestor(host, port);
            _ingestor.SamplesReceived += OnSamplesReceived;
            _ingestor.ConnectionStatusChanged += OnConnectionStatusChanged;

            _connectButton.IsEnabled = false;
            _disconnectButton.IsEnabled = true;
            await _ingestor.StartAsync();
        }

        private void OnConnectionStatusChanged(object? sender, TcpConnectionStatusEventArgs e)
        {
            Dispatcher.UIThread.Post(() => UpdateStatus(e.Message, e.IsConnected));
        }

        private void UpdateStatus(string message, bool connected)
        {
            _statusText.Text = message;
            _statusText.Foreground = connected ? Brushes.LimeGreen : Brushes.OrangeRed;
            if (!connected)
            {
                _connectButton.IsEnabled = true;
                _disconnectButton.IsEnabled = false;
            }
        }

        private void OnSamplesReceived(object? sender, TcpSamplesEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var descriptor = _channels.FirstOrDefault(c => c.Key == e.ChannelKey);
                if (descriptor == null)
                {
                    descriptor = new ChannelDescriptor(e.ChannelKey, e.DisplayName);
                    _channels.Add(descriptor);
                }

                if (_selectedChannelKey == null)
                {
                    _selectedChannelKey = descriptor.Key;
                    _channelCombo.SelectedItem = descriptor;
                }

                if (_selectedChannelKey == e.ChannelKey)
                {
                    _channelCombo.SelectedItem = descriptor;
                    _canvas.AppendSamples(e.Samples, e.SampleIntervalMs);
                    _lastPacketText.Text = $"最近包：{e.Timestamp:HH:mm:ss}";
                }
            });
        }

        private async Task DisconnectAsync()
        {
            if (_ingestor != null)
            {
                _ingestor.SamplesReceived -= OnSamplesReceived;
                _ingestor.ConnectionStatusChanged -= OnConnectionStatusChanged;
                await _ingestor.DisposeAsync();
                _ingestor = null;
            }

            _channels.Clear();
            _canvas.Reset();
            _selectedChannelKey = null;
            _channelCombo.SelectedItem = null;
            _lastPacketText.Text = "暂无数据";
            _windowInfoText.Text = "当前窗口：0ms - 2000ms";
            UpdateStatus("未连接", false);
        }

        protected override async void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            await DisconnectAsync();
            base.OnDetachedFromVisualTree(e);
        }
    }

    internal sealed class TcpRealtimeIngestor : IAsyncDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _endpointTag;
        private readonly StreamDecoder _decoder = new();
        private CancellationTokenSource? _cts;
        private Task? _worker;

        public event EventHandler<TcpSamplesEventArgs>? SamplesReceived;
        public event EventHandler<TcpConnectionStatusEventArgs>? ConnectionStatusChanged;

        public TcpRealtimeIngestor(string host, int port)
        {
            _host = host;
            _port = port;
            _endpointTag = $"{host}:{port}";
        }

        public Task StartAsync(CancellationToken token = default)
        {
            if (_worker != null) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            _worker = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_cts == null) return;
            _cts.Cancel();
            try
            {
                if (_worker != null)
                {
                    await _worker.ConfigureAwait(false);
                }
            }
            catch { }
            finally
            {
                _worker = null;
                _cts = null;
            }
        }

        private async Task RunAsync(CancellationToken token)
        {
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "正在连接..."));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_host, _port, token).ConfigureAwait(false);
                using var stream = client.GetStream();
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(true, $"已连接: {_host}:{_port}"));
                await ReceiveLoopAsync(stream, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "连接已取消"));
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"连接失败: {ex.Message}"));
            }
            finally
            {
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "连接已结束"));
            }
        }

        private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }
                    _decoder.Append(buffer, 0, read);
                    DrainPackets();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void DrainPackets()
        {
            while (_decoder.TryDequeue(out var packet))
            {
                if (packet.Header.Command != 0x7C)
                    continue;

                if (!TimeSeriesParser.TryParsePayload(packet.Payload, _endpointTag, out var payload, out var error))
                {
                    ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(true, $"解析失败: {error ?? "未知"}"));
                    continue;
                }

                for (int i = 0; i < payload.ChannelCount; i++)
                {
                    var samples = payload.SamplesByChannel[i];
                    if (samples == null || samples.Length == 0)
                        continue;
                    string display = i < payload.ChannelNames.Length ? payload.ChannelNames[i] : $"CH{i + 1}";
                    string key = i < payload.ChannelKeys.Length ? payload.ChannelKeys[i] : $"{_endpointTag}-dev1-ch{display}";
                    double interval = payload.BucketDuration.TotalMilliseconds / Math.Max(1, samples.Length);
                    SamplesReceived?.Invoke(this, new TcpSamplesEventArgs(key, display, samples, payload.BucketDuration, interval, payload.UtcTimestamp));
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _decoder.Dispose();
        }
    }

    internal sealed class TcpSamplesEventArgs : EventArgs
    {
        public string ChannelKey { get; }
        public string DisplayName { get; }
        public float[] Samples { get; }
        public TimeSpan BucketDuration { get; }
        public double SampleIntervalMs { get; }
        public DateTimeOffset Timestamp { get; }

        public TcpSamplesEventArgs(string channelKey, string displayName, float[] samples, TimeSpan bucket, double sampleIntervalMs, DateTimeOffset timestamp)
        {
            ChannelKey = channelKey;
            DisplayName = displayName;
            Samples = samples;
            BucketDuration = bucket;
            SampleIntervalMs = sampleIntervalMs;
            Timestamp = timestamp;
        }
    }

    internal sealed class TcpConnectionStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string Message { get; }

        public TcpConnectionStatusEventArgs(bool isConnected, string message)
        {
            IsConnected = isConnected;
            Message = message;
        }
    }

    internal sealed class Packet
    {
        public PacketHeader Header { get; }
        public byte[] Payload { get; }

        public Packet(PacketHeader header, byte[] payload)
        {
            Header = header;
            Payload = payload;
        }
    }

    internal readonly record struct PacketHeader(uint Magic, uint Command, uint Length);

    internal sealed class StreamDecoder : IDisposable
    {
        private const uint MagicConst = 0x55AAAA55;
        private const int HeaderSize = 12;

        private readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;
        private byte[] _buffer = Array.Empty<byte>();
        private int _count;

        public StreamDecoder()
        {
            _buffer = _pool.Rent(4096);
        }

        public void Append(byte[] source, int offset, int length)
        {
            if (length <= 0) return;
            EnsureCapacity(_count + length);
            Buffer.BlockCopy(source, offset, _buffer, _count, length);
            _count += length;
        }

        public bool TryDequeue(out Packet packet)
        {
            packet = null!;
            while (true)
            {
                if (_count < HeaderSize) return false;
                uint magic = ReadU32(_buffer, 0);
                if (magic != MagicConst)
                {
                    TrimLeft(1);
                    continue;
                }
                uint cmd = ReadU32(_buffer, 4);
                uint len = ReadU32(_buffer, 8);
                long total = HeaderSize + len;
                if (_count < total) return false;
                var payload = new byte[len];
                Buffer.BlockCopy(_buffer, HeaderSize, payload, 0, (int)len);
                packet = new Packet(new PacketHeader(magic, cmd, len), payload);
                TrimLeft((int)total);
                return true;
            }
        }

        private void TrimLeft(int count)
        {
            if (count <= 0) return;
            if (count >= _count)
            {
                _count = 0;
                return;
            }
            Buffer.BlockCopy(_buffer, count, _buffer, 0, _count - count);
            _count -= count;
        }

        private void EnsureCapacity(int needed)
        {
            if (_buffer.Length >= needed) return;
            int size = _buffer.Length;
            while (size < needed)
            {
                size = size < 1024 * 1024 ? size * 2 : size + 1024 * 1024;
            }
            var next = _pool.Rent(size);
            if (_count > 0)
            {
                Buffer.BlockCopy(_buffer, 0, next, 0, _count);
            }
            _pool.Return(_buffer);
            _buffer = next;
        }

        private static uint ReadU32(byte[] buffer, int offset)
            => (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

        public void Dispose()
        {
            var buf = _buffer;
            _buffer = Array.Empty<byte>();
            if (buf.Length > 0)
            {
                _pool.Return(buf);
            }
        }
    }

    internal static class TimeSeriesParser
    {
        public static bool TryParsePayload(ReadOnlySpan<byte> payload, string endpointTag, out TimeSeriesPayload result, out string? error)
        {
            result = default!;
            error = null;

            if (payload.Length < 16)
            {
                error = $"payload太短：{payload.Length}";
                return false;
            }

            ulong total = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(0, 8));
            int pkt = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4));
            int ch = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4));

            long need = 16L + 4L * pkt * ch;
            if (payload.Length < need)
            {
                error = $"payload长度不足：{payload.Length} < {need}";
                return false;
            }

            ReadOnlySpan<float> all = MemoryMarshal.Cast<byte, float>(payload.Slice(16, pkt * ch * 4));
            int offset = 16 + pkt * ch * 4;

            if (payload.Length - offset < 4)
            {
                error = "缺少通道名称长度字段";
                return false;
            }
            uint nameLen = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));
            offset += 4;
            if (payload.Length - offset < nameLen)
            {
                error = "通道名称数据不足";
                return false;
            }

            string[] names = Array.Empty<string>();
            if (nameLen > 0)
            {
                var nameBytes = payload.Slice(offset, (int)nameLen);
                string raw = Encoding.ASCII.GetString(nameBytes);
                names = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
            }
            offset += (int)nameLen;

            if (payload.Length - offset < 12)
            {
                error = "缺少时间戳字段";
                return false;
            }

            ulong epochSeconds = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset, 8));
            offset += 8;
            uint micro = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4));

            if (names.Length < ch)
            {
                var expanded = new string[ch];
                for (int i = 0; i < ch; i++)
                {
                    expanded[i] = i < names.Length ? names[i] : $"CH{i + 1}";
                }
                names = expanded;
            }

            var outputs = new float[ch][];
            var channelKeys = new string[ch];

            for (int channelIndex = 0; channelIndex < ch; channelIndex++)
            {
                string channelName = names[channelIndex];
                string key = $"{endpointTag}-dev1-ch{channelName}";
                channelKeys[channelIndex] = key;

                outputs[channelIndex] = new float[pkt];
                for (int i = 0; i < pkt; i++)
                {
                    outputs[channelIndex][i] = all[i * ch + channelIndex];
                }
            }

            // 无协议采样间隔字段，默认每样本 1ms，对应包总时长 pkt ms
            var bucketDuration = TimeSpan.FromMilliseconds(Math.Max(1, pkt));

            result = new TimeSeriesPayload
            {
                CollectedTotal = total,
                PacketCount = pkt,
                ChannelCount = ch,
                SamplesByChannel = outputs,
                ChannelNames = names,
                ChannelKeys = channelKeys,
                BucketDuration = bucketDuration,
                UtcTimestamp = DateTimeOffset.FromUnixTimeSeconds((long)epochSeconds)
                    .AddTicks(micro * 10L)
            };
            return true;
        }
    }

    internal sealed class TimeSeriesPayload
    {
        public ulong CollectedTotal { get; init; }
        public int PacketCount { get; init; }
        public int ChannelCount { get; init; }
        public float[][] SamplesByChannel { get; init; } = Array.Empty<float[]>();
        public string[] ChannelNames { get; init; } = Array.Empty<string>();
        public string[] ChannelKeys { get; init; } = Array.Empty<string>();
        public TimeSpan BucketDuration { get; init; }
        public DateTimeOffset UtcTimestamp { get; init; }
    }
}
