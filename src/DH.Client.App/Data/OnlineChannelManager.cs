using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace DH.Client.App.Data
{
    /// <summary>
    /// 在线通道管理器 - 控制哪些通道在线并生成数据
    /// </summary>
    public class OnlineChannelManager : INotifyPropertyChanged
    {
        private readonly HashSet<int> _onlineChannels;
        private readonly object _lock = new object();
        
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<OnlineChannelsChangedEventArgs> OnlineChannelsChanged;
        
        public OnlineChannelManager()
        {
            _onlineChannels = new HashSet<int>();
            // 默认不启用任何通道，等待用户手动设置
        }

        /// <summary>
        /// 获取所有在线通道
        /// </summary>
        public int[] GetOnlineChannels()
        {
            lock (_lock)
            {
                return _onlineChannels.ToArray();
            }
        }
        
        /// <summary>
        /// 检查通道是否在线
        /// </summary>
        public bool IsChannelOnline(int channelId)
        {
            lock (_lock)
            {
                return _onlineChannels.Contains(channelId);
            }
        }

        /// <summary>
        /// 设置通道在线状态
        /// </summary>
        public void SetChannelOnline(int channelId, bool isOnline)
        {
            bool changed = false;
            lock (_lock)
            {
                if (isOnline)
                {
                    changed = _onlineChannels.Add(channelId);
                }
                else
                {
                    changed = _onlineChannels.Remove(channelId);
                }
            }
            
            if (changed)
            {
                OnPropertyChanged();
                OnlineChannelsChanged?.Invoke(this, new OnlineChannelsChangedEventArgs(GetOnlineChannels()));
            }
        }

        /// <summary>
        /// 批量设置在线通道
        /// </summary>
        public void SetOnlineChannels(int[] channelIds)
        {
            lock (_lock)
            {
                _onlineChannels.Clear();
                if (channelIds != null)
                {
                    foreach (var id in channelIds)
                    {
                        _onlineChannels.Add(id);
                    }
                }
            }
            
            OnPropertyChanged();
            OnlineChannelsChanged?.Invoke(this, new OnlineChannelsChangedEventArgs(GetOnlineChannels()));
        }

        /// <summary>
        /// 获取在线通道数量
        /// </summary>
        public int OnlineChannelCount
        {
            get
            {
                lock (_lock)
                {
                    return _onlineChannels.Count;
                }
            }
        }
        
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
    
    /// <summary>
    /// 在线通道变更事件参数
    /// </summary>
    public class OnlineChannelsChangedEventArgs : EventArgs
    {
        public int[] OnlineChannels { get; }
        
        public OnlineChannelsChangedEventArgs(int[] onlineChannels)
        {
            OnlineChannels = onlineChannels;
        }
    }
}
