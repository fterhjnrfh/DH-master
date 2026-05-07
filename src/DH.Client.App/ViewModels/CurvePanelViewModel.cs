using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DH.Contracts.Models;
using DH.Client.App.Data;

namespace DH.Client.App.ViewModels
{
    public partial class CurvePanelViewModel : ObservableObject
    {
        private readonly DataBus _dataBus;
        
        [ObservableProperty] private int _selectedChannelId;
        [ObservableProperty] private float _zoomLevel = 1.0f;
        [ObservableProperty] private bool _isRunning;
        
        public ObservableCollection<int> AvailableChannels { get; } = new();
        
        public IRelayCommand ZoomInCommand { get; }
        public IRelayCommand ZoomOutCommand { get; }
        
        public CurvePanelViewModel(DataBus dataBus)
        {
            _dataBus = dataBus;
            
            // 初始化命令
            ZoomInCommand = new RelayCommand(ZoomIn);
            ZoomOutCommand = new RelayCommand(ZoomOut);
            
            // 默认选择通道1
            SelectedChannelId = 1;
            
            // 更新可用通道列表
            UpdateAvailableChannels();
            
            // 监听数据总线上的通道变化
            _dataBus.ChannelAdded += OnChannelAdded;
            _dataBus.ChannelRemoved += OnChannelRemoved;
        }
        
        private void ZoomIn()
        {
            ZoomLevel *= 1.2f;
            if (ZoomLevel > 10.0f)
                ZoomLevel = 10.0f;
        }
        
        private void ZoomOut()
        {
            ZoomLevel /= 1.2f;
            if (ZoomLevel < 0.1f)
                ZoomLevel = 0.1f;
        }
        
        private void UpdateAvailableChannels()
        {
            AvailableChannels.Clear();
            foreach (var channel in _dataBus.GetAvailableChannels())
            {
                AvailableChannels.Add(channel);
            }
            
            // 如果有通道，默认选择第一个
            if (AvailableChannels.Count > 0 && SelectedChannelId == 0)
            {
                SelectedChannelId = AvailableChannels[0];
            }
        }
        
        private void OnChannelAdded(object sender, int channelId)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AvailableChannels.Add(channelId));
        }
        
        private void OnChannelRemoved(object sender, int channelId)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => AvailableChannels.Remove(channelId));
        }
        
        public void Start()
        {
            IsRunning = true;
        }
        
        public void Stop()
        {
            IsRunning = false;
        }
    }
}