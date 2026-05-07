using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using DH.Client.App.Data;
using DH.Client.App.Services.Performance;
using System.Collections.Generic;
using System.Linq;
using DH.Client.App.Controls;

namespace DH.Client.App.Views
{
    public partial class CurvePanel : UserControl
    {
        private SkiaMultiChannelView _skView;
        private Border? _openGLContainerRef;
        private ChannelSelector? _channelSelector;
        private ChannelSelector? _flyoutChannelSelector;
        private Button? _toggleChannelSelectorButton;
        private Button? _zoomInXButton;
        private Button? _zoomOutXButton;
        private Button? _zoomInYButton;
        private Button? _zoomOutYButton;
        private Button? _resetZoomButton;
        private TextBlock? _selectedChannelsText;
        private OnlineChannelManager? _onlineChannelManager; // 在线通道管理器
        private List<int> _selectedChannelIds = new();
        private float _zoomLevelX = 1.0f;  // 横轴缩放
        private float _zoomLevelY = 2.0f;  // 纵轴缩放，调整为适合50-100振幅范围，显示为100-200像素
        private bool _autoFitX = true;   // 自动适配X轴以完整显示曲线
        private bool _autoFitY = true;   // 自动适配Y轴以完整显示曲线
        public bool IsSelected { get; private set; } = false; // 视图选中状态
        private int _deviceFilterId = 0; // 当前设备过滤（0=不限制）
        private bool _disableAutoSelection = false; // 禁用自动选择通道（用户主动清空时）

        private const double SweepWindowSeconds = 10.0;
        private readonly CurvePanelSnapshotCache _snapshotCache = new();
        private readonly CurvePanelFrameProviderShim _frameProviderShim;
        private bool _useExternalFrameSnapshot;
        private bool _requiresExternalFrameSnapshot;
        private int _selectionChangeVersion;

        private bool UsesExternalFramePipeline => _requiresExternalFrameSnapshot || _useExternalFrameSnapshot;

        public event EventHandler<IReadOnlyList<int>>? SelectedChannelsCommitted;
        public event EventHandler<CurvePanelZoomStateChangedEventArgs>? ZoomStateCommitted;

        public CurvePanel()
        {
            _frameProviderShim = new CurvePanelFrameProviderShim(
                _snapshotCache,
                () => _requiresExternalFrameSnapshot,
                () => _useExternalFrameSnapshot,
                () => _selectedChannelIds.Count,
                GetDiagnosticTag,
                ClearMissingFrameSnapshot);
            InitializeComponent();
            
            // 获取UI元素引用
            _channelSelector = this.FindControl<ChannelSelector>("ChannelSelector");
            _flyoutChannelSelector = this.FindControl<ChannelSelector>("FlyoutChannelSelector");
            _toggleChannelSelectorButton = this.FindControl<Button>("ToggleChannelSelectorButton");
            _zoomInXButton = this.FindControl<Button>("ZoomInXButton");
            _zoomOutXButton = this.FindControl<Button>("ZoomOutXButton");
            _zoomInYButton = this.FindControl<Button>("ZoomInYButton");
            _zoomOutYButton = this.FindControl<Button>("ZoomOutYButton");
            _resetZoomButton = this.FindControl<Button>("ResetZoomButton");
            _selectedChannelsText = this.FindControl<TextBlock>("SelectedChannelsText");
            
            // 创建 OpenGL 曲线视图（专门用于曲线绘制）
            _skView = new SkiaMultiChannelView
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                UseExtremaAggregation = false,
                UseEnvelopeRepresentativePoints = true,
                SamplingDensityFactor = 1.0,
                // 时间轴滚动模式
                UseTimeAxis = false,
                UseDataXValues = true,
                UseFixedDataXRange = true,
                FixedDataXMin = 0.0,
                FixedDataXMax = SweepWindowSeconds,
                SampleRateHz = 100,  // 采样率 100 Hz（可根据实际调整）
                TimeWindowSeconds = 20.0, // 固定显示 20 秒
                ScrollMode = false,
                ScrollWindowSize = 2000, // 不使用
                UseOscilloscopeMode = false, // 不使用示波器模式
                ReverseXRendering = false // 禁用 X 轴反转
            };

            // 横轴刻度与标签格式：显示整数秒（如 0s、5s、10s、15s、20s）
            _skView.DesiredXTicks = 5; // 20 秒窗口 → 5 个主刻度 → 步长约 5s
            _skView.ShowAbsoluteTime = false;
            _skView.TimeWindowSeconds = SweepWindowSeconds;
            _skView.FormatXLabel = FormatXAxisLabel;
            
            // 添加到容器并保存引用（用于选中高亮）
            _openGLContainerRef = this.FindControl<Border>("OpenGLContainer");
            _openGLContainerRef.Child = _skView;
            
            // 设置事件处理
            if (_zoomInXButton != null) _zoomInXButton.Click += OnZoomInXClick;
            if (_zoomOutXButton != null) _zoomOutXButton.Click += OnZoomOutXClick;
            if (_zoomInYButton != null) _zoomInYButton.Click += OnZoomInYClick;
            if (_zoomOutYButton != null) _zoomOutYButton.Click += OnZoomOutYClick;
            if (_resetZoomButton != null) _resetZoomButton.Click += OnResetZoomClick;
            if (_channelSelector != null) _channelSelector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            if (_flyoutChannelSelector != null)
                _flyoutChannelSelector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            
            // Flyout 打开时确保其内部 ChannelSelector 已正确接线
            var flyout = _toggleChannelSelectorButton?.Flyout as Flyout;
            if (flyout != null)
            {
                flyout.Opened += (s, e) => EnsureFlyoutSelectorWired();
            }
            
            // 初始化缩放函数
            UpdateZoomFunctions();
            _skView.GetPrecomputedWindowMaxAbsY = _frameProviderShim.GetWindowMaxAbsY;
            _skView.GetDiagnosticTag = GetDiagnosticTag;
            _skView.DataProvider = _frameProviderShim.GetPrimaryChannelData;
            _skView.MultiChannelDataProvider = _frameProviderShim.GetAllChannelData;
            
            // 默认：不强制修改选中状态，等待管理器设置后由ChannelSelector默认勾选在线通道
            Dispatcher.UIThread.Post(() =>
            {
                UpdateSelectedChannelsDisplay();
                _skView?.InvalidateVisual();
            }, DispatcherPriority.Loaded);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // 横轴放大按钮点击事件
        private void OnZoomInXClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = false; // 手动缩放后关闭X轴自适配
            _zoomLevelX *= 1.2f; // 放大
            CommitZoomState();
        }
        
        // 横轴缩小按钮点击事件
        private void OnZoomOutXClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = false;
            _zoomLevelX /= 1.2f; // 缩小
            CommitZoomState();
        }
        
        // 纵轴放大按钮点击事件
        private void OnZoomInYClick(object? sender, RoutedEventArgs e)
        {
            _autoFitY = false; // 手动缩放后关闭Y轴自适配
            _zoomLevelY *= 1.2f; // 放大
            CommitZoomState();
        }
        
        // 纵轴缩小按钮点击事件
        private void OnZoomOutYClick(object? sender, RoutedEventArgs e)
        {
            _autoFitY = false;
            _zoomLevelY /= 1.2f; // 缩小
            CommitZoomState();
        }
        
        // 重置缩放按钮点击事件
        private void OnResetZoomClick(object? sender, RoutedEventArgs e)
        {
            _autoFitX = true; // 重置到自适配
            _autoFitY = true;
            _zoomLevelX = 1.0f;
            _zoomLevelY = 1.0f; // 重置为1，由自适配决定最终显示比例
            CommitZoomState();
        }
        
        // 更新缩放函数
        private void UpdateZoomFunctions()
        {
            _skView.GetZoomX = () => _zoomLevelX;
            _skView.GetZoomY = () => _zoomLevelY;
            _skView.IsAutoFitX = () => _autoFitX;
            _skView.IsAutoFitY = () => _autoFitY;
        }

        private void CommitZoomState()
        {
            _zoomLevelX = Math.Max(0.01f, _zoomLevelX);
            _zoomLevelY = Math.Max(0.01f, _zoomLevelY);
            UpdateZoomFunctions();
            ZoomStateCommitted?.Invoke(this, new CurvePanelZoomStateChangedEventArgs(
                _zoomLevelX,
                _zoomLevelY,
                _autoFitX,
                _autoFitY));
            _skView.InvalidateVisual();
        }
        
        // 切换通道选择器显示/隐藏
        // 通道选择变更事件
        private void OnSelectedChannelsChanged(object? sender, SelectedChannelsChangedEventArgs e)
        {
            int selectionVersion = ++_selectionChangeVersion;
            string senderName = sender?.GetType().Name ?? "null";
            RenderPhaseTimingLogger.LogSelectionFlow(
                $"CurvePanel.OnSelectedChannelsChanged[{senderName}]",
                GetDiagnosticTag(),
                e.SelectedChannels.Count,
                $"v={selectionVersion};channels={string.Join(",", e.SelectedChannels.Take(8))}");
            _selectedChannelIds = e.SelectedChannels;
            UpdateSelectedChannelsDisplay();

            // 通道集合变化后，丢弃上一轮 sweep 提交状态，避免旧背景层/旧成品帧残留。
            _skView?.InvalidateSweepRenderState();

            if (_selectedChannelIds.Count == 0)
            {
                // ChannelSelector 在单选切换过程中会短暂发出空选择。
                // 延后一拍确认，避免把“切到另一根曲线”的中间态误判成用户主动清空。
                Dispatcher.UIThread.Post(() =>
                {
                    if (selectionVersion != _selectionChangeVersion || _selectedChannelIds.Count != 0)
                    {
                        return;
                    }

                    _skView?.ResetView();
                    _disableAutoSelection = true;
                }, DispatcherPriority.Background);
            }
            else
            {
                _disableAutoSelection = false;
            }
            SelectedChannelsCommitted?.Invoke(this, _selectedChannelIds.ToArray());
            _skView?.InvalidateVisual();
        }
        
        private void EnsureFlyoutSelectorWired()
        {
            var flyout = _toggleChannelSelectorButton?.Flyout as Flyout;
            if (flyout?.Content is ChannelSelector selector)
            {
                if (_onlineChannelManager != null)
                {
                    selector.SetOnlineChannelManager(_onlineChannelManager);
                }
                // 先取消再订阅，避免重复绑定
                selector.SelectedChannelsChanged -= OnSelectedChannelsChanged;
                selector.SelectedChannelsChanged += OnSelectedChannelsChanged;
            }
        }
        
        // External-frame 主路径只把 DataBus 作为通道选择器的数据源。
        public void AttachSelectorDataBus(DataBus dataBus)
        {
            if (_channelSelector != null)
            {
                _channelSelector.AttachDataBus(dataBus);
            }
            if (_flyoutChannelSelector != null)
            {
                _flyoutChannelSelector.AttachDataBus(dataBus);
            }
        }

        // 设置在线通道管理器
        public void SetOnlineChannelManager(OnlineChannelManager onlineChannelManager)
        {
            _onlineChannelManager = onlineChannelManager;
            
            // 将在线通道管理器传递给ChannelSelector
            if (_channelSelector != null)
            {
                _channelSelector.SetOnlineChannelManager(onlineChannelManager);
            }
            if (_flyoutChannelSelector != null)
            {
                _flyoutChannelSelector.SetOnlineChannelManager(onlineChannelManager);
            }
            
        }

        // 设置设备过滤。External-frame 主路径只把它作为视图状态下发，不自动改通道。
        public void SetDeviceFilter(int deviceId)
        {
            _deviceFilterId = Math.Clamp(deviceId, 0, 64);
            _skView?.InvalidateVisual();
        }

        // 请求重绘（由外部统一定时器调用）
        public void Invalidate()
        {
            _skView?.InvalidateVisual();
        }

        private static string FormatXAxisLabel(double seconds)
        {
            return seconds < 1.0 ? $"{seconds * 1000:0} ms" : $"{seconds:0} s";
        }

        private void ClearMissingFrameSnapshot()
        {
            ClearCachedSnapshot();
        }

        private void ClearCachedSnapshot()
        {
            _snapshotCache.Clear();
        }

        private void StoreCachedSnapshot(
            IReadOnlyDictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> windowData,
            IReadOnlyList<DH.Contracts.Models.CurvePoint> primaryChannelData,
            double windowMaxAbsY,
            int maxActualPointsPerCurve,
            int totalActualPoints,
            bool markExternalFrameAvailable = false)
        {
            _snapshotCache.Store(
                windowData,
                primaryChannelData,
                windowMaxAbsY,
                maxActualPointsPerCurve,
                totalActualPoints);
            if (markExternalFrameAvailable)
            {
                _useExternalFrameSnapshot = true;
            }
        }

        private string GetDiagnosticTag()
        {
            var channels = _selectedChannelIds.Count == 0
                ? "none"
                : string.Join("-", _selectedChannelIds.Take(4)) + (_selectedChannelIds.Count > 4 ? $"+{_selectedChannelIds.Count - 4}" : string.Empty);
            return $"panel-{GetHashCode():X8}|dev={_deviceFilterId}|ch={channels}";
        }

        // 更新选中通道显示文本
        private void UpdateSelectedChannelsDisplay()
        {
            if (_selectedChannelsText == null)
                return;
            
            if (!_selectedChannelIds.Any())
            {
                _selectedChannelsText.Text = "未选择通道";
            }
            else if (_selectedChannelIds.Count == 1)
            {
                _selectedChannelsText.Text = $"通道 {_selectedChannelIds[0]}";
            }
            else
            {
                _selectedChannelsText.Text = $"已选择 {_selectedChannelIds.Count} 个通道";
            }
        }

        // 停止渲染
        public void Stop()
        {
            // Realtime refresh is owned by MainWindow.
        }

        public void SetSelectedChannels(List<int> channelIds)
        {
            _selectedChannelIds = channelIds ?? new List<int>();
            UpdateSelectedChannelsDisplay();
            
            // 当通道列表为空时，清除视图状态
            if (_selectedChannelIds.Count == 0)
            {
                _skView?.ResetView();
                _disableAutoSelection = true; // 禁用自动选择
            }
            else
            {
                _disableAutoSelection = false; // 重新启用自动选择
            }
            SelectedChannelsCommitted?.Invoke(this, _selectedChannelIds.ToArray());
            _skView?.InvalidateVisual();
        }

        // ===== 选中与高亮 =====
        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (_openGLContainerRef != null)
            {
                _openGLContainerRef.BorderBrush = new SolidColorBrush(Color.Parse(selected ? "#409EFF" : "#2B2B2B"));
                _openGLContainerRef.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
            }
        }

        public void SetZoomState(float zoomX, float zoomY, bool autoFitX, bool autoFitY)
        {
            _zoomLevelX = Math.Max(0.01f, zoomX);
            _zoomLevelY = Math.Max(0.01f, zoomY);
            _autoFitX = autoFitX;
            _autoFitY = autoFitY;
            UpdateZoomFunctions();
            _skView.InvalidateVisual();
        }

        public void RequireExternalFrameSnapshot()
        {
            _requiresExternalFrameSnapshot = true;
            _useExternalFrameSnapshot = false;
        }

        public void SetExternalFrameSnapshot(
            IReadOnlyDictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> windowData,
            double windowMaxAbsY,
            int maxActualPointsPerCurve,
            int totalActualPoints)
        {
            IReadOnlyList<DH.Contracts.Models.CurvePoint> primaryChannelData = Array.Empty<DH.Contracts.Models.CurvePoint>();
            if (_selectedChannelIds.Count > 0 &&
                windowData.TryGetValue(_selectedChannelIds[0], out var primary))
            {
                primaryChannelData = primary;
            }
            else if (windowData.Count > 0)
            {
                primaryChannelData = windowData.Values.FirstOrDefault()
                    ?? Array.Empty<DH.Contracts.Models.CurvePoint>();
            }

            StoreCachedSnapshot(
                windowData,
                primaryChannelData,
                windowMaxAbsY,
                maxActualPointsPerCurve,
                totalActualPoints,
                markExternalFrameAvailable: true);
        }

        // 更新采样频率
        public void UpdateSampleRate(int sampleRate)
        {
            if (_skView != null)
            {
                _skView.SampleRateHz = sampleRate;
                _autoFitX = true;
                _zoomLevelX = 1.0f;
                UpdateZoomFunctions();
                _skView.UseDataXValues = true;
                _skView.UseFixedDataXRange = true;
                _skView.FixedDataXMin = 0.0;
                _skView.FixedDataXMax = SweepWindowSeconds;
                _skView.TimeWindowSeconds = SweepWindowSeconds;
                _skView.UseTimeAxis = false;
                _skView.InvalidateVisual();
                _skView.UseTimeAxis = false;
                _skView.ShowAbsoluteTime = false;
                _skView.ResetView();
                _skView.InvalidateVisual();
            }
        }
    }

    public sealed class CurvePanelZoomStateChangedEventArgs : EventArgs
    {
        public CurvePanelZoomStateChangedEventArgs(float zoomX, float zoomY, bool autoFitX, bool autoFitY)
        {
            ZoomX = zoomX;
            ZoomY = zoomY;
            AutoFitX = autoFitX;
            AutoFitY = autoFitY;
        }

        public float ZoomX { get; }
        public float ZoomY { get; }
        public bool AutoFitX { get; }
        public bool AutoFitY { get; }
    }
}
