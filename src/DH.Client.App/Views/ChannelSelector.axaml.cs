using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DH.Client.App.Data;

namespace DH.Client.App.Views
{
    public partial class ChannelSelector : UserControl
    {
        private ObservableCollection<ChannelItem> _allChannels;
        private ObservableCollection<ChannelItem> _filteredChannels;
        private Data.DataBus? _dataBus;
        private OnlineChannelManager _onlineChannelManager;
        private bool _handlersHooked;
        private bool _isSingleSelectMode = true; // 默认单选模式
        private bool _suppressSelectionChanged;
        private RadioButton? _singleModeRadio;
        private RadioButton? _multiModeRadio;

        public ChannelSelector()
        {
            InitializeComponent();
            InitializeChannels();
            SetupEventHandlers();
        }
        
        // 设置在线通道管理器
        public void SetOnlineChannelManager(OnlineChannelManager onlineChannelManager)
        {
            if (_onlineChannelManager != null)
            {
                _onlineChannelManager.OnlineChannelsChanged -= OnOnlineChannelsChanged;
            }
            
            _onlineChannelManager = onlineChannelManager;
            
            if (_onlineChannelManager != null)
            {
                _onlineChannelManager.OnlineChannelsChanged += OnOnlineChannelsChanged;
                
                // 初始化：根据在线通道创建通道项
                var onlineChannels = _onlineChannelManager.GetOnlineChannels();
                RefreshChannelList(onlineChannels);
                
                UpdateOnlineStatusText();
                SetupEventHandlers();
            }
        }
        
        /// <summary>
        /// 根据在线通道刷新通道列表
        /// </summary>
        private void RefreshChannelList(int[] onlineChannels)
        {
            // 清空现有通道
            foreach (var ch in _allChannels)
            {
                ch.PropertyChanged -= OnChannelItemPropertyChanged;
            }
            _allChannels.Clear();
            _filteredChannels.Clear();
            
            // 添加在线通道
            foreach (var channelId in onlineChannels.OrderBy(id => id))
            {
                var ci = new ChannelItem(channelId);
                ci.IsOnline = true;
                ci.IsSelected = false; // 默认不选中
                ci.PropertyChanged += OnChannelItemPropertyChanged;
                _allChannels.Add(ci);
                _filteredChannels.Add(ci);
            }
            
            UpdateOnlineStatusText();
            UpdateStatusText();
            
            Console.WriteLine($"[ChannelSelector] 刷新通道列表，共 {_allChannels.Count} 个通道");
        }

        // 选中通道变化事件
        public event EventHandler<SelectedChannelsChangedEventArgs>? SelectedChannelsChanged;

        // 获取选中的通道ID列表
        public List<int> GetSelectedChannels()
        {
            return _allChannels.Where(c => c.IsSelected).Select(c => c.ChannelId).ToList();
        }

        // 设置选中的通道
        public void SetSelectedChannels(IEnumerable<int> channelIds)
        {
            var selectedSet = new HashSet<int>(channelIds);

            _suppressSelectionChanged = true;
            try
            {
                foreach (var channel in _allChannels)
                {
                    channel.IsSelected = selectedSet.Contains(channel.ChannelId);
                }
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            UpdateStatusText();
            OnSelectedChannelsChanged();
        }

        public void AttachDataBus(Data.DataBus dataBus)
        {
            _dataBus = dataBus;
            _dataBus.ChannelAdded += OnChannelAdded;
            _dataBus.ChannelRemoved += OnChannelRemoved;
        }

        private void InitializeChannels()
        {
            _allChannels = new ObservableCollection<ChannelItem>();
            _filteredChannels = new ObservableCollection<ChannelItem>();
            ChannelList.ItemsSource = _filteredChannels;
            UpdateStatusText();
        }

        private void SetupEventHandlers()
        {
            if (_handlersHooked) return;
            _handlersHooked = true;
            
            // 视图通道选择按钮
            ViewSelectAllButton.Click += OnViewSelectAllClick;
            ViewClearAllButton.Click += OnViewClearAllClick;
            ViewInvertButton.Click += OnViewInvertClick;
            SearchBox.TextChanged += OnSearchTextChanged;
            
            // 模式切换
            _singleModeRadio = this.FindControl<RadioButton>("SingleModeRadio");
            _multiModeRadio = this.FindControl<RadioButton>("MultiModeRadio");
            
            if (_singleModeRadio != null)
            {
                _singleModeRadio.IsCheckedChanged += OnSelectModeChanged;
            }
            if (_multiModeRadio != null)
            {
                _multiModeRadio.IsCheckedChanged += OnSelectModeChanged;
            }
        }
        
        private void OnSelectModeChanged(object? sender, RoutedEventArgs e)
        {
            _isSingleSelectMode = _singleModeRadio?.IsChecked == true;
            Console.WriteLine($"[ChannelSelector] 选择模式: {(_isSingleSelectMode ? "单选" : "多选叠加")}");
            
            // 切换到单选模式时，如果已选多个，只保留第一个
            if (_isSingleSelectMode)
            {
                var selected = _allChannels.Where(c => c.IsSelected).ToList();
                if (selected.Count > 1)
                {
                    _suppressSelectionChanged = true;
                    try
                    {
                        // 只保留第一个选中的
                        foreach (var ch in selected.Skip(1))
                        {
                            ch.IsSelected = false;
                        }
                    }
                    finally
                    {
                        _suppressSelectionChanged = false;
                    }

                    UpdateStatusText();
                    OnSelectedChannelsChanged();
                }
            }
        }

        private void OnChannelItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChannelItem.IsSelected))
            {
                if (_suppressSelectionChanged)
                {
                    return;
                }

                var changedItem = sender as ChannelItem;
                
                // 单选模式下，选中一个通道时取消其他所有选中
                if (_isSingleSelectMode && changedItem != null && changedItem.IsSelected)
                {
                    _suppressSelectionChanged = true;
                    try
                    {
                        foreach (var ch in _allChannels.Where(c => c != changedItem && c.IsSelected))
                        {
                            ch.IsSelected = false;
                        }
                    }
                    finally
                    {
                        _suppressSelectionChanged = false;
                    }
                }
                
                UpdateStatusText();
                OnSelectedChannelsChanged();
            }
        }

        private void OnOnlineChannelsChanged(object? sender, OnlineChannelsChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // 刷新通道列表
                RefreshChannelList(e.OnlineChannels);
            });
        }
        
        private void OnChannelAdded(object? sender, int channelId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_allChannels.All(c => c.ChannelId != channelId))
                {
                    var ci = new ChannelItem(channelId);
                    ci.IsOnline = true;
                    ci.IsSelected = false;
                    ci.PropertyChanged += OnChannelItemPropertyChanged;
                    _allChannels.Add(ci);
                    _filteredChannels.Add(ci);
                }
                UpdateOnlineStatusText();
                UpdateStatusText();
            });
        }

        private void OnChannelRemoved(object? sender, int channelId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var ci = _allChannels.FirstOrDefault(c => c.ChannelId == channelId);
                if (ci != null)
                {
                    ci.PropertyChanged -= OnChannelItemPropertyChanged;
                    _allChannels.Remove(ci);
                    _filteredChannels.Remove(ci);
                }
                UpdateOnlineStatusText();
                UpdateStatusText();
            });
        }

        private void UpdateOnlineStatusText()
        {
            var onlineCount = _allChannels.Count(c => c.IsOnline);
            var total = _allChannels.Count;
            OnlineStatusText.Text = $"在线通道: {onlineCount}/{total}";
        }

        // 视图通道选择事件处理
        private void OnViewSelectAllClick(object? sender, RoutedEventArgs e)
        {
            if (_isSingleSelectMode)
            {
                // 单选模式下，全选只选择第一个在线通道
                var firstOnline = _filteredChannels.FirstOrDefault(c => c.IsOnline);
                foreach (var ch in _allChannels) ch.IsSelected = false;
                if (firstOnline != null) firstOnline.IsSelected = true;
            }
            else
            {
                // 多选模式下，全选所有在线通道
                foreach (var channel in _filteredChannels.Where(c => c.IsOnline))
                {
                    channel.IsSelected = true;
                }
            }
        }

        private void OnViewClearAllClick(object? sender, RoutedEventArgs e)
        {
            foreach (var channel in _filteredChannels)
            {
                channel.IsSelected = false;
            }
        }

        private void OnViewInvertClick(object? sender, RoutedEventArgs e)
        {
            if (_isSingleSelectMode)
            {
                // 单选模式下，反选选中下一个在线通道
                var currentSelected = _filteredChannels.FirstOrDefault(c => c.IsSelected && c.IsOnline);
                var onlineList = _filteredChannels.Where(c => c.IsOnline).ToList();
                
                if (onlineList.Count == 0) return;
                
                int currentIndex = currentSelected != null ? onlineList.IndexOf(currentSelected) : -1;
                int nextIndex = (currentIndex + 1) % onlineList.Count;
                
                foreach (var ch in _allChannels) ch.IsSelected = false;
                onlineList[nextIndex].IsSelected = true;
            }
            else
            {
                // 多选模式下，反选所有在线通道
                foreach (var channel in _filteredChannels.Where(c => c.IsOnline))
                {
                    channel.IsSelected = !channel.IsSelected;
                }
            }
        }

        private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
        {
            var searchText = SearchBox.Text?.Trim().ToLower() ?? "";
            
            _filteredChannels.Clear();
            
            var filtered = string.IsNullOrEmpty(searchText) 
                ? _allChannels 
                : _allChannels.Where(c => c.ChannelId.ToString().Contains(searchText));
            
            foreach (var channel in filtered)
            {
                _filteredChannels.Add(channel);
            }
        }

        private void UpdateStatusText()
        {
            var selectedCount = _allChannels.Count(c => c.IsSelected);
            var total = _allChannels.Count;
            var modeText = _isSingleSelectMode ? "单选" : "叠加";
            ViewStatusText.Text = $"已选择: {selectedCount} 个通道 ({modeText}模式)";
        }

        private void OnSelectedChannelsChanged()
        {
            var selectedChannels = GetSelectedChannels();
            SelectedChannelsChanged?.Invoke(this, new SelectedChannelsChangedEventArgs(selectedChannels));
        }
    }

    // 通道项数据模型
    public class ChannelItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isOnline = true; // 默认在线

        public ChannelItem(int channelId)
        {
            ChannelId = channelId;
            
            // 解析通道ID，生成更友好的显示名称
            // 通道ID格式: MachineId * 100 + ChannelNumber
            // 注意：MachineId从0开始（对应设备0、设备1、设备2...），不做+1处理
            int machineId = channelId / 100;
            int channelNumber = channelId % 100;
            DisplayText = $"AI{machineId}-{channelNumber:D2}";
        }

        public int ChannelId { get; }
        
        public string DisplayText { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set
            {
                if (_isOnline != value)
                {
                    _isOnline = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsOnline)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextBrush)));
                }
            }
        }

        public IBrush TextBrush => IsOnline ? Brushes.White : Brushes.Gray;

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // 选中通道变化事件参数
    public class SelectedChannelsChangedEventArgs : EventArgs
    {
        public SelectedChannelsChangedEventArgs(List<int> selectedChannels)
        {
            SelectedChannels = selectedChannels;
        }

        public List<int> SelectedChannels { get; }
    }
}
