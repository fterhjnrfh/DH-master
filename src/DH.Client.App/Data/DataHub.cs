using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts.Models;

namespace DH.Client.App.Data
{
    public class DataHub
    {
        private readonly DataBus _dataBus;
        private readonly ConcurrentDictionary<int, List<Action<IReadOnlyList<CurvePoint>>>> _subscribers = new();
        private readonly ConcurrentDictionary<int, bool> _activeChannels = new();
        private CancellationTokenSource _cts;
        private Task _processingTask;
        private bool _isRunning;

        // 公共属性访问DataBus
        public DataBus DataBus => _dataBus;

        public DataHub(DataBus dataBus)
        {
            _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
            
            // 监听数据总线上的通道变化
            _dataBus.ChannelAdded += OnChannelAdded;
            _dataBus.ChannelRemoved += OnChannelRemoved;
            _dataBus.DataUpdated += OnDataUpdated;
        }

        public void Start()
        {
            if (_isRunning)
                return;
                
            _cts = new CancellationTokenSource();
            _processingTask = Task.Run(ProcessingLoop, _cts.Token);
            _isRunning = true;
        }

        public void Stop()
        {
            if (!_isRunning)
                return;
                
            _cts?.Cancel();
            _processingTask?.Wait(1000);
            _isRunning = false;
        }

        // 订阅特定通道的数据
        public void Subscribe(int channelId, Action<IReadOnlyList<CurvePoint>> callback)
        {
            var subscribers = _subscribers.GetOrAdd(channelId, _ => new List<Action<IReadOnlyList<CurvePoint>>>());
            
            lock (subscribers)
            {
                subscribers.Add(callback);
            }
            
            // 激活通道
            _activeChannels[channelId] = true;
        }

        // 取消订阅
        public void Unsubscribe(int channelId, Action<IReadOnlyList<CurvePoint>> callback)
        {
            if (_subscribers.TryGetValue(channelId, out var subscribers))
            {
                lock (subscribers)
                {
                    subscribers.Remove(callback);
                    
                    // 如果没有订阅者，则停用通道
                    if (subscribers.Count == 0)
                    {
                        _activeChannels[channelId] = false;
                    }
                }
            }
        }

        // 获取所有活跃通道
        public IReadOnlyList<int> GetActiveChannels()
        {
            var activeChannels = new List<int>();
            
            foreach (var channel in _activeChannels)
            {
                if (channel.Value)
                {
                    activeChannels.Add(channel.Key);
                }
            }
            
            return activeChannels;
        }

        // 处理循环
        private async Task ProcessingLoop()
        {
            var token = _cts.Token;
            
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 处理所有活跃通道的数据
                    foreach (var channelId in GetActiveChannels())
                    {
                        // 获取最新数据
                        var data = _dataBus.GetLatestData(channelId);
                        
                        // 通知所有订阅者
                        NotifySubscribers(channelId, data);
                    }
                    
                    // 短暂休眠，避免CPU占用过高
                    await Task.Delay(10, token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不做处理
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DataHub处理异常: {ex.Message}");
            }
        }

        // 通知订阅者
        private void NotifySubscribers(int channelId, IReadOnlyList<CurvePoint> data)
        {
            if (_subscribers.TryGetValue(channelId, out var subscribers))
            {
                lock (subscribers)
                {
                    foreach (var callback in subscribers)
                    {
                        try
                        {
                            callback(data);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"通知订阅者异常: {ex.Message}");
                        }
                    }
                }
            }
        }

        // 通道添加事件处理
        private void OnChannelAdded(object sender, int channelId)
        {
            // 默认不激活
            _activeChannels[channelId] = false;
        }

        // 通道移除事件处理
        private void OnChannelRemoved(object sender, int channelId)
        {
            _activeChannels.TryRemove(channelId, out _);
            _subscribers.TryRemove(channelId, out _);
        }

        // 数据更新事件处理
        private void OnDataUpdated(object sender, DataUpdateEventArgs e)
        {
            // 如果通道活跃，则通知订阅者
            if (_activeChannels.TryGetValue(e.ChannelId, out var isActive) && isActive)
            {
                NotifySubscribers(e.ChannelId, e.Data);
            }
        }
    }
}