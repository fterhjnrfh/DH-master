using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Client.App.Data.Query;
using DH.Client.App.Services.Performance;
using DH.Client.App.Services.Storage;
using DH.Contracts.Models;
using Avalonia.Media;
using Avalonia.Threading;

namespace DH.Client.App.ViewModels;

public class TdmsViewerViewModel : ObservableObject
{
    private const int ReplayRawPointsPerChannel = 4000;
    private const int ReplayPreferredPreviewPointsPerChannel = 60000;
    private const double ReplayInitialRawWindowSeconds = 2.0;
    private const double ReplayMaxRawWindowSeconds = 5.0;
    private PersistedReplayContext? _persistedReplayContext;
    private int _persistedReplayQuerySerial;

    public bool HasPersistedReplay => _persistedReplayContext is not null;

    private double _replayTotalDurationSeconds;
    public double ReplayTotalDurationSeconds
    {
        get => _replayTotalDurationSeconds;
        private set => SetProperty(ref _replayTotalDurationSeconds, value);
    }

    private double _replayWindowStartSeconds;
    public double ReplayWindowStartSeconds
    {
        get => _replayWindowStartSeconds;
        private set
        {
            if (SetProperty(ref _replayWindowStartSeconds, value))
            {
                OnPropertyChanged(nameof(ReplayWindowText));
                OnPropertyChanged(nameof(ReplayWindowCenterSeconds));
            }
        }
    }

    private double _replayWindowEndSeconds;
    public double ReplayWindowEndSeconds
    {
        get => _replayWindowEndSeconds;
        private set
        {
            if (SetProperty(ref _replayWindowEndSeconds, value))
            {
                OnPropertyChanged(nameof(ReplayWindowText));
                OnPropertyChanged(nameof(ReplayWindowCenterSeconds));
            }
        }
    }

    public double ReplayWindowCenterSeconds => (ReplayWindowStartSeconds + ReplayWindowEndSeconds) * 0.5;

    public string ReplayWindowText => HasPersistedReplay
        ? $"{ReplayWindowStartSeconds:F3}s - {ReplayWindowEndSeconds:F3}s / {ReplayTotalDurationSeconds:F3}s"
        : string.Empty;

    private string _replayStatisticsText = string.Empty;
    public string ReplayStatisticsText
    {
        get => _replayStatisticsText;
        private set => SetProperty(ref _replayStatisticsText, value);
    }

    public ObservableCollection<ReplayStatisticsItem> ReplayStatisticsItems { get; } = new();
    public ObservableCollection<ReplayCursorValueItem> ReplayCursorValueItems { get; } = new();
    public IReadOnlyDictionary<int, double> ReplayCursorChannelValues { get; private set; } =
        new Dictionary<int, double>();

    public IReadOnlyDictionary<int, double> BuildCursorChannelValues(double seconds)
    {
        if (_persistedReplayContext is null || CurrentMultiChannelData.Count == 0)
        {
            return new Dictionary<int, double>();
        }

        seconds = Math.Clamp(seconds, ReplayWindowStartSeconds, ReplayWindowEndSeconds);
        var cursorValues = new Dictionary<int, double>();
        foreach (var kv in CurrentMultiChannelData)
        {
            if (TryInterpolateValueAt(kv.Value, seconds, out double value))
            {
                cursorValues[kv.Key] = value;
            }
        }

        return cursorValues;
    }

    private string _replayCursorText = string.Empty;
    public string ReplayCursorText
    {
        get => _replayCursorText;
        private set => SetProperty(ref _replayCursorText, value);
    }

    private bool _hasSingleCursor;
    public bool HasSingleCursor
    {
        get => _hasSingleCursor;
        private set => SetProperty(ref _hasSingleCursor, value);
    }

    private double _singleCursorSeconds;
    public double SingleCursorSeconds
    {
        get => _singleCursorSeconds;
        private set => SetProperty(ref _singleCursorSeconds, value);
    }

    // 当前曲线数据（单通道）
    public IReadOnlyList<CurvePoint> CurrentCurveData { get; private set; } = Array.Empty<CurvePoint>();
    // 当前多通道数据
    public Dictionary<int, IReadOnlyList<CurvePoint>> CurrentMultiChannelData { get; private set; } = new();
    public Dictionary<int, IReadOnlyList<CurvePoint>> ReplayOverviewData { get; private set; } = new();
    // 数据更新事件（供视图刷新）
    public event Action? CurveDataUpdated;

    // 通道颜色映射
    public Dictionary<int, Color> ChannelColorsMap { get; private set; } = new();
    
    // 设备列表
    public ObservableCollection<string> Devices { get; } = new();
    public ObservableCollection<DeviceSelectionItem> DeviceSelectionItems { get; } = new();
    
    // 通道选择项（多选）
    public ObservableCollection<ChannelSelectionItem> ChannelSelectionItems { get; } = new();
    
    // 内部存储：组名->通道名列表
    private Dictionary<string, List<string>> _groupChannels = new();
    // 设备->组名映射
    private Dictionary<string, string> _deviceGroupMap = new();
    private bool _suppressDeviceSelectionUpdates;

    // 记录用户最后一次视图状态（缩放与窗口）
    public class ViewState
    {
        public float ZoomX { get; set; }
        public float ZoomY { get; set; } = 1.0f;
        public int ViewLeft { get; set; }
        public int ViewCount { get; set; }
    }
    public ViewState? LastView { get; set; }

    private string? _selectedFile;
    public string? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                OnSelectedFileChanged();
            }
        }
    }
    
    private string? _selectedDevice;
    public string? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                OnSelectedDeviceChanged();
            }
        }
    }
    
    public bool HasDevices => Devices.Count > 0;
    
    private bool _hasPlottedData;
    public bool HasPlottedData
    {
        get => _hasPlottedData;
        set => SetProperty(ref _hasPlottedData, value);
    }
    
    private string _statusMessage = "请选择TDMS会话或文件";
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    // 跳转状态与进度
    private bool _isJumping;
    public bool IsJumping
    {
        get => _isJumping;
        set => SetProperty(ref _isJumping, value);
    }
    private int _jumpProgress;
    public int JumpProgress
    {
        get => _jumpProgress;
        set => SetProperty(ref _jumpProgress, value);
    }

    // 旧的通道列表（兼容保留）
    public ObservableCollection<string> Channels { get; } = new();
    private string? _selectedChannel;
    public string? SelectedChannel
    {
        get => _selectedChannel;
        set => SetProperty(ref _selectedChannel, value);
    }

    // 命令
    public IRelayCommand PlotSelectedChannelsCommand { get; }
    public IRelayCommand SelectAllDevicesCommand { get; }
    public IRelayCommand DeselectAllDevicesCommand { get; }
    public IRelayCommand SelectAllChannelsCommand { get; }
    public IRelayCommand DeselectAllChannelsCommand { get; }

    public TdmsViewerViewModel()
    {
        PlotSelectedChannelsCommand = new RelayCommand(PlotSelectedChannels, CanPlotSelectedChannels);

        SelectAllDevicesCommand = new RelayCommand(() =>
        {
            _suppressDeviceSelectionUpdates = true;
            foreach (var it in DeviceSelectionItems) it.IsSelected = true;
            _suppressDeviceSelectionUpdates = false;
            RebuildChannelsForSelectedDevices();
        }, () => DeviceSelectionItems.Count > 0);

        DeselectAllDevicesCommand = new RelayCommand(() =>
        {
            _suppressDeviceSelectionUpdates = true;
            foreach (var it in DeviceSelectionItems) it.IsSelected = false;
            _suppressDeviceSelectionUpdates = false;
            RebuildChannelsForSelectedDevices();
        }, () => DeviceSelectionItems.Count > 0);

        SelectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var it in ChannelSelectionItems) it.IsSelected = true;
        }, () => ChannelSelectionItems.Count > 0);

        DeselectAllChannelsCommand = new RelayCommand(() =>
        {
            foreach (var it in ChannelSelectionItems) it.IsSelected = false;
        }, () => ChannelSelectionItems.Count > 0);
    }

    // 设备过滤ID
    public int DeviceFilterId { get; set; } = 0;
    // 在线通道管理器
    public DH.Client.App.Data.OnlineChannelManager? OnlineChannelManager { get; set; }

    private void OnSelectedFileChanged()
    {
        try
        {
        // 清空所有状态
        Devices.Clear();
        DeviceSelectionItems.Clear();
        ChannelSelectionItems.Clear();
        Channels.Clear();
        ChannelColorsMap.Clear();
        _groupChannels.Clear();
        _deviceGroupMap.Clear();
        CurrentMultiChannelData = new();
        ReplayOverviewData = new();
        CurrentCurveData = Array.Empty<CurvePoint>();
        HasPlottedData = false;
        ClearPersistedReplayContext();
        
        OnPropertyChanged(nameof(HasDevices));
        
        if (string.IsNullOrWhiteSpace(_selectedFile)
            || (!File.Exists(_selectedFile) && !Directory.Exists(_selectedFile)))
        {
            StatusMessage = "请选择TDMS会话或文件";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }
        
        StatusMessage = "正在读取文件...";
        
        // 读取组和通道。直存会话优先从 session.manifest.json 组装完整通道表，
        // 避免选中 raw/source_*.tdms 时只看到单个 source 文件。
        Dictionary<string, string[]> dict;
        if (TryLoadPersistedSessionChannels(_selectedFile, out var persistedChannels))
        {
            dict = persistedChannels;
        }
        else
        {
            string? tdmsProbePath = ResolveTdmsStructureProbePath(_selectedFile);
            if (string.IsNullOrWhiteSpace(tdmsProbePath))
            {
                StatusMessage = "会话目录中没有找到可读取的 TDMS 文件";
                return;
            }

            dict = TdmsReaderUtil.ListGroupsAndChannels(tdmsProbePath)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        if (dict.Count == 0)
        {
            StatusMessage = "会话或文件中没有找到数据通道";
            return;
        }
        
        _groupChannels = dict.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        
        // 解析设备列表
        var deviceSet = new HashSet<string>();
        foreach (var groupName in dict.Keys)
        {
            foreach (var channelName in dict[groupName])
            {
                var deviceId = DH.Contracts.ChannelNaming.ParseDeviceId(channelName);
                // 支持设备ID从0开始（SDK模式设备号可能为0、1、2...）
                if (deviceId >= 0)
                {
                    var deviceName = $"设备 {deviceId} ({DH.Contracts.ChannelNaming.DeviceDisplayName(deviceId)})";
                    if (deviceSet.Add(deviceName))
                    {
                        _deviceGroupMap[deviceName] = groupName;
                    }
                }
            }
            
            // 如果没有解析出设备，使用组名作为设备
            if (deviceSet.Count == 0)
            {
                var deviceName = $"组: {groupName}";
                deviceSet.Add(deviceName);
                _deviceGroupMap[deviceName] = groupName;
            }
        }
        
        // 如果还是没有设备，创建一个默认设备
        if (deviceSet.Count == 0)
        {
            var defaultDevice = "默认设备";
            deviceSet.Add(defaultDevice);
            if (_groupChannels.Count > 0)
            {
                _deviceGroupMap[defaultDevice] = _groupChannels.Keys.First();
            }
        }
        
        foreach (var device in deviceSet.OrderBy(d => d))
        {
            Devices.Add(device);
            var item = new DeviceSelectionItem(device);
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DeviceSelectionItem.IsSelected))
                {
                    if (!_suppressDeviceSelectionUpdates)
                    {
                        RebuildChannelsForSelectedDevices();
                    }
                }
            };
            DeviceSelectionItems.Add(item);
        }
        
        OnPropertyChanged(nameof(HasDevices));
        
        // 自动选择第一个设备
        if (Devices.Count > 0)
        {
            SelectedDevice = Devices[0];
            DeviceSelectionItems[0].IsSelected = true;
        }
        
            StatusMessage = $"已加载 {dict.Values.Sum(v => v.Length)} 个通道，请选择设备和通道";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (SelectAllDevicesCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeselectAllDevicesCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TDMS] failed to open selected path {_selectedFile}: {ex}");
            StatusMessage = $"读取会话失败: {ex.Message}";
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }
    
    private void OnSelectedDeviceChanged()
    {
        if (string.IsNullOrWhiteSpace(_selectedDevice))
        {
            return;
        }

        _suppressDeviceSelectionUpdates = true;
        foreach (var item in DeviceSelectionItems)
        {
            item.IsSelected = string.Equals(item.DeviceName, _selectedDevice, StringComparison.Ordinal);
        }
        _suppressDeviceSelectionUpdates = false;

        RebuildChannelsForSelectedDevices();
    }

    private void RebuildChannelsForSelectedDevices()
    {
        var previouslySelectedChannelIds = ChannelSelectionItems
            .Where(item => item.IsSelected)
            .Select(item => item.ChannelId)
            .ToHashSet();
        ChannelSelectionItems.Clear();

        var selectedDevices = DeviceSelectionItems
            .Where(item => item.IsSelected)
            .Select(item => item.DeviceName)
            .ToList();

        if (selectedDevices.Count == 0)
        {
            UpdateStatusMessage();
            (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            return;
        }

        int colorIndex = 0;
        var addedChannelIds = new HashSet<int>();
        foreach (string deviceName in selectedDevices.OrderBy(ParseDeviceIdFromDeviceName).ThenBy(d => d))
        {
            if (!_deviceGroupMap.TryGetValue(deviceName, out var groupName))
            {
                groupName = _groupChannels.Keys.FirstOrDefault() ?? "";
            }

            if (string.IsNullOrEmpty(groupName) || !_groupChannels.ContainsKey(groupName))
            {
                continue;
            }

            var channels = _groupChannels[groupName];
            var deviceId = ParseDeviceIdFromDeviceName(deviceName);
            var filteredChannels = deviceId >= 0
                ? channels.Where(c => DH.Contracts.ChannelNaming.ParseDeviceId(c) == deviceId).ToList()
                : channels.ToList();

            if (filteredChannels.Count == 0)
            {
                filteredChannels = channels.ToList();
            }

            foreach (var channelName in filteredChannels.OrderBy(c => DH.Contracts.ChannelNaming.ParseChannelName(c)))
            {
                var channelId = DH.Contracts.ChannelNaming.ParseChannelName(channelName);
                var normalizedChannelId = channelId >= 0 ? channelId : Math.Abs(channelName.GetHashCode());
                if (!addedChannelIds.Add(normalizedChannelId))
                {
                    continue;
                }

                var color = GetColorByIndex(colorIndex++);
                var item = new ChannelSelectionItem(groupName, channelName, channelId, color);
                item.IsSelected = previouslySelectedChannelIds.Contains(item.ChannelId);
                item.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(ChannelSelectionItem.IsSelected))
                    {
                        (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
                        UpdateStatusMessage();
                    }
                };
                ChannelSelectionItems.Add(item);
            }
        }
        
        UpdateStatusMessage();
        (PlotSelectedChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SelectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (DeselectAllChannelsCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    
    private void UpdateStatusMessage()
    {
        var selectedCount = ChannelSelectionItems.Count(c => c.IsSelected);
        var totalCount = ChannelSelectionItems.Count;
        var selectedDeviceCount = DeviceSelectionItems.Count(d => d.IsSelected);
        var totalDeviceCount = DeviceSelectionItems.Count;
        StatusMessage = $"设备 {selectedDeviceCount}/{totalDeviceCount}，通道 {selectedCount}/{totalCount}";
    }

    private bool CanPlotSelectedChannels()
    {
        return !string.IsNullOrWhiteSpace(_selectedFile) 
               && ChannelSelectionItems.Any(c => c.IsSelected);
    }

    private void PlotSelectedChannels()
    {
        if (!CanPlotSelectedChannels()) return;
        
        var file = _selectedFile!;
        var selectedItems = ChannelSelectionItems.Where(c => c.IsSelected).ToList();
        
        if (selectedItems.Count == 0)
        {
            StatusMessage = "请至少选择一个通道";
            return;
        }
        
        StatusMessage = $"正在绘制 {selectedItems.Count} 个通道...";
        
        Task.Run(async () =>
        {
            try
            {
                var persistedResult = await TryBuildPersistedPreviewPlotAsync(file, selectedItems);
                if (persistedResult is not null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ChannelColorsMap = persistedResult.Colors;
                        CurrentMultiChannelData = persistedResult.Data;
                        ReplayOverviewData = persistedResult.ReplayContext.Index.HasPreviewIndex
                            ? persistedResult.Data
                            : new Dictionary<int, IReadOnlyList<CurvePoint>>();
                        CurrentCurveData = Array.Empty<CurvePoint>();
                        HasPlottedData = persistedResult.Data.Count > 0;
                        SetPersistedReplayContext(
                            persistedResult.ReplayContext,
                            persistedResult.WindowStartSeconds,
                            persistedResult.WindowEndSeconds);
                        StatusMessage = persistedResult.StatusMessage;
                        CurveDataUpdated?.Invoke();
                    });
                    return;
                }

                var tmpData = new Dictionary<int, IReadOnlyList<CurvePoint>>();
                var tmpColors = new Dictionary<int, Color>();
                const int MaxPoints = 100_000;
                
                foreach (var item in selectedItems)
                {
                    try
                    {
                        var y = TdmsReaderUtil.ReadChannelData(file, item.GroupName, item.ChannelName);
                        var props = TdmsReaderUtil.ReadChannelProperties(file, item.GroupName, item.ChannelName);
                        
                        double increment = TryGetDouble(props, "wf_increment") ?? 1.0;
                        double offset = TryGetDouble(props, "wf_start_offset") ?? 0.0;
                        
                        double[] x;
                        if (y.Length > MaxPoints)
                        {
                            int stride = (int)Math.Ceiling((double)y.Length / MaxPoints);
                            int n = (int)Math.Ceiling((double)y.Length / stride);
                            var y2 = new double[n];
                            x = new double[n];
                            for (int i = 0, j = 0; i < y.Length; i += stride, j++)
                            {
                                y2[j] = y[i];
                                x[j] = offset + i * increment;
                            }
                            y = y2;
                        }
                        else
                        {
                            x = new double[y.Length];
                            for (int i = 0; i < y.Length; i++) x[i] = offset + i * increment;
                        }
                        
                        var list = new List<CurvePoint>(y.Length);
                        for (int i = 0; i < y.Length; i++) list.Add(new CurvePoint(x[i], y[i]));
                        
                        tmpData[item.ChannelId] = list;
                        tmpColors[item.ChannelId] = item.Color;
                        
                        Console.WriteLine($"[TDMS] 读取通道 {item.ChannelName}, 数据点: {list.Count}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TDMS] 读取通道 {item.ChannelName} 失败: {ex.Message}");
                    }
                }
                
                Dispatcher.UIThread.Post(() =>
                {
                    ChannelColorsMap = tmpColors;
                    CurrentMultiChannelData = tmpData;
                    ReplayOverviewData = new();
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = tmpData.Count > 0;
                    ClearPersistedReplayContext();
                    StatusMessage = $"已绘制 {tmpData.Count} 个通道";
                    CurveDataUpdated?.Invoke();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    StatusMessage = $"绘制失败: {ex.Message}";
                    CurrentMultiChannelData = new();
                    ReplayOverviewData = new();
                    CurrentCurveData = Array.Empty<CurvePoint>();
                    HasPlottedData = false;
                    ClearPersistedReplayContext();
                    CurveDataUpdated?.Invoke();
                });
            }
        });
    }

    public async Task<bool> ZoomPersistedReplayWindowAsync(
        double wheelDelta,
        double anchorFraction)
    {
        PersistedReplayContext? context = _persistedReplayContext;
        if (context is null)
        {
            return false;
        }

        double currentStart = ReplayWindowStartSeconds;
        double currentEnd = ReplayWindowEndSeconds;
        double currentSpan = Math.Max(0.001, currentEnd - currentStart);
        double factor = wheelDelta > 0 ? 0.75 : 1.3333333333333333;
        double maxSpan = GetReplayMaxWindowSeconds(context);
        double minSpan = Math.Min(ReplayInitialRawWindowSeconds, maxSpan);
        double newSpan = Math.Clamp(currentSpan * factor, minSpan, maxSpan);
        anchorFraction = Math.Clamp(anchorFraction, 0.0, 1.0);
        double anchorTime = currentStart + currentSpan * anchorFraction;
        double newStart = anchorTime - newSpan * anchorFraction;
        newStart = Math.Clamp(newStart, 0.0, Math.Max(0.0, context.TotalDurationSeconds - newSpan));
        double newEnd = Math.Min(context.TotalDurationSeconds, newStart + newSpan);
        return await LoadPersistedReplayWindowAsync(newStart, newEnd);
    }

    public async Task<bool> SeekPersistedReplayCenterAsync(double centerSeconds)
    {
        PersistedReplayContext? context = _persistedReplayContext;
        if (context is null)
        {
            return false;
        }

        double span = Math.Min(
            Math.Max(0.001, ReplayWindowEndSeconds - ReplayWindowStartSeconds),
            GetReplayMaxWindowSeconds(context));
        double halfSpan = span * 0.5;
        centerSeconds = Math.Clamp(centerSeconds, halfSpan, Math.Max(halfSpan, context.TotalDurationSeconds - halfSpan));
        double start = centerSeconds - span * 0.5;
        double end = start + span;
        return await LoadPersistedReplayWindowAsync(start, end);
    }

    public void SetSingleCursorSeconds(double seconds)
    {
        if (_persistedReplayContext is null || CurrentMultiChannelData.Count == 0)
        {
            return;
        }

        seconds = Math.Clamp(seconds, ReplayWindowStartSeconds, ReplayWindowEndSeconds);
        HasSingleCursor = true;
        SingleCursorSeconds = seconds;
        ReplayCursorValueItems.Clear();
        IReadOnlyDictionary<int, double> cursorValues = BuildCursorChannelValues(seconds);

        foreach (var kv in cursorValues.OrderBy(kv => kv.Key))
        {
            int deviceId = kv.Key / 100;
            int channelId = kv.Key % 100;
            ReplayCursorValueItems.Add(new ReplayCursorValueItem(
                $"设备{deviceId}",
                $"通道{channelId}",
                kv.Value));
        }

        ReplayCursorChannelValues = cursorValues;
        ReplayCursorText = ReplayCursorValueItems.Count == 0
            ? $"光标 {seconds:F6}s"
            : $"光标 {seconds:F6}s  |  " + string.Join("  |  ", ReplayCursorValueItems.Select(item => item.ToolTipText));
    }

    public bool TryGetPersistedReplayWindow(
        ViewState state,
        out double windowStart,
        out double windowEnd)
    {
        windowStart = 0.0;
        windowEnd = 0.0;
        if (_persistedReplayContext is null || CurrentMultiChannelData.Count == 0)
        {
            return false;
        }

        IReadOnlyList<CurvePoint>? longest = CurrentMultiChannelData.Values
            .Where(points => points.Count > 1)
            .OrderByDescending(points => points.Count)
            .FirstOrDefault();
        if (longest is null)
        {
            return false;
        }

        int startIndex = Math.Clamp(state.ViewLeft, 0, Math.Max(0, longest.Count - 2));
        int count = state.ViewCount > 1
            ? state.ViewCount
            : longest.Count;
        int endIndex = Math.Clamp(startIndex + count - 1, startIndex + 1, longest.Count - 1);

        windowStart = Math.Max(0.0, longest[startIndex].X);
        windowEnd = Math.Min(_persistedReplayContext.TotalDurationSeconds, longest[endIndex].X);
        return windowEnd - windowStart > 0.001;
    }

    public async Task<bool> LoadPersistedReplayWindowAsync(
        double windowStart,
        double windowEnd)
    {
        PersistedReplayContext? context = _persistedReplayContext;
        if (context is null)
        {
            return false;
        }

        double minimumWindowSeconds = 0.001;
        windowStart = Math.Clamp(
            windowStart,
            0.0,
            Math.Max(0.0, context.TotalDurationSeconds - minimumWindowSeconds));
        windowEnd = Math.Clamp(
            windowEnd,
            Math.Min(context.TotalDurationSeconds, windowStart + minimumWindowSeconds),
            context.TotalDurationSeconds);
        double maxWindowSeconds = GetReplayMaxWindowSeconds(context);
        if (windowEnd - windowStart > maxWindowSeconds)
        {
            double center = (windowStart + windowEnd) * 0.5;
            windowStart = Math.Clamp(center - (maxWindowSeconds * 0.5), 0.0, Math.Max(0.0, context.TotalDurationSeconds - maxWindowSeconds));
            windowEnd = Math.Min(context.TotalDurationSeconds, windowStart + maxWindowSeconds);
        }

        int serial = System.Threading.Interlocked.Increment(ref _persistedReplayQuerySerial);
        PreviewLevel level = ChoosePreviewLevel(context.Index, windowEnd - windowStart);
        var timing = System.Diagnostics.Stopwatch.StartNew();

        var request = new PreviewReadRequest
        {
            SessionId = context.Session.SessionId,
            ViewId = "tdms-browser-window",
            ChannelIds = context.ChannelIds,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            PreviewLevel = level,
            MaxPointsPerChannel = GetReplayMaxPointsPerChannel(context),
            RequireEnvelopeSemantics = true,
            AllowDegradedResult = true,
            RequireCompleteWindow = false
        };

        CurveWindowSnapshot snapshot = await context.Runtime.QueryAsync(request);
        timing.Stop();
        if (snapshot.BuildState == BuildState.Missing || snapshot.ChannelData.Count == 0)
        {
            return false;
        }

        RenderPhaseTimingLogger.LogReplayBrowserQuery(
            context.Session.SessionId.ToString("N"),
            level.ToString(),
            windowStart,
            windowEnd,
            context.ChannelIds.Count,
            timing.Elapsed.TotalMilliseconds,
            snapshot.TotalActualPoints,
            snapshot.IsComplete);

        IReadOnlyList<ReplayStatisticsItem> statisticsItems = context.Index.HasPreviewIndex
            ? await BuildReplayStatisticsItemsAsync(
                context,
                windowStart,
                windowEnd,
                level)
            : Array.Empty<ReplayStatisticsItem>();

        bool applied = false;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (serial != _persistedReplayQuerySerial)
            {
                return;
            }

            ChannelColorsMap = context.Colors;
            CurrentMultiChannelData = snapshot.ChannelData.ToDictionary(kv => kv.Key, kv => kv.Value);
            CurrentCurveData = Array.Empty<CurvePoint>();
            HasPlottedData = CurrentMultiChannelData.Count > 0;
            ReplayWindowStartSeconds = windowStart;
            ReplayWindowEndSeconds = windowEnd;
            RenderPhaseTimingLogger.LogReplayBrowserState(
                context.Session.SessionId.ToString("N"),
                context.Index.HasPreviewIndex,
                context.TotalDurationSeconds,
                windowStart,
                windowEnd,
                context.ChannelIds.Count,
                CurrentMultiChannelData.Sum(static kv => kv.Value.Count),
                ReplayOverviewData.Sum(static kv => kv.Value.Count),
                snapshot.TotalActualPoints);
            ApplyReplayStatisticsItems(statisticsItems);
            if (HasSingleCursor)
            {
                SetSingleCursorSeconds(SingleCursorSeconds);
            }
            StatusMessage = context.Index.HasPreviewIndex
                ? $"已绘制 {CurrentMultiChannelData.Count} 个通道（{level}，{windowEnd - windowStart:F3}s）"
                : $"已绘制 {CurrentMultiChannelData.Count} 个通道（TDMS L0，{windowEnd - windowStart:F3}s，无预览层）";
            CurveDataUpdated?.Invoke();
            applied = true;
        });
        return applied;
    }

    private static async Task<IReadOnlyList<ReplayStatisticsItem>> BuildReplayStatisticsItemsAsync(
        PersistedReplayContext context,
        double windowStart,
        double windowEnd,
        PreviewLevel level)
    {
        try
        {
            var statisticsRequest = new CurveStatisticsRequest
            {
                SessionId = context.Session.SessionId,
                ViewId = "tdms-browser-stats",
                ChannelIds = context.ChannelIds,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                PreviewLevel = level
            };
            CurveStatisticsResult result = await context.Runtime.QueryStatisticsAsync(statisticsRequest);
            return result.ChannelStatistics
                .OrderBy(kv => kv.Key)
                .Select(kv =>
                {
                    CurveChannelStatistics stat = kv.Value;
                    int deviceId = kv.Key / 100;
                    int channelId = kv.Key % 100;
                    return new ReplayStatisticsItem(
                        $"设备{deviceId}",
                        $"通道{channelId}",
                        $"min {stat.Min:F2}",
                        $"max {stat.Max:F2}",
                        $"σ {stat.StandardDeviation:F2}");
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TDMS] replay statistics failed: {ex.Message}");
            return Array.Empty<ReplayStatisticsItem>();
        }
    }

    private void ApplyReplayStatisticsItems(IReadOnlyList<ReplayStatisticsItem> items)
    {
        ReplayStatisticsItems.Clear();
        foreach (ReplayStatisticsItem item in items)
        {
            ReplayStatisticsItems.Add(item);
        }

        ReplayStatisticsText = items.Count == 0
            ? string.Empty
            : string.Join("  |  ", items.Select(item => item.ToolTipText));
    }

    private static int GetReplayMaxPointsPerChannel(PersistedReplayContext context) =>
        GetReplayMaxPointsPerChannel(context.Index);

    private static int GetReplayMaxPointsPerChannel(PreviewIndexSummary index) =>
        index.HasPreviewIndex
            ? ReplayPreferredPreviewPointsPerChannel
            : ReplayRawPointsPerChannel;

    private static double GetReplayMaxWindowSeconds(PersistedReplayContext context) =>
        context.Index.HasPreviewIndex
            ? context.TotalDurationSeconds
            : Math.Min(context.TotalDurationSeconds, ReplayMaxRawWindowSeconds);

    private void SetPersistedReplayContext(
        PersistedReplayContext context,
        double windowStart,
        double windowEnd)
    {
        _persistedReplayContext = context;
        ReplayTotalDurationSeconds = context.TotalDurationSeconds;
        ReplayWindowStartSeconds = windowStart;
        ReplayWindowEndSeconds = windowEnd;
        OnPropertyChanged(nameof(HasPersistedReplay));
        OnPropertyChanged(nameof(ReplayWindowText));
        OnPropertyChanged(nameof(ReplayWindowCenterSeconds));
    }

    private void ClearPersistedReplayContext()
    {
        _persistedReplayContext = null;
        ReplayOverviewData = new();
        ReplayStatisticsItems.Clear();
        ReplayStatisticsText = string.Empty;
        ReplayCursorValueItems.Clear();
        ReplayCursorChannelValues = new Dictionary<int, double>();
        ReplayCursorText = string.Empty;
        HasSingleCursor = false;
        SingleCursorSeconds = 0.0;
        ReplayTotalDurationSeconds = 0.0;
        ReplayWindowStartSeconds = 0.0;
        ReplayWindowEndSeconds = 0.0;
        OnPropertyChanged(nameof(HasPersistedReplay));
        OnPropertyChanged(nameof(ReplayWindowText));
        OnPropertyChanged(nameof(ReplayWindowCenterSeconds));
    }

    private static bool TryInterpolateValueAt(
        IReadOnlyList<CurvePoint> points,
        double x,
        out double value)
    {
        value = 0.0;
        if (points.Count == 0)
        {
            return false;
        }

        if (points.Count == 1 || x <= points[0].X)
        {
            value = points[0].Y;
            return true;
        }

        int last = points.Count - 1;
        if (x >= points[last].X)
        {
            value = points[last].Y;
            return true;
        }

        int lo = 0;
        int hi = last;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            double midX = points[mid].X;
            if (midX < x)
            {
                lo = mid + 1;
            }
            else if (midX > x)
            {
                hi = mid - 1;
            }
            else
            {
                value = points[mid].Y;
                return true;
            }
        }

        int right = Math.Clamp(lo, 1, last);
        int left = right - 1;
        CurvePoint p0 = points[left];
        CurvePoint p1 = points[right];
        double span = p1.X - p0.X;
        if (Math.Abs(span) <= 1e-12)
        {
            value = p0.Y;
            return true;
        }

        double t = Math.Clamp((x - p0.X) / span, 0.0, 1.0);
        value = p0.Y + (p1.Y - p0.Y) * t;
        return true;
    }

    private static async Task<PlotBuildResult?> TryBuildPersistedPreviewPlotAsync(
        string inputPath,
        IReadOnlyList<ChannelSelectionItem> selectedItems)
    {
        string? artifactPath = ResolveArtifactPath(inputPath);
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return null;
        }

        try
        {
            var index = await ReadReplayIndexSummaryAsync(artifactPath, selectedItems.Select(item => item.ChannelId));
            if (index is null || index.SampleRateHz <= 0 || index.TotalDurationSeconds <= 0)
            {
                return null;
            }

            var catalog = new FileSystemDataSessionCatalog();
            SessionDescriptor session = await catalog.OpenAsync(artifactPath);
            var runtime = new PersistedPreviewQueryRuntime(artifactPath, session);

            int[] channelIds = selectedItems
                .Select(item => item.ChannelId)
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (channelIds.Length == 0)
            {
                return null;
            }

            double initialWindowStart = 0.0;
            double initialWindowEnd = index.HasPreviewIndex
                ? index.TotalDurationSeconds
                : Math.Min(index.TotalDurationSeconds, ReplayInitialRawWindowSeconds);
            PreviewLevel level = ChoosePreviewLevel(index, initialWindowEnd - initialWindowStart);
            var request = new PreviewReadRequest
            {
                SessionId = session.SessionId,
                ViewId = "tdms-browser-full",
                ChannelIds = channelIds,
                WindowStart = initialWindowStart,
                WindowEnd = initialWindowEnd,
                PreviewLevel = level,
                MaxPointsPerChannel = GetReplayMaxPointsPerChannel(index),
                RequireEnvelopeSemantics = true,
                AllowDegradedResult = true,
                RequireCompleteWindow = false
            };

            CurveWindowSnapshot snapshot = await runtime.QueryAsync(request);
            if (snapshot.BuildState == BuildState.Missing || snapshot.ChannelData.Count == 0)
            {
                return null;
            }

            var colors = selectedItems
                .Where(item => snapshot.ChannelData.ContainsKey(item.ChannelId))
                .ToDictionary(item => item.ChannelId, item => item.Color);
            string status = index.HasPreviewIndex
                ? $"已绘制 {snapshot.ChannelData.Count} 个通道（{level} 全览，{index.TotalDurationSeconds:F2}s）"
                : $"已绘制 {snapshot.ChannelData.Count} 个通道（TDMS L0，{initialWindowEnd - initialWindowStart:F3}s / {index.TotalDurationSeconds:F2}s，无预览层）";
            var replayContext = new PersistedReplayContext(
                runtime,
                session,
                index,
                channelIds,
                colors,
                index.TotalDurationSeconds);
            RenderPhaseTimingLogger.LogReplayBrowserOpen(
                session.SessionId.ToString("N"),
                artifactPath,
                index.HasPreviewIndex,
                index.SampleRateHz,
                index.TotalDurationSeconds,
                initialWindowStart,
                initialWindowEnd,
                channelIds.Length,
                snapshot.TotalActualPoints,
                snapshot.BuildState.ToString());

            return new PlotBuildResult(
                snapshot.ChannelData.ToDictionary(kv => kv.Key, kv => kv.Value),
                colors,
                status,
                replayContext,
                initialWindowStart,
                initialWindowEnd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TDMS] persisted preview query fallback: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveArtifactPath(string inputPath)
    {
        if (Directory.Exists(inputPath))
        {
            string fullPath = Path.GetFullPath(inputPath);
            string? artifactRoot = FindArtifactRootInDirectory(fullPath);
            if (!string.IsNullOrWhiteSpace(artifactRoot))
            {
                return artifactRoot;
            }

            string leafName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (leafName.EndsWith(".artifacts", StringComparison.OrdinalIgnoreCase)
                && ResolveRecoveredRawRoot(fullPath) is not null)
            {
                return fullPath;
            }

            if (string.Equals(leafName, "raw", StringComparison.OrdinalIgnoreCase)
                || string.Equals(leafName, "compressed", StringComparison.OrdinalIgnoreCase))
            {
                string? parent = Directory.GetParent(fullPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    return FindArtifactRootInDirectory(parent) ?? parent;
                }
            }

            if (ContainsSourceSegmentTdmsFiles(fullPath))
            {
                return fullPath;
            }

            return null;
        }

        if (!File.Exists(inputPath))
        {
            return null;
        }

        string directory = Path.GetDirectoryName(inputPath) ?? ".";
        string fileName = Path.GetFileName(inputPath);

        if (fileName.EndsWith(".tdms", StringComparison.OrdinalIgnoreCase))
        {
            string? siblingArtifactRoot = FindArtifactRootInDirectory(directory);
            if (!string.IsNullOrWhiteSpace(siblingArtifactRoot))
            {
                return siblingArtifactRoot;
            }

            string? parentArtifactRoot = FindArtifactRootInDirectory(
                Directory.GetParent(directory)?.FullName ?? directory);
            if (!string.IsNullOrWhiteSpace(parentArtifactRoot))
            {
                return parentArtifactRoot;
            }

            string? parentDirectory = Directory.GetParent(directory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDirectory)
                && ContainsSourceSegmentTdmsFiles(parentDirectory))
            {
                return parentDirectory;
            }

            string artifacts = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(fileName)}.artifacts");
            return IsSessionArtifactRoot(artifacts) ? artifacts : null;
        }

        if (fileName.EndsWith(".sdkraw.bin", StringComparison.OrdinalIgnoreCase))
        {
            string stem = fileName[..^".sdkraw.bin".Length];
            return Directory
                .EnumerateDirectories(directory, $"{stem}_converted_*.artifacts", SearchOption.TopDirectoryOnly)
                .Where(IsSessionArtifactRoot)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return null;
    }

    private static bool ContainsSourceSegmentTdmsFiles(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        string rawPath = Path.Combine(directoryPath, "raw");
        string searchRoot = Directory.Exists(rawPath) ? rawPath : directoryPath;
        return Directory
            .EnumerateFiles(searchRoot, "source_*_seg*.tdms", SearchOption.TopDirectoryOnly)
            .Any();
    }

    private static string? FindArtifactRootInDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return null;
        }

        if (IsSessionArtifactRoot(directoryPath))
        {
            return directoryPath;
        }

        string? artifactRoot = Directory
            .EnumerateDirectories(directoryPath, "*.artifacts", SearchOption.TopDirectoryOnly)
            .Where(IsSessionArtifactRoot)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(artifactRoot))
        {
            return artifactRoot;
        }

        return Directory
            .EnumerateDirectories(directoryPath, "*.artifacts", SearchOption.TopDirectoryOnly)
            .Where(path => ContainsSourceSegmentTdmsFiles(Directory.GetParent(path)?.FullName ?? directoryPath))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? ResolveTdmsStructureProbePath(string inputPath)
    {
        if (File.Exists(inputPath))
        {
            return inputPath;
        }

        if (!Directory.Exists(inputPath))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(inputPath);
        string rawPath = Path.Combine(fullPath, "raw");
        IEnumerable<string> roots = Directory.Exists(rawPath)
            ? new[] { rawPath, fullPath }
            : new[] { fullPath };

        foreach (string root in roots)
        {
            string? tdmsFile = Directory
                .EnumerateFiles(root, "*.tdms", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(tdmsFile))
            {
                return tdmsFile;
            }
        }

        return Directory
            .EnumerateFiles(fullPath, "*.tdms", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool TryLoadPersistedSessionChannels(
        string inputPath,
        out Dictionary<string, string[]> channelsByGroup)
    {
        channelsByGroup = new Dictionary<string, string[]>();
        string? artifactPath = ResolveArtifactPath(inputPath);
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return false;
        }

        string manifestPath = Path.Combine(artifactPath, "session.manifest.json");
        if (!File.Exists(manifestPath))
        {
            return TryLoadRecoveredSourceSegmentChannels(artifactPath, out channelsByGroup);
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("Sources", out JsonElement sourcesElement)
                || sourcesElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var results = new Dictionary<string, string[]>();
            foreach (JsonElement sourceElement in sourcesElement.EnumerateArray())
            {
                int sourceId = sourceElement.TryGetProperty("SourceId", out JsonElement sourceIdElement)
                    && sourceIdElement.TryGetInt32(out int parsedSourceId)
                        ? parsedSourceId
                        : 0;
                int channelCount = sourceElement.TryGetProperty("ChannelCount", out JsonElement channelCountElement)
                    && channelCountElement.TryGetInt32(out int parsedChannelCount)
                        ? parsedChannelCount
                        : 0;
                if (channelCount <= 0)
                {
                    continue;
                }

                string groupName = $"source_{sourceId:D4}";
                results[groupName] = Enumerable.Range(1, channelCount)
                    .Select(channelNumber => $"AI{sourceId * 100 + channelNumber:D4}")
                    .ToArray();
            }

            if (results.Count == 0)
            {
                return false;
            }

            channelsByGroup = results;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TDMS] failed to read persisted session channels: {ex.Message}");
            return TryLoadRecoveredSourceSegmentChannels(artifactPath, out channelsByGroup);
        }
    }

    private static bool TryLoadRecoveredSourceSegmentChannels(
        string artifactOrSessionPath,
        out Dictionary<string, string[]> channelsByGroup)
    {
        channelsByGroup = new Dictionary<string, string[]>();
        string? rawRoot = ResolveRecoveredRawRoot(artifactOrSessionPath);
        if (string.IsNullOrWhiteSpace(rawRoot) || !Directory.Exists(rawRoot))
        {
            return false;
        }

        var results = new Dictionary<string, string[]>();
        foreach (var group in Directory
            .EnumerateFiles(rawRoot, "source_*_seg*.tdms", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: TryParseSourceSegment(path)))
            .Where(item => item.Parsed.SourceId >= 0)
            .GroupBy(item => item.Parsed.SourceId)
            .OrderBy(group => group.Key))
        {
            string groupName = $"source_{group.Key:D4}";
            foreach (string filePath in group.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var structure = TdmsReaderUtil.ListGroupsAndChannels(filePath);
                    if (structure.TryGetValue(groupName, out string[] channels) && channels.Length > 0)
                    {
                        results[groupName] = channels;
                        break;
                    }
                }
                catch
                {
                    // Try the next segment for this source. A power-loss test can leave the newest file partial.
                }
            }
        }

        if (results.Count == 0)
        {
            return false;
        }

        channelsByGroup = results;
        return true;
    }

    private static string? ResolveRecoveredRawRoot(string artifactOrSessionPath)
    {
        string fullPath = Path.GetFullPath(artifactOrSessionPath);
        string leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (leaf.EndsWith(".artifacts", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Directory.GetParent(fullPath)?.FullName;
            string siblingRaw = !string.IsNullOrWhiteSpace(parent) ? Path.Combine(parent, "raw") : string.Empty;
            return Directory.Exists(siblingRaw) ? siblingRaw : null;
        }

        string childRaw = Path.Combine(fullPath, "raw");
        return Directory.Exists(childRaw) ? childRaw : null;
    }

    private static (int SourceId, int SegmentIndex) TryParseSourceSegment(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        var match = System.Text.RegularExpressions.Regex.Match(
            name,
            @"source_(\d+)_seg(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out int sourceId)
            || !int.TryParse(match.Groups[2].Value, out int segmentIndex))
        {
            return (-1, -1);
        }

        return (sourceId, segmentIndex);
    }

    private static bool IsSessionArtifactRoot(string artifactPath)
    {
        return !string.IsNullOrWhiteSpace(artifactPath)
            && File.Exists(Path.Combine(artifactPath, "session.manifest.json"));
    }

    private static bool HasPreviewIndex(string artifactPath)
    {
        return !string.IsNullOrWhiteSpace(artifactPath)
            && File.Exists(Path.Combine(artifactPath, "preview_levels", "preview.index.json"));
    }

    private static async Task<PreviewIndexSummary?> ReadReplayIndexSummaryAsync(
        string artifactPath,
        IEnumerable<int> preferredChannelIds)
    {
        PreviewIndexSummary? preview = await ReadPreviewIndexSummaryAsync(artifactPath);
        if (preview is not null)
        {
            return preview;
        }

        PreviewIndexSummary? manifest = await ReadTdmsManifestSummaryAsync(artifactPath, preferredChannelIds);
        if (manifest is not null)
        {
            return manifest;
        }

        return ReadRecoveredTdmsSummary(artifactPath, preferredChannelIds);
    }

    private static async Task<PreviewIndexSummary?> ReadPreviewIndexSummaryAsync(string artifactPath)
    {
        string indexPath = Path.Combine(artifactPath, "preview_levels", "preview.index.json");
        if (!File.Exists(indexPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(indexPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        JsonElement root = document.RootElement;
        double sampleRateHz = root.TryGetProperty("SampleRateHz", out JsonElement sampleRateElement)
            ? sampleRateElement.GetDouble()
            : 0.0;

        var levels = new Dictionary<PreviewLevel, long>();
        long maxEndSampleIndex = 0;
        if (root.TryGetProperty("Files", out JsonElement filesElement)
            && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.TryGetProperty("EndSampleIndex", out JsonElement endElement)
                    && endElement.TryGetInt64(out long endSampleIndex))
                {
                    maxEndSampleIndex = Math.Max(maxEndSampleIndex, endSampleIndex);
                }

                string? levelText = fileElement.TryGetProperty("LevelName", out JsonElement levelElement)
                    ? levelElement.GetString()
                    : null;
                if (!Enum.TryParse(levelText, ignoreCase: true, out PreviewLevel level))
                {
                    continue;
                }

                if (fileElement.TryGetProperty("BucketSampleSpan", out JsonElement spanElement)
                    && spanElement.TryGetInt64(out long span)
                    && span > 0)
                {
                    levels[level] = levels.TryGetValue(level, out long existing)
                        ? Math.Min(existing, span)
                        : span;
                }
            }
        }

        double totalDurationSeconds = sampleRateHz > 0
            ? maxEndSampleIndex / sampleRateHz
            : 0.0;
        return new PreviewIndexSummary(sampleRateHz, totalDurationSeconds, levels, HasPreviewIndex: true);
    }

    private static async Task<PreviewIndexSummary?> ReadTdmsManifestSummaryAsync(
        string artifactPath,
        IEnumerable<int> preferredChannelIds)
    {
        string manifestPath = Path.Combine(artifactPath, "session.manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(manifestPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        JsonElement root = document.RootElement;
        double sampleRateHz = root.TryGetProperty("SampleRateHz", out JsonElement rootSampleRateElement)
            && rootSampleRateElement.TryGetDouble(out double parsedRootSampleRate)
                ? parsedRootSampleRate
                : 0.0;

        var channelIds = preferredChannelIds.Where(id => id > 0).Distinct().ToHashSet();
        var maxEndByChannel = channelIds.ToDictionary(id => id, _ => 0L);
        long maxEndSampleIndex = 0L;
        bool timelineAlreadyAligned = root.TryGetProperty("TdmsTimelineAlignedBySourceTiming", out JsonElement alignedElement)
            && alignedElement.ValueKind == JsonValueKind.True;
        IReadOnlyDictionary<int, long> sourceTimingOffsets = timelineAlreadyAligned
            ? new Dictionary<int, long>()
            : ReadTdmsManifestSourceTimingOffsets(root);
        if (root.TryGetProperty("TdmsSegments", out JsonElement segmentsElement)
            && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segmentElement in segmentsElement.EnumerateArray())
            {
                if (sampleRateHz <= 0
                    && segmentElement.TryGetProperty("SampleRateHz", out JsonElement segmentSampleRateElement)
                    && segmentSampleRateElement.TryGetDouble(out double parsedSegmentSampleRate))
                {
                    sampleRateHz = parsedSegmentSampleRate;
                }

                long endSample = segmentElement.TryGetProperty("EndSampleExclusive", out JsonElement endElement)
                    && endElement.TryGetInt64(out long parsedEnd)
                        ? parsedEnd
                        : 0L;
                if (endSample <= 0
                    && segmentElement.TryGetProperty("StartSample", out JsonElement startElement)
                    && startElement.TryGetInt64(out long startSample)
                    && segmentElement.TryGetProperty("SamplesPerChannel", out JsonElement samplesElement)
                    && samplesElement.TryGetInt64(out long samplesPerChannel))
                {
                    endSample = startSample + samplesPerChannel;
                }

                int sourceId = segmentElement.TryGetProperty("SourceId", out JsonElement sourceElement)
                    && sourceElement.TryGetInt32(out int parsedSourceId)
                        ? parsedSourceId
                        : -1;
                if (sourceTimingOffsets.TryGetValue(sourceId, out long sourceOffsetSamples))
                {
                    endSample += sourceOffsetSamples;
                }

                maxEndSampleIndex = Math.Max(maxEndSampleIndex, endSample);
                if (channelIds.Count == 0
                    || !segmentElement.TryGetProperty("ChannelIds", out JsonElement segmentChannels)
                    || segmentChannels.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement channelElement in segmentChannels.EnumerateArray())
                {
                    if (channelElement.TryGetInt32(out int channelId)
                        && maxEndByChannel.ContainsKey(channelId))
                    {
                        maxEndByChannel[channelId] = Math.Max(maxEndByChannel[channelId], endSample);
                    }
                }
            }
        }

        long coveredEndSample = maxEndByChannel.Count > 0
            ? maxEndByChannel.Values.Where(value => value > 0).DefaultIfEmpty(maxEndSampleIndex).Min()
            : maxEndSampleIndex;
        double totalDurationSeconds = sampleRateHz > 0
            ? coveredEndSample / sampleRateHz
            : 0.0;

        return sampleRateHz > 0 && totalDurationSeconds > 0
            ? new PreviewIndexSummary(sampleRateHz, totalDurationSeconds, new Dictionary<PreviewLevel, long>(), HasPreviewIndex: false)
            : null;
    }

    private static IReadOnlyDictionary<int, long> ReadTdmsManifestSourceTimingOffsets(JsonElement root)
    {
        if (!root.TryGetProperty("SourceTimingDiagnostics", out JsonElement timingsElement)
            || timingsElement.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<int, long>();
        }

        var offsets = new Dictionary<int, long>();
        foreach (JsonElement timingElement in timingsElement.EnumerateArray())
        {
            if (!timingElement.TryGetProperty("SourceId", out JsonElement sourceElement)
                || !sourceElement.TryGetInt32(out int sourceId)
                || !timingElement.TryGetProperty("FirstTotalDataOffsetSamples", out JsonElement offsetElement)
                || !offsetElement.TryGetInt64(out long offsetSamples)
                || offsetSamples <= 0)
            {
                continue;
            }

            offsets[sourceId] = offsetSamples;
        }

        return offsets;
    }

    private static PreviewIndexSummary? ReadRecoveredTdmsSummary(
        string artifactPath,
        IEnumerable<int> preferredChannelIds)
    {
        string? rawRoot = ResolveRecoveredRawRoot(artifactPath);
        if (string.IsNullOrWhiteSpace(rawRoot) || !Directory.Exists(rawRoot))
        {
            return null;
        }

        var selectedChannelIds = preferredChannelIds.Where(id => id > 0).Distinct().ToHashSet();
        var maxEndByChannel = selectedChannelIds.ToDictionary(id => id, _ => 0L);
        double sampleRateHz = 0.0;
        long maxEndSampleIndex = 0L;

        foreach (var sourceGroup in Directory
            .EnumerateFiles(rawRoot, "source_*_seg*.tdms", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Parsed: TryParseSourceSegment(path)))
            .Where(item => item.Parsed.SourceId >= 0 && item.Parsed.SegmentIndex > 0)
            .GroupBy(item => item.Parsed.SourceId)
            .OrderBy(group => group.Key))
        {
            long sourceCursor = 0L;
            string groupName = $"source_{sourceGroup.Key:D4}";
            foreach (var item in sourceGroup.OrderBy(item => item.Parsed.SegmentIndex))
            {
                try
                {
                    var structure = TdmsReaderUtil.ListGroupsAndChannels(item.Path);
                    if (!structure.TryGetValue(groupName, out string[] channels) || channels.Length == 0)
                    {
                        continue;
                    }

                    IReadOnlyDictionary<string, object> props;
                    try
                    {
                        props = TdmsReaderUtil.ReadChannelProperties(item.Path, groupName, channels[0]);
                    }
                    catch
                    {
                        props = new Dictionary<string, object>();
                    }

                    int samplesPerChannel = TryGetInt(props, "dh_original_sample_count")
                        ?? EstimateUncompressedTdmsSamplesPerChannel(item.Path, channels.Length);
                    if (samplesPerChannel <= 0)
                    {
                        continue;
                    }

                    if (sampleRateHz <= 0)
                    {
                        double? increment = TryGetDouble(props, "wf_increment");
                        if (increment is > 0)
                        {
                            sampleRateHz = 1.0 / increment.Value;
                        }
                    }

                    long endSample = sourceCursor + samplesPerChannel;
                    sourceCursor = endSample;
                    maxEndSampleIndex = Math.Max(maxEndSampleIndex, endSample);

                    if (maxEndByChannel.Count == 0)
                    {
                        continue;
                    }

                    var channelIds = channels
                        .Select(ParseChannelId)
                        .Where(id => id > 0 && maxEndByChannel.ContainsKey(id))
                        .Distinct();
                    foreach (int channelId in channelIds)
                    {
                        maxEndByChannel[channelId] = Math.Max(maxEndByChannel[channelId], endSample);
                    }
                }
                catch
                {
                    // Abrupt shutdown can leave the latest segment incomplete; skip unreadable segments.
                }
            }
        }

        if (sampleRateHz <= 0 && maxEndSampleIndex > 0)
        {
            sampleRateHz = 1_000_000.0;
        }

        if (sampleRateHz <= 0 || maxEndSampleIndex <= 0)
        {
            return null;
        }

        long coveredEndSample = maxEndByChannel.Count > 0
            ? maxEndByChannel.Values.Where(value => value > 0).DefaultIfEmpty(maxEndSampleIndex).Min()
            : maxEndSampleIndex;
        double totalDurationSeconds = coveredEndSample / sampleRateHz;
        return totalDurationSeconds > 0
            ? new PreviewIndexSummary(sampleRateHz, totalDurationSeconds, new Dictionary<PreviewLevel, long>(), HasPreviewIndex: false)
            : null;
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l when l <= int.MaxValue && l >= int.MinValue => (int)l,
            short s => s,
            byte b => b,
            double d when d <= int.MaxValue && d >= int.MinValue => (int)d,
            float f when f <= int.MaxValue && f >= int.MinValue => (int)f,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => null
        };
    }

    private static int EstimateUncompressedTdmsSamplesPerChannel(string filePath, int channelCount)
    {
        if (channelCount <= 0)
        {
            return 0;
        }

        long rawDataOffset = TryReadTdmsRawDataOffset(filePath);
        if (rawDataOffset < 0)
        {
            return 0;
        }

        try
        {
            long payloadBytes = new FileInfo(filePath).Length - rawDataOffset;
            long samples = payloadBytes / sizeof(float) / channelCount;
            return samples > 0 && samples <= int.MaxValue ? (int)samples : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static long TryReadTdmsRawDataOffset(string filePath)
    {
        Span<byte> header = stackalloc byte[28];
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            int read = stream.Read(header);
            if (read < header.Length
                || header[0] != (byte)'T'
                || header[1] != (byte)'D'
                || header[2] != (byte)'S'
                || header[3] != (byte)'m')
            {
                return -1;
            }

            ulong rawDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8));
            return checked(28L + (long)rawDataOffset);
        }
        catch
        {
            return -1;
        }
    }

    private static PreviewLevel ChoosePreviewLevel(
        PreviewIndexSummary index,
        double windowSeconds)
    {
        double windowSamples = Math.Max(1.0, windowSeconds * index.SampleRateHz);
        var candidates = index.LevelBucketSpans
            .Where(kv => kv.Key != PreviewLevel.L0 && kv.Value > 0)
            .Select(kv => new
            {
                Level = kv.Key,
                BucketSampleSpan = kv.Value,
                EstimatedPoints = Math.Ceiling(windowSamples / kv.Value) * 2.0
            })
            .OrderBy(item => item.BucketSampleSpan)
            .ToArray();
        if (candidates.Length == 0)
        {
            return PreviewLevel.L0;
        }

        var finestWithinBudget = candidates
            .Where(item => item.EstimatedPoints <= ReplayPreferredPreviewPointsPerChannel)
            .OrderBy(item => item.BucketSampleSpan)
            .FirstOrDefault();
        if (finestWithinBudget is not null)
        {
            return finestWithinBudget.Level;
        }

        return candidates
            .OrderByDescending(item => item.BucketSampleSpan)
            .Select(item => item.Level)
            .FirstOrDefault();
    }

    private sealed record PreviewIndexSummary(
        double SampleRateHz,
        double TotalDurationSeconds,
        IReadOnlyDictionary<PreviewLevel, long> LevelBucketSpans,
        bool HasPreviewIndex);

    private sealed record PlotBuildResult(
        Dictionary<int, IReadOnlyList<CurvePoint>> Data,
        Dictionary<int, Color> Colors,
        string StatusMessage,
        PersistedReplayContext ReplayContext,
        double WindowStartSeconds,
        double WindowEndSeconds);

    private sealed record PersistedReplayContext(
        PersistedPreviewQueryRuntime Runtime,
        SessionDescriptor Session,
        PreviewIndexSummary Index,
        IReadOnlyList<int> ChannelIds,
        Dictionary<int, Color> Colors,
        double TotalDurationSeconds);

    private static int ParseDeviceIdFromDeviceName(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return -1;
        // 解析 "设备 X (AIxx)" 格式
        var match = System.Text.RegularExpressions.Regex.Match(deviceName, @"设备\s*(\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id))
            return id;  // 可以是0、1、2等
        return -1;  // -1表示未找到
    }

    private static int ParseChannelId(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        // 尝试解析 AI{设备号}-{通道号} 格式，如 AI0-01 -> 1, AI1-16 -> 116
        var match = System.Text.RegularExpressions.Regex.Match(name, @"AI(\d+)-(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            if (int.TryParse(match.Groups[1].Value, out var dev) && int.TryParse(match.Groups[2].Value, out var ch))
                return dev * 100 + ch;
        }
        // 退化：提取所有数字
        var digits = new string(name.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var id)) return id;
        return -1;  // -1表示未找到
    }

    private static int ParseDeviceIdFromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;
        try
        {
            // 尝试解析 AI{设备号}-{通道号} 格式，如 AI0-01, AI1-16
            int ai = name.IndexOf("AI", StringComparison.OrdinalIgnoreCase);
            if (ai >= 0 && ai + 2 < name.Length)
            {
                string s = name.Substring(ai + 2);
                // 提取设备号（直到遇到非数字字符）
                var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
                if (digits.Length >= 1 && int.TryParse(digits, out var dev)) return dev;
            }
            // 尝试从通道ID解析（格式：设备ID*100+通道号）
            int chId = ParseChannelId(name);
            if (chId >= 0) return chId / 100;  // 支持设备ID=0
            return -1;
        }
        catch { return -1; }
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            double d => d,
            float f => (double)f,
            int i => (double)i,
            long l => (double)l,
            string s when double.TryParse(s, out var d2) => d2,
            _ => null
        };
    }

    private static Color GetColorByIndex(int index)
    {
        var palette = new List<Color>
        {
            Color.Parse("#D62728"), // 红
            Color.Parse("#4169E1"), // 蓝
            Color.Parse("#2CA02C"), // 绿
            Color.Parse("#FF8C00"), // 深橙
            Color.Parse("#9467BD"), // 紫色
            Color.Parse("#8B4513"), // 褐色
            Color.Parse("#E377C2"), // 粉色
            Color.Parse("#7F7F7F"), // 灰色
            Color.Parse("#BCBD22"), // 黄绿
            Color.Parse("#17BECF"), // 青色
            Color.Parse("#FF6347"), // 番茄红
            Color.Parse("#4682B4"), // 钢蓝
            Color.Parse("#32CD32"), // 酸橙绿
            Color.Parse("#FF69B4"), // 热粉
            Color.Parse("#CD5C5C"), // 印度红
            Color.Parse("#6A5ACD"), // 石板蓝
        };
        
        return palette[index % palette.Count];
    }
}

/// <summary>
/// 通道选择项（用于多选列表）
/// </summary>
public class ChannelSelectionItem : ObservableObject
{
    public string GroupName { get; }
    public string ChannelName { get; }
    public int ChannelId { get; }
    public Color Color { get; }
    public IBrush ColorBrush { get; }
    
    public string DisplayName => ChannelName;
    
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
    
    public ChannelSelectionItem(string groupName, string channelName, int channelId, Color color)
    {
        GroupName = groupName;
        ChannelName = channelName;
        // channelId >= 0 表示有效的通道ID（设备0的通道1的ID是1）
        // -1 表示解析失败，使用哈希值作为备用ID
        ChannelId = channelId >= 0 ? channelId : Math.Abs(channelName.GetHashCode());
        Color = color;
        ColorBrush = new SolidColorBrush(color);
    }
}

public class DeviceSelectionItem : ObservableObject
{
    public string DeviceName { get; }

    public string DisplayName => DeviceName;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public DeviceSelectionItem(string deviceName)
    {
        DeviceName = deviceName;
    }
}

public sealed record ReplayStatisticsItem(
    string DeviceName,
    string ChannelName,
    string MinText,
    string MaxText,
    string StdDevText)
{
    public string ToolTipText => $"{DeviceName} {ChannelName}: {MinText}, {MaxText}, {StdDevText}";
}

public sealed record ReplayCursorValueItem(
    string DeviceName,
    string ChannelName,
    double Value)
{
    public string ValueText => Value.ToString("F3");
    public string ToolTipText => $"{DeviceName} {ChannelName}: {Value:F6}";
}
