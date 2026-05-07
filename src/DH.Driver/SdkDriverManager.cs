// DH.Driver/SdkDriverManager.cs
// SDK驱动管理器 - 提供与TcpDriverManager类似的接口
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DH.Contracts.Abstractions;
using DH.Datamanage.Realtime;
using DH.Driver.SDK;

namespace DH.Driver;

/// <summary>
/// SDK驱动管理器
/// 管理SDK的初始化、采样控制等功能
/// 提供与TcpDriverManager类似的接口，便于UI层统一调用
/// </summary>
public class SdkDriverManager : INotifyPropertyChanged, IDisposable
{
    private readonly SdkDataProcessor _dataProcessor;
    
    public SdkDriverManager(IDataBus dataBus, StreamTable streamTable, Action<bool, string> statusCallback)
    {
        _dataProcessor = new SdkDataProcessor(dataBus, streamTable, statusCallback);
        _dataProcessor.StatusChanged += (s, connected) => OnPropertyChanged(nameof(IsInitialized));
        _dataProcessor.DataActivityChanged += (s, active) => 
        {
            IsDataActive = active;
            DataActivityChanged?.Invoke(active);
        };
    }

    #region 属性

    /// <summary>
    /// SDK是否已初始化
    /// </summary>
    public bool IsInitialized => _dataProcessor.IsInitialized;

    /// <summary>
    /// 是否正在采样
    /// </summary>
    public bool IsSampling => _dataProcessor.IsSampling;

    /// <summary>
    /// 当前采样率
    /// </summary>
    public float SampleRate => _dataProcessor.SampleRate;

    /// <summary>
    /// 是否启用实时发布到结果显示链路
    /// </summary>
    public bool RealtimePublishEnabled => _dataProcessor.RealtimePublishEnabled;
    
    /// <summary>
    /// 在线设备数量
    /// </summary>
    public int OnlineDeviceCount => _dataProcessor.OnlineDeviceCount;
    
    /// <summary>
    /// 总通道数量
    /// </summary>
    public int TotalChannelCount => _dataProcessor.TotalChannelCount;
    
    /// <summary>
    /// 获取设备信息列表
    /// </summary>
    public IReadOnlyList<SdkDeviceInfo> DeviceInfoList => _dataProcessor.DeviceInfoList;

    private string _connectionStatus = "SDK未初始化";
    /// <summary>
    /// 连接状态描述
    /// </summary>
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    private bool _isDataActive;
    /// <summary>
    /// 数据是否活跃（正在接收数据）
    /// </summary>
    public bool IsDataActive
    {
        get => _isDataActive;
        private set => SetField(ref _isDataActive, value);
    }

    #endregion

    #region 事件

    /// <summary>
    /// 数据活动状态变化事件
    /// </summary>
    public event Action<bool>? DataActivityChanged;

    /// <summary>
    /// SDK原始块旁路事件
    /// </summary>
    public event Action<SdkRawBlock>? RawBlockReceived
    {
        add => _dataProcessor.RawBlockReceived += value;
        remove => _dataProcessor.RawBlockReceived -= value;
    }

    #endregion

    #region 方法

    /// <summary>
    /// 初始化SDK
    /// </summary>
    /// <param name="configPath">配置文件夹路径（包含Config文件夹的目录）</param>
    /// <returns>是否成功</returns>
    public bool Initialize(string configPath)
    {
        ConnectionStatus = "正在初始化SDK...";
        
        bool result = _dataProcessor.Initialize(configPath);
        
        if (result)
        {
            ConnectionStatus = $"SDK已初始化，采样率: {SampleRate}Hz";
        }
        else
        {
            ConnectionStatus = "SDK初始化失败";
        }
        
        OnPropertyChanged(nameof(IsInitialized));
        return result;
    }

    /// <summary>
    /// 启动采样
    /// </summary>
    /// <returns>是否成功</returns>
    public bool StartSampling()
    {
        if (!IsInitialized)
        {
            ConnectionStatus = "请先初始化SDK";
            return false;
        }
        
        bool result = _dataProcessor.StartSampling();
        
        if (result)
        {
            ConnectionStatus = "SDK采样中...";
        }
        else
        {
            ConnectionStatus = "启动采样失败";
        }
        
        OnPropertyChanged(nameof(IsSampling));
        return result;
    }

    /// <summary>
    /// 停止采样
    /// </summary>
    public void StopSampling()
    {
        _dataProcessor.StopSampling();
        ConnectionStatus = "SDK采样已停止";
        OnPropertyChanged(nameof(IsSampling));
    }

    /// <summary>
    /// 控制是否向实时显示链路发布拆包后的通道数据
    /// </summary>
    public void SetRealtimePublishEnabled(bool enabled)
    {
        _dataProcessor.SetRealtimePublishEnabled(enabled);
        OnPropertyChanged(nameof(RealtimePublishEnabled));
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        _dataProcessor?.Dispose();
        ConnectionStatus = "SDK已释放";
    }

    #endregion

    #region INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    #endregion
}
