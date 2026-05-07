using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using System;

namespace NewAvalonia.Views
{
    public partial class ConnectionLineView : UserControl
    {
        public event EventHandler? DeleteRequested;
        public string ConnectionId { get; set; } = string.Empty;

        public ConnectionLineView()
        {
            InitializeComponent();
            this.ContextRequested += OnContextRequested;
        }

        public void SetPoints(Point startPoint, Point endPoint)
        {
            connectionLine.StartPoint = startPoint;
            connectionLine.EndPoint = endPoint;
        }



        private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            ShowContextMenu();
            e.Handled = true;
        }

        private void ShowContextMenu()
        {
            var contextMenu = new ContextMenu();
            
            var deleteItem = new MenuItem()
            {
                Header = "删除连接"
            };
            deleteItem.Click += (s, e) => DeleteRequested?.Invoke(this, EventArgs.Empty);
            
            contextMenu.Items.Add(deleteItem);
            contextMenu.Open(this);
        }
    }
}