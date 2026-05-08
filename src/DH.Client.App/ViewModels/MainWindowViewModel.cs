using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Contracts.Models;
using DH.Driver;
using DH.Driver.SDK;
using DH.Datamanage.Realtime;
using DH.Algorithms.Builtins;
using DH.Client.App.Data;
using DH.Client.App.Services;
using DH.Client.App.Services.Storage;
using DH.Client.App.Controls;

namespace DH.Client.App.ViewModels;

/// <summary>文件列表项：携带路径和格式化的显示文本（含文件大小）</summary>
public sealed class TdmsFileItem
{
    public string FullPath { get; }
    public string DisplayText { get; }
    public string FileName { get; }
    public string SizeText { get; }
    public string FolderText { get; }
    public string DetailText { get; }

    public TdmsFileItem(FileInfo fi)
    {
        FullPath = fi.FullName;
        // 显示所在文件夹名（时间命名子文件夹）+ 文件名 + 大小
        var folderName = fi.Directory?.Name ?? "";
        FolderText = !string.IsNullOrEmpty(folderName) && folderName != "data"
            ? $"[{folderName}]"
            : "[data]";
        FileName = fi.Name;
        SizeText = FormatSize(fi.Length);
        DetailText = $"{FolderText}  {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
        DisplayText = $"{FolderText} {FileName}  ({SizeText})";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    public override string ToString() => DisplayText;
}

public partial class MainWindowViewModel : ObservableObject
{
    private static readonly string DefaultStoragePath = AppDataPaths.DataRoot;
    private static readonly string DefaultSdkConfigPath = SdkPathDefaults.ResolveDefaultConfigPath();
    private static readonly string StorageUiPreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DH.Client.App",
        "storage-ui.json");

    public ObservableCollection<ChannelInfo> Channels { get; } = new();
    public ObservableCollection<DeviceInfo> Devices { get; } = new();
    public ObservableCollection<ChannelStatus> DeviceChannels { get; } = new();
    public ObservableCollection<ChannelStatus> OnlineChannels { get; } = new();
    
    // 在线通道统计
    public string OnlineChannelStatus => $"在线通道: {Channels.Count(c => c.Online)}/{Channels.Count}";
    [ObservableProperty] private int _selectedTab = 3;
    [ObservableProperty] private string _storagePath = DefaultStoragePath;
    // 新增：存储控制与模式
    private enum StorageRuntimeKind { Tdms, SdkRawCapture, SdkTdmsCapture }
    [ObservableProperty] private bool _storageEnabled;
    [ObservableProperty] private string _storageSessionName = "session";
    [ObservableProperty] private int _storageSessionNamingModeIndex = 1;
    [ObservableProperty] private int _storageCompressionAlgorithmIndex;
    [ObservableProperty] private int _storageCompressionPreprocessIndex;
    [ObservableProperty] private int _storageCompressionZstdLevel = 3;
    [ObservableProperty] private int _storageCompressionZstdWindowLog = 23;
    [ObservableProperty] private int _storageCompressionLz4Level;
    [ObservableProperty] private int _storageCompressionLz4HcLevel = 12;
    [ObservableProperty] private int _storageCompressionZlibLevel = 6;
    [ObservableProperty] private int _storageCompressionBZip2BlockSize = 9;
    [ObservableProperty] private string _storageCompressionConfigStatus = "compression disabled";
    public ObservableCollection<string> StorageCompressionAlgorithmOptions { get; } = new()
    {
        "无压缩",
        "ZSTD",
        "LZ4",
        "Snappy",
        "Zlib",
        "LZ4 HC",
        "BZip2"
    };
    public ObservableCollection<string> StorageCompressionPreprocessOptions { get; } = new()
    {
        "None",
        "Diff 1",
        "Diff 2",
        "LPC"
    };
    public ObservableCollection<string> StorageSessionNamingOptions { get; } = new()
    {
        "自定义名称",
        "按存储时间"
    };
    public bool IsCustomSessionNamingSelected => StorageSessionNamingModeIndex == 0;
    public bool IsTimeSessionNamingSelected => StorageSessionNamingModeIndex == 1;
    public bool IsZstdCompressionSelected => GetSelectedCompressionType() == CompressionType.Zstd;
    public bool IsLz4CompressionSelected => GetSelectedCompressionType() == CompressionType.LZ4;
    public bool IsLz4HcCompressionSelected => GetSelectedCompressionType() == CompressionType.LZ4_HC;
    public bool IsZlibCompressionSelected => GetSelectedCompressionType() == CompressionType.Zlib;
    public bool IsBZip2CompressionSelected => GetSelectedCompressionType() == CompressionType.BZip2;
    // 文件无损验证结果
    [ObservableProperty] private string _fileVerifyResult = "";
    [ObservableProperty] private bool _fileVerifyPassed;
    // 写入哈希缓存：文件路径 → {通道名 → hash/sampleCount}（支持跨文件手动验证）
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _writeHashesByFile = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _writeSampleCountsByFile = new();
    private IReadOnlyList<string>? _lastWrittenFiles;
    private StorageRuntimeKind? _activeStorageRuntime;
    // 新增：存储状态与最近文件列表
    [ObservableProperty] private string _storageStatusMessage = "未开始写入";
    // 写入计时器
    [ObservableProperty] private string _storageElapsed = "00:00:00";
    private DateTime _storageStartTime;
    private Avalonia.Threading.DispatcherTimer? _storageTimer;
    public ObservableCollection<TdmsFileItem> RecentTdmsFiles { get; } = new();
    [ObservableProperty] private TdmsFileItem? _selectedTdmsFile;

    // 命令：存储控制
    public IRelayCommand StartStorageCommand { get; }
    public IRelayCommand StopStorageCommand { get; }
    public IRelayCommand BrowseStoragePathCommand { get; }
    // 新增：最近文件与读取相关命令
    public IRelayCommand RefreshRecentFilesCommand { get; }
    public IRelayCommand OpenOutputFolderCommand { get; }
    public IRelayCommand TestReadSelectedFileCommand { get; }
    public IRelayCommand VerifyStoredFileCommand { get; }
    private SdkRawCaptureWriter? _sdkRawCaptureWriter;
    private SdkTdmsCaptureWriter? _sdkTdmsCaptureWriter;
    private Action<SdkRawBlock>? _sdkRawBlockHandler;
    private bool _sdkRawCaptureProtectionStopPending;
    private bool _sdkTdmsCaptureStopInProgress;
    [ObservableProperty] private int _maWindow = 16;
    [ObservableProperty] private bool _isRunning;
    
    [ObservableProperty] private string _tcpServerIp = "127.0.0.1";
    [ObservableProperty] private string _tcpServerPort = "4008";
    [ObservableProperty] private string _tcpConnectionStatus = "未连接";
    [ObservableProperty] private bool _isTcpConnected;
    [ObservableProperty] private bool _isDataVerified;
    [ObservableProperty] private bool _isDataActive;
    [ObservableProperty] private int _channelId = 1;
    
    [ObservableProperty] private int _sampleRate = 1000; // 默认采样频率 1000Hz

    private const int DefaultOnlineChannelCount = 8;

    // 计算属性：根据连接状态返回颜色
    public IBrush TcpStatusColor => IsTcpConnected ? Brushes.Green : Brushes.Red;

    private Task? _consumerTask;
    private readonly TcpDriverManager _tcpDriverManager;
    private readonly DataBus _bus = new();
    private readonly StreamTable _table;
    
    private CancellationTokenSource? _cts = new();
    
    private MovingAverageAlgorithm _algo;
    private OnlineChannelManager _onlineChannelManager;
    private LocalTestServer? _localServer;
    private System.Timers.Timer? _channelTimeUpdateTimer; // 通道计时器

    // ==================== SDK模式相关属性 ====================
    private readonly ConcurrentDictionary<int, byte> _pendingOnlineChannelIds = new();
    private Avalonia.Threading.DispatcherTimer? _onlineStatusFlushTimer;
    private SdkDriverManager? _sdkDriverManager;
    
    /// <summary>
    /// 数据源模式: 0=TCP, 1=SDK
    /// </summary>
    [ObservableProperty] private int _dataSourceMode = 0;
    
    /// <summary>
    /// SDK配置路径
    /// </summary>
    [ObservableProperty] private string _sdkConfigPath = DefaultSdkConfigPath;
    
    /// <summary>
    /// SDK连接状态
    /// </summary>
    [ObservableProperty] private string _sdkConnectionStatus = "SDK未初始化";
    
    /// <summary>
    /// SDK是否已初始化
    /// </summary>
    [ObservableProperty] private bool _isSdkInitialized;
    
    /// <summary>
    /// SDK是否正在采样
    /// </summary>
    [ObservableProperty] private bool _isSdkSampling;
    
    /// <summary>
    /// SDK数据是否活跃
    /// </summary>
    [ObservableProperty] private bool _isSdkDataActive;
    
    // SDK模式计算属性
    public IBrush SdkStatusColor => IsSdkInitialized ? (IsSdkSampling ? Brushes.Green : Brushes.Orange) : Brushes.Red;
    
    /// <summary>
    /// 设备统计摘要（显示在通道管理界面）
    /// </summary>
    public string DeviceSummary
    {
        get
        {
            if (DataSourceMode == 1 && IsSdkInitialized && _sdkDriverManager != null) // SDK模式
            {
                int deviceCount = _sdkDriverManager.OnlineDeviceCount;
                int channelCount = _sdkDriverManager.TotalChannelCount;
                return $"📊 在线设备: {deviceCount} 台 | 总通道数: {channelCount} 个 | 采样率: {SampleRate}Hz";
            }
            else if (DataSourceMode == 0 && IsTcpConnected) // TCP模式
            {
                int onlineDevices = Devices.Count(d => d.Online);
                int onlineChannels = Channels.Count(c => c.Online);
                return $"📊 在线设备: {onlineDevices} 台 | 在线通道: {onlineChannels} 个";
            }
            return "📊 未连接数据源";
        }
    }
    
    // SDK命令
    public IRelayCommand InitializeSdkCommand { get; private set; } = null!;
    public IRelayCommand StartSdkSamplingCommand { get; private set; } = null!;
    public IRelayCommand StopSdkSamplingCommand { get; private set; } = null!;
    public IRelayCommand BrowseSdkConfigCommand { get; private set; } = null!;
    // ==================== SDK模式相关属性结束 ====================


    
    public IRelayCommand ApplyAlgoCommand { get; }
    public IRelayCommand ConnectTcpCommand { get; }
    public IRelayCommand DisconnectTcpCommand { get; }
    public IRelayCommand SendTestPacketCommand { get; }
    public IRelayCommand StartLocalServerCommand { get; }
    public IRelayCommand StopLocalServerCommand { get; }
    
    // 批量通道管理命令
    public IRelayCommand SetAllOnlineCommand { get; }
    public IRelayCommand SetAllOfflineCommand { get; }
    public IRelayCommand SetCh1To32OnlineCommand { get; }
    public IRelayCommand SetCh33To64OnlineCommand { get; }
    public IRelayCommand SetSelectedDeviceCommand { get; }
    
    // 采样频率调节命令
    public IRelayCommand SampleRateChangedCommand { get; }
    public DataBus Bus => _bus;
    // 默认选中设备0（支持SDK的nGroupID从0开始）
    [ObservableProperty] private int _selectedDeviceId = 0;
    public DeviceInfo? SelectedDevice => Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
    public string SelectedDeviceTitle => $"通道在线状态 - AI{SelectedDeviceId}";
    public MainWindowViewModel()
    {
        _bus = new DataBus();
        _table = new StreamTable(_bus);
        
        _algo = new MovingAverageAlgorithm(_maWindow);
        // 将当前算法配置窗口应用到所有曲线视图的绘制平滑
        SkiaMultiChannelView.SetGlobalMovingAverage(true, _maWindow);
        _onlineChannelManager = new OnlineChannelManager();

        // 预创建通道，支持设备ID从0开始（SDK的nGroupID可能从0开始）
        // 设备0的通道ID: 1-64, 设备1的通道ID: 101-164, ...
        for (int d = 0; d < 64; d++)
        {
            for (int c = 1; c <= 64; c++)
            {
                int id = d * 100 + c;
                var ch = _table.EnsureChannel(id, DH.Contracts.ChannelNaming.ChannelName(id));
                Channels.Add(ch);
            }
        }

        BuildDevices();
        EnsureDeviceChannelStatuses($"AI{SelectedDeviceId:D2}");

        // 同步Channels初始在线状态到管理器的默认集合
        foreach (var channel in Channels)
        {
            channel.Online = _onlineChannelManager.IsChannelOnline(channel.ChannelId);
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));

        // 监听在线通道变化事件
        _onlineChannelManager.OnlineChannelsChanged += OnOnlineChannelsChanged;
        
        // 启动通道计时器，每秒更新一次在线时长
        _channelTimeUpdateTimer = new System.Timers.Timer(1000); // 1秒
        _channelTimeUpdateTimer.Elapsed += (s, e) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var ch in DeviceChannels)
                {
                    ch.UpdateOnlineTime();
                }
            });
        };
        _channelTimeUpdateTimer.Start();

        _onlineStatusFlushTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _onlineStatusFlushTimer.Tick += (_, _) => FlushPendingOnlineChannelUpdates();
        _onlineStatusFlushTimer.Start();

        // 创建TCP驱动管理器，传入数据总线和流表       
        _tcpDriverManager = new TcpDriverManager(_bus, _table, OnTcpStatusChanged);
        _tcpDriverManager.VerifiedChanged += v => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsDataVerified = v;
            if (!v)
            {
                foreach (var c in Channels) c.Online = false;
                foreach (var dev in Devices)
                {
                    dev.OnlineChannelCount = 0;
                    dev.Online = false;
                }
                UpdateDeviceChannels();
            }
            if (v)
            {
                var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
                if (dev != null) dev.Online = true;
                UpdateDeviceChannels();
            }
        });
        _tcpDriverManager.ActivityChanged += a => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsDataActive = a;
            if (!a)
            {
                foreach (var c in Channels) c.Online = false;
                foreach (var dev in Devices)
                {
                    dev.OnlineChannelCount = 0;
                    dev.Online = false;
                }
                UpdateDeviceChannels();
            }
        });

        // 同步在线通道与数据总线（连接成功后由数据到达驱动）
        _bus.ChannelAdded += (_, ch) => { };
        _bus.ChannelRemoved += (_, ch) => { };
        _bus.DataUpdated += (_, e) =>
        {
            if (IsRealtimePreviewActive())
            {
                QueueOnlineChannelUpdate(e.ChannelId);
            }
        };

        //命令初始化
        ApplyAlgoCommand = new RelayCommand(ApplyAlgo);
        ConnectTcpCommand = new RelayCommand(ConnectTcp, () => !IsTcpConnected);
        DisconnectTcpCommand = new RelayCommand(DisconnectTcp, () => IsTcpConnected);
        SendTestPacketCommand = new RelayCommand(SendTestPacket, () => _tcpDriverManager.IsConnected);
        StartLocalServerCommand = new RelayCommand(StartLocalServer);
        StopLocalServerCommand = new RelayCommand(StopLocalServer);

        SetAllOnlineCommand = new RelayCommand(SetAllOnline);
        SetAllOfflineCommand = new RelayCommand(SetAllOffline);
        SetCh1To32OnlineCommand = new RelayCommand(SetCh1To32Online);
        SetCh33To64OnlineCommand = new RelayCommand(SetCh33To64Online);

        // 采样频率调节命令初始化
        SampleRateChangedCommand = new RelayCommand<int>(OnSampleRateChangedCommand);
        SetSelectedDeviceCommand = new RelayCommand<int>(id =>
        {
            // 支持设备ID从0开始（SDK的nGroupID可能从0开始）
            SelectedDeviceId = Math.Clamp(id, 0, 63);
            EnsureDeviceChannelStatuses($"AI{SelectedDeviceId:D2}");
            UpdateDeviceChannels();
            OnPropertyChanged(nameof(SelectedDevice));
            OnPropertyChanged(nameof(SelectedDeviceTitle));
        });

        // 存储命令初始化
        StartStorageCommand = new AsyncRelayCommand(StartStorageAsync, () => !StorageEnabled && !_sdkTdmsCaptureStopInProgress);
        StopStorageCommand = new RelayCommand(StopStorage, () => StorageEnabled && !_sdkTdmsCaptureStopInProgress);
        BrowseStoragePathCommand = new AsyncRelayCommand(BrowseStoragePathAsync);
        // 新增命令初始化
        RefreshRecentFilesCommand = new RelayCommand(RefreshRecentFiles);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        TestReadSelectedFileCommand = new RelayCommand(TestReadSelectedFile, () => !string.IsNullOrEmpty(SelectedTdmsFile?.FullPath));
        VerifyStoredFileCommand = new AsyncRelayCommand(VerifyStoredFileAsync, () => !string.IsNullOrEmpty(SelectedTdmsFile?.FullPath));
        LoadStorageUiPreferences();

        // 运行时诊断：输出 TDMS 原生库可用性
        try
        {
            var tdmsAvail = DH.Client.App.Services.Storage.TdmsNative.IsAvailable;
            Console.WriteLine($"[TDMS] nilibddc.dll available: {tdmsAvail}");
            if (!tdmsAvail)
            {
                StorageStatusMessage = "高速采集段文件可用；TDMS库未检测到，仅影响离线TDMS导出/旧TDMS写入";
            }
            else
            {
                StorageStatusMessage = "高速采集段文件可用；TDMS库已检测到，可用于离线TDMS导出";
            }
        }
        catch { /* ignore */ }

        // ==================== SDK模式初始化 ====================
        InitializeSdkSupport();
    }

    /// <summary>
    /// 初始化SDK支持
    /// </summary>
    private void InitializeSdkSupport()
    {
        SdkConfigPath = DefaultSdkConfigPath;

        // 创建SDK驱动管理器
        _sdkDriverManager = new SdkDriverManager(_bus, _table, OnSdkStatusChanged);
        _sdkDriverManager.DataActivityChanged += active => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                IsSdkDataActive = active;
                OnPropertyChanged(nameof(SdkStatusColor));
                
                // SDK数据到达时更新通道状态
                if (active && DataSourceMode == 1 && Devices != null) // SDK模式
                {
                    UpdateDevicesFromSdk();
                    foreach (var dev in Devices.ToList()) // 使用ToList()避免并发修改
                    {
                        if (dev.Channels != null)
                        {
                            int cnt = dev.Channels.Count(c => c.Online);
                            dev.OnlineChannelCount = cnt;
                            dev.Online = cnt > 0;
                        }
                    }
                    OnPropertyChanged(nameof(OnlineChannelStatus));
                    UpdateDeviceChannels();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SDK] DataActivityChanged处理异常: {ex.Message}");
            }
        });

        // SDK模式下，DataBus数据更新事件
        _bus.DataUpdated += (_, e) =>
        {
            if (DataSourceMode == 1 && IsSdkSampling && IsSdkDataActive) // SDK模式
            {
                QueueOnlineChannelUpdate(e.ChannelId);
            }
        };

        // SDK命令初始化
        InitializeSdkCommand = new RelayCommand(InitializeSdk, () => !IsSdkInitialized);
        StartSdkSamplingCommand = new RelayCommand(StartSdkSampling, () => IsSdkInitialized && !IsSdkSampling);
        StopSdkSamplingCommand = new RelayCommand(StopSdkSampling, () => IsSdkSampling);
        BrowseSdkConfigCommand = new AsyncRelayCommand(BrowseSdkConfigAsync);
    }

    /// <summary>
    /// SDK状态变更回调
    /// </summary>
    private void OnSdkStatusChanged(bool isConnected, string status)
    {
        Console.WriteLine($"[SDK] 状态更新: {status}, 已连接: {isConnected}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SdkConnectionStatus = status;
            OnPropertyChanged(nameof(SdkStatusColor));
            
            // 更新命令可用性
            (InitializeSdkCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    /// <summary>
    /// 初始化SDK
    /// </summary>
    private void InitializeSdk()
    {
        if (_sdkDriverManager == null) return;
        
        string configPath = SdkConfigPath;
        if (string.IsNullOrEmpty(configPath))
        {
            SdkConnectionStatus = "请先设置配置路径";
            return;
        }
        
        if (!Directory.Exists(configPath))
        {
            SdkConnectionStatus = $"配置路径不存在: {configPath}";
            return;
        }
        
        bool result = _sdkDriverManager.Initialize(configPath);
        IsSdkInitialized = result;
        
        if (result)
        {
            // 根据SDK返回的设备信息更新UI
            UpdateDevicesFromSdk();
            
            // 更新采样率
            SampleRate = (int)_sdkDriverManager.SampleRate;
        }
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (InitializeSdkCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }
    
    /// <summary>
    /// 根据SDK返回的设备信息更新Devices集合
    /// </summary>
    private void UpdateDevicesFromSdk()
    {
        if (_sdkDriverManager == null) return;
        
        var sdkDevices = _sdkDriverManager.DeviceInfoList;
        int onlineDeviceCount = _sdkDriverManager.OnlineDeviceCount;
        int totalChannelCount = _sdkDriverManager.TotalChannelCount;
        
        Console.WriteLine($"[SDK] 更新设备信息: 在线设备={onlineDeviceCount}, 总通道数={totalChannelCount}");
        
        // 清空现有设备
        Devices.Clear();
        Channels.Clear();
        
        // 收集所有在线通道ID
        var onlineChannelIds = new List<int>();
        
        // 只添加在线且有通道的设备
        foreach (var sdkDev in sdkDevices.Where(d => d.IsOnline && d.ChannelCount > 0))
        {
            int deviceId = ResolveSdkChannelDeviceId(sdkDev);
            var dev = new DeviceInfo { DeviceId = deviceId };
            dev.Online = sdkDev.IsOnline;
            dev.OnlineChannelCount = sdkDev.ChannelCount;
            
            // 为该设备创建通道
            for (int ch = 1; ch <= sdkDev.ChannelCount; ch++)
            {
                // 使用 MachineId 构建通道ID（与SDK回调一致）
                int channelId = deviceId * 100 + ch;
                var channelInfo = new ChannelInfo
                {
                    ChannelId = channelId,
                    Name = DH.Contracts.ChannelNaming.ChannelName(channelId),
                    Online = sdkDev.IsOnline
                };
                Channels.Add(channelInfo);
                dev.Channels.Add(channelInfo);
                
                // 添加到在线通道列表
                if (sdkDev.IsOnline)
                {
                    onlineChannelIds.Add(channelId);
                }
            }
            
            Devices.Add(dev);
            Console.WriteLine($"[SDK] 添加设备: DeviceId={dev.DeviceId}, MachineId={sdkDev.MachineId}, ChannelDeviceId={sdkDev.ChannelDeviceId}, 通道数={sdkDev.ChannelCount}, 在线={sdkDev.IsOnline}");
        }
        
        // 同步在线通道到OnlineChannelManager（供结果显示页面使用）
        _onlineChannelManager.SetOnlineChannels(onlineChannelIds.ToArray());
        Console.WriteLine($"[SDK] 已同步 {onlineChannelIds.Count} 个在线通道到OnlineChannelManager");
        
        // 更新选中设备
        if (Devices.Count > 0 && Devices.All(d => d.DeviceId != SelectedDeviceId))
        {
            SelectedDeviceId = Devices[0].DeviceId;
        }
        
        OnPropertyChanged(nameof(OnlineChannelStatus));
        OnPropertyChanged(nameof(DeviceSummary));
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDeviceTitle));
        UpdateDeviceChannels();
    }

    private static int ResolveSdkChannelDeviceId(SdkDeviceInfo sdkDevice)
    {
        return SdkDeviceIdResolver.ResolveDeviceId(sdkDevice);
    }

    private void EnsureSdkChannelRegistration(int channelId)
    {
        if (channelId <= 0)
        {
            return;
        }

        int deviceId = channelId / 100;
        if (deviceId < 0)
        {
            return;
        }

        var channelInfo = Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (channelInfo == null)
        {
            channelInfo = new ChannelInfo
            {
                ChannelId = channelId,
                Name = DH.Contracts.ChannelNaming.ChannelName(channelId),
                Online = true
            };
            Channels.Add(channelInfo);
        }

        var device = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (device == null)
        {
            device = new DeviceInfo { DeviceId = deviceId, Online = true };
            Devices.Add(device);
        }

        if (!device.Channels.Any(c => c.ChannelId == channelId))
        {
            device.Channels.Add(channelInfo);
        }
    }

    private void AlignSdkSelectedDevice(int channelId)
    {
        if (DataSourceMode != 1)
        {
            return;
        }

        int deviceId = channelId / 100;
        if (deviceId < 0 || SelectedDeviceId == deviceId)
        {
            return;
        }

        bool currentDeviceHasData = _bus.GetAvailableChannels().Any(id => id / 100 == SelectedDeviceId);
        if (currentDeviceHasData)
        {
            return;
        }

        SelectedDeviceId = deviceId;
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDeviceTitle));
        UpdateDeviceChannels();
    }

    private int[] ResolveStorageChannelIds()
    {
        if (DataSourceMode == 1)
        {
            var actualSdkChannels = _bus.GetAvailableChannels()
                .Where(id => id > 0)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (actualSdkChannels.Length > 0)
            {
                return actualSdkChannels;
            }
        }

        return Channels
            .Select(c => c.ChannelId)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>
    /// 启动SDK采样
    /// </summary>
    private void StartSdkSampling()
    {
        if (_sdkDriverManager == null || !IsSdkInitialized) return;
        
        _bus.ResetPreviewTimeline();
        bool result = _sdkDriverManager.StartSampling();
        IsSdkSampling = result;
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 停止SDK采样
    /// </summary>
    private void StopSdkSampling()
    {
        if (_sdkDriverManager == null) return;
        
        _sdkDriverManager.StopSampling();
        IsSdkSampling = false;
        IsSdkDataActive = false;
        
        // 清除在线状态
        foreach (var c in Channels) c.Online = false;
        foreach (var dev in Devices)
        {
            dev.OnlineChannelCount = 0;
            dev.Online = false;
        }
        UpdateDeviceChannels();
        
        OnPropertyChanged(nameof(SdkStatusColor));
        (StartSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StopSdkSamplingCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 浏览SDK配置文件夹
    /// </summary>
    private async Task BrowseSdkConfigAsync()
    {
        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;
            
            if (topLevel == null) return;
            
            var folder = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择SDK配置文件夹（包含Config的目录）",
                AllowMultiple = false
            });
            
            if (folder.Count > 0)
            {
                SdkConfigPath = folder[0].Path.LocalPath;
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SDK] 浏览文件夹异常: {ex.Message}");
        }
    }

    private void BuildDevices()
    {
        Devices.Clear();
        int deviceCount = 64;  // 支持0-63共64台设备
        int channelsPerDevice = 64;
        // 从设备0开始，支持SDK的nGroupID从0开始的情况
        for (int d = 0; d < deviceCount; d++)
        {
            var dev = new DeviceInfo { DeviceId = d };
            for (int idx = 1; idx <= channelsPerDevice; idx++)
            {
                int id = d * 100 + idx;  // 设备0: 1-64, 设备1: 101-164
                var ch = Channels.FirstOrDefault(c => c.ChannelId == id);
                if (ch != null)
                {
                    dev.Channels.Add(ch);
                }
            }
            dev.OnlineChannelCount = dev.Channels.Count(c => c.Online);
            dev.Online = dev.OnlineChannelCount > 0;
            Devices.Add(dev);
        }
    }

    private static int MapPortToDevice(int port)
    {
        int basePort = 4008;
        int dev = port - basePort + 1;
        if (dev < 1) dev = 1;
        if (dev > 64) dev = 64;
        return dev;
    }

    private void OnTcpStatusChanged(bool isConnected, string status)
    {
        Console.WriteLine($"TCP状态更新: {status}, 连接: {isConnected}");
        // 确保在UI线程上更新属性
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsTcpConnected = isConnected;
            TcpConnectionStatus = status;

            if (!isConnected)
            {
                IsDataVerified = false;
                IsDataActive = false;
            }

            // 断开连接时清空在线通道，避免离线显示曲线
            if (!isConnected)
            {
                _onlineChannelManager.SetOnlineChannels(Array.Empty<int>());
            }

            // 通知命令的可执行状态变化
            (ConnectTcpCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (DisconnectTcpCommand as RelayCommand)?.NotifyCanExecuteChanged();

            Console.WriteLine($"TCP状态更新: {status}, 连接: {isConnected}");
        });
    }

    private void ConnectTcp()
    {
        if (int.TryParse(TcpServerPort, out var port))
        {
            _bus.ResetPreviewTimeline();
            SelectedDeviceId = MapPortToDevice(port);
            _tcpDriverManager.Connect(TcpServerIp, port);
            OnPropertyChanged(nameof(SelectedDevice));
            OnPropertyChanged(nameof(SelectedDeviceTitle));
        }
    }

    private void DisconnectTcp()
    {
        Console.WriteLine("[MainWindowViewModel] TCP disconnected 1111 ");
        _tcpDriverManager.Disconnect();
        Console.WriteLine("[MainWindowViewModel] TCP disconnected 2222");
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null) dev.Online = false;
    }

    private void SendTestPacket()
    {
        if (!_tcpDriverManager.IsConnected) return;
        int pktCount = 128;
        var ch1 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Sin(2 * Math.PI * i / pktCount)).ToArray();
        var ch2 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Cos(2 * Math.PI * i / pktCount)).ToArray();
        var channels = new[] { ch1, ch2 };
        var names = new[] { "AI1-01,mV", "AI1-02,mV" };
        _tcpDriverManager.SendTimeSeriesPacket((ulong)pktCount, channels, names, DateTime.UtcNow);
    }

    private void StartLocalServer()
    {
        if (_localServer != null) return;
        if (!int.TryParse(TcpServerPort, out var port)) return;
        _localServer = new LocalTestServer("127.0.0.1", port);
        _localServer.Start();
    }

    private void StopLocalServer()
    {
        _localServer?.Stop();
        _localServer = null;
    }

    

    private void ApplyAlgo()
    {
        _algo = new MovingAverageAlgorithm(MaWindow);
        // 同步到曲线视图的全局可视化移动平均设置
        SkiaMultiChannelView.SetGlobalMovingAverage(true, MaWindow);
        Console.WriteLine($"[MainWindowViewModel] Algorithm applied with window size: {MaWindow}");
    }

    private void OnOnlineChannelsChanged(object sender, OnlineChannelsChangedEventArgs e)
    {
        // 事件可能来自后台线程，调度到UI线程更新绑定
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var set = new HashSet<int>(e.OnlineChannels);
            foreach (var channel in Channels)
            {
                channel.Online = set.Contains(channel.ChannelId);
            }
            foreach (var dev in Devices)
            {
                int cnt = dev.Channels.Count(c => c.Online);
                dev.OnlineChannelCount = cnt;
                dev.Online = cnt > 0;
            }
            
            OnPropertyChanged(nameof(OnlineChannelStatus));
            UpdateDeviceChannels();
        });
    }

    public class DeviceInfo : ObservableObject
    {
        public int DeviceId { get; init; }
        public ObservableCollection<ChannelInfo> Channels { get; } = new();
        private bool _online;
        public bool Online { get => _online; set => SetProperty(ref _online, value); }
        private int _onlineChannelCount;
        public int OnlineChannelCount { get => _onlineChannelCount; set => SetProperty(ref _onlineChannelCount, value); }
    }

    public class ChannelStatus : ObservableObject
    {
        public string DeviceId { get; set; } = string.Empty;
        public int ChannelNumber { get; set; }
        private bool _isOnline;
        public bool IsOnline 
        { 
            get => _isOnline; 
            set 
            {
                if (SetProperty(ref _isOnline, value))
                {
                    if (value && !_onlineStartTime.HasValue)
                    {
                        // 变为在线状态，开始计时
                        _onlineStartTime = DateTimeOffset.UtcNow;
                        OnPropertyChanged(nameof(OnlineTimeText));
                    }
                    else if (!value)
                    {
                        // 变为离线状态，重置计时
                        _onlineStartTime = null;
                        OnPropertyChanged(nameof(OnlineTimeText));
                    }
                }
            }
        }
        private DateTimeOffset _lastActiveTime;
        public DateTimeOffset LastActiveTime { get => _lastActiveTime; set => SetProperty(ref _lastActiveTime, value); }
        
        // 在线开始时间
        private DateTimeOffset? _onlineStartTime;
        
        // 计算在线时长文本（格式：HH:MM:SS）
        public string OnlineTimeText
        {
            get
            {
                if (_isOnline && _onlineStartTime.HasValue)
                {
                    var duration = DateTimeOffset.UtcNow - _onlineStartTime.Value;
                    return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
                }
                return "00:00:00";
            }
        }
        
        // 更新在线时长显示（由外部定时器调用）
        public void UpdateOnlineTime()
        {
            if (_isOnline && _onlineStartTime.HasValue)
            {
                OnPropertyChanged(nameof(OnlineTimeText));
            }
        }
    }

    private void EnsureDeviceChannelStatuses(string deviceId)
    {
        if (DeviceChannels.Count != 64 || DeviceChannels.FirstOrDefault()?.DeviceId != deviceId)
        {
            DeviceChannels.Clear();
            for (int i = 1; i <= 64; i++)
            {
                DeviceChannels.Add(new ChannelStatus { DeviceId = deviceId, ChannelNumber = i, IsOnline = false, LastActiveTime = DateTimeOffset.MinValue });
            }
        }
    }

    private void UpdateDeviceChannels()
    {
        try
        {
            var devIdText = $"AI{SelectedDeviceId:D2}";
            
            // SDK模式下使用不同的逻辑
            if (DataSourceMode == 1 && IsSdkInitialized)
            {
                UpdateDeviceChannelsForSdk();
                return;
            }
            
            // TCP模式
            EnsureDeviceChannelStatuses(devIdText);
            var access = _tcpDriverManager.GetChannelAccessTimes();
            var now = DateTimeOffset.UtcNow;
            var deviceChannels = access
                .Select(kv =>
                {
                    var parsed = ChannelIdentifierExtensions.ParseCanonicalKey(kv.Key);
                    if (!parsed.HasValue || parsed.Value.DeviceId != devIdText) return ((int, DateTimeOffset)?)null;
                    return (parsed.Value.ChannelNumber, kv.Value);
                })
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToList();

            foreach (var ch in DeviceChannels)
            {
                var found = deviceChannels.FirstOrDefault(dc => dc.Item1 == ch.ChannelNumber);
                if (found != default)
                {
                    ch.LastActiveTime = found.Item2;
                    ch.IsOnline = (now - found.Item2).TotalSeconds < 5;
                }
                else
                {
                    ch.IsOnline = false;
                }
            }

            OnlineChannels.Clear();
            foreach (var ch in DeviceChannels.Where(c => c.IsOnline)) OnlineChannels.Add(ch);

            // 更新所有设备的在线统计
            var parsedAll = access
                .Select(kv => ChannelIdentifierExtensions.ParseCanonicalKey(kv.Key))
                .Where(p => p.HasValue)
                .Select(p => p!.Value)
                .ToList();
            var groups = parsedAll.GroupBy(p => p.DeviceId);
            foreach (var g in groups)
            {
                int devNum = int.TryParse(new string(g.Key.Where(char.IsDigit).ToArray()), out var n) ? n : 0;
                var devInfo = Devices.FirstOrDefault(d => d.DeviceId == devNum);
                if (devInfo != null)
                {
                    // 在线判断：5秒内活跃计数
                    int cnt = g.Count(x => (now - access[$"{GetEndpointText()}/{g.Key}/CH{x.ChannelNumber:D2}"]).TotalSeconds < 5);
                    devInfo.OnlineChannelCount = cnt;
                    devInfo.Online = cnt > 0;
                }
            }
        }
        catch { /* ignore */ }
    }
    
    /// <summary>
    /// SDK模式下更新设备通道状态
    /// </summary>
    private void UpdateDeviceChannelsForSdk()
    {
        try
        {
            // 安全检查：确保Devices集合非空
            if (Devices == null || Devices.Count == 0)
            {
                DeviceChannels.Clear();
                OnlineChannels.Clear();
                return;
            }
            
            // 找到当前选中的设备
            var selectedDevice = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
            if (selectedDevice == null)
            {
                // 如果找不到选中的设备，选择第一个
                selectedDevice = Devices.FirstOrDefault();
                if (selectedDevice != null)
                {
                    SelectedDeviceId = selectedDevice.DeviceId;
                }
            }
            
            if (selectedDevice == null)
            {
                DeviceChannels.Clear();
                OnlineChannels.Clear();
                return;
            }
            
            var devIdText = $"AI{SelectedDeviceId:D2}";
            
            // 快照设备通道列表，避免并发修改
            var deviceChannelSnapshot = selectedDevice.Channels?.ToList();
            
            // 获取该设备的通道数量
            int channelCount = deviceChannelSnapshot?.Count ?? 0;
            if (channelCount == 0) channelCount = 16; // 默认16通道
            
            // 确保DeviceChannels有正确数量的通道
            bool needRebuild = DeviceChannels.Count != channelCount;
            if (!needRebuild && DeviceChannels.Count > 0)
            {
                var firstCh = DeviceChannels.FirstOrDefault();
                needRebuild = firstCh?.DeviceId != devIdText;
            }
            
            if (needRebuild)
            {
                DeviceChannels.Clear();
                for (int i = 1; i <= channelCount; i++)
                {
                    // 检查该通道在Channels集合中的在线状态
                    var channelInfo = deviceChannelSnapshot?.FirstOrDefault(c => c.ChannelId % 100 == i);
                    bool isOnline = channelInfo?.Online ?? selectedDevice.Online;
                    
                    DeviceChannels.Add(new ChannelStatus 
                    { 
                        DeviceId = devIdText, 
                        ChannelNumber = i, 
                        IsOnline = isOnline, 
                        LastActiveTime = isOnline ? DateTimeOffset.UtcNow : DateTimeOffset.MinValue 
                    });
                }
            }
            else
            {
                // 更新现有通道的在线状态
                foreach (var ch in DeviceChannels.ToList()) // 使用ToList()避免枚举时修改
                {
                    var channelInfo = deviceChannelSnapshot?.FirstOrDefault(c => c.ChannelId % 100 == ch.ChannelNumber);
                    ch.IsOnline = channelInfo?.Online ?? selectedDevice.Online;
                    if (ch.IsOnline)
                    {
                        ch.LastActiveTime = DateTimeOffset.UtcNow;
                    }
                }
            }
            
            // 更新在线通道列表：先收集再批量更新，减少UI中间状态
            var onlineList = DeviceChannels.Where(c => c.IsOnline).ToList();
            OnlineChannels.Clear();
            foreach (var ch in onlineList)
            {
                OnlineChannels.Add(ch);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SDK] UpdateDeviceChannelsForSdk异常: {ex.Message}");
        }
    }

    private string GetEndpointText()
    {
        try
        {
            var ip = IPAddress.Parse(TcpServerIp);
            if (int.TryParse(TcpServerPort, out var port))
            {
                var ep = new IPEndPoint(ip, port);
                return ep.ToString();
            }
        }
        catch { }
        return "127.0.0.1:0";
    }

    // 批量通道管理方法
    private void SetAllOnline()
    {
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null)
        {
            foreach (var channel in dev.Channels)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetAllOffline()
    {
        var dev = Devices.FirstOrDefault(d => d.DeviceId == SelectedDeviceId);
        if (dev != null)
        {
            foreach (var channel in dev.Channels)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetCh1To32Online()
    {
        var devId = SelectedDeviceId;
        for (int i = 1; i <= 32; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        for (int i = 33; i <= 64; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }
    
    private void SetCh33To64Online()
    {
        var devId = SelectedDeviceId;
        for (int i = 1; i <= 32; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = false;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, false);
            }
        }
        for (int i = 33; i <= 64; i++)
        {
            var channel = Channels.FirstOrDefault(c => c.ChannelId == devId * 100 + i);
            if (channel != null)
            {
                channel.Online = true;
                _onlineChannelManager.SetChannelOnline(channel.ChannelId, true);
            }
        }
        OnPropertyChanged(nameof(OnlineChannelStatus));
    }

    // 公开在线通道管理器，供UI绑定
    public OnlineChannelManager OnlineChannelManager => _onlineChannelManager;
    
    // 采样频率变更事件
    public event EventHandler<int>? SampleRateChanged;

    // 存储控制方法
    // 解析存储路径：绝对路径直接返回；相对路径相对于仓库根（包含 DH.sln）
    private static string ResolveStoragePath(string path)
    {
        return AppDataPaths.ResolveStoragePath(path);
    }

    private void LoadStorageUiPreferences()
    {
        try
        {
            if (!File.Exists(StorageUiPreferencePath))
            {
                return;
            }

            string json = File.ReadAllText(StorageUiPreferencePath);
            var prefs = JsonSerializer.Deserialize<StorageUiPreferences>(json);
            if (prefs == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(prefs.StoragePath))
            {
                StoragePath = prefs.StoragePath;
            }

            if (!string.IsNullOrWhiteSpace(prefs.SessionName))
            {
                StorageSessionName = prefs.SessionName;
            }

            StorageSessionNamingModeIndex = Math.Clamp(prefs.SessionNamingModeIndex, 0, StorageSessionNamingOptions.Count - 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Storage] Load UI preferences failed: {ex.Message}");
        }
    }

    private void SaveStorageUiPreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorageUiPreferencePath) ?? ".");
            var prefs = new StorageUiPreferences
            {
                StoragePath = StoragePath,
                SessionName = StorageSessionName,
                SessionNamingModeIndex = StorageSessionNamingModeIndex
            };
            File.WriteAllText(
                StorageUiPreferencePath,
                JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Storage] Save UI preferences failed: {ex.Message}");
        }
    }

    partial void OnStoragePathChanged(string value) => SaveStorageUiPreferences();

    partial void OnStorageSessionNameChanged(string value) => SaveStorageUiPreferences();

    partial void OnStorageSessionNamingModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomSessionNamingSelected));
        OnPropertyChanged(nameof(IsTimeSessionNamingSelected));
        SaveStorageUiPreferences();
    }

    private string ResolveStorageSessionName()
        => StorageSessionNamingModeIndex == 1
            ? $"session_{DateTime.Now:yyyyMMdd_HHmmss_fff}"
            : StorageSessionName;

    private async Task BrowseStoragePathAsync()
    {
        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            if (topLevel == null)
            {
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择存储输出目录",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                StoragePath = folders[0].Path.LocalPath;
                SaveStorageUiPreferences();
                RefreshRecentFiles();
            }
        }
        catch (Exception ex)
        {
            StorageStatusMessage = $"选择输出目录失败: {ex.Message}";
        }
    }

    private sealed class StorageUiPreferences
    {
        public string StoragePath { get; set; } = DefaultStoragePath;

        public string SessionName { get; set; } = "session";

        public int SessionNamingModeIndex { get; set; } = 1;
    }

    private StorageCompressionSettings BuildStorageCompressionSettings(string basePath)
    {
        if (StorageCompressionSettings.TryLoad(basePath, out var settings, out string configPath, out string error))
        {
            StorageCompressionConfigStatus = $"config: {configPath}";
            return settings;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            StorageCompressionConfigStatus = $"config error: {error}; using UI";
        }
        else
        {
            StorageCompressionConfigStatus = $"ui settings; optional config: {configPath}";
        }

        var uiSettings = new StorageCompressionSettings
        {
            Enabled = GetSelectedCompressionType() != CompressionType.None,
            Algorithm = GetSelectedCompressionType(),
            Preprocess = GetSelectedPreprocessType(),
            Options = new CompressionOptions
            {
                LZ4Level = StorageCompressionLz4Level,
                LZ4HCLevel = StorageCompressionLz4HcLevel,
                ZstdLevel = StorageCompressionZstdLevel,
                ZstdWindowLog = StorageCompressionZstdWindowLog,
                ZlibLevel = StorageCompressionZlibLevel,
                BZip2BlockSize = StorageCompressionBZip2BlockSize
            }
        };
        uiSettings.Normalize();
        return uiSettings;
    }

    private CompressionType GetSelectedCompressionType()
        => StorageCompressionAlgorithmIndex switch
        {
            1 => CompressionType.Zstd,
            2 => CompressionType.LZ4,
            3 => CompressionType.Snappy,
            4 => CompressionType.Zlib,
            5 => CompressionType.LZ4_HC,
            6 => CompressionType.BZip2,
            _ => CompressionType.None,
        };

    partial void OnStorageCompressionAlgorithmIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsZstdCompressionSelected));
        OnPropertyChanged(nameof(IsLz4CompressionSelected));
        OnPropertyChanged(nameof(IsLz4HcCompressionSelected));
        OnPropertyChanged(nameof(IsZlibCompressionSelected));
        OnPropertyChanged(nameof(IsBZip2CompressionSelected));
    }

    private PreprocessType GetSelectedPreprocessType()
        => StorageCompressionPreprocessIndex switch
        {
            1 => PreprocessType.DiffOrder1,
            2 => PreprocessType.DiffOrder2,
            3 => PreprocessType.LinearPrediction,
            _ => PreprocessType.None,
        };

    private async Task StartStorageAsync()
    {
        if (StorageEnabled)
        {
            return;
        }

        if (!ShouldUseSdkTdmsCapture())
        {
            StorageStatusMessage = "当前仅支持 SDK TDMS source/segment 直存会话；旧单文件/每通道 TDMS 导出已移除。";
            return;
        }

        var basePath = ResolveStoragePath(StoragePath);
        Directory.CreateDirectory(basePath);
        var channelIds = ResolveStorageChannelIds();
        var sessionName = ResolveStorageSessionName();
        var compressionSettings = BuildStorageCompressionSettings(basePath);
        SaveStorageUiPreferences();

        await Task.Run(() =>
        {
            try
            {
                StartSdkTdmsCaptureSession(basePath, sessionName, channelIds, compressionSettings);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StorageEnabled = true;
                    _storageStartTime = DateTime.Now;
                    StorageElapsed = "00:00:00";
                    _storageTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _storageTimer.Tick += (_, _) =>
                    {
                        var elapsed = DateTime.Now - _storageStartTime;
                        StorageElapsed = elapsed.ToString(@"hh\:mm\:ss");
                        UpdateSdkTdmsCaptureStatusMessage();
                    };
                    _storageTimer.Start();
                    UpdateSdkTdmsCaptureStatusMessage();
                    (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Storage] Start failed: {ex.Message}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    CleanupSdkRawCaptureSubscription();
                    SetSdkRealtimePublishEnabled(true);
                    _sdkRawCaptureWriter?.Dispose();
                    _sdkRawCaptureWriter = null;
                    _sdkTdmsCaptureWriter?.Dispose();
                    _sdkTdmsCaptureWriter = null;
                    _sdkRawCaptureProtectionStopPending = false;
                    _activeStorageRuntime = null;
                    StorageEnabled = false;
                    StorageStatusMessage = $"写入启动失败: {ex.Message}";
                    (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                    (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
                });
            }
        });
    }

    private bool ShouldUseSdkTdmsCapture() => DataSourceMode == 1;

    private void SetSdkRealtimePublishEnabled(bool enabled)
    {
        try
        {
            _sdkDriverManager?.SetRealtimePublishEnabled(enabled);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkRawCapture] 切换实时预览失败: {ex.Message}");
        }
    }

    private void UpdateSdkRawCaptureStatusMessage()
    {
        if (_activeStorageRuntime != StorageRuntimeKind.SdkRawCapture || _sdkRawCaptureWriter == null)
        {
            return;
        }

        var stats = _sdkRawCaptureWriter.GetStatistics();
        string queueBytes = FormatStorageSize(stats.PendingPayloadBytes);
        string peakBytes = FormatStorageSize(stats.PeakPendingPayloadBytes);
        string limitBytes = FormatStorageSize(stats.PendingPayloadByteLimit);
        string modeSummary = $"SDK原始采集中，已写 {stats.WrittenBlockCount:N0} 块，待写 {stats.PendingBlockCount:N0}/{stats.PendingBlockLimit:N0} 块，队列 {queueBytes}/{limitBytes}，峰值 {stats.PeakPendingBlockCount:N0} 块/{peakBytes}";
        if (stats.HasTimingAnalysis && stats.ConfiguredSampleRateHz > 0d)
        {
            string effectiveRateText = FormatTimingRange(stats.MinEffectiveSampleRateHz, stats.MaxEffectiveSampleRateHz, "N0");
            double minRatioPercent = (stats.MinEffectiveSampleRateHz / stats.ConfiguredSampleRateHz) * 100d;
            double maxRatioPercent = (stats.MaxEffectiveSampleRateHz / stats.ConfiguredSampleRateHz) * 100d;
            string ratioText = FormatTimingRange(minRatioPercent, maxRatioPercent, "N1");
            modeSummary += $"，反推采样率 {effectiveRateText} Hz（{ratioText}%）";
        }

        if (stats.ProtectionTriggered || stats.WriteFaultCount > 0)
        {
            string reason = !string.IsNullOrWhiteSpace(stats.ProtectionReason) ? stats.ProtectionReason : stats.LastError;
            StorageStatusMessage = $"{modeSummary}，已触发保护停止：{reason}";
            RequestSdkRawCaptureProtectionStop(stats);
            return;
        }

        if (stats.HasTimingAnalysis && !stats.TimingConsistent)
        {
            StorageStatusMessage = $"{modeSummary}，采样率疑似异常";
            return;
        }

        if (stats.PendingBlockCount * 2 >= stats.PendingBlockLimit
            || stats.PendingPayloadBytes * 2 >= stats.PendingPayloadByteLimit)
        {
            StorageStatusMessage = $"{modeSummary}，已接近保护阈值";
            return;
        }

        StorageStatusMessage = modeSummary;
    }

    private void UpdateSdkTdmsCaptureStatusMessage()
    {
        if (_activeStorageRuntime != StorageRuntimeKind.SdkTdmsCapture || _sdkTdmsCaptureWriter == null)
        {
            return;
        }

        var stats = _sdkTdmsCaptureWriter.GetStatistics();
        string queueBytes = FormatStorageSize(stats.PendingPayloadBytes);
        string peakBytes = FormatStorageSize(stats.PeakPendingPayloadBytes);
        string limitBytes = FormatStorageSize(stats.PendingPayloadByteLimit);
        string modeSummary = $"SDK高速段写入中，已写 {stats.WrittenBlockCount:N0} 块，待写 {stats.PendingBlockCount:N0}/{stats.PendingBlockLimit:N0} 块，队列 {queueBytes}/{limitBytes}，峰值 {stats.PeakPendingBlockCount:N0} 块/{peakBytes}，压缩 {StorageCompressionConfigStatus}";

        if (stats.ProtectionTriggered || stats.WriteFaultCount > 0)
        {
            string reason = !string.IsNullOrWhiteSpace(stats.ProtectionReason) ? stats.ProtectionReason : stats.LastError;
            StorageStatusMessage = $"{modeSummary}，已触发保护停止：{reason}";
            RequestSdkRawCaptureProtectionStop(stats);
            return;
        }

        if (stats.PendingBlockCount * 2 >= stats.PendingBlockLimit
            || stats.PendingPayloadBytes * 2 >= stats.PendingPayloadByteLimit)
        {
            StorageStatusMessage = $"{modeSummary}，已接近保护阈值";
            return;
        }

        StorageStatusMessage = modeSummary;
    }

    private void RequestSdkRawCaptureProtectionStop(SdkRawCaptureWriterStatistics stats)
    {
        if (_sdkRawCaptureProtectionStopPending
            || (_activeStorageRuntime != StorageRuntimeKind.SdkRawCapture
                && _activeStorageRuntime != StorageRuntimeKind.SdkTdmsCapture))
        {
            return;
        }

        _sdkRawCaptureProtectionStopPending = true;
        string reason = !string.IsNullOrWhiteSpace(stats.ProtectionReason) ? stats.ProtectionReason : stats.LastError;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_activeStorageRuntime != StorageRuntimeKind.SdkRawCapture
                && _activeStorageRuntime != StorageRuntimeKind.SdkTdmsCapture)
            {
                _sdkRawCaptureProtectionStopPending = false;
                return;
            }

            StorageStatusMessage = $"原始采集已触发保护停止：{reason}";
            FileVerifyPassed = false;
            FileVerifyResult = StorageStatusMessage;

            if (StorageEnabled)
            {
                StopStorage();
            }
            else
            {
                _sdkRawCaptureProtectionStopPending = false;
            }
        });
    }

    private void StartSdkRawCaptureSession(string basePath, string sessionName, IReadOnlyCollection<int> channelIds)
    {
        if (_sdkDriverManager == null)
        {
            throw new InvalidOperationException("SDK driver is not initialized.");
        }

        CleanupSdkRawCaptureSubscription();
        _sdkRawCaptureWriter?.Dispose();
        _sdkRawCaptureWriter = new SdkRawCaptureWriter();
        _sdkRawCaptureWriter.Start(basePath, sessionName, SampleRate, channelIds);
        _sdkRawCaptureProtectionStopPending = false;
        SetSdkRealtimePublishEnabled(true);

        _sdkRawBlockHandler = rawBlock =>
        {
            try
            {
                if (!(_sdkRawCaptureWriter?.TryEnqueue(rawBlock) ?? false) && _sdkRawCaptureWriter != null)
                {
                    var stats = _sdkRawCaptureWriter.GetStatistics();
                    if (stats.ProtectionTriggered || stats.WriteFaultCount > 0)
                    {
                        RequestSdkRawCaptureProtectionStop(stats);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SdkRawCapture] 入队失败: {ex.Message}");
            }
        };

        _sdkDriverManager.RawBlockReceived += _sdkRawBlockHandler;
        _activeStorageRuntime = StorageRuntimeKind.SdkRawCapture;
    }

    private void StartSdkTdmsCaptureSession(
        string basePath,
        string sessionName,
        IReadOnlyCollection<int> channelIds,
        StorageCompressionSettings compressionSettings)
    {
        if (_sdkDriverManager == null)
        {
            throw new InvalidOperationException("SDK driver is not initialized.");
        }

        CleanupSdkRawCaptureSubscription();
        // Disable realtime publishing before creating the high-rate writer so the
        // SDK callback is dedicated to raw block handoff for the whole capture.
        SetSdkRealtimePublishEnabled(false);
        _sdkTdmsCaptureWriter?.Dispose();
        _sdkTdmsCaptureWriter = new SdkTdmsCaptureWriter();
        _sdkTdmsCaptureWriter.Start(
            basePath,
            sessionName,
            SampleRate,
            channelIds,
            compressionSettings);
        _sdkRawCaptureProtectionStopPending = false;

        _sdkRawBlockHandler = rawBlock =>
        {
            try
            {
                if (!(_sdkTdmsCaptureWriter?.TryEnqueue(rawBlock) ?? false) && _sdkTdmsCaptureWriter != null)
                {
                    var stats = _sdkTdmsCaptureWriter.GetStatistics();
                    if (stats.ProtectionTriggered || stats.WriteFaultCount > 0)
                    {
                        RequestSdkRawCaptureProtectionStop(stats);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SdkTdmsCapture] 入队失败: {ex.Message}");
            }
        };

        _sdkDriverManager.RawBlockReceived += _sdkRawBlockHandler;
        _activeStorageRuntime = StorageRuntimeKind.SdkTdmsCapture;
    }

    private void CleanupSdkRawCaptureSubscription()
    {
        if (_sdkDriverManager != null && _sdkRawBlockHandler != null)
        {
            _sdkDriverManager.RawBlockReceived -= _sdkRawBlockHandler;
        }

        _sdkRawBlockHandler = null;
    }

    private void StopSdkRawCaptureStorage(TimeSpan finalElapsed)
    {

        StorageEnabled = false;
        CleanupSdkRawCaptureSubscription();

        try
        {
            var result = _sdkRawCaptureWriter?.Complete();
            _lastWrittenFiles = result?.WrittenFiles;

            if (_lastWrittenFiles != null && result?.SampleCounts != null)
            {
                foreach (var fp in _lastWrittenFiles)
                {
                    _writeSampleCountsByFile[fp] = result.SampleCounts;
                }
            }

            _sdkRawCaptureWriter?.Dispose();
            _sdkRawCaptureWriter = null;
            _activeStorageRuntime = null;

            bool captureHealthy = result?.Manifest is { } manifest && IsRawCaptureHealthy(manifest);

            StorageStatusMessage = captureHealthy
                ? "写入已停止，SDK原始采集文件已封存"
                : $"写入已停止，但检测到积压/写入异常：保护 {result?.Manifest.ProtectionTriggered ?? false}，拒绝 {result?.Manifest.RejectedBlockCount ?? 0:N0}，故障 {result?.Manifest.WriteFaultCount ?? 0:N0}";

            if (_lastWrittenFiles != null && _lastWrittenFiles.Count > 0 && result?.Manifest != null)
            {
                FileVerifyPassed = captureHealthy;
                FileVerifyResult = BuildRawCaptureSummary(_lastWrittenFiles[0], result.Manifest);
            }
            else
            {
                FileVerifyPassed = captureHealthy;
                FileVerifyResult = captureHealthy
                    ? "原始采集已完成，可使用“验证”按钮检查清单和文件大小。"
                    : "原始采集已结束，但存在异常，请使用“验证”按钮检查清单。";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkRawCapture] 停止写入时出错: {ex.Message}");
            StorageStatusMessage = $"停止写入时出错: {ex.Message}";
        }
        finally
        {
            SetSdkRealtimePublishEnabled(true);
            _sdkRawCaptureProtectionStopPending = false;
            _sdkRawCaptureWriter = null;
            _activeStorageRuntime = null;
        }

        RefreshRecentFiles();
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();

        _ = AutoVerifyAfterStopAsync();
    }

    private void StopSdkTdmsCaptureStorage(TimeSpan finalElapsed)
    {
        if (_sdkTdmsCaptureStopInProgress)
        {
            return;
        }

        _sdkTdmsCaptureStopInProgress = true;
        CleanupSdkRawCaptureSubscription();
        var writer = _sdkTdmsCaptureWriter;
        _sdkTdmsCaptureWriter = null;
        _activeStorageRuntime = null;
        StorageStatusMessage = "正在停止高速段写入，后台封存文件和生成清单...";
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();

        _ = Task.Run(() =>
        {
            try
            {
                return writer?.Complete();
            }
            finally
            {
                writer?.Dispose();
            }
        }).ContinueWith(task =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                FinishSdkTdmsCaptureStorageStop(task, finalElapsed));
        }, TaskScheduler.Default);
    }

    private void FinishSdkTdmsCaptureStorageStop(Task<SdkRawCaptureResult?> stopTask, TimeSpan finalElapsed)
    {

        try
        {
            var result = stopTask.GetAwaiter().GetResult();
            _lastWrittenFiles = result?.WrittenFiles;

            if (_lastWrittenFiles != null && result?.SampleCounts != null)
            {
                foreach (var fp in _lastWrittenFiles)
                {
                    _writeSampleCountsByFile[fp] = result.SampleCounts;
                }
            }

            bool captureHealthy = result?.Manifest is { } manifest && manifest.DataIntegrityPassed;
            StorageStatusMessage = captureHealthy
                ? "写入已停止，SDK数据已保存为高速段文件，并已生成清单"
                : $"写入已停止，但检测到高速段写入异常：保护 {result?.Manifest.ProtectionTriggered ?? false}，拒绝 {result?.Manifest.RejectedBlockCount ?? 0:N0}，故障 {result?.Manifest.WriteFaultCount ?? 0:N0}";

            FileVerifyPassed = captureHealthy;
            FileVerifyResult = captureHealthy
                ? "高速段采集已完成，可使用“验证”按钮检查文件。"
                : StorageStatusMessage;
            if (result?.Manifest is { } completedManifest)
            {
                var tdmsVerification = VerifyTdmsCaptureManifest(completedManifest);
                FileVerifyPassed = tdmsVerification.passed;
                FileVerifyResult = tdmsVerification.summary;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkTdmsCapture] 停止写入时出错: {ex.Message}");
            StorageStatusMessage = $"停止写入时出错: {ex.Message}";
        }
        finally
        {
            SetSdkRealtimePublishEnabled(true);
            _sdkRawCaptureProtectionStopPending = false;
            _sdkTdmsCaptureWriter = null;
            _activeStorageRuntime = null;
            _sdkTdmsCaptureStopInProgress = false;
            StorageEnabled = false;
        }

        RefreshRecentFiles();
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void StopStorage()
    {
        if (!StorageEnabled)
        {
            return;
        }

        _storageTimer?.Stop();
        _storageTimer = null;
        var finalElapsed = DateTime.Now - _storageStartTime;
        StorageElapsed = finalElapsed.ToString(@"hh\:mm\:ss");

        if (_activeStorageRuntime == StorageRuntimeKind.SdkRawCapture)
        {
            StopSdkRawCaptureStorage(finalElapsed);
            return;
        }

        if (_activeStorageRuntime == StorageRuntimeKind.SdkTdmsCapture)
        {
            StopSdkTdmsCaptureStorage(finalElapsed);
            return;
        }

        StorageEnabled = false;
        _activeStorageRuntime = null;
        StorageStatusMessage = "没有正在运行的 TDMS source/segment 写入会话。";
        (StartStorageCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopStorageCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task AutoVerifyAfterStopAsync()
    {
        if (_lastWrittenFiles == null || _lastWrittenFiles.Count == 0)
        {
            FileVerifyResult = "";
            return;
        }

        try
        {
            var allResults = new List<string>();
            bool allPassed = true;

            foreach (var file in _lastWrittenFiles)
            {
                if (!File.Exists(file)) continue;

                if (SdkRawCaptureFormat.IsRawCaptureFile(file))
                {
                    var (passed, summary) = VerifyRawCaptureFile(file);
                    allResults.Add(summary);
                    if (!passed) allPassed = false;
                    continue;
                }

                if (IsFastSegmentFile(file))
                {
                    var (passed, summary) = VerifyFastSegmentFile(file);
                    allResults.Add(summary);
                    if (!passed) allPassed = false;
                    continue;
                }

                if (IsTdmsSourceSegmentFile(file))
                {
                    var (passed, summary) = VerifyTdmsSourceSegmentFile(file);
                    allResults.Add(summary);
                    if (!passed) allPassed = false;
                    continue;
                }

                _writeHashesByFile.TryGetValue(file, out var hashes);
                _writeSampleCountsByFile.TryGetValue(file, out var counts);

                // 内存中没有哈希时，尝试从 .sha256 清单文件加载
                if (hashes == null || hashes.Count == 0)
                {
                    var (loadedHashes, loadedCounts) = StorageVerifier.LoadManifest(file);
                    hashes ??= loadedHashes;
                    counts ??= loadedCounts;
                }

                var result = await Task.Run(() => StorageVerifier.Verify(file, hashes, counts));
                allResults.Add(result.Summary);
                if (!result.AllLossless) allPassed = false;
            }

            FileVerifyPassed = allPassed;
            FileVerifyResult = allResults.Count > 0
                ? string.Join("\n───────────────────\n", allResults)
                : "未找到可验证的文件";
            StorageStatusMessage = allPassed ? "写入已停止 ✅ 自动验证通过" : "写入已停止 ❌ 自动验证发现差异";
        }
        catch (Exception ex)
        {
            FileVerifyPassed = false;
            FileVerifyResult = $"自动验证异常: {ex.Message}";
        }
    }

    private static bool IsTdmsLikeFile(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".tdms", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".tdm", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFastSegmentFile(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".dhseg", StringComparison.OrdinalIgnoreCase);

    private static bool IsTdmsSourceSegmentFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        return string.Equals(Path.GetExtension(filePath), ".tdms", StringComparison.OrdinalIgnoreCase)
            && fileName.StartsWith("source_", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatStorageSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    private static bool IsRawCaptureRuntimeHealthy(SdkRawCaptureManifest manifest)
        => !manifest.ProtectionTriggered
            && manifest.RejectedBlockCount == 0
            && manifest.WriteFaultCount == 0
            && string.IsNullOrWhiteSpace(manifest.LastError);

    private static bool IsConstantBlockIndexArtifact(SdkRawCaptureDeviceIntegrity device)
        => !device.BlockIndexContinuityEnabled
            || (device.BlockCount > 1
                && device.FirstBlockIndex == device.LastBlockIndex
                && device.NonMonotonicBlockCount >= device.BlockCount - 1
                && device.MissingBlockCount == 0
                && device.TotalDataGapSampleCount == 0
                && device.TotalDataRegressionCount == 0);

    private static bool HasMeaningfulDeviceIntegrityIssue(SdkRawCaptureDeviceIntegrity device)
    {
        bool hasBlockIndexIssue = !IsConstantBlockIndexArtifact(device)
            && (device.MissingBlockCount > 0 || device.NonMonotonicBlockCount > 0);

        return hasBlockIndexIssue
            || device.TotalDataGapSampleCount > 0
            || device.TotalDataRegressionCount > 0
            || device.ChannelLayoutChanged
            || device.BlockSizeChanged;
    }

    private static IEnumerable<SdkRawCaptureDeviceIntegrity> GetMeaningfulDeviceIntegrityIssues(SdkRawCaptureManifest manifest)
        => manifest.DeviceIntegrity.Where(HasMeaningfulDeviceIntegrityIssue);

    private static bool TryGetBoundaryTailSkewInfo(
        SdkRawCaptureManifest manifest,
        out long minSamplesPerChannel,
        out long maxSamplesPerChannel,
        out int commonSamplesPerBlockPerChannel,
        out int affectedDeviceCount)
    {
        minSamplesPerChannel = 0;
        maxSamplesPerChannel = 0;
        commonSamplesPerBlockPerChannel = 0;
        affectedDeviceCount = 0;

        if (manifest.DeviceIntegrity.Count <= 1
            || manifest.DeviceSampleCountsBalanced
            || GetMeaningfulDeviceIntegrityIssues(manifest).Any())
        {
            return false;
        }

        var devices = manifest.DeviceIntegrity
            .Where(d => d.SamplesPerChannel > 0 && d.SamplesPerBlockPerChannel > 0)
            .ToList();
        if (devices.Count != manifest.DeviceIntegrity.Count)
        {
            return false;
        }

        long localMinSamplesPerChannel = devices.Min(d => d.SamplesPerChannel);
        long localMaxSamplesPerChannel = devices.Max(d => d.SamplesPerChannel);
        long spread = localMaxSamplesPerChannel - localMinSamplesPerChannel;
        if (spread <= 0)
        {
            return false;
        }

        var blockSizes = devices
            .Select(d => d.SamplesPerBlockPerChannel)
            .Distinct()
            .ToList();
        if (blockSizes.Count != 1)
        {
            return false;
        }

        int localCommonSamplesPerBlockPerChannel = blockSizes[0];
        if (localCommonSamplesPerBlockPerChannel <= 0
            || spread > localCommonSamplesPerBlockPerChannel
            || (spread % localCommonSamplesPerBlockPerChannel) != 0)
        {
            return false;
        }

        if (devices.Any(device =>
            {
                long tailSpread = device.SamplesPerChannel - localMinSamplesPerChannel;
                return tailSpread < 0
                    || tailSpread > localCommonSamplesPerBlockPerChannel
                    || (tailSpread % localCommonSamplesPerBlockPerChannel) != 0;
            }))
        {
            return false;
        }

        int localAffectedDeviceCount = devices.Count(d => d.SamplesPerChannel != localMinSamplesPerChannel);
        minSamplesPerChannel = localMinSamplesPerChannel;
        maxSamplesPerChannel = localMaxSamplesPerChannel;
        commonSamplesPerBlockPerChannel = localCommonSamplesPerBlockPerChannel;
        affectedDeviceCount = localAffectedDeviceCount;
        return affectedDeviceCount > 0;
    }

    private static bool HasBoundaryTailSkewOnly(SdkRawCaptureManifest manifest)
        => TryGetBoundaryTailSkewInfo(
            manifest,
            out _,
            out _,
            out _,
            out _);

    private static string GetDeviceSampleConsistencyText(SdkRawCaptureManifest manifest)
    {
        if (manifest.DeviceSampleCountsBalanced)
        {
            return "鏄?";
        }

        return TryGetBoundaryTailSkewInfo(
            manifest,
            out _,
            out _,
            out _,
            out int affectedDeviceCount)
            ? $"杈圭晫灏惧樊锛?{affectedDeviceCount} 鍙拌澶囧 1 涓熬鍧楋紝鍙鍑哄榻愶級"
            : "鍚?";
    }

    private static string BuildEffectiveIntegritySummary(SdkRawCaptureManifest manifest)
    {
        var issues = new List<string>();
        var meaningfulDevices = GetMeaningfulDeviceIntegrityIssues(manifest).ToList();
        long missingBlocks = meaningfulDevices.Sum(d => d.MissingBlockCount);
        long nonMonotonicBlocks = meaningfulDevices.Sum(d => d.NonMonotonicBlockCount);
        long totalDataGaps = meaningfulDevices.Sum(d => d.TotalDataGapSampleCount);
        long totalDataRegressions = meaningfulDevices.Sum(d => d.TotalDataRegressionCount);

        if (missingBlocks > 0)
        {
            issues.Add($"block index 缺块 {missingBlocks:N0}");
        }

        if (nonMonotonicBlocks > 0)
        {
            issues.Add($"block index 乱序 {nonMonotonicBlocks:N0}");
        }

        if (totalDataGaps > 0)
        {
            issues.Add($"TotalData 缺口 {totalDataGaps:N0}");
        }

        if (totalDataRegressions > 0)
        {
            issues.Add($"TotalData 回退 {totalDataRegressions:N0}");
        }

        if (!manifest.DeviceSampleCountsBalanced)
        {
            var minDevice = manifest.DeviceIntegrity
                .OrderBy(d => d.SamplesPerChannel)
                .ThenBy(d => d.DeviceId)
                .FirstOrDefault();
            var maxDevice = manifest.DeviceIntegrity
                .OrderByDescending(d => d.SamplesPerChannel)
                .ThenBy(d => d.DeviceId)
                .FirstOrDefault();

            if (minDevice != null && maxDevice != null)
            {
                if (TryGetBoundaryTailSkewInfo(
                    manifest,
                    out long minSamplesPerChannel,
                    out long maxSamplesPerChannel,
                    out int samplesPerBlockPerChannel,
                    out int affectedDeviceCount))
                {
                    issues.Add($"鍋滃綍杈圭晫灏惧潡宸紓 AI{minDevice.DeviceId:00}={minSamplesPerChannel:N0} 鍒?AI{maxDevice.DeviceId:00}={maxSamplesPerChannel:N0}锛?{affectedDeviceCount} 鍙拌澶囧 1 涓?{samplesPerBlockPerChannel:N0} 鐐瑰熬鍧楋紝TDMS 瀵煎嚭浼氭寜鏈€鐭澶囧榻愶級");
                }
                else
                {
                    issues.Add($"device samples/channel range AI{minDevice.DeviceId:00}={minDevice.SamplesPerChannel:N0} to AI{maxDevice.DeviceId:00}={maxDevice.SamplesPerChannel:N0}");
                }
            }
        }

        return issues.Count == 0
            ? "未发现有效的设备连续性异常"
            : string.Join("; ", issues);
    }

    private static (bool HasAnalysis, bool IsConsistent, double WallClockDurationSeconds, double MinSampleDerivedDurationSeconds, double MaxSampleDerivedDurationSeconds, double MinEffectiveSampleRateHz, double MaxEffectiveSampleRateHz, string Summary) GetRawCaptureTimingAnalysis(SdkRawCaptureManifest manifest)
    {
        if (manifest.MaxSampleDerivedDurationSeconds > 0d && manifest.MaxEffectiveSampleRateHz > 0d)
        {
            double wallClockDurationSeconds = manifest.WallClockDurationSeconds > 0d
                ? manifest.WallClockDurationSeconds
                : Math.Max(0d, (manifest.StoppedAtUtc - manifest.StartedAtUtc).TotalSeconds);
            string summary = !string.IsNullOrWhiteSpace(manifest.SampleRateConsistencySummary)
                ? manifest.SampleRateConsistencySummary
                : BuildRawCaptureTimingSummaryText(
                    manifest.SampleRateHz,
                    wallClockDurationSeconds,
                    manifest.MinSampleDerivedDurationSeconds,
                    manifest.MaxSampleDerivedDurationSeconds,
                    manifest.MinEffectiveSampleRateHz,
                    manifest.MaxEffectiveSampleRateHz);
            return (
                true,
                manifest.SampleRateConsistencyPassed,
                wallClockDurationSeconds,
                manifest.MinSampleDerivedDurationSeconds,
                manifest.MaxSampleDerivedDurationSeconds,
                manifest.MinEffectiveSampleRateHz,
                manifest.MaxEffectiveSampleRateHz,
                summary);
        }

        double derivedWallClockDurationSeconds = Math.Max(0d, (manifest.StoppedAtUtc - manifest.StartedAtUtc).TotalSeconds);
        long minSamplesPerChannel = 0;
        long maxSamplesPerChannel = 0;

        var deviceSamples = manifest.DeviceIntegrity
            .Select(device => device.SamplesPerChannel)
            .Where(value => value > 0)
            .ToList();
        if (deviceSamples.Count > 0)
        {
            minSamplesPerChannel = deviceSamples.Min();
            maxSamplesPerChannel = deviceSamples.Max();
        }
        else
        {
            var channelSamples = manifest.ChannelSampleCounts.Values
                .Where(value => value > 0)
                .ToList();
            if (channelSamples.Count > 0)
            {
                minSamplesPerChannel = channelSamples.Min();
                maxSamplesPerChannel = channelSamples.Max();
            }
        }

        if (manifest.SampleRateHz <= 0d || derivedWallClockDurationSeconds <= 0d || maxSamplesPerChannel <= 0)
        {
            return (false, true, derivedWallClockDurationSeconds, 0d, 0d, 0d, 0d, "缺少足够的时基数据");
        }

        double minSampleDerivedDurationSeconds = minSamplesPerChannel / manifest.SampleRateHz;
        double maxSampleDerivedDurationSeconds = maxSamplesPerChannel / manifest.SampleRateHz;
        double minEffectiveSampleRateHz = minSamplesPerChannel / derivedWallClockDurationSeconds;
        double maxEffectiveSampleRateHz = maxSamplesPerChannel / derivedWallClockDurationSeconds;
        double minRateRatio = minEffectiveSampleRateHz / manifest.SampleRateHz;
        double maxRateRatio = maxEffectiveSampleRateHz / manifest.SampleRateHz;

        const double toleranceRatio = 0.15d;
        bool isConsistent =
            derivedWallClockDurationSeconds < 1.0d
            || (minRateRatio >= 1.0d - toleranceRatio && maxRateRatio <= 1.0d + toleranceRatio);

        return (
            true,
            isConsistent,
            derivedWallClockDurationSeconds,
            minSampleDerivedDurationSeconds,
            maxSampleDerivedDurationSeconds,
            minEffectiveSampleRateHz,
            maxEffectiveSampleRateHz,
            BuildRawCaptureTimingSummaryText(
                manifest.SampleRateHz,
                derivedWallClockDurationSeconds,
                minSampleDerivedDurationSeconds,
                maxSampleDerivedDurationSeconds,
                minEffectiveSampleRateHz,
                maxEffectiveSampleRateHz));
    }

    private static bool HasRawCaptureTimingAnalysis(SdkRawCaptureManifest manifest)
        => GetRawCaptureTimingAnalysis(manifest).HasAnalysis;

    private static bool IsRawCaptureTimingHealthy(SdkRawCaptureManifest manifest)
    {
        var analysis = GetRawCaptureTimingAnalysis(manifest);
        return !analysis.HasAnalysis || analysis.IsConsistent;
    }

    private static void AppendRawCaptureTimingSummary(StringBuilder sb, SdkRawCaptureManifest manifest)
    {
        var analysis = GetRawCaptureTimingAnalysis(manifest);
        if (!analysis.HasAnalysis)
        {
            return;
        }

        sb.AppendLine($"墙钟时长: {analysis.WallClockDurationSeconds:N2} s");
        sb.AppendLine($"样本换算时长: {FormatTimingRange(analysis.MinSampleDerivedDurationSeconds, analysis.MaxSampleDerivedDurationSeconds, "N2")} s");
        sb.AppendLine($"反推有效采样率: {FormatTimingRange(analysis.MinEffectiveSampleRateHz, analysis.MaxEffectiveSampleRateHz, "N0")} Hz");
        sb.AppendLine($"采样率校验: {(analysis.IsConsistent ? "正常" : "异常")}");
        if (!string.IsNullOrWhiteSpace(analysis.Summary))
        {
            sb.AppendLine($"采样率摘要: {analysis.Summary}");
        }
    }

    private static string BuildRawCaptureTimingSummaryText(
        double sampleRateHz,
        double wallClockDurationSeconds,
        double minSampleDerivedDurationSeconds,
        double maxSampleDerivedDurationSeconds,
        double minEffectiveSampleRateHz,
        double maxEffectiveSampleRateHz)
    {
        string effectiveRateText = FormatTimingRange(minEffectiveSampleRateHz, maxEffectiveSampleRateHz, "N0");
        string durationText = FormatTimingRange(minSampleDerivedDurationSeconds, maxSampleDerivedDurationSeconds, "N2");
        double minRatioPercent = sampleRateHz > 0d ? (minEffectiveSampleRateHz / sampleRateHz) * 100d : 0d;
        double maxRatioPercent = sampleRateHz > 0d ? (maxEffectiveSampleRateHz / sampleRateHz) * 100d : 0d;
        string ratioText = FormatTimingRange(minRatioPercent, maxRatioPercent, "N1");
        return $"文件头采样率={sampleRateHz:N0} Hz，反推采样率={effectiveRateText} Hz，墙钟时长={wallClockDurationSeconds:N2}s，样本换算时长={durationText}s，比例={ratioText}%";
    }

    private static string FormatTimingRange(double minValue, double maxValue, string format)
        => Math.Abs(minValue - maxValue) < 0.000001d
            ? minValue.ToString(format)
            : $"{minValue.ToString(format)} ~ {maxValue.ToString(format)}";

    private static bool IsRawCaptureIntegrityHealthy(SdkRawCaptureManifest manifest)
        => !GetMeaningfulDeviceIntegrityIssues(manifest).Any()
            && (manifest.DeviceSampleCountsBalanced || HasBoundaryTailSkewOnly(manifest));

    private static bool IsRawCaptureHealthy(SdkRawCaptureManifest manifest)
        => IsRawCaptureRuntimeHealthy(manifest)
            && IsRawCaptureIntegrityHealthy(manifest)
            && IsRawCaptureTimingHealthy(manifest);

    private static string BuildRawCaptureSummary(string filePath, SdkRawCaptureManifest manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"文件: {Path.GetFileName(filePath)}");
        sb.AppendLine($"会话: {manifest.SessionName}");
        sb.AppendLine($"采样率: {manifest.SampleRateHz:N0} Hz");
        sb.AppendLine($"块数: {manifest.BlockCount:N0}");
        sb.AppendLine($"总样本: {manifest.TotalSamples:N0}");
        sb.AppendLine($"原始载荷: {FormatStorageSize(manifest.RawPayloadBytes)}");
        sb.AppendLine($"捕获文件大小: {FormatStorageSize(manifest.CaptureFileBytes)}");
        sb.AppendLine($"通道数: 期望 {manifest.ExpectedChannelCount} / 实际 {manifest.ObservedChannelCount}");
        sb.AppendLine($"入队块/写入块: {manifest.EnqueuedBlockCount:N0} / {manifest.WrittenBlockCount:N0}");
        sb.AppendLine($"保护阈值: {manifest.PendingBlockLimit:N0} blocks / {FormatStorageSize(manifest.PendingPayloadByteLimit)}");
        sb.AppendLine($"峰值积压: {manifest.PeakPendingBlockCount:N0} blocks / {FormatStorageSize(manifest.PeakPendingPayloadBytes)}");
        sb.AppendLine($"拒绝块/写入故障: {manifest.RejectedBlockCount:N0} / {manifest.WriteFaultCount:N0}");
        sb.AppendLine($"保护触发: {(manifest.ProtectionTriggered ? "是" : "否")}");
        sb.AppendLine($"开始: {manifest.StartedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"结束: {manifest.StoppedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        AppendRawCaptureTimingSummary(sb, manifest);

        if (!string.IsNullOrWhiteSpace(manifest.ProtectionReason))
        {
            sb.AppendLine($"保护原因: {manifest.ProtectionReason}");
        }

        if (!string.IsNullOrWhiteSpace(manifest.LastError))
        {
            sb.AppendLine($"最后错误: {manifest.LastError}");
        }

        bool integrityHealthy = IsRawCaptureIntegrityHealthy(manifest);
        var timingAnalysis = GetRawCaptureTimingAnalysis(manifest);
        sb.AppendLine($"完整性检查: {(integrityHealthy ? "通过" : "异常")}");
        sb.AppendLine(timingAnalysis.HasAnalysis
            ? (timingAnalysis.IsConsistent
                ? $"采样率校验通过: {timingAnalysis.Summary}"
                : $"采样率校验异常: {timingAnalysis.Summary}")
            : "采样率校验: 缺少足够的时基数据");

        string effectiveIntegritySummary = BuildEffectiveIntegritySummary(manifest);
        if (!string.IsNullOrWhiteSpace(effectiveIntegritySummary))
        {
            sb.AppendLine($"完整性摘要: {effectiveIntegritySummary}");
        }

        if (manifest.ObservedDeviceCount > 0)
        {
            sb.AppendLine($"设备数: {manifest.ObservedDeviceCount}");
            sb.AppendLine($"设备样本量一致: {GetDeviceSampleConsistencyText(manifest)} ({manifest.MinDeviceSamplesPerChannel:N0} ~ {manifest.MaxDeviceSamplesPerChannel:N0} samples/channel)");
        }

        int blockIndexIgnoredDeviceCount = manifest.DeviceIntegrity.Count(IsConstantBlockIndexArtifact);
        if (blockIndexIgnoredDeviceCount > 0)
        {
            sb.AppendLine($"BlockIndex 连续性检查: 已忽略 {blockIndexIgnoredDeviceCount} 台设备（字段未递增）");
        }

        foreach (var device in GetMeaningfulDeviceIntegrityIssues(manifest)
            .OrderByDescending(d => d.MissingBlockCount)
            .ThenByDescending(d => d.TotalDataGapSampleCount)
            .ThenBy(d => d.DeviceId)
            .Take(6))
        {
            sb.AppendLine($"AI{device.DeviceId:00}: blocks={device.BlockCount:N0}, samples/ch={device.SamplesPerChannel:N0}, 缺块={device.MissingBlockCount:N0}, 乱序={device.NonMonotonicBlockCount:N0}, TotalData缺口={device.TotalDataGapSampleCount:N0}");
            if (device.IssueExamples.Count > 0)
            {
                sb.AppendLine($"  {device.IssueExamples[0]}");
            }
        }

        foreach (var kv in manifest.ChannelSampleCounts
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Take(8))
        {
            sb.AppendLine($"{kv.Key}: {kv.Value:N0} samples");
        }

        if (manifest.ChannelSampleCounts.Count > 8)
        {
            sb.AppendLine($"... 其余 {manifest.ChannelSampleCounts.Count - 8} 个通道已省略");
        }

        return sb.ToString();
    }

    private static (bool passed, string summary) VerifyRawCaptureFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return (false, $"原始采集文件不存在: {filePath}");
        }

        if (!SdkRawCaptureFormat.TryLoadManifest(filePath, out var manifest) || manifest == null)
        {
            return (false, $"原始采集清单不存在或无法读取: {SdkRawCaptureFormat.GetManifestPath(filePath)}");
        }

        long fileSize = new FileInfo(filePath).Length;
        bool sizeMatches = fileSize == manifest.CaptureFileBytes;
        bool hasData = manifest.BlockCount > 0 && manifest.TotalSamples > 0;
        bool runtimeHealthy = IsRawCaptureRuntimeHealthy(manifest);
        bool integrityHealthy = IsRawCaptureIntegrityHealthy(manifest);
        var timingAnalysis = GetRawCaptureTimingAnalysis(manifest);
        bool timingHealthy = !timingAnalysis.HasAnalysis || timingAnalysis.IsConsistent;
        bool passed = sizeMatches && hasData && runtimeHealthy && integrityHealthy && timingHealthy;

        var summary = new StringBuilder();
        summary.AppendLine($"原始采集校验: {Path.GetFileName(filePath)}");
        summary.AppendLine(sizeMatches
            ? $"文件大小匹配清单: {FormatStorageSize(fileSize)}"
            : $"文件大小与清单不一致: 当前 {FormatStorageSize(fileSize)} / 清单 {FormatStorageSize(manifest.CaptureFileBytes)}");
        summary.AppendLine(hasData
            ? $"块数/样本数有效: {manifest.BlockCount:N0} blocks, {manifest.TotalSamples:N0} samples"
            : "块数或样本数无效");
        summary.AppendLine(runtimeHealthy
            ? $"运行期无拒绝/写入故障: 峰值积压 {manifest.PeakPendingBlockCount:N0} blocks / {FormatStorageSize(manifest.PeakPendingPayloadBytes)}"
            : $"存在运行期异常: 保护 {manifest.ProtectionTriggered}, 拒绝 {manifest.RejectedBlockCount:N0}, 故障 {manifest.WriteFaultCount:N0}, 原因 {manifest.ProtectionReason}, 最后错误 {manifest.LastError}");

        string effectiveIntegritySummary = BuildEffectiveIntegritySummary(manifest);
        summary.AppendLine(integrityHealthy
            ? $"完整性检查通过: {effectiveIntegritySummary}"
            : $"完整性检查异常: {effectiveIntegritySummary}");
        if (timingAnalysis.HasAnalysis)
        {
            AppendRawCaptureTimingSummary(summary, manifest);
            summary.AppendLine(timingAnalysis.IsConsistent
                ? $"采样率校验通过: {timingAnalysis.Summary}"
                : $"采样率校验异常: {timingAnalysis.Summary}");
        }
        else
        {
            summary.AppendLine("采样率校验: 缺少足够的时基数据");
        }

        int blockIndexIgnoredDeviceCount = manifest.DeviceIntegrity.Count(IsConstantBlockIndexArtifact);
        if (blockIndexIgnoredDeviceCount > 0)
        {
            summary.AppendLine($"BlockIndex 连续性检查已忽略 {blockIndexIgnoredDeviceCount} 台设备（字段未递增）。");
        }

        foreach (var device in GetMeaningfulDeviceIntegrityIssues(manifest)
            .OrderByDescending(d => d.MissingBlockCount)
            .ThenByDescending(d => d.TotalDataGapSampleCount)
            .ThenBy(d => d.DeviceId)
            .Take(4))
        {
            summary.AppendLine($"AI{device.DeviceId:00}: blocks={device.BlockCount:N0}, samples/ch={device.SamplesPerChannel:N0}, 缺块={device.MissingBlockCount:N0}, 乱序={device.NonMonotonicBlockCount:N0}, TotalData缺口={device.TotalDataGapSampleCount:N0}");
        }

        return (passed, summary.ToString());
    }

    private static (bool passed, string summary) VerifyTdmsCaptureManifest(SdkRawCaptureManifest manifest)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"TDMS source/segment 会话结构校验: {manifest.SessionName}");
        summary.AppendLine($"采样率: {manifest.SampleRateHz:N0} Hz");
        summary.AppendLine($"通道数: 期望 {manifest.ExpectedChannelCount:N0} / 实际 {manifest.ObservedChannelCount:N0}");
        summary.AppendLine($"写入块: {manifest.WrittenBlockCount:N0}, 拒绝: {manifest.RejectedBlockCount:N0}, 故障: {manifest.WriteFaultCount:N0}");
        summary.AppendLine($"保护触发: {(manifest.ProtectionTriggered ? "是" : "否")}");
        summary.AppendLine($"TDMS 段数: {manifest.TdmsSegments.Count:N0}");
        summary.AppendLine($"原始 payload: {FormatStorageSize(manifest.RawPayloadBytes)}");
        summary.AppendLine($"文件总大小: {FormatStorageSize(manifest.CaptureFileBytes)}");

        var segmentCheck = CheckTdmsSegments(manifest.TdmsSegments);
        bool runtimeHealthy = !manifest.ProtectionTriggered
            && manifest.RejectedBlockCount == 0
            && manifest.WriteFaultCount == 0
            && string.IsNullOrWhiteSpace(manifest.LastError);
        bool passed = manifest.DataIntegrityPassed && runtimeHealthy && segmentCheck.Passed;

        summary.AppendLine(runtimeHealthy
            ? "运行期状态: 正常"
            : $"运行期状态: 异常，原因={manifest.ProtectionReason}, 最后错误={manifest.LastError}");
        summary.AppendLine(segmentCheck.Passed
            ? $"段结构: 通过，已检查 {segmentCheck.CheckedCount:N0}/{segmentCheck.TotalCount:N0} 个 TDMS 段"
            : $"段结构: 异常，已检查 {segmentCheck.CheckedCount:N0}/{segmentCheck.TotalCount:N0} 个 TDMS 段，问题 {segmentCheck.IssueCount:N0} 个");
        summary.AppendLine($"TDMS payload 字节: {FormatStorageSize(segmentCheck.ExpectedPayloadBytes)}");
        summary.AppendLine($"TDMS 文件字节: {FormatStorageSize(segmentCheck.FileBytes)}");
        if (segmentCheck.CompressedSegmentCount > 0)
        {
            summary.AppendLine($"压缩段: {segmentCheck.CompressedSegmentCount:N0} 个");
        }

        foreach (string issue in segmentCheck.Issues.Take(8))
        {
            summary.AppendLine($"- {issue}");
        }

        if (segmentCheck.Issues.Count > 8)
        {
            summary.AppendLine($"... 其余 {segmentCheck.Issues.Count - 8:N0} 个问题已省略");
        }

        return (passed, summary.ToString());
    }

    private static TdmsSegmentCheckResult CheckTdmsSegments(IReadOnlyCollection<TdmsSegmentManifestEntry> segments)
    {
        var issues = new List<string>();
        long expectedPayloadBytes = 0;
        long fileBytes = 0;
        int checkedCount = 0;
        int compressedCount = 0;

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment.Path) || !File.Exists(segment.Path))
            {
                issues.Add($"缺少段文件: {segment.Path}");
                continue;
            }

            checkedCount++;
            var fileInfo = new FileInfo(segment.Path);
            fileBytes += fileInfo.Length;

            if (!TryReadTdmsLeadIn(segment.Path, out var leadIn, out string leadInError))
            {
                issues.Add($"{Path.GetFileName(segment.Path)} lead-in 异常: {leadInError}");
                continue;
            }

            long expectedSegmentPayloadBytes;
            if (segment.CompressionEnabled)
            {
                compressedCount++;
                if (segment.ChannelPayloadBytes.Length != segment.ChannelIds.Length)
                {
                    issues.Add($"{Path.GetFileName(segment.Path)} 压缩通道 payload 数量与通道数不一致: {segment.ChannelPayloadBytes.Length}/{segment.ChannelIds.Length}");
                    continue;
                }

                expectedSegmentPayloadBytes = segment.ChannelPayloadBytes.Sum(static value => (long)value);
            }
            else
            {
                expectedSegmentPayloadBytes = checked((long)segment.ChannelIds.Length * segment.SamplesPerChannel * sizeof(float));
            }

            expectedPayloadBytes += Math.Max(0, expectedSegmentPayloadBytes);

            if (!leadIn.HeaderOk)
            {
                issues.Add($"{Path.GetFileName(segment.Path)} TDMS 标识异常");
            }

            if (leadIn.Version != 4713)
            {
                issues.Add($"{Path.GetFileName(segment.Path)} TDMS version 异常: {leadIn.Version}");
            }

            if (leadIn.DataBytes != expectedSegmentPayloadBytes)
            {
                issues.Add($"{Path.GetFileName(segment.Path)} payload 字节不一致: 当前 {FormatStorageSize(leadIn.DataBytes)} / manifest {FormatStorageSize(expectedSegmentPayloadBytes)}");
            }

            if (segment.SamplesPerChannel <= 0 || segment.ChannelIds.Length == 0 || segment.SampleRateHz <= 0)
            {
                issues.Add($"{Path.GetFileName(segment.Path)} manifest 时间线字段无效");
            }
        }

        bool passed = segments.Count > 0 && checkedCount == segments.Count && issues.Count == 0;
        return new TdmsSegmentCheckResult(
            passed,
            segments.Count,
            checkedCount,
            issues.Count,
            expectedPayloadBytes,
            fileBytes,
            compressedCount,
            issues);
    }

    private static bool TryReadTdmsLeadIn(string filePath, out TdmsLeadInInfo info, out string error)
    {
        info = new TdmsLeadInInfo(false, 0, 0, 0, 0, 0, 0, 0);
        error = string.Empty;
        Span<byte> header = stackalloc byte[28];
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < header.Length)
            {
                error = $"文件过小: {FormatStorageSize(fileInfo.Length)}";
                return false;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            int read = stream.Read(header);
            if (read < header.Length)
            {
                error = $"lead-in 读取不足: {read}/{header.Length}";
                return false;
            }

            bool headerOk = header[0] == (byte)'T'
                && header[1] == (byte)'D'
                && header[2] == (byte)'S'
                && header[3] == (byte)'m';
            uint toc = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8, 4));
            ulong nextSegmentOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(12, 8));
            ulong rawDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8));
            long rawDataStart = checked(28L + (long)rawDataOffset);
            long dataBytes = fileInfo.Length - rawDataStart;

            info = new TdmsLeadInInfo(headerOk, toc, version, nextSegmentOffset, rawDataOffset, rawDataStart, dataBytes, fileInfo.Length);
            if (rawDataStart < header.Length || rawDataStart > fileInfo.Length)
            {
                error = $"raw data offset 越界: {rawDataStart:N0}/{fileInfo.Length:N0}";
                return false;
            }

            if (nextSegmentOffset != (ulong)(fileInfo.Length - 28L))
            {
                error = $"next segment offset 与文件大小不一致: {nextSegmentOffset:N0}/{fileInfo.Length - 28L:N0}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private sealed record TdmsLeadInInfo(
        bool HeaderOk,
        uint Toc,
        uint Version,
        ulong NextSegmentOffset,
        ulong RawDataOffset,
        long RawDataStart,
        long DataBytes,
        long FileBytes);

    private sealed record TdmsSegmentCheckResult(
        bool Passed,
        int TotalCount,
        int CheckedCount,
        int IssueCount,
        long ExpectedPayloadBytes,
        long FileBytes,
        int CompressedSegmentCount,
        List<string> Issues);

    private static (bool passed, string summary) VerifyFastSegmentFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return (false, $"高速段文件不存在: {filePath}");
        }

        var summary = new StringBuilder();
        summary.AppendLine($"高速段结构校验: {Path.GetFileName(filePath)}");

        try
        {
            const int headerBytes = 4096;
            byte[] header = new byte[headerBytes];
            using var stream = File.OpenRead(filePath);
            if (stream.Length < headerBytes)
            {
                summary.AppendLine($"文件过小: {FormatStorageSize(stream.Length)}");
                return (false, summary.ToString());
            }

            int read = stream.Read(header, 0, header.Length);
            if (read != header.Length)
            {
                summary.AppendLine($"读取文件头失败: {read}/{header.Length} bytes");
                return (false, summary.ToString());
            }

            bool magicOk = header[0] == (byte)'D'
                && header[1] == (byte)'H'
                && header[2] == (byte)'F'
                && header[3] == (byte)'S'
                && header[4] == (byte)'E'
                && header[5] == (byte)'G'
                && header[6] == (byte)'1';
            int version = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            int sourceId = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
            int segmentIndex = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
            int samplesPerChannel = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
            double sampleRateHz = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(32));
            long payloadBytes = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
            int declaredHeaderBytes = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48));
            long expectedBytes = declaredHeaderBytes + payloadBytes;
            long actualBytes = stream.Length;

            bool structureOk = magicOk
                && version == 1
                && sourceId >= 0
                && segmentIndex >= 0
                && channelCount > 0
                && samplesPerChannel > 0
                && sampleRateHz > 0
                && declaredHeaderBytes == headerBytes
                && payloadBytes == (long)channelCount * samplesPerChannel * sizeof(float)
                && actualBytes == expectedBytes;

            summary.AppendLine(magicOk ? "文件头标识正确: DHFSEG1" : "文件头标识异常");
            summary.AppendLine($"source={sourceId}, segment={segmentIndex}, channels={channelCount:N0}, samples/ch={samplesPerChannel:N0}, sampleRate={sampleRateHz:N0} Hz");
            summary.AppendLine(actualBytes == expectedBytes
                ? $"文件大小匹配: {FormatStorageSize(actualBytes)}"
                : $"文件大小不一致: 当前 {FormatStorageSize(actualBytes)} / 期望 {FormatStorageSize(expectedBytes)}");
            summary.AppendLine(structureOk
                ? "高速段结构校验通过（头部、通道数、样本数、payload 尺寸一致）"
                : "高速段结构校验失败");

            return (structureOk, summary.ToString());
        }
        catch (Exception ex)
        {
            summary.AppendLine($"校验异常: {ex.Message}");
            return (false, summary.ToString());
        }
    }

    private static (bool passed, string summary) VerifyManualTdmsSourceSegmentFile(string filePath)
    {
        var summary = new StringBuilder();
        summary.AppendLine($"TDMS source/segment 单文件结构校验: {Path.GetFileName(filePath)}");

        if (!TryReadTdmsLeadIn(filePath, out var leadIn, out string error))
        {
            summary.AppendLine($"lead-in 校验失败: {error}");
            return (false, summary.ToString());
        }

        bool passed = leadIn.HeaderOk && leadIn.Version == 4713 && leadIn.DataBytes > 0;
        summary.AppendLine(leadIn.HeaderOk ? "TDMS 标识: TDSm" : "TDMS 标识异常");
        summary.AppendLine($"TDMS version: {leadIn.Version}");
        summary.AppendLine($"raw data offset: {leadIn.RawDataStart:N0} bytes");
        summary.AppendLine($"payload bytes: {FormatStorageSize(leadIn.DataBytes)}");
        summary.AppendLine($"file bytes: {FormatStorageSize(leadIn.FileBytes)}");
        summary.AppendLine("说明: 当前主线是多个 raw/source_*.tdms + session.manifest.json 组成一个逻辑会话；单文件只做结构校验，不再执行旧的全通道 DDC 回读无损验证。");
        summary.AppendLine(passed ? "结构校验通过" : "结构校验失败");
        return (passed, summary.ToString());
    }

    private static (bool passed, string summary) VerifyTdmsSourceSegmentFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            return VerifyManualTdmsSourceSegmentFile(filePath);
        }

        if (!File.Exists(filePath))
        {
            return (false, $"TDMS 源段文件不存在: {filePath}");
        }

        var summary = new StringBuilder();
        summary.AppendLine($"TDMS 源文件结构校验: {Path.GetFileName(filePath)}");

        try
        {
            var info = TdmsSourceSegmentFileWriter.ReadStructure(filePath);
            long fileBytes = new FileInfo(filePath).Length;
            bool passed = info.GroupCount > 0 && info.ChannelCount > 0 && fileBytes > 0;
            summary.AppendLine($"Group数: {info.GroupCount:N0}");
            summary.AppendLine($"Channel数: {info.ChannelCount:N0}");
            summary.AppendLine($"文件大小: {FormatStorageSize(fileBytes)}");
            summary.AppendLine(passed ? "结构校验通过" : "结构校验失败");
            return (passed, summary.ToString());
        }
        catch (Exception ex)
        {
            summary.AppendLine($"结构校验异常: {ex.Message}");
            return (false, summary.ToString());
        }
    }

    private bool IsRealtimePreviewActive()
    {
        bool tcpActive = DataSourceMode == 0 && IsTcpConnected && IsDataVerified && IsDataActive;
        bool sdkActive = DataSourceMode == 1 && IsSdkInitialized && IsSdkSampling && IsSdkDataActive;
        return tcpActive || sdkActive;
    }

    private void QueueOnlineChannelUpdate(int channelId)
    {
        _pendingOnlineChannelIds[channelId] = 0;
    }

    private void FlushPendingOnlineChannelUpdates()
    {
        if (!IsRealtimePreviewActive())
        {
            _pendingOnlineChannelIds.Clear();
            return;
        }

        if (_pendingOnlineChannelIds.IsEmpty)
        {
            return;
        }

        try
        {
            var channelIds = _pendingOnlineChannelIds.Keys.ToArray();
            if (channelIds.Length == 0)
            {
                return;
            }

            foreach (var channelId in channelIds)
            {
                _pendingOnlineChannelIds.TryRemove(channelId, out _);

                if (DataSourceMode == 1)
                {
                    EnsureSdkChannelRegistration(channelId);
                    AlignSdkSelectedDevice(channelId);
                }

                var ci = Channels.FirstOrDefault(c => c.ChannelId == channelId);
                if (ci != null)
                {
                    ci.Online = true;
                }

                _onlineChannelManager.SetChannelOnline(channelId, true);
            }

            foreach (var dev in Devices)
            {
                int cnt = dev.Channels.Count(c => c.Online);
                dev.OnlineChannelCount = cnt;
                dev.Online = cnt > 0;
            }

            OnPropertyChanged(nameof(OnlineChannelStatus));
            UpdateDeviceChannels();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[OnlineFlush] UI状态合并刷新异常: {ex.Message}");
        }
    }

    private async Task VerifyStoredFileAsync()
    {
        var filePath = SelectedTdmsFile?.FullPath;
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            FileVerifyResult = "请先从列表中选择一个文件";
            FileVerifyPassed = false;
            return;
        }

        FileVerifyResult = "正在验证…";
        try
        {
            if (SdkRawCaptureFormat.IsRawCaptureFile(filePath))
            {
                var (passed, summary) = VerifyRawCaptureFile(filePath);
                FileVerifyPassed = passed;
                FileVerifyResult = summary;
                return;
            }

            if (IsFastSegmentFile(filePath))
            {
                var (passed, summary) = VerifyFastSegmentFile(filePath);
                FileVerifyPassed = passed;
                FileVerifyResult = summary;
                return;
            }

            if (IsTdmsSourceSegmentFile(filePath))
            {
                var (passed, summary) = VerifyTdmsSourceSegmentFile(filePath);
                FileVerifyPassed = passed;
                FileVerifyResult = summary;
                return;
            }

            if (!IsTdmsLikeFile(filePath))
            {
                FileVerifyPassed = false;
                FileVerifyResult = $"暂不支持校验该文件格式: {Path.GetExtension(filePath)}";
                return;
            }

            _writeHashesByFile.TryGetValue(filePath, out var hashes);
            _writeSampleCountsByFile.TryGetValue(filePath, out var counts);

            // 内存中没有哈希时，尝试从 .sha256 清单文件加载
            if (hashes == null || hashes.Count == 0)
            {
                var (loadedHashes, loadedCounts) = StorageVerifier.LoadManifest(filePath);
                hashes ??= loadedHashes;
                counts ??= loadedCounts;
            }

            var result = await Task.Run(() =>
                StorageVerifier.Verify(filePath, hashes, counts));
            FileVerifyPassed = result.AllLossless;
            FileVerifyResult = result.Summary;
        }
        catch (Exception ex)
        {
            FileVerifyPassed = false;
            FileVerifyResult = $"验证异常: {ex.Message}";
        }
    }
    partial void OnSelectedTdmsFileChanged(TdmsFileItem? value)
    {
        (TestReadSelectedFileCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (VerifyStoredFileCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RefreshRecentFiles()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(StoragePath)) return;
            var path = ResolveStoragePath(StoragePath);
            Directory.CreateDirectory(path);
            // 递归搜索子目录（文件整理后 .tdms 位于时间命名的子文件夹中）
            var tdmsFiles = Directory.EnumerateFiles(path, "*.tdms", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase));
            var tdmFiles = Directory.EnumerateFiles(path, "*.tdm", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase));
            var rawCaptureFiles = Directory.EnumerateFiles(path, $"*{SdkRawCaptureFormat.FileSuffix}", SearchOption.AllDirectories);
            // 也收集公共文档下的 TDMS/TDM（ASCII 路径回退时产生）
            var altBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "DH", "TDMS");
            var altTdmsFiles = Directory.Exists(altBase)
                ? Directory.EnumerateFiles(altBase, "*.tdms", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
            var altTdmFiles = Directory.Exists(altBase)
                ? Directory.EnumerateFiles(altBase, "*.tdm", SearchOption.AllDirectories)
                    .Where(f => !f.EndsWith("_index", StringComparison.OrdinalIgnoreCase))
                : Array.Empty<string>();
            var files = tdmsFiles.Concat(tdmFiles).Concat(rawCaptureFiles).Concat(altTdmsFiles).Concat(altTdmFiles)
                .Select(fp => new FileInfo(fp))
                .Where(fi => fi.Exists)
                .OrderByDescending(fi => fi.LastWriteTimeUtc)
                .Take(20)
                .Select(fi => new TdmsFileItem(fi))
                .ToArray();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RecentTdmsFiles.Clear();
                foreach (var f in files) RecentTdmsFiles.Add(f);
            });
        }
        catch (Exception ex)
        {
            StorageStatusMessage = $"刷新列表失败: {ex.Message}";
        }
    }

    private void OpenOutputFolder()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(StoragePath)) return;
            var fullPath = ResolveStoragePath(StoragePath);
            Directory.CreateDirectory(fullPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{fullPath}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StorageStatusMessage = $"打开目录失败: {ex.Message}";
        }
    }
    private void TestReadSelectedFile()
    {
        var fp = SelectedTdmsFile?.FullPath;
        if (string.IsNullOrEmpty(fp)) return;
        try
        {
            if (SdkRawCaptureFormat.IsRawCaptureFile(fp))
            {
                if (!SdkRawCaptureFormat.TryLoadManifest(fp, out var manifest) || manifest == null)
                {
                    throw new InvalidOperationException($"找不到原始采集清单: {SdkRawCaptureFormat.GetManifestPath(fp)}");
                }

                FileVerifyResult = BuildRawCaptureSummary(fp, manifest);
                FileVerifyPassed = true;
                return;
            }

            if (!IsTdmsLikeFile(fp))
            {
                throw new InvalidOperationException($"暂不支持读取该文件格式: {Path.GetExtension(fp)}");
            }

            var map = TdmsReaderUtil.ListGroupsAndChannels(fp);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"文件: {Path.GetFileName(fp)}");
            foreach (var kv in map)
            {
                sb.AppendLine($"组: {kv.Key} 通道: {string.Join(", ", kv.Value)}");
            }
            var g = map.Keys.FirstOrDefault();
            var ch = g != null ? map[g].FirstOrDefault() : null;
            if (g != null && ch != null)
            {
                var data = TdmsReaderUtil.ReadChannelData(fp, g, ch);
                var sample = string.Join(", ", data.Take(10).Select(v => v.ToString("F3")));
                sb.AppendLine($"示例({g}/{ch}): {sample}");
            }
            FileVerifyResult = sb.ToString();
            FileVerifyPassed = true;
        }
        catch (Exception ex)
        {
            FileVerifyResult = $"读取失败: {ex.Message}";
            FileVerifyPassed = false;
        }
    }

    private void OnSampleRateChangedCommand(int newSampleRate)
    {
        // 确保采样频率在合理范围内 (100-10000 Hz)
        if (newSampleRate < 100 || newSampleRate > 10000)
            return;

        SampleRate = newSampleRate;
        Console.WriteLine($"[MainWindowViewModel] Sample rate changed to: {SampleRate} Hz");
        
        // 通知所有曲线面板更新采样频率
        UpdateAllCurvePanelsSampleRate();
        
        // 如果模拟数据正在运行，重启它以应用新的采样频率
        
    }
    
    // 更新所有曲线面板的采样频率
    private void UpdateAllCurvePanelsSampleRate()
    {
        // 这个方法将在主窗口中实现，通过事件或消息机制通知所有CurvePanel
        SampleRateChanged?.Invoke(this, SampleRate);
    }

    partial void OnSelectedDeviceIdChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedDevice));
        OnPropertyChanged(nameof(SelectedDeviceTitle));
    }

    private sealed class LocalTestServer
    {
        private readonly string _ip;
        private readonly int _port;
        private TcpListener? _listener;
        private Thread? _thread;
        private CancellationTokenSource? _cts;
        public LocalTestServer(string ip, int port) { _ip = ip; _port = port; }
        public void Start()
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Parse(_ip), _port);
            _listener.Start();
            _thread = new Thread(() => Run(_cts.Token)) { IsBackground = true };
            _thread.Start();
        }
        public void Stop()
        {
            _cts?.Cancel();
            try { _thread?.Join(500); } catch { }
            _listener?.Stop();
            _thread = null;
            _listener = null;
            _cts = null;
        }
        private void Run(CancellationToken ct)
        {
            using var client = _listener!.AcceptTcpClient();
            using var stream = client.GetStream();
            int pktCount = 128;
            var names = new[] { "AI1-01,mV", "AI1-02,mV" };
            var rand = new Random();
            ulong total = 0;
            while (!ct.IsCancellationRequested)
            {
                var ch1 = Enumerable.Range(0, pktCount).Select(i => (float)Math.Sin(2 * Math.PI * i / pktCount)).ToArray();
                var ch2 = Enumerable.Range(0, pktCount).Select(i => (float)(Math.Cos(2 * Math.PI * i / pktCount) + 0.05 * (rand.NextDouble() - 0.5))).ToArray();
                var packet = BuildPacket(total, new[] { ch1, ch2 }, names, DateTime.UtcNow);
                stream.Write(packet, 0, packet.Length);
                total += (ulong)pktCount;
                Thread.Sleep(50);
            }
        }
        private static byte[] BuildPacket(ulong total, float[][] channels, string[] channelNames, DateTime timestampUtc)
        {
            int chCount = channels.Length;
            int pktCount = channels[0].Length;
            var payload = new List<byte>();
            void WLE(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); payload.AddRange(b); }
            WLE(BitConverter.GetBytes(total));
            WLE(BitConverter.GetBytes((uint)pktCount));
            WLE(BitConverter.GetBytes((uint)chCount));
            for (int p = 0; p < pktCount; p++) for (int c = 0; c < chCount; c++) WLE(BitConverter.GetBytes(channels[c][p]));
            var namesStr = string.Join("|", channelNames);
            var nameBytes = Encoding.ASCII.GetBytes(namesStr);
            WLE(BitConverter.GetBytes((uint)nameBytes.Length));
            payload.AddRange(nameBytes);
            var dto = new DateTimeOffset(timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime());
            ulong epochSec = (ulong)dto.ToUnixTimeSeconds();
            long ticksInSec = timestampUtc.Ticks % TimeSpan.TicksPerSecond;
            uint usec = (uint)(ticksInSec / 10);
            WLE(BitConverter.GetBytes(epochSec));
            WLE(BitConverter.GetBytes(usec));
            uint magic = 0x55AAAA55;
            uint cmd = 0x7C;
            uint len = (uint)payload.Count;
            var packet = new List<byte>(12 + payload.Count);
            void WH(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); packet.AddRange(b); }
            WH(BitConverter.GetBytes(magic));
            WH(BitConverter.GetBytes(cmd));
            WH(BitConverter.GetBytes(len));
            packet.AddRange(payload);
            return packet.ToArray();
        }
    }
    
    // 清理资源（当窗口关闭时调用）
    public void Cleanup()
    {
        CleanupSdkRawCaptureSubscription();
        SetSdkRealtimePublishEnabled(true);
        _sdkRawCaptureWriter?.Dispose();
        _sdkRawCaptureWriter = null;
        _sdkTdmsCaptureWriter?.Dispose();
        _sdkTdmsCaptureWriter = null;
        _sdkRawCaptureProtectionStopPending = false;
        _activeStorageRuntime = null;
        _onlineStatusFlushTimer?.Stop();
        _onlineStatusFlushTimer = null;
        _channelTimeUpdateTimer?.Stop();
        _channelTimeUpdateTimer?.Dispose();
        _channelTimeUpdateTimer = null;
    }
}
