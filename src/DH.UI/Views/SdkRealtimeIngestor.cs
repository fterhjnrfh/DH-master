// DH.UI/Views/SdkRealtimeIngestor.cs
// SDK实时数据接收器 - 与TcpRealtimeIngestor接口兼容
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DH.Driver.SDK;
using NewAvalonia.Views;

namespace DH.UI.Views;

/// <summary>
/// SDK实时数据接收器
/// 用于接收东华硬件SDK回调数据，并以与TCP相同的接口提供给波形显示控件
/// </summary>
internal sealed class SdkRealtimeIngestor : IAsyncDisposable
{
    #region P/Invoke 定义
    
    private const string DllName = "Hardware_Standard_C_Interface.dll";
    
    // 回调委托 - 必须使用Cdecl
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public delegate void SampleDataChangeEventHandle(
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
        long varSampleData);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int InitMacControl(string path);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int SetDataChangeCallBackFun(SampleDataChangeEventHandle callback);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern bool RefindAndConnecMac();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetAllMacOnlineCount();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetMacInfoFromIndex(int nIndex, out int pMacID, IntPtr strMacIp, int nMacBuffer, out int nUseBuffer);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetMacCurrentChnCount(int nMachineID, [MarshalAs(UnmanagedType.LPStr)] string strMacIp);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int GetChannelIDFromAllChannelIndex(int nMachineID, [MarshalAs(UnmanagedType.LPStr)] string pMacIp, int nIndex, out int nMacChnId, out int bOnLine);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern float GetMacCurrentSampleFreq();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int ChangeGetDataStatus(bool singleMachine);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void SetGetDataCountEveryTime(int count);
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void StartMacSample();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void StopMacSample();
    
    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern void QuitMacControl();
    
    // Windows API 用于设置DLL搜索路径
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);
    
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddDllDirectory(string NewDirectory);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint DirectoryFlags);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetLastError();
    
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    
    // 消息类型常量
    private const int SAMPLE_ANALOG_DATA = 0x81;
    private const int SAMPLE_ANALOG_MULTICHN_DATA = 0x82;
    private const int SAMPLE_SINGLEGROUP_ANALOGDATA = 0x83;
    
    #endregion
    
    private readonly string _configPath;
    private CancellationTokenSource? _cts;
    private SampleDataChangeEventHandle? _callbackDelegate;
    
    private bool _isInitialized;
    private bool _isSampling;
    private float _sampleRate = 1000f;
    
    // 数据缓存
    private readonly ConcurrentDictionary<int, ConcurrentQueue<float>> _channelBuffers = new();
    private Timer? _dispatchTimer;
    private readonly object _lock = new();
    
    // 事件 - 与TcpRealtimeIngestor相同的接口
    public event EventHandler<TcpSamplesEventArgs>? SamplesReceived;
    public event EventHandler<TcpConnectionStatusEventArgs>? ConnectionStatusChanged;
    
    private const string SDK_OWNER = "DH.UI.AlgorithmConfig";
    
    public SdkRealtimeIngestor(string configPath)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
    }
    
    public Task StartAsync(CancellationToken token = default)
    {
        // 检查SDK锁 - 如果主界面已连接SDK，则不能在这里连接
        if (DH.Driver.SDK.SdkGlobalLock.IsLocked)
        {
            var owner = DH.Driver.SDK.SdkGlobalLock.CurrentOwner;
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"SDK已被 '{owner}' 占用，请先在主界面断开SDK连接"));
            return Task.CompletedTask;
        }
        
        // 尝试获取SDK锁
        if (!DH.Driver.SDK.SdkGlobalLock.TryAcquire(SDK_OWNER))
        {
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "无法获取SDK锁"));
            return Task.CompletedTask;
        }
        
        if (_isInitialized) return Task.CompletedTask;
        
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        
        // 简化版初始化流程 - 对齐 Demo_C# 逻辑
        try
        {
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "正在连接SDK..."));
            
            // 1. 准备路径 (确保以 \ 结尾)
            string path = _configPath.Trim();
            if (System.IO.File.Exists(path) || path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
            {
                path = System.IO.Path.GetDirectoryName(path) ?? path;
            }
            if (!path.EndsWith("\\") && !path.EndsWith("/"))
            {
                path += "\\";
            }
            
            // 2. 设置 DLL 目录 (防止加载不到依赖项)
            string exeDir = AppContext.BaseDirectory;
            SetDllDirectory(exeDir);
            SdkNativeLoader.EnsureLoaded(path);
            
            // 3. 初始化控制 (InitMacControl)
            // 注意: 返回值<0 表示失败。如果这里返回乱码负数，通常是DLL版本不匹配或内存问题，但我们先按Demo处理
            int initResult = InitMacControl(path);
            if (initResult < 0)
            {
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"SDK初始化失败，代码: {initResult}"));
                return Task.CompletedTask;
            }
            
            // 4. 注册回调 (SetDataChangeCallBackFun)
            _callbackDelegate = OnSampleDataReceived;
            int cbResult = SetDataChangeCallBackFun(_callbackDelegate);
            if (cbResult < 0)
            {
                ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"注册回调失败，代码: {cbResult}"));
                return Task.CompletedTask;
            }
            
            // 5. 连接设备 (RefindAndConnecMac)
            RefindAndConnecMac();
            int deviceCount = GetAllMacOnlineCount();
            
            // 6. 获取采样率
            _sampleRate = GetMacCurrentSampleFreq();
            if (_sampleRate <= 0) _sampleRate = 1000f;
            
            // 7. 启动采样
            ChangeGetDataStatus(true); // 对齐Demo默认: 单机模式
            SetGetDataCountEveryTime(128); // 设定回调块大小
            StartMacSample();
            
            // 8. 启动定时器分发数据
            _isInitialized = true;
            _isSampling = true;
            _dispatchTimer = new Timer(DispatchBufferedData, null, 10, 10);
            
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(true, $"SDK运行中: 设备{deviceCount}台, 采样率{_sampleRate}Hz"));
            
        }
        catch (DllNotFoundException ex)
        {
            DH.Driver.SDK.SdkGlobalLock.Release(SDK_OWNER);
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"DLL缺失: {ex.Message}"));
        }
        catch (Exception ex)
        {
            DH.Driver.SDK.SdkGlobalLock.Release(SDK_OWNER);
            ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, $"异常: {ex.Message}"));
        }
        
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// SDK数据回调
    /// 数据格式：交织格式(interleaved) - [Ch0_S0, Ch1_S0, ... ChN_S0, Ch0_S1, Ch1_S1, ... ChN_S1, ...]
    /// nGroupID: 设备/组ID (用于区分不同设备)
    /// nChannelStyle: 通道数量（在单机模式下）
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
            if (nDataCountPerChannel <= 0 || nBufferCount <= 0)
            {
                return;
            }
            
            // 读取原始数据
            byte[] rawData = new byte[nBufferCount];
            Marshal.Copy((IntPtr)varSampleData, rawData, 0, nBufferCount);
            
            // 计算通道数: bufferCount = channelCount * nDataCountPerChannel * sizeof(float)
            int floatCount = nBufferCount / sizeof(float);
            int channelCount = floatCount / nDataCountPerChannel;
            if (channelCount <= 0) channelCount = 1;
            
            // 转换为float数组
            float[] allData = new float[floatCount];
            Buffer.BlockCopy(rawData, 0, allData, 0, nBufferCount);
            
            // 使用 nGroupID 作为设备标识符（每台模拟器有唯一的GroupID）
            // 通道ID格式: GroupID * 100 + ChannelIndex (1-based)
            // 例如: GroupID=1 的设备通道为 101-116, GroupID=2 为 201-216
            int deviceId = nGroupID;
            
            // 数据是交织格式: [Ch0_S0, Ch1_S0, Ch2_S0... Ch0_S1, Ch1_S1, Ch2_S1...]
            lock (_lock)
            {
                for (int ch = 0; ch < channelCount; ch++)
                {
                    // 生成唯一通道ID: 设备ID * 100 + 通道号(1-based)
                    int channelId = deviceId * 100 + (ch + 1);
                    var buffer = _channelBuffers.GetOrAdd(channelId, _ => new ConcurrentQueue<float>());
                    
                    // 交织格式解析: 第i个采样点的第ch个通道数据在 allData[i * channelCount + ch]
                    for (int i = 0; i < nDataCountPerChannel; i++)
                    {
                        int idx = i * channelCount + ch;
                        if (idx < allData.Length)
                        {
                            buffer.Enqueue(allData[idx]);
                        }
                    }
                }
            }
            
            // 调试日志（仅首次或每100次打印）
            if (_callbackCount % 100 == 0)
            {
                Console.WriteLine($"[SDK回调] type=0x{nMessageType:X2}, groupID={nGroupID}, channels={channelCount}, samples={nDataCountPerChannel}, 总通道数={_channelBuffers.Count}");
            }
            _callbackCount++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SdkIngestor] 回调异常: {ex.Message}");
        }
    }
    
    private long _callbackCount = 0;
    
    /// <summary>
    /// 分发缓冲数据到UI
    /// 通道ID格式: deviceId * 100 + channelIndex
    /// 例如: 101 = 设备1的通道1, 215 = 设备2的通道15
    /// </summary>
    private void DispatchBufferedData(object? state)
    {
        const int chunkSize = 128;
        
        lock (_lock)
        {
            foreach (var kvp in _channelBuffers)
            {
                int channelId = kvp.Key;
                var buffer = kvp.Value;
                
                while (buffer.Count >= chunkSize)
                {
                    var samples = new float[chunkSize];
                    for (int i = 0; i < chunkSize; i++)
                    {
                        if (!buffer.TryDequeue(out samples[i]))
                            break;
                    }
                    
                    // 计算采样间隔
                    double intervalMs = 1000.0 / _sampleRate;
                    double bucketMs = intervalMs * chunkSize;
                    
                    // 解析设备ID和通道号: channelId = deviceId * 100 + chIndex
                    int deviceId = channelId / 100;
                    int chIndex = channelId % 100;
                    
                    // 使用统一命名规则: AI{设备号:D2}_CH{通道号:D2}
                    string key = $"sdk-dev{deviceId}-ch{chIndex}";
                    string displayName = DH.Contracts.ChannelNaming.ChannelName(deviceId, chIndex);
                    
                    SamplesReceived?.Invoke(this, new TcpSamplesEventArgs(
                        key,
                        displayName,
                        samples,
                        TimeSpan.FromMilliseconds(bucketMs),
                        intervalMs,
                        DateTimeOffset.UtcNow
                    ));
                }
            }
        }
    }
    
    public async Task StopAsync()
    {
        try
        {
            _dispatchTimer?.Dispose();
            _dispatchTimer = null;
            
            if (_isSampling)
            {
                StopMacSample();
                _isSampling = false;
            }
            
            if (_cts != null)
            {
                _cts.Cancel();
                _cts = null;
            }
        }
        catch { }
        
        ConnectionStatusChanged?.Invoke(this, new TcpConnectionStatusEventArgs(false, "SDK已断开"));
        await Task.CompletedTask;
    }
    
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        
        try
        {
            if (_isInitialized)
            {
                QuitMacControl();
                _isInitialized = false;
            }
            
            _callbackDelegate = null;
            _channelBuffers.Clear();
            
            // 释放SDK锁
            DH.Driver.SDK.SdkGlobalLock.Release(SDK_OWNER);
        }
        catch { }
    }
}
