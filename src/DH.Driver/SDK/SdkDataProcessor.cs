// DH.Driver/SDK/SdkDataProcessor.cs
// SDK数据处理器 - 将SDK回调数据转换为DH项目格式
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;
using DH.Datamanage.Realtime;

namespace DH.Driver.SDK;

/// <summary>
/// SDK设备信息
/// </summary>
public class SdkDeviceInfo
{
    /// <summary>
    /// 设备索引（从0开始）
    /// </summary>
    public int DeviceIndex { get; set; }
    
    /// <summary>
    /// 机器ID
    /// </summary>
    public int MachineId { get; set; }

    /// <summary>
    /// 回调/通道使用的设备ID（与 nGroupID 保持一致）
    /// </summary>
    public int ChannelDeviceId { get; set; }
    
    /// <summary>
    /// 机器IP地址
    /// </summary>
    public string MachineIp { get; set; } = "";
    
    /// <summary>
    /// 通道数量
    /// </summary>
    public int ChannelCount { get; set; }
    
    /// <summary>
    /// 是否在线
    /// </summary>
    public bool IsOnline { get; set; }
}

/// <summary>
/// SDK数据处理器
/// 负责接收SDK回调数据，解析后发布到DataBus
/// </summary>
public class SdkDataProcessor : IDisposable
{
    private readonly IDataBus _dataBus;
    private readonly StreamTable _streamTable;
    private readonly Action<bool, string> _statusCallback;
    
    // 保持回调委托引用，防止GC回收
    private HardwareSDK.SampleDataChangeEventHandle? _callbackDelegate;
    
    // 数据缓存 - 按通道缓冲
    private readonly ConcurrentDictionary<int, ConcurrentQueue<float>> _channelBuffers = new();
    private const int MinChunkSize = 512;
    private const int MaxChunkSize = 4096;
    private const int TargetCallbackBytes = 4 * 1024 * 1024;
    private const int MaxRealtimePreviewQueueDepth = 256;
    private const int MaxRealtimePreviewSamplesPerChannel = 1024;
    private int _chunkSize = 2048;
    private int _sdkCallbackDataCount = 2048;
    
    // 日志标记，防止重复日志
    private readonly ConcurrentDictionary<int, bool> _firstDataLogged = new();
    private readonly ConcurrentDictionary<int, bool> _firstPublishLogged = new();
    private readonly ConcurrentDictionary<string, CallbackFlowStats> _callbackFlowStats = new();
    private readonly Channel<SdkRealtimePreviewBlock> _realtimePreviewQueue;
    private readonly CancellationTokenSource _realtimePreviewCts = new();
    private readonly Task _realtimePreviewPumpTask;
    private int _realtimePreviewQueueDepth;
    private long _realtimePreviewDroppedBlocks;
    private long _realtimePreviewAcceptedBlocks;
    private int _frameSequence;
    
    // 状态
    private bool _isInitialized;
    private bool _isSampling;
    private volatile bool _realtimePublishEnabled = true;
    private float _sampleRate = 1000f;
    private int _onlineDeviceCount;
    private int _totalChannelCount;
    
    // 设备信息存储
    private readonly List<SdkDeviceInfo> _deviceInfoList = new();
    
    // 线程同步
    private readonly SynchronizationContext? _syncContext;
    
    public event EventHandler<bool>? StatusChanged;
    public event EventHandler<bool>? DataActivityChanged;
    public event Action<SdkRawBlock>? RawBlockReceived;
    
    private DateTime _lastDataTime;
    private Timer? _activityTimer;
    private bool _isActive;

    public bool IsInitialized => _isInitialized;
    public bool IsSampling => _isSampling;
    public bool RealtimePublishEnabled => _realtimePublishEnabled;
    public float SampleRate => _sampleRate;
    
    /// <summary>
    /// 在线设备数量
    /// </summary>
    public int OnlineDeviceCount => _onlineDeviceCount;
    
    /// <summary>
    /// 总通道数量
    /// </summary>
    public int TotalChannelCount => _totalChannelCount;
    
    /// <summary>
    /// 获取设备信息列表（只读）
    /// </summary>
    public IReadOnlyList<SdkDeviceInfo> DeviceInfoList => _deviceInfoList.AsReadOnly();

    public void SetRealtimePublishEnabled(bool enabled)
    {
        _realtimePublishEnabled = enabled;
        if (!enabled)
        {
            DrainRealtimePreviewQueue();
            ClearBufferedChannels();
        }
    }

    public SdkDataProcessor(IDataBus dataBus, StreamTable streamTable, Action<bool, string> statusCallback)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _streamTable = streamTable ?? throw new ArgumentNullException(nameof(streamTable));
        _statusCallback = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
        _syncContext = SynchronizationContext.Current;
        _realtimePreviewQueue = Channel.CreateBounded<SdkRealtimePreviewBlock>(
            new BoundedChannelOptions(MaxRealtimePreviewQueueDepth)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        _realtimePreviewPumpTask = Task.Run(ProcessRealtimePreviewQueueAsync);
        
        // 活动检测定时器
        _activityTimer = new Timer(CheckActivity, null, 500, 500);
    }

    private const string SDK_OWNER = "DH.Client.App.MainWindow";

    /// <summary>
    /// 初始化SDK
    /// </summary>
    /// <param name="configPath">配置文件夹路径</param>
    /// <returns>是否成功</returns>
    public bool Initialize(string configPath)
    {
        try
        {
            // 尝试获取SDK锁
            if (!SdkGlobalLock.TryAcquire(SDK_OWNER))
            {
                UpdateStatus(false, $"SDK已被 '{SdkGlobalLock.CurrentOwner}' 占用，请先断开该连接");
                return false;
            }
            
            UpdateStatus(false, $"正在初始化SDK: {configPath}");
            
            // 确保路径以反斜杠结尾
            if (!configPath.EndsWith("\\"))
            {
                configPath += "\\";
            }
            
            // 检查配置路径是否存在
            if (!System.IO.Directory.Exists(configPath.TrimEnd('\\')))
            {
                UpdateStatus(false, $"SDK配置路径不存在: {configPath}");
                return false;
            }
            
            Console.WriteLine($"[SDK] 初始化配置路径: {configPath}");
            SdkNativeLoader.EnsureLoaded(configPath);
            
            // 尝试先释放之前的SDK实例（如果有）
            try
            {
                // 先停止采样（如果正在采样）
                try { HardwareSDK.StopMacSample(); } catch { }
                System.Threading.Thread.Sleep(100);
                
                // 释放SDK
                HardwareSDK.QuitMacControl();
                Console.WriteLine("[SDK] 已释放之前的SDK实例");
                
                // 等待一段时间让资源完全释放
                System.Threading.Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SDK] 释放SDK实例时出现异常（可忽略）: {ex.Message}");
            }
            
            // 初始化SDK（注意：返回值不是错误码，Demo_C#中也不检查返回值）
            int result = HardwareSDK.InitMacControl(configPath);
            Console.WriteLine($"[SDK] InitMacControl 返回: {result} (0x{result:X8})");
            // Demo_C#源码中不检查InitMacControl返回值，直接继续执行
            
            // 注册回调函数 - 必须保持委托引用
            _callbackDelegate = OnSampleDataReceived;
            int callbackResult = HardwareSDK.SetDataChangeCallBackFun(_callbackDelegate);
            Console.WriteLine($"[SDK] SetDataChangeCallBackFun 返回: {callbackResult}");
            // 回调注册也不检查返回值，与Demo_C#保持一致
            
            // 查找并连接设备
            bool connected = HardwareSDK.RefindAndConnecMac();
            Console.WriteLine($"[SDK] RefindAndConnecMac 返回: {connected}");
            
            //int deviceCount = HardwareSDK.GetAllMacOnlineCount();
            //增加轮询等待机制，由于模型硬件或其他网络设备的相应需要时间
            int deviceCount = 0;
            for(int retry = 0; retry < 10; retry++)
            {
                deviceCount = HardwareSDK.GetAllMacOnlineCount();
                if(deviceCount > 0) // 一旦找到一些，再稍等一下确认所有设备都跟上
                {
                    System.Threading.Thread.Sleep(500);
                    deviceCount = HardwareSDK.GetAllMacOnlineCount();
                    break;
                }
                System.Threading.Thread.Sleep(200);
            }

            Console.WriteLine($"[SDK] GetAllMacOnlineCount 返回: {deviceCount}");
            _onlineDeviceCount = deviceCount;
            
            // 获取每个设备的详细信息
            _deviceInfoList.Clear();
            _totalChannelCount = 0;
            for (int i = 0; i < deviceCount; i++)
            {
                try
                {
                    // 分配IP字符串缓冲区
                    IntPtr ipBuffer = Marshal.AllocHGlobal(64);
                    try
                    {
                        int machineId;
                        int usedBuffer;
                        int infoResult = HardwareSDK.GetMacInfoFromIndex(i, out machineId, ipBuffer, 64, out usedBuffer);
                        
                        string machineIp = "";
                        if (infoResult >= 0 && usedBuffer > 0)
                        {
                            machineIp = Marshal.PtrToStringAnsi(ipBuffer) ?? "";
                        }
                        
                        // 获取该设备的通道数量
                        int channelCount = HardwareSDK.GetMacCurrentChnCount(machineId, machineIp);
                        if (channelCount < 0) channelCount = 0;
                        
                        // 获取设备连接状态
                        byte linkStatus = HardwareSDK.GetMacLinkStatus(machineId, machineIp);
                        bool isOnline = linkStatus > 0;
                        
                        var deviceInfo = new SdkDeviceInfo
                        {
                            DeviceIndex = i,
                            MachineId = machineId,
                            ChannelDeviceId = SdkDeviceIdResolver.ResolveDeviceId(
                                groupId: -1,
                                machineId: machineId,
                                channelDeviceId: i,
                                deviceIndex: i),
                            MachineIp = machineIp,
                            ChannelCount = channelCount,
                            IsOnline = isOnline
                        };
                        _deviceInfoList.Add(deviceInfo);
                        _totalChannelCount += channelCount;
                        
                        Console.WriteLine($"[SDK] 设备{i}: MachineId={machineId}, IP={machineIp}, 通道数={channelCount}, 在线={isOnline}");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ipBuffer);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SDK] 获取设备{i}信息异常: {ex.Message}");
                }
            }
            
            // 获取当前采样率
            _sampleRate = HardwareSDK.GetMacCurrentSampleFreq();
            if (_sampleRate <= 0) _sampleRate = 1000f;
            
            _isInitialized = true;
            UpdateStatus(true, $"SDK初始化成功，在线设备: {deviceCount}，总通道数: {_totalChannelCount}，采样率: {_sampleRate}Hz");
            StatusChanged?.Invoke(this, true);
            
            return true;
        }
        catch (DllNotFoundException ex)
        {
            UpdateStatus(false, $"找不到SDK DLL: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"SDK初始化异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 启动采样
    /// </summary>
    public bool StartSampling()
    {
        if (!_isInitialized)
        {
            UpdateStatus(false, "SDK未初始化，无法启动采样");
            return false;
        }
        
        try
        {
            // 清空缓冲区
            _channelBuffers.Clear();
            
            // 设置每次获取的数据量
            RefreshIngestBatchSettings();
            HardwareSDK.SetGetDataCountEveryTime(_sdkCallbackDataCount);
            Console.WriteLine($"[SDK] Callback block size={_sdkCallbackDataCount}, publish chunk size={_chunkSize}, total channels={_totalChannelCount}, sample rate={_sampleRate}Hz");
            
            // 启动采样
            HardwareSDK.StartMacSample();
            
            _isSampling = true;
            UpdateStatus(true, "SDK采样已启动");
            
            return true;
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"启动采样失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 停止采样
    /// </summary>
    public void StopSampling()
    {
        if (!_isSampling) return;
        
        try
        {
            HardwareSDK.StopMacSample();
            FlushBufferedChannels();
            _isSampling = false;
            _isActive = false;
            DataActivityChanged?.Invoke(this, false);
            UpdateStatus(true, "SDK采样已停止");
        }
        catch (Exception ex)
        {
            UpdateStatus(false, $"停止采样失败: {ex.Message}");
        }
    }

    /// <summary>
    /// SDK数据回调处理
    /// </summary>
    private void OnSampleDataReceived(
        long sampleTime,
        int groupIdSize,
        IntPtr groupInfo,
        int nMessageType,
        int nGroupID,
        int nChannelStyle,
        int nChannelID,
        int nMachineID,
        long nTotalDataCount,
        int nDataCountPerChannel,
        int nBufferCount,
        int nBlockIndex,
        long varSampleData)
    {
        try
        {
            RecordCallbackFlow(
                nMessageType,
                nGroupID,
                nMachineID,
                nDataCountPerChannel,
                nBufferCount);

            // 更新活动时间
            UpdateObservedDeviceMapping(nMachineID, nGroupID);
            _lastDataTime = DateTime.UtcNow;
            if (!_isActive)
            {
                _isActive = true;
                DataActivityChanged?.Invoke(this, true);
                Console.WriteLine($"[SDK回调] 数据活动开始，MessageType={nMessageType}, GroupID={nGroupID}, MachineID={nMachineID}, 每通道数据量={nDataCountPerChannel}");
            }
            
            // 检查消息类型
            if (nMessageType != SdkMessageTypes.SAMPLE_ANALOG_DATA &&
                nMessageType != SdkMessageTypes.SAMPLE_ANALOG_MULTICHN_DATA &&
                nMessageType != SdkMessageTypes.SAMPLE_SINGLEGROUP_ANALOGDATA)
            {
                return;
            }
            
            if (nDataCountPerChannel <= 0 || nBufferCount <= 0)
            {
                return;
            }
            
            // 从指针读取数据
            
            
            // 计算通道数
            int floatCount = nBufferCount / sizeof(float);
            int channelCount = floatCount / nDataCountPerChannel;
            if (channelCount <= 0) channelCount = 1;
            
            // 解析float数据
            bool needsRawBlock = RawBlockReceived is not null;
            bool needsRealtimePublish = _realtimePublishEnabled;
            if (!needsRawBlock && !needsRealtimePublish)
            {
                return;
            }

            float[] allData = SdkRawFloatBufferPool.Rent(floatCount);
            bool bufferOwnershipTransferred = false;

            try
            {
                Marshal.Copy((IntPtr)varSampleData, allData, 0, floatCount);

                if (needsRawBlock)
                {
                    var rawBlock = new SdkRawBlock
                    {
                        SampleTime = sampleTime,
                        MessageType = nMessageType,
                        GroupId = nGroupID,
                        MachineId = nMachineID,
                        TotalDataCount = nTotalDataCount,
                        DataCountPerChannel = nDataCountPerChannel,
                        BufferCountBytes = nBufferCount,
                        BlockIndex = nBlockIndex,
                        ChannelCount = channelCount,
                        SampleRateHz = _sampleRate,
                        ReceivedAtUtc = DateTime.UtcNow,
                        InterleavedSamples = allData,
                        PayloadFloatCount = floatCount,
                        ReturnBufferToPool = true
                    };

                    bufferOwnershipTransferred = true;

                    try
                    {
                        RawBlockReceived?.Invoke(rawBlock);
                    }
                    catch (Exception rawEx)
                    {
                        Console.WriteLine($"[SdkDataProcessor] 原始块旁路异常: {rawEx.Message}");
                    }
                }

                if (needsRealtimePublish)
                {
                    TryEnqueueRealtimePreviewBlock(
                        allData,
                        floatCount,
                        nGroupID,
                        nMachineID,
                        nDataCountPerChannel,
                        channelCount,
                        nTotalDataCount);
                }
            }
            finally
            {
                if (!bufferOwnershipTransferred)
                {
                    SdkRawFloatBufferPool.Return(allData);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkDataProcessor] 回调处理异常: {ex.Message}");
        }
    }

    private bool TryEnqueueRealtimePreviewBlock(
        float[] source,
        int payloadFloatCount,
        int groupId,
        int machineId,
        int dataCountPerChannel,
        int channelCount,
        long totalDataCount)
    {
        if (dataCountPerChannel <= 0 || channelCount <= 0 || payloadFloatCount <= 0)
        {
            return false;
        }

        int deviceId = SdkDeviceIdResolver.ResolveDeviceId(
            groupId: groupId,
            machineId: machineId);
        int previewSampleCount = CalculateRealtimePreviewSampleCount(dataCountPerChannel);
        double sampleRate = Math.Max(1.0, _sampleRate);
        double sampleIntervalSeconds = dataCountPerChannel <= 1 || previewSampleCount <= 1
            ? 1.0 / sampleRate
            : (dataCountPerChannel - 1) / (double)(previewSampleCount - 1) / sampleRate;
        var channels = new SdkRealtimePreviewChannel[channelCount];

        for (int ch = 0; ch < channelCount; ch++)
        {
            int channelId = deviceId * 100 + (ch + 1);
            var samples = new float[previewSampleCount];

            for (int previewIndex = 0; previewIndex < previewSampleCount; previewIndex++)
            {
                int sourceSampleIndex = previewSampleCount <= 1
                    ? 0
                    : (int)Math.Round(previewIndex * (dataCountPerChannel - 1) / (double)(previewSampleCount - 1));
                int sourceIndex = (sourceSampleIndex * channelCount) + ch;
                if ((uint)sourceIndex < (uint)payloadFloatCount)
                {
                    samples[previewIndex] = source[sourceIndex];
                }
            }

            channels[ch] = new SdkRealtimePreviewChannel(channelId, samples);
        }

        var block = new SdkRealtimePreviewBlock(
            channels,
            totalDataCount,
            sampleIntervalSeconds);

        if (!_realtimePreviewQueue.Writer.TryWrite(block))
        {
            RecordRealtimePreviewDrop();
            return false;
        }

        Interlocked.Increment(ref _realtimePreviewQueueDepth);
        Interlocked.Increment(ref _realtimePreviewAcceptedBlocks);
        return true;
    }

    private static int CalculateRealtimePreviewSampleCount(int dataCountPerChannel)
    {
        if (dataCountPerChannel <= 0)
        {
            return 0;
        }

        if (dataCountPerChannel <= MinChunkSize)
        {
            return dataCountPerChannel;
        }

        return Math.Min(dataCountPerChannel, MaxRealtimePreviewSamplesPerChannel);
    }

    private void RecordRealtimePreviewDrop()
    {
        long dropped = Interlocked.Increment(ref _realtimePreviewDroppedBlocks);
        if (dropped == 1 || dropped % 1024 == 0)
        {
            Console.WriteLine($"[SdkRealtimePreview] 预览队列已丢弃 {dropped:N0} 个中间块，raw 写盘不受影响。");
        }
    }

    private async Task ProcessRealtimePreviewQueueAsync()
    {
        try
        {
            while (await _realtimePreviewQueue.Reader.WaitToReadAsync(_realtimePreviewCts.Token))
            {
                while (_realtimePreviewQueue.Reader.TryRead(out var block))
                {
                    Interlocked.Decrement(ref _realtimePreviewQueueDepth);

                    try
                    {
                        PublishRealtimePreviewBlock(block);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SdkRealtimePreview] 发布预览块失败: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkRealtimePreview] 后台预览队列异常: {ex.Message}");
        }
    }

    private void PublishRealtimePreviewBlock(SdkRealtimePreviewBlock block)
    {
        if (!_realtimePublishEnabled)
        {
            return;
        }

        foreach (var channel in block.Channels)
        {
            PublishChunk(
                channel.ChannelId,
                channel.Samples,
                block.TotalDataCount,
                block.SampleIntervalSeconds,
                "SdkDataProcessorPreview");
        }
    }

    /// <summary>
    /// 发布通道数据到DataBus
    /// </summary>
    private void PublishChannelData(int channelId, float[] samples, long totalCount)
    {
        // 确保通道存在
        _streamTable.EnsureChannel(channelId, DH.Contracts.ChannelNaming.ChannelName(channelId));
        _dataBus.EnsureChannel(channelId);
        
        // 获取或创建通道缓冲区
        var buffer = _channelBuffers.GetOrAdd(channelId, _ => new ConcurrentQueue<float>());
        
        // 添加数据到缓冲区
        int offset = 0;
        if (buffer.IsEmpty)
        {
            if (samples.Length >= _chunkSize)
            {
                while (offset + _chunkSize <= samples.Length)
                {
                    var directChunk = new float[_chunkSize];
                    Array.Copy(samples, offset, directChunk, 0, _chunkSize);
                    PublishChunk(channelId, directChunk, totalCount + offset);
                    offset += _chunkSize;
                }
            }
            else if (samples.Length >= MinChunkSize)
            {
                var directChunk = new float[samples.Length];
                Array.Copy(samples, directChunk, samples.Length);
                PublishChunk(channelId, directChunk, totalCount);

                if (_firstDataLogged.TryAdd(channelId, true))
                {
                    Console.WriteLine($"[SDK数据] 通道{channelId}直接发布回调块, 样本数={samples.Length}");
                }

                return;
            }
        }

        for (int i = offset; i < samples.Length; i++)
        {
            buffer.Enqueue(samples[i]);
        }
        
        // 记录首次数据到达的日志
        if (_firstDataLogged.TryAdd(channelId, true))
        {
            Console.WriteLine($"[SDK数据] 通道{channelId}首次收到数据，样本数={samples.Length}, 缓冲区大小={buffer.Count}");
        }
        
        // 达到批次大小时发布
        while (buffer.Count >= _chunkSize)
        {
            var chunk = new float[_chunkSize];
            for (int i = 0; i < _chunkSize; i++)
            {
                if (!buffer.TryDequeue(out chunk[i]))
                    break;
            }
            
            // 创建数据帧
            
            
            // 异步发布
            PublishChunk(channelId, chunk, null);
            
            // 记录发布日志（仅首次）
            if (_firstPublishLogged.TryAdd(channelId, true))
            {
                Console.WriteLine($"[SDK发布] 通道{channelId}首次发布数据帧，采样率={_sampleRate}Hz, chunk大小={_chunkSize}");
            }
        }
    }

    /// <summary>
    /// 检查数据活动状态
    /// </summary>
    private void PublishChunk(
        int channelId,
        float[] chunk,
        long? startSampleIndex,
        double? sampleIntervalSeconds = null,
        string producerTag = "SdkDataProcessor")
    {
        var frame = new SimpleFrame
        {
            ChannelId = channelId,
            FrameId = Interlocked.Increment(ref _frameSequence),
            Timestamp = DateTime.UtcNow,
            Samples = chunk,
            Header = new FrameHeader
            {
                SampleRate = (int)_sampleRate,
                StartSampleIndex = startSampleIndex,
                SampleIntervalSeconds = sampleIntervalSeconds,
                ProducerTag = producerTag
            }
        };

        _ = _streamTable.PublishAsync(frame, CancellationToken.None);

        if (_firstPublishLogged.TryAdd(channelId, true))
        {
            Console.WriteLine($"[SDK发布] 通道{channelId}首次发布数据帧，采样率={_sampleRate}Hz, chunk大小={_chunkSize}");
        }
    }

    private void FlushBufferedChannels()
    {
        foreach (var kvp in _channelBuffers)
        {
            int count = kvp.Value.Count;
            if (count <= 0)
            {
                continue;
            }

            var remaining = new float[count];
            int actualCount = 0;
            while (actualCount < remaining.Length && kvp.Value.TryDequeue(out var sample))
            {
                remaining[actualCount++] = sample;
            }

            if (actualCount <= 0)
            {
                continue;
            }

            if (actualCount != remaining.Length)
            {
                Array.Resize(ref remaining, actualCount);
            }

            PublishChunk(kvp.Key, remaining, null);
        }
    }

    private void DrainRealtimePreviewQueue()
    {
        while (_realtimePreviewQueue.Reader.TryRead(out var block))
        {
            Interlocked.Decrement(ref _realtimePreviewQueueDepth);
        }
    }

    private void ClearBufferedChannels()
    {
        foreach (var kvp in _channelBuffers)
        {
            while (kvp.Value.TryDequeue(out _))
            {
            }
        }
    }

    private void UpdateObservedDeviceMapping(int machineId, int channelDeviceId)
    {
        //屏蔽在回调期间强制篡改ChannelDeviceId的逻辑
        //因为在多台模拟仪器环境下，可能多个仪器的GroupId / MachineId 返回重复或跳号
        /*
        if (channelDeviceId < 0)
        {
            return;
        }

        int canonicalDeviceId = SdkDeviceIdResolver.ResolveDeviceId(
            groupId: channelDeviceId,
            machineId: machineId);

        lock (_deviceInfoList)
        {
            var device = _deviceInfoList.Find(d => d.MachineId == machineId);
            if (device != null)
            {
                device.ChannelDeviceId = canonicalDeviceId;
                return;
            }

            if (canonicalDeviceId >= 0)
            {
                int index = canonicalDeviceId;
                if (index >= 0 && index < _deviceInfoList.Count)
                {
                    _deviceInfoList[index].ChannelDeviceId = canonicalDeviceId;
                }
            }
        }
        */
    }

    private void RefreshIngestBatchSettings()
    {
        int channelCount = Math.Max(1, _totalChannelCount);
        int samplesByBytes = Math.Max(MinChunkSize, TargetCallbackBytes / Math.Max(1, channelCount * sizeof(float)));
        int normalized = NormalizePowerOfTwo(samplesByBytes);
        _sdkCallbackDataCount = Math.Clamp(normalized, MinChunkSize, MaxChunkSize);
        _chunkSize = _sdkCallbackDataCount;
    }

    private void RecordCallbackFlow(
        int messageType,
        int groupId,
        int machineId,
        int dataCountPerChannel,
        int bufferCountBytes)
    {
        string key = $"{groupId}:{machineId}";
        var stats = _callbackFlowStats.GetOrAdd(key, static _ => new CallbackFlowStats());
        DateTime nowUtc = DateTime.UtcNow;
        int floatCount = bufferCountBytes > 0 ? bufferCountBytes / sizeof(float) : 0;
        int channelCount = dataCountPerChannel > 0 ? Math.Max(1, floatCount / dataCountPerChannel) : 0;

        lock (stats.SyncRoot)
        {
            if (stats.WindowStartUtc == DateTime.MinValue)
            {
                stats.WindowStartUtc = nowUtc;
            }

            stats.CallbackCount++;
            stats.CallbackSamplesPerChannel += Math.Max(0, dataCountPerChannel);

            double wallSeconds = Math.Max(0.0, (nowUtc - stats.WindowStartUtc).TotalSeconds);
            if (wallSeconds >= 1.0)
            {
                double callbacksPerSecond = stats.CallbackCount / Math.Max(1e-9, wallSeconds);
                double effectiveSamplesPerSecondPerChannel = stats.CallbackSamplesPerChannel / Math.Max(1e-9, wallSeconds);
                SdkCallbackFlowFileLogger.WriteLine(
                    $"messageType={messageType}",
                    $"groupId={groupId}",
                    $"machineId={machineId}",
                    $"channelCount={channelCount}",
                    $"dataCountPerChannel={dataCountPerChannel}",
                    $"bufferCountBytes={bufferCountBytes}",
                    $"configuredCallbackSamples={_sdkCallbackDataCount}",
                    $"configuredChunkSize={_chunkSize}",
                    $"realtimePublishEnabled={_realtimePublishEnabled}",
                    $"previewQueueDepth={Volatile.Read(ref _realtimePreviewQueueDepth)}",
                    $"previewAcceptedBlocks={Interlocked.Read(ref _realtimePreviewAcceptedBlocks)}",
                    $"previewDroppedBlocks={Interlocked.Read(ref _realtimePreviewDroppedBlocks)}",
                    $"sampleRateHz={_sampleRate:F3}",
                    $"summaryWallSeconds={wallSeconds:F3}",
                    $"callbackCount={stats.CallbackCount}",
                    $"callbackSamplesPerChannel={stats.CallbackSamplesPerChannel}",
                    $"callbacksPerSecond={callbacksPerSecond:F3}",
                    $"effectiveSamplesPerSecondPerChannel={effectiveSamplesPerSecondPerChannel:F3}");

                stats.WindowStartUtc = nowUtc;
                stats.CallbackCount = 0;
                stats.CallbackSamplesPerChannel = 0;
            }
        }
    }

    private sealed record SdkRealtimePreviewChannel(
        int ChannelId,
        float[] Samples);

    private sealed record SdkRealtimePreviewBlock(
        SdkRealtimePreviewChannel[] Channels,
        long TotalDataCount,
        double SampleIntervalSeconds);

    private sealed class CallbackFlowStats
    {
        public object SyncRoot { get; } = new();
        public DateTime WindowStartUtc { get; set; }
        public long CallbackCount { get; set; }
        public long CallbackSamplesPerChannel { get; set; }
    }

    private static int NormalizePowerOfTwo(int value)
    {
        int result = 1;
        while (result < value && result < MaxChunkSize)
        {
            result <<= 1;
        }

        return result;
    }

    private void CheckActivity(object? state)
    {
        if (_isActive && (DateTime.UtcNow - _lastDataTime).TotalMilliseconds > 500)
        {
            _isActive = false;
            DataActivityChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// 更新状态
    /// </summary>
    private void UpdateStatus(bool isConnected, string message)
    {
        Console.WriteLine($"[SDK] {message}");
        _statusCallback?.Invoke(isConnected, message);
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        try
        {
            _activityTimer?.Dispose();
            _activityTimer = null;
            _realtimePreviewCts.Cancel();
            _realtimePreviewQueue.Writer.TryComplete();
            DrainRealtimePreviewQueue();
            
            if (_isSampling)
            {
                StopSampling();
            }
            
            if (_isInitialized)
            {
                HardwareSDK.QuitMacControl();
                _isInitialized = false;
            }
            
            _callbackDelegate = null;
            _channelBuffers.Clear();
            _firstDataLogged.Clear();
            _firstPublishLogged.Clear();
            
            // 释放SDK锁
            SdkGlobalLock.Release(SDK_OWNER);
            
            StatusChanged?.Invoke(this, false);
            UpdateStatus(false, "SDK已释放");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SdkDataProcessor] 释放资源异常: {ex.Message}");
        }
    }
}
