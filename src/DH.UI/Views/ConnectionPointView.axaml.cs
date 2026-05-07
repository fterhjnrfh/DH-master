using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace NewAvalonia.Views
{
    public partial class ConnectionPointView : UserControl
    {
        public event EventHandler<PointerPressedEventArgs>? ConnectionPointPressed;
        public event EventHandler<PointerEventArgs>? ConnectionPointMoved;
        public event EventHandler<PointerReleasedEventArgs>? ConnectionPointReleased;

        public string ControlId { get; set; } = string.Empty;
        public bool IsConnecting { get; set; }
        public bool IsConnected { get; set; }
        
        public enum ConnectionPointState
        {
            Default,    // 蓝色 - 默认状态
            Connecting, // 红色 - 连接状态
            Connected   // 绿色 - 已连接状态
        }
        
        public ConnectionPointState CurrentState { get; private set; } = ConnectionPointState.Default;

        public ConnectionPointView()
        {
            InitializeComponent();
            AttachEvents();
        }

        private void AttachEvents()
        {
            connectionPoint.PointerPressed += OnConnectionPointPressed;
            connectionPoint.PointerMoved += OnConnectionPointMoved;
            connectionPoint.PointerReleased += OnConnectionPointReleased;
        }

        private void OnConnectionPointPressed(object? sender, PointerPressedEventArgs e)
        {
            SetConnectingState(true);
            ConnectionPointPressed?.Invoke(this, e);
            e.Handled = true;
        }

        private void OnConnectionPointMoved(object? sender, PointerEventArgs e)
        {
            if (IsConnecting)
            {
                ConnectionPointMoved?.Invoke(this, e);
            }
        }

        private void OnConnectionPointReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (IsConnecting)
            {
                SetConnectingState(false);
                ConnectionPointReleased?.Invoke(this, e);
            }
        }

        public void SetState(ConnectionPointState state)
        {
            CurrentState = state;
            UpdateVisualState();
        }

        public void SetConnectingState(bool isConnecting)
        {
            if (isConnecting)
                SetState(ConnectionPointState.Connecting);
            else if (IsConnected)
                SetState(ConnectionPointState.Connected);
            else
                SetState(ConnectionPointState.Default);
        }

        public void SetConnectedState(bool isConnected)
        {
            IsConnected = isConnected;
            if (isConnected)
                SetState(ConnectionPointState.Connected);
            else
                SetState(ConnectionPointState.Default);
        }

        private void UpdateVisualState()
        {
            connectionPoint.Classes.Clear();
            connectionPoint.Classes.Add("connectionPoint");

            switch (CurrentState)
            {
                case ConnectionPointState.Connecting:
                    connectionPoint.Classes.Add("connecting");
                    break;
                case ConnectionPointState.Connected:
                    connectionPoint.Classes.Add("connected");
                    break;
                case ConnectionPointState.Default:
                default:
                    // 默认样式，不需要额外的类
                    break;
            }
        }
    }
}