using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DH.Client.App.Controls;
using DH.Client.App.Data;
using DH.Client.App.Services.Performance;
using DH.Client.App.Views;
using DH.Configmanage.MockConfig;
// 暂时移除 ScottPlot 5.x 依赖以兼容 .NET 6.0
// using DH.Display.Realtime;
// using DH.Display.Skia.Realtime;

namespace DH.Client.App.Views;

public partial class MainWindow : Window
{
    private static readonly TimeSpan MetricsSampleInterval = TimeSpan.FromSeconds(1);
    private const double SingleViewTargetFramesPerSecond = 80.0;
    private const double MultiViewTargetFramesPerSecond = 60.0;
    private const int TargetPointsPerCurveThreshold = 4000;
    private const double BatchSweepWindowSeconds = 10.0;
    private const int BatchSweepHistoryPointBudget = 8192;
    private const int BatchMinPreviewPointsPerChannel = 64;
    private const int BatchMaxPreviewPointsPerChannel = 4000;
    private const float DefaultZoomX = 1.0f;
    private const float DefaultZoomY = 1.0f;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _metricsTimer;
    private MockConfig? _cfg;
    // private GridHub? _hub;
    // UI曲线面板的数据总线（将 IDataBus 的帧桥接为 CurvePoint）

    private DataHub? _dataHub;
    private RealtimeDisplayCache? _displayCache;
    private RealtimeSweepSnapshotCache? _sweepSnapshotCache;
    // 多视图容器及面板集合
    private UniformGrid? _viewsContainer;
    private Button? _addViewButton;
    private Button? _removeViewButton;
    private StackPanel? _resultsControlsPanel;
    private Button? _globalChannelSelectorButton;
    private ChannelSelector? _globalChannelSelector;
    private Button? _preset1x64Button;
    private Button? _preset16x16Button;
    private Button? _preset64x16Button;
    private Button? _preset64x64Button;
    private string _lastFrameSource = "legacy-uninitialized";
    private int _lastExternalFrameFallbackPanelCount;
    private int _lastFrameActiveChannelCount;
    private int _lastFrameCachedChannelCount;
    private int _lastFrameMaxActualPointsPerCurve;
    private int _lastFrameTotalActualPoints;
    private double _batchSweepWindowStartSeconds;
    // 视图选择机制
    private CurvePanel? _selectedPanel;
    private int _selectedIndex = -1;
    private readonly List<CurvePanel> _curvePanels = new();
    private readonly List<CurveViewState> _curveViewStates = new();
    private bool _globalSelectionFlyoutOpen;
    private CurvePanel? _pendingGlobalSelectionTarget;
    private List<int>? _pendingGlobalSelectionChannels;
    private bool _suppressGlobalSelectionApply;
    private PerformanceMetricsRecorder? _performanceRecorder;
    private string? _baseTitle;
    private readonly Random _presetRandom = new();

    private sealed class CurveViewState
    {
        public List<int> SelectedChannelIds { get; set; } = new();
        public int DeviceFilterId { get; set; }
        public float ZoomX { get; set; } = 1.0f;
        public float ZoomY { get; set; } = 1.0f;
        public bool AutoFitX { get; set; } = true;
        public bool AutoFitY { get; set; } = true;
        public bool IsSelected { get; set; }
    }

    private readonly record struct RealtimeCurveFrameSnapshot(
        int ActiveChannelCount,
        IReadOnlyDictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>> WindowData,
        double WindowMaxAbsY,
        int MaxActualPointsPerChannel,
        int TotalActualPoints)
    {
        public static RealtimeCurveFrameSnapshot Empty { get; } = new(
            ActiveChannelCount: 0,
            WindowData: new Dictionary<int, IReadOnlyList<DH.Contracts.Models.CurvePoint>>(),
            WindowMaxAbsY: 1.0,
            MaxActualPointsPerChannel: 0,
            TotalActualPoints: 0);
    }

    /// <summary>
    /// 根据视图数量计算最优的网格布局（行×列）
    /// 优先选择正方形布局，如8×8, 7×7, 6×6等
    /// 对于非完全平方数，选择最接近的矩形布局
    /// </summary>
    /// <param name="viewCount">视图数量</param>
    /// <returns>元组(行数, 列数)</returns>
    private static (int rows, int cols) CalculateOptimalGrid(int viewCount)
    {
        if (viewCount <= 0) return (1, 1);
        if (viewCount == 1) return (1, 1);
        
        // 计算平方根，优先选择正方形布局
        var sqrt = (int)Math.Ceiling(Math.Sqrt(viewCount));
        
        // 检查是否为完全平方数
        if (sqrt * sqrt == viewCount)
        {
            return (sqrt, sqrt);
        }
        
        // 对于非完全平方数，寻找最接近的矩形布局
        // 尝试从sqrt开始向下寻找合适的行数
        for (int rows = sqrt; rows >= 1; rows--)
        {
            int cols = (int)Math.Ceiling((double)viewCount / rows);
            if (rows * cols >= viewCount && Math.Abs(rows - cols) <= 1)
            {
                return (rows, cols);
            }
        }
        
        // 如果没有找到理想的布局，使用默认计算
        int defaultRows = sqrt;
        int defaultCols = (int)Math.Ceiling((double)viewCount / defaultRows);
        return (defaultRows, defaultCols);
    }

    private static double GetScenarioTargetFramesPerSecond(
        int viewCount,
        int attachedViewCount,
        int maxCurvesPerView)
    {
        int effectiveViewCount = Math.Max(viewCount, attachedViewCount);
        if (effectiveViewCount <= 1)
        {
            return SingleViewTargetFramesPerSecond;
        }

        return MultiViewTargetFramesPerSecond;
    }

    /// <summary>
    /// 更新UniformGrid的行列布局
    /// </summary>
    private void UpdateGridLayout()
    {
        if (_viewsContainer is null) return;
        
        var (rows, cols) = CalculateOptimalGrid(_curvePanels.Count);
        _viewsContainer.Rows = rows;
        _viewsContainer.Columns = cols;
        
        Console.WriteLine($"Updated grid layout: {rows}×{cols} for {_curvePanels.Count} views");
    }

    public MainWindow()
    {
        InitializeComponent();
        Console.WriteLine("MainWindow constructor completed");
        _baseTitle = Title;

        this.Opened += OnOpenedMultiGrid;
        this.Closing += async (_, __) =>
        {
            // 清理ViewModel资源
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.Cleanup();
            }
            
            _timer?.Stop();
            _metricsTimer?.Stop();
            _performanceRecorder?.Dispose();
            if (DataContext is ViewModels.MainWindowViewModel closingVm)
            {
                closingVm.OnlineChannelManager.OnlineChannelsChanged -= OnOnlineChannelsChanged;
            }
            _displayCache?.Dispose();
            _sweepSnapshotCache?.Dispose();
            // 停止所有 CurvePanel
            foreach (var panel in _curvePanels)
            {
                panel.Stop();
            }
        };

        _cfg = MockConfig.Instance;
        _performanceRecorder = new PerformanceMetricsRecorder();
        Console.WriteLine($"[Perf] Metrics CSV: {Path.GetFullPath(_performanceRecorder.CsvPath)}");
    }

    private void OnOpenedMultiGrid(object? s, EventArgs e)
    {

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;
    
        // 直接使用真实数据总线，避免离线时显示模拟数据
        _dataHub = new DataHub(_bus);
        _displayCache = new RealtimeDisplayCache(_bus);
        _sweepSnapshotCache = new RealtimeSweepSnapshotCache(_bus, _displayCache);
    
        // 连接前不生成/桥接任何数据；连接成功后由 _bus 推送真实帧
    
        // 找到多视图容器与按钮
        _viewsContainer = this.FindControl<UniformGrid>("ViewsContainer");
        _addViewButton = this.FindControl<Button>("AddViewButton");
        _removeViewButton = this.FindControl<Button>("RemoveViewButton");
        _resultsControlsPanel = this.FindControl<StackPanel>("ResultsControlsPanel");
        _globalChannelSelectorButton = this.FindControl<Button>("GlobalChannelSelectorButton");
        _globalChannelSelector = this.FindControl<ChannelSelector>("GlobalChannelSelector");
        _preset1x64Button = this.FindControl<Button>("Preset1x64Button");
        _preset16x16Button = this.FindControl<Button>("Preset16x16Button");
        _preset64x16Button = this.FindControl<Button>("Preset64x16Button");
        _preset64x64Button = this.FindControl<Button>("Preset64x64Button");
    
        if (_addViewButton is not null)
            _addViewButton.Click += (_, __) => AddView();
        if (_removeViewButton is not null)
            _removeViewButton.Click += (_, __) => RemoveView();
        if (_preset1x64Button is not null)
            _preset1x64Button.Click += (_, __) => ApplyViewPreset(1, CreatePresetChannels64);
        if (_preset16x16Button is not null)
            _preset16x16Button.Click += (_, __) => ApplyViewPreset(16, CreatePresetChannels16);
        if (_preset64x16Button is not null)
            _preset64x16Button.Click += (_, __) => ApplyViewPreset(64, CreatePresetChannels16);
        if (_preset64x64Button is not null)
            _preset64x64Button.Click += (_, __) => ApplyViewPreset(64, CreatePresetChannels64);

        // 设置全局通道选择器的数据源，并将选择应用到当前激活视图
        if (_globalChannelSelector is not null && vm is not null)
        {
            _globalChannelSelector.SetOnlineChannelManager(vm.OnlineChannelManager);
            vm.OnlineChannelManager.OnlineChannelsChanged += OnOnlineChannelsChanged;
            if (_dataHub is not null)
            {
                _globalChannelSelector.AttachDataBus(_dataHub.DataBus);
            }
            if (_globalChannelSelectorButton?.Flyout is Flyout globalSelectorFlyout)
            {
                globalSelectorFlyout.Opened += (_, __) =>
                {
                    _globalSelectionFlyoutOpen = true;
                    _pendingGlobalSelectionTarget = TargetPanel();
                    var pendingState = TargetViewState();
                    _pendingGlobalSelectionChannels = pendingState is null
                        ? new List<int>()
                        : new List<int>(pendingState.SelectedChannelIds);
                    RenderPhaseTimingLogger.LogSelectionFlow(
                        "MainWindow.GlobalSelectorFlyoutOpened",
                        _pendingGlobalSelectionTarget is null ? "none" : $"panel-{_curvePanels.IndexOf(_pendingGlobalSelectionTarget)}",
                        _pendingGlobalSelectionChannels.Count,
                        string.Join(",", _pendingGlobalSelectionChannels.Take(8)));
                };
                globalSelectorFlyout.Closed += (_, __) =>
                {
                    _globalSelectionFlyoutOpen = false;
                    if (_pendingGlobalSelectionTarget is null || _pendingGlobalSelectionChannels is null)
                    {
                        return;
                    }

                    RenderPhaseTimingLogger.LogSelectionFlow(
                        "MainWindow.GlobalSelectorFlyoutClosedCommit",
                        $"panel-{_curvePanels.IndexOf(_pendingGlobalSelectionTarget)}",
                        _pendingGlobalSelectionChannels.Count,
                        string.Join(",", _pendingGlobalSelectionChannels.Take(8)));
                    ApplySelectedChannelsToPanel(_pendingGlobalSelectionTarget, _pendingGlobalSelectionChannels);
                };
            }
            _globalChannelSelector.SelectedChannelsChanged += (s2, e2) =>
            {
                if (_suppressGlobalSelectionApply)
                {
                    return;
                }

                var target = TargetPanel();
                RenderPhaseTimingLogger.LogSelectionFlow(
                    "GlobalChannelSelector.SelectedChannelsChanged",
                    target is null ? "none" : $"panel-{_curvePanels.IndexOf(target)}",
                    e2.SelectedChannels.Count,
                    string.Join(",", e2.SelectedChannels.Take(8)));
                _pendingGlobalSelectionTarget = target;
                _pendingGlobalSelectionChannels = new List<int>(e2.SelectedChannels);
                if (!_globalSelectionFlyoutOpen && target is not null)
                {
                    RenderPhaseTimingLogger.LogSelectionFlow(
                        "MainWindow.GlobalSelectionImmediateCommit",
                        $"panel-{_curvePanels.IndexOf(target)}",
                        _pendingGlobalSelectionChannels.Count,
                        string.Join(",", _pendingGlobalSelectionChannels.Take(8)));
                    ApplySelectedChannelsToPanel(target, _pendingGlobalSelectionChannels);
                }
            };

            vm.PropertyChanged += (s2, e2) =>
            {
                if (e2.PropertyName == "SelectedDeviceId")
                {
                    ApplySelectedDeviceToTarget(vm.SelectedDeviceId);
                }
            };
        }

        // 初始填充：从1个视图开始，支持动态调整
        AddView();
    
        // 统一刷新策略：保留原 5ms 高频调度；实际出帧由 Avalonia/Skia 与控件缓存复用共同决定。
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        _timer.Tick += (_, __) =>
        {
            UpdateSingleViewFrame();
            foreach (var p in _curvePanels)
            {
                p.Invalidate();
            }
        };
        _timer.Start();

        StartMetricsSampling();
    
        
        
        // 订阅采样频率变更事件
        vm.SampleRateChanged += OnSampleRateChanged;
    }

    private void AddView()
    {
        AddView(selectPanel: true);
    }

    private void AddView(bool selectPanel)
    {
        if (_viewsContainer is null || _dataHub is null) return;
        // 限制最多视图数量以避免过载（可按需调整）
        const int MaxViews = 64;
        if (_curvePanels.Count >= MaxViews) return;
        SkiaMultiChannelView.SetLogicalViewCount(_curvePanels.Count + 1);

        var panel = new CurvePanel
        {
            Margin = new Thickness(4)
        };
        panel.RequireExternalFrameSnapshot();
        panel.SelectedChannelsCommitted += OnPanelSelectedChannelsCommitted;
        panel.ZoomStateCommitted += OnPanelZoomStateCommitted;
        panel.AttachSelectorDataBus(_dataHub.DataBus);
        
        // 设置在线通道管理器
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            panel.SetOnlineChannelManager(vm.OnlineChannelManager);
            panel.SetDeviceFilter(vm.SelectedDeviceId);
            panel.UpdateSampleRate(vm.SampleRate);
        }
        
        var state = CreateViewStateForPanel(panel);
        panel.SetZoomState(state.ZoomX, state.ZoomY, state.AutoFitX, state.AutoFitY);
        _curvePanels.Add(panel);
        _curveViewStates.Add(state);
        _viewsContainer.Children.Add(panel);

        // 视图点击选择：点击后设为当前选中，并高亮
        panel.PointerPressed += (_, __) => SelectPanel(panel);

        // 新增视图默认设为选中，便于立即联动
        if (selectPanel)
        {
            SelectPanel(panel);
        }

        // 动态更新网格布局
        UpdateGridLayout();
        SyncLogicalViewCount();
    }

    private CurveViewState CreateViewStateForPanel(CurvePanel panel)
    {
        int deviceFilterId = 0;
        if (DataContext is ViewModels.MainWindowViewModel vm)
        {
            deviceFilterId = vm.SelectedDeviceId;
        }

        return new CurveViewState
        {
            SelectedChannelIds = new List<int>(),
            DeviceFilterId = deviceFilterId,
            ZoomX = DefaultZoomX,
            ZoomY = DefaultZoomY,
            AutoFitX = true,
            AutoFitY = true,
            IsSelected = panel.IsSelected
        };
    }

    private void EnsureViewCount(int targetViewCount)
    {
        targetViewCount = Math.Clamp(targetViewCount, 1, 64);

        while (_curvePanels.Count < targetViewCount)
        {
            AddView(selectPanel: false);
        }

        while (_curvePanels.Count > targetViewCount)
        {
            RemoveView();
        }

        SyncLogicalViewCount();
    }

    private void ApplyViewPreset(int targetViewCount, Func<ViewModels.MainWindowViewModel, List<int>> channelFactory)
    {
        if (DataContext is not ViewModels.MainWindowViewModel vm || _dataHub is null)
        {
            return;
        }

        EnsureViewCount(targetViewCount);
        if (_curvePanels.Count == 0)
        {
            return;
        }

        var channelIds = channelFactory(vm);
        for (int i = 0; i < _curvePanels.Count; i++)
        {
            var panel = _curvePanels[i];
            var state = _curveViewStates[i];
            state.DeviceFilterId = vm.SelectedDeviceId;
            state.SelectedChannelIds = new List<int>(channelIds);
            panel.SetDeviceFilter(vm.SelectedDeviceId);
            panel.SetSelectedChannels(new List<int>(channelIds));
        }

        SelectPanel(_curvePanels[0]);
        Console.WriteLine($"Applied result preset: views={_curvePanels.Count}, channelsPerView={channelIds.Count}");
    }

    private void UpdateSingleViewFrame()
    {
        if (_curvePanels.Count == 0)
        {
            return;
        }

        var frameBuildStopwatch = Stopwatch.StartNew();
        int maxPointsPerChannel = GetBatchPreviewSampleCount();
        int fallbackPanelCount = 0;
        int activeChannelCount = 0;
        int cachedChannelCount = 0;
        int maxActualPointsPerCurve = 0;
        int totalActualPoints = 0;
        for (int i = 0; i < _curvePanels.Count; i++)
        {
            var panel = _curvePanels[i];
            if (i >= _curveViewStates.Count)
            {
                fallbackPanelCount++;
                continue;
            }

            var frame = BuildFrameSnapshot(_curveViewStates[i], maxPointsPerChannel);
            activeChannelCount += frame.ActiveChannelCount;
            cachedChannelCount += frame.WindowData.Count;
            maxActualPointsPerCurve = Math.Max(maxActualPointsPerCurve, frame.MaxActualPointsPerChannel);
            totalActualPoints += frame.TotalActualPoints;
            panel.SetExternalFrameSnapshot(
                frame.WindowData,
                frame.WindowMaxAbsY,
                frame.MaxActualPointsPerChannel,
                frame.TotalActualPoints);
        }

        _lastFrameSource = fallbackPanelCount == 0
            ? "external-single"
            : "external-single+legacy-fallback";
        _lastExternalFrameFallbackPanelCount = fallbackPanelCount;
        _lastFrameActiveChannelCount = activeChannelCount;
        _lastFrameCachedChannelCount = cachedChannelCount;
        _lastFrameMaxActualPointsPerCurve = maxActualPointsPerCurve;
        _lastFrameTotalActualPoints = totalActualPoints;
        LogRealtimeFrameSourceSummary();
        RenderPhaseTimingLogger.LogRealtimeFrameBuild(
            _lastFrameSource,
            _curvePanels.Count,
            activeChannelCount,
            maxPointsPerChannel,
            maxActualPointsPerCurve,
            totalActualPoints,
            frameBuildStopwatch.Elapsed.TotalMilliseconds);
    }

    private RealtimeCurveFrameSnapshot BuildFrameSnapshot(CurveViewState state, int maxPointsPerChannel)
    {
        var channels = state.SelectedChannelIds
            .Where(channelId => DataContext is not ViewModels.MainWindowViewModel vm
                || vm.OnlineChannelManager.IsChannelOnline(channelId))
            .ToArray();

        if (channels.Length == 0 || _sweepSnapshotCache is null)
        {
            return RealtimeCurveFrameSnapshot.Empty;
        }

        int historyCount = Math.Max(maxPointsPerChannel, BatchSweepHistoryPointBudget);
        if (!_sweepSnapshotCache.TryGetLatestSeconds(channels, historyCount, out var latestSeconds))
        {
            return RealtimeCurveFrameSnapshot.Empty with
            {
                ActiveChannelCount = channels.Length
            };
        }

        double windowStartSeconds = GetBatchSweepWindowStart(latestSeconds);
        var snapshot = _sweepSnapshotCache.GetSweepSnapshot(
            channels,
            historyCount,
            windowStartSeconds,
            BatchSweepWindowSeconds,
            maxPointsPerChannel);

        return new RealtimeCurveFrameSnapshot(
            ActiveChannelCount: channels.Length,
            WindowData: snapshot.WindowData,
            WindowMaxAbsY: snapshot.WindowMaxAbsY,
            MaxActualPointsPerChannel: snapshot.MaxActualPointsPerChannel,
            TotalActualPoints: snapshot.TotalActualPoints);
    }

    private void LogRealtimeFrameSourceSummary()
    {
        RenderPhaseTimingLogger.LogRealtimeFrameSourceSummary(
            _lastFrameSource,
            _curvePanels.Count,
            _lastFrameActiveChannelCount,
            _lastFrameCachedChannelCount,
            _lastFrameMaxActualPointsPerCurve,
            _lastFrameTotalActualPoints,
            _lastExternalFrameFallbackPanelCount);
    }

    private int GetBatchPreviewSampleCount()
    {
        double width = _viewsContainer?.Bounds.Width ?? 0.0;
        int viewCount = Math.Max(1, _curvePanels.Count);
        int cols = Math.Clamp((int)Math.Ceiling(Math.Sqrt(viewCount)), 1, viewCount);
        double cellWidth = width > 0.0 ? width / cols : 0.0;
        if (double.IsNaN(cellWidth) || cellWidth <= 0.0)
        {
            return BatchMaxPreviewPointsPerChannel;
        }

        int pixelBudget = (int)Math.Ceiling(cellWidth * 2.0);
        return Math.Clamp(pixelBudget, BatchMinPreviewPointsPerChannel, BatchMaxPreviewPointsPerChannel);
    }

    private double GetBatchSweepWindowStart(double latestSeconds)
    {
        if (latestSeconds <= 0.0)
        {
            _batchSweepWindowStartSeconds = 0.0;
            return 0.0;
        }

        double sharedCycleStart = Math.Floor(latestSeconds / BatchSweepWindowSeconds) * BatchSweepWindowSeconds;
        if (latestSeconds - sharedCycleStart < 0.05 && sharedCycleStart >= BatchSweepWindowSeconds)
        {
            sharedCycleStart -= BatchSweepWindowSeconds;
        }

        _batchSweepWindowStartSeconds = sharedCycleStart;
        return sharedCycleStart;
    }

    private List<int> CreatePresetChannels16(ViewModels.MainWindowViewModel vm)
    {
        return GetDeviceChannels(vm, vm.SelectedDeviceId, 16);
    }

    private List<int> CreatePresetChannels64(ViewModels.MainWindowViewModel vm)
    {
        const int targetChannelCount = 64;
        const int channelsPerDevice = 16;

        var candidateChannels = GetCandidateChannels(vm);
        var candidateDeviceIds = candidateChannels
            .Select(DH.Contracts.ChannelNaming.GetDeviceId)
            .Distinct()
            .ToList();

        var selectedDeviceIds = new List<int> { vm.SelectedDeviceId };
        var randomDeviceIds = candidateDeviceIds
            .Where(deviceId => deviceId != vm.SelectedDeviceId)
            .OrderBy(_ => _presetRandom.Next())
            .Take(3)
            .ToList();
        selectedDeviceIds.AddRange(randomDeviceIds);

        var result = new List<int>(targetChannelCount);
        foreach (var deviceId in selectedDeviceIds.Distinct())
        {
            foreach (var channelId in GetDeviceChannels(vm, deviceId, channelsPerDevice))
            {
                if (!result.Contains(channelId))
                {
                    result.Add(channelId);
                }
            }
        }

        if (result.Count < targetChannelCount)
        {
            foreach (var deviceId in candidateDeviceIds.Except(selectedDeviceIds))
            {
                foreach (var channelId in GetDeviceChannels(vm, deviceId, channelsPerDevice))
                {
                    if (!result.Contains(channelId))
                    {
                        result.Add(channelId);
                    }

                    if (result.Count >= targetChannelCount)
                    {
                        return result;
                    }
                }
            }
        }

        if (result.Count < targetChannelCount)
        {
            foreach (var channelId in candidateChannels)
            {
                if (!result.Contains(channelId))
                {
                    result.Add(channelId);
                }

                if (result.Count >= targetChannelCount)
                {
                    break;
                }
            }
        }

        return result;
    }

    private List<int> GetDeviceChannels(ViewModels.MainWindowViewModel vm, int deviceId, int maxCount)
    {
        var primaryChannels = GetCandidateChannels(vm)
            .Where(channelId => DH.Contracts.ChannelNaming.GetDeviceId(channelId) == deviceId)
            .Distinct()
            .OrderBy(channelId => DH.Contracts.ChannelNaming.GetChannelNumber(channelId))
            .Take(Math.Max(1, maxCount))
            .ToList();

        if (primaryChannels.Count >= Math.Max(1, maxCount))
        {
            return primaryChannels;
        }

        var fallbackChannels = vm.Channels
            .Where(channel => DH.Contracts.ChannelNaming.GetDeviceId(channel.ChannelId) == deviceId)
            .OrderBy(channel => channel.ChannelId)
            .Select(channel => channel.ChannelId)
            .ToList();

        foreach (var channelId in fallbackChannels)
        {
            if (!primaryChannels.Contains(channelId))
            {
                primaryChannels.Add(channelId);
            }

            if (primaryChannels.Count >= Math.Max(1, maxCount))
            {
                break;
            }
        }

        return primaryChannels;
    }

    private List<int> GetCandidateChannels(ViewModels.MainWindowViewModel vm)
    {
        var onlineChannels = vm.OnlineChannelManager.GetOnlineChannels()
            .Where(channelId => channelId > 0)
            .Distinct()
            .OrderBy(channelId => channelId)
            .ToList();
        if (onlineChannels.Count > 0)
        {
            return onlineChannels;
        }

        var availableChannels = vm.Bus.GetAvailableChannels()
            .Where(channelId => channelId > 0)
            .Distinct()
            .OrderBy(channelId => channelId)
            .ToList();
        if (availableChannels.Count > 0)
        {
            return availableChannels;
        }

        return vm.Channels
            .Where(channel => channel.ChannelId > 0)
            .OrderBy(channel => channel.ChannelId)
            .Select(channel => channel.ChannelId)
            .ToList();
    }

    private void ApplySelectedDeviceToTarget(int deviceId)
    {
        var state = TargetViewState();
        if (state is not null)
        {
            state.DeviceFilterId = deviceId;
        }

        var target = TargetPanel();
        target?.SetDeviceFilter(deviceId);
    }

    private void OnOnlineChannelsChanged(object? sender, OnlineChannelsChangedEventArgs e)
    {
    }

    private void StartMetricsSampling()
    {
        _metricsTimer?.Stop();
        _metricsTimer = new DispatcherTimer { Interval = MetricsSampleInterval };
        _metricsTimer.Tick += (_, __) => RecordPerformanceMetrics();
        _metricsTimer.Start();
    }

    private void RecordPerformanceMetrics()
    {
        if (_performanceRecorder is null)
        {
            return;
        }

        var renderStats = SkiaMultiChannelView.SnapshotAndResetRenderStats();
        var panelMetrics = SnapshotExternalFramePerformanceMetrics();
        int viewCount = _curvePanels.Count;
        int attachedViewCount = Math.Max(0, renderStats.AttachedViews);
        int curveCount = panelMetrics.Sum(metrics => metrics.ActiveCurveCount);
        double averageCurvesPerView = viewCount > 0
            ? panelMetrics.Average(metrics => metrics.ActiveCurveCount)
            : 0.0;
        int maxCurvesPerView = _curvePanels.Count > 0
            ? panelMetrics.Max(metrics => metrics.ActiveCurveCount)
            : 0;
        double averageEstimatedPointsPerCurve = _curvePanels.Count > 0
            ? panelMetrics.Average(metrics => metrics.EstimatedPointsPerCurve)
            : 0.0;
        int maxEstimatedPointsPerCurve = _curvePanels.Count > 0
            ? panelMetrics.Max(metrics => metrics.EstimatedPointsPerCurve)
            : 0;
        double averageActualPointsPerCurve = _curvePanels.Count > 0
            ? panelMetrics.Average(metrics => metrics.ActualPointsPerCurve)
            : 0.0;
        int maxActualPointsPerCurve = _curvePanels.Count > 0
            ? panelMetrics.Max(metrics => metrics.ActualPointsPerCurve)
            : 0;
        int totalEstimatedPoints = panelMetrics.Sum(metrics => metrics.EstimatedVisiblePointBudget);
        int totalActualPoints = panelMetrics.Sum(metrics => metrics.ActualVisiblePointBudget);
        int fpsDivisor = Math.Max(1, Math.Max(viewCount, renderStats.AttachedViews));
        double fps = renderStats.RenderCalls / (double)fpsDivisor;
        double renderCallsPerSecond = renderStats.RenderCalls;
        double targetFramesPerSecond = GetScenarioTargetFramesPerSecond(
            viewCount,
            attachedViewCount,
            maxCurvesPerView);
        var sample = _performanceRecorder.Capture(new PerformanceMetricsCaptureContext(
            FrameSource: _lastFrameSource,
            ViewCount: viewCount,
            AttachedViewCount: attachedViewCount,
            CurveCount: curveCount,
            AverageCurvesPerView: averageCurvesPerView,
            MaxCurvesPerView: maxCurvesPerView,
            FramesPerSecond: fps,
            TargetFramesPerSecond: targetFramesPerSecond,
            RenderCallsPerSecond: renderCallsPerSecond,
            AverageEstimatedPointsPerCurve: averageEstimatedPointsPerCurve,
            MaxEstimatedPointsPerCurve: maxEstimatedPointsPerCurve,
            AverageActualPointsPerCurve: averageActualPointsPerCurve,
            MaxActualPointsPerCurve: maxActualPointsPerCurve,
            TargetPointsPerCurve: TargetPointsPerCurveThreshold,
            TotalEstimatedPoints: totalEstimatedPoints,
            TotalActualPoints: totalActualPoints));

        string fpsTargetText = sample.MeetsPerViewFpsTarget ? "达标" : "未达标";
        Title = $"{_baseTitle} | 视图 {sample.ViewCount} | 曲线 {sample.CurveCount} | FPS {sample.FramesPerSecond:F1}/{sample.TargetFramesPerSecond:F0} {fpsTargetText} | 点/曲线 {sample.MaxActualPointsPerCurve}/{sample.TargetPointsPerCurve}+ | CPU {sample.CpuPercent:F1}% | GPU {sample.GpuPercent:F1}%";
    }

    private List<(
        int ActiveCurveCount,
        int EstimatedPointsPerCurve,
        int EstimatedVisiblePointBudget,
        int ActualPointsPerCurve,
        int ActualVisiblePointBudget)> SnapshotExternalFramePerformanceMetrics()
    {
        if (_curveViewStates.Count == 0)
        {
            return new List<(int, int, int, int, int)>();
        }

        int activeChannelCount = _curveViewStates.Sum(state => state.SelectedChannelIds.Count);
        int viewCount = Math.Max(1, _curveViewStates.Count);
        int estimatedVisiblePointBudget = _lastFrameTotalActualPoints;
        int estimatedPointsPerCurve = activeChannelCount > 0
            ? (int)Math.Round(estimatedVisiblePointBudget / (double)activeChannelCount)
            : 0;
        int actualPointsPerCurve = _lastFrameMaxActualPointsPerCurve;
        int actualVisiblePointBudget = _lastFrameTotalActualPoints;

        var metrics = new List<(int, int, int, int, int)>(_curveViewStates.Count);
        foreach (var state in _curveViewStates)
        {
            int stateChannelCount = state.SelectedChannelIds.Count;
            int stateEstimatedBudget = activeChannelCount > 0
                ? (int)Math.Round(estimatedVisiblePointBudget * (stateChannelCount / (double)activeChannelCount))
                : 0;
            int stateActualBudget = activeChannelCount > 0
                ? (int)Math.Round(actualVisiblePointBudget * (stateChannelCount / (double)activeChannelCount))
                : 0;

            metrics.Add((
                stateChannelCount,
                estimatedPointsPerCurve,
                stateEstimatedBudget,
                actualPointsPerCurve,
                stateActualBudget));
        }

        return metrics;
    }

    // 顶部全局控件事件：仅作用于当前选中视图（无选中则默认最后一个）
    private CurvePanel? TargetPanel() => _selectedPanel ?? (_curvePanels.Count > 0 ? _curvePanels[^1] : null);

    private CurveViewState? TargetViewState()
    {
        int index = _selectedIndex >= 0 ? _selectedIndex : _curvePanels.Count - 1;
        return index >= 0 && index < _curveViewStates.Count ? _curveViewStates[index] : null;
    }

    private void OnGlobalZoomInX(object? sender, RoutedEventArgs e)
    {
        ApplyZoomToTarget(state =>
        {
            state.AutoFitX = false;
            state.ZoomX *= 1.2f;
        });
    }

    private void OnGlobalZoomOutX(object? sender, RoutedEventArgs e)
    {
        ApplyZoomToTarget(state =>
        {
            state.AutoFitX = false;
            state.ZoomX /= 1.2f;
        });
    }

    private void OnGlobalZoomInY(object? sender, RoutedEventArgs e)
    {
        ApplyZoomToTarget(state =>
        {
            state.AutoFitY = false;
            state.ZoomY *= 1.2f;
        });
    }

    private void OnGlobalZoomOutY(object? sender, RoutedEventArgs e)
    {
        ApplyZoomToTarget(state =>
        {
            state.AutoFitY = false;
            state.ZoomY /= 1.2f;
        });
    }

    private void OnGlobalResetZoom(object? sender, RoutedEventArgs e)
    {
        ApplyZoomToTarget(state =>
        {
            state.AutoFitX = true;
            state.AutoFitY = true;
            state.ZoomX = 1.0f;
            state.ZoomY = 1.0f;
        });
    }

    private void ApplyZoomToTarget(Action<CurveViewState> mutate)
    {
        var state = TargetViewState();
        if (state is null)
        {
            return;
        }

        mutate(state);
        state.ZoomX = Math.Max(0.01f, state.ZoomX);
        state.ZoomY = Math.Max(0.01f, state.ZoomY);

        var target = TargetPanel();
        if (target is not null)
        {
            target.SetZoomState(state.ZoomX, state.ZoomY, state.AutoFitX, state.AutoFitY);
        }
    }

    private void OnSampleRateValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel && e.NewValue.HasValue)
        {
            viewModel.SampleRateChangedCommand.Execute(e.NewValue.Value);
        }
    }

    private void OnSampleRateSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRateChangedCommand.Execute((int)e.NewValue);
        }
    }

    private void OnQuickSampleRate100(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 100;
            viewModel.SampleRateChangedCommand.Execute(100);
        }
    }

    private void OnQuickSampleRate1k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 1000;
            viewModel.SampleRateChangedCommand.Execute(1000);
        }
    }

    private void OnQuickSampleRate5k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 5000;
            viewModel.SampleRateChangedCommand.Execute(5000);
        }
    }

    private void OnQuickSampleRate10k(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.SampleRate = 10000;
            viewModel.SampleRateChangedCommand.Execute(10000);
        }
    }

    private void RemoveView()
    {
        if (_viewsContainer is null) return;
        if (_curvePanels.Count == 0) return;

        var panel = _curvePanels[^1];
        panel.Stop();
        panel.SelectedChannelsCommitted -= OnPanelSelectedChannelsCommitted;
        panel.ZoomStateCommitted -= OnPanelZoomStateCommitted;
        _curvePanels.RemoveAt(_curvePanels.Count - 1);
        if (_curveViewStates.Count > _curvePanels.Count)
        {
            _curveViewStates.RemoveAt(_curveViewStates.Count - 1);
        }
        _viewsContainer.Children.Remove(panel);
        
        // 若删除的是当前选中视图，重置选中到最后一个
        if (_selectedPanel == panel)
        {
            _selectedPanel = _curvePanels.Count > 0 ? _curvePanels[^1] : null;
            _selectedIndex = _selectedPanel != null ? _curvePanels.IndexOf(_selectedPanel) : -1;
            foreach (var p in _curvePanels)
            {
                p.SetSelected(p == _selectedPanel);
            }
            SyncControlsToSelectedView();
        }
        else
        {
            _selectedIndex = _selectedPanel is null ? -1 : _curvePanels.IndexOf(_selectedPanel);
        }

        SyncAllViewSelectionStates();
        
        // 动态更新网格布局
        UpdateGridLayout();
        SyncLogicalViewCount();
    }

    private void SyncAllViewSelectionStates()
    {
        for (int i = 0; i < _curveViewStates.Count; i++)
        {
            _curveViewStates[i].IsSelected = i == _selectedIndex;
        }
    }

    private void SyncLogicalViewCount()
    {
        SkiaMultiChannelView.SetLogicalViewCount(_curvePanels.Count);
    }

    private void OnChannelOnlineChecked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is int channelId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.OnlineChannelManager.SetChannelOnline(channelId, true);
            }
        }
    }

    // ===== 视图选择与联动 =====
    private void SelectPanel(CurvePanel panel)
    {
        _selectedPanel = panel;
        _selectedIndex = _curvePanels.IndexOf(panel);
        SyncPanelStateFromPanel(panel);
        RenderPhaseTimingLogger.LogSelectionFlow(
            "MainWindow.SelectPanel",
            $"panel-{_selectedIndex}",
            TargetViewState()?.SelectedChannelIds.Count ?? 0,
            string.Join(",", (TargetViewState()?.SelectedChannelIds ?? new List<int>()).Take(8)));
        // 高亮选中视图
        for (int i = 0; i < _curvePanels.Count; i++)
        {
            var p = _curvePanels[i];
            p.SetSelected(p == panel);
        }
        SyncAllViewSelectionStates();
        // 同步上方控件状态到当前选中视图
        SyncControlsToSelectedView();
        Console.WriteLine($"Selected view index: {_selectedIndex}");
    }

    private void ApplySelectedChannelsToPanel(CurvePanel panel, IReadOnlyList<int> selectedChannels)
    {
        int panelIndex = _curvePanels.IndexOf(panel);
        if (panelIndex >= 0 && panelIndex < _curveViewStates.Count)
        {
            _curveViewStates[panelIndex].SelectedChannelIds = selectedChannels.ToList();
        }

        panel.SetSelectedChannels(selectedChannels.ToList());
    }

    private void OnPanelSelectedChannelsCommitted(object? sender, IReadOnlyList<int> selectedChannels)
    {
        if (sender is not CurvePanel panel)
        {
            return;
        }

        int panelIndex = _curvePanels.IndexOf(panel);
        if (panelIndex < 0 || panelIndex >= _curveViewStates.Count)
        {
            return;
        }

        _curveViewStates[panelIndex].SelectedChannelIds = selectedChannels.ToList();
        RenderPhaseTimingLogger.LogSelectionFlow(
            "MainWindow.PanelSelectedChannelsCommitted",
            $"panel-{panelIndex}",
            selectedChannels.Count,
            string.Join(",", selectedChannels.Take(8)));
    }

    private void OnPanelZoomStateCommitted(object? sender, CurvePanelZoomStateChangedEventArgs e)
    {
        if (sender is not CurvePanel panel)
        {
            return;
        }

        int panelIndex = _curvePanels.IndexOf(panel);
        if (panelIndex < 0 || panelIndex >= _curveViewStates.Count)
        {
            return;
        }

        var state = _curveViewStates[panelIndex];
        state.ZoomX = Math.Max(0.01f, e.ZoomX);
        state.ZoomY = Math.Max(0.01f, e.ZoomY);
        state.AutoFitX = e.AutoFitX;
        state.AutoFitY = e.AutoFitY;
    }

    private void SyncPanelStateFromPanel(CurvePanel panel)
    {
        int panelIndex = _curvePanels.IndexOf(panel);
        if (panelIndex < 0 || panelIndex >= _curveViewStates.Count)
        {
            return;
        }

        var state = _curveViewStates[panelIndex];
        state.IsSelected = panel.IsSelected;
    }

    private void SyncControlsToSelectedView()
    {
        if (_globalChannelSelector is null) return;
        var sel = _selectedPanel;
        _suppressGlobalSelectionApply = true;
        if (sel is null)
        {
            RenderPhaseTimingLogger.LogSelectionFlow(
                "MainWindow.SyncControlsToSelectedView",
                "GlobalChannelSelector",
                0,
                "selectedPanel=null");
            _globalChannelSelector.SetSelectedChannels(Array.Empty<int>());
            _suppressGlobalSelectionApply = false;
            return;
        }
        var state = TargetViewState();
        var chs = state?.SelectedChannelIds.ToArray() ?? Array.Empty<int>();
        RenderPhaseTimingLogger.LogSelectionFlow(
            "MainWindow.SyncControlsToSelectedView",
            $"GlobalChannelSelector<=panel-{_curvePanels.IndexOf(sel)}",
            chs.Length,
            string.Join(",", chs.Take(8)));
        _globalChannelSelector.SetSelectedChannels(chs);
        _suppressGlobalSelectionApply = false;
    }

    private void OnChannelOnlineUnchecked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.Tag is int channelId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.OnlineChannelManager.SetChannelOnline(channelId, false);
            }
        }
    }

    private void OnDeviceTilePointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        if (sender is Border border && border.Tag is int deviceId)
        {
            if (DataContext is ViewModels.MainWindowViewModel vm)
            {
                vm.SelectedDeviceId = deviceId;
                ApplySelectedDeviceToTarget(deviceId);
            }
        }
    }
    
    // 采样频率变更事件处理
    private void OnSampleRateChanged(object? sender, int newSampleRate)
    {
        // 更新所有曲线面板的采样频率
        foreach (var panel in _curvePanels)
        {
            panel.UpdateSampleRate(newSampleRate);
        }
    }



#if false
    private void OnOpenedHistorySkia(object? s, EventArgs e)
    {

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;

        // 启动历史写入
        _hist = new HistoryWorker(_bus, channelId: 1, _cfg.SampleRate, historySeconds: 600);
        _hist.Start();

        // 附着到控件
        var plot = this.FindControl<SkiaHistoryPlotControl>("Plot");
        plot.SampleRate = _cfg.SampleRate;
        plot.YMin = -1.2; plot.YMax = 1.2;
        plot.AttachWorker(_hist, initialSeconds: 5.0);

        // 30 FPS 刷新 + 实时跟随
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, __) => plot.LiveFollowAndInvalidate();
        _timer.Start();
    }

    private void OnGoLiveClicked(object? sender, RoutedEventArgs e)
    {
        var plot = this.FindControl<SkiaHistoryPlotControl>("Plot");
        plot?.GoLive();
    }



    private void OnOpenedSkia(object? sender, EventArgs e)
    {
        // 暂时禁用 ScottPlot 5.x 功能以兼容 .NET 6.0
        /*
        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus; //公共数据总线，MockThread是生产者
        var _channelId = vm.ChannelId;

        // 启动 Skia 后台渲染器（独立线程订阅并绘制到离屏位图）
        _worker = new SkiaRealtimePlotWorker(_bus, _channelId, width: 900, height: 360);
        _worker.YMin = -1.2; _worker.YMax = 1.2;
        _worker.Start();

        // 将 worker 附着到控件；UI 仅负责贴图与画轴，Plot为MainWindow.axaml中 SkiaPlotControl的别名
        Plot.AttachWorker(_worker);
        Plot.XSeconds = 5.0;  // 仅刻度显示
        Plot.YMin = -1.2; Plot.YMax = 1.2;

        // UI 定时刷新（30 FPS），不参与绘制运算
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, __) => Plot.InvalidateVisual();
        _timer.Start();
        */
    }


    private void OnOpenedEcg(object? sender, EventArgs e)
    {
        // 暂时禁用 ScottPlot 5.x 功能以兼容 .NET 6.0
        /*
        var avaPlot = this.FindControl<ScottPlot.Avalonia.AvaPlot>("EcgPlot");
        if (avaPlot is null) return;

        var host = new AvaloniaPlotHost(avaPlot);

        if (DataContext is not ViewModels.MainWindowViewModel vm)
            return;

        var _bus = vm.Bus;
        var _channelId = vm.ChannelId;

        _renderer = new EcgSignalRenderer(_bus, _channelId, MockConfig.Instance);
        _renderer.AttachHost(host);
        _renderer.Start();
        */
    }
} 
#endif
}
