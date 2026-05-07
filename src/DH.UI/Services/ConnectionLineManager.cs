using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using NewAvalonia.Models;
using NewAvalonia.Views;

namespace NewAvalonia.Services
{
    public class ConnectionLineManager
    {
        private readonly Canvas _canvas;
        private readonly Dictionary<string, ConnectionLineView> _connectionLines = new();

        public ConnectionLineManager(Canvas canvas)
        {
            _canvas = canvas;
        }



        public void AddPermanentLine(ControlConnection connection, Point startPoint, Point endPoint)
        {
            var lineView = new ConnectionLineView()
            {
                ConnectionId = connection.Id,
                ZIndex = 1, // 在控件下方但在背景上方
                Width = _canvas.Width > 0 ? _canvas.Width : 1000,
                Height = _canvas.Height > 0 ? _canvas.Height : 800
            };

            lineView.SetPoints(startPoint, endPoint);

            // 添加删除事件处理
            lineView.DeleteRequested += (sender, e) =>
            {
                RemovePermanentLine(connection.Id);
                ConnectionLineDeleted?.Invoke(connection.Id);
            };

            _connectionLines[connection.Id] = lineView;
            _canvas.Children.Add(lineView);
        }

        public void RemovePermanentLine(string connectionId)
        {
            if (_connectionLines.TryGetValue(connectionId, out var lineView))
            {
                _canvas.Children.Remove(lineView);
                _connectionLines.Remove(connectionId);
            }
        }

        public void UpdatePermanentLine(string connectionId, Point startPoint, Point endPoint)
        {
            if (_connectionLines.TryGetValue(connectionId, out var lineView))
            {
                lineView.SetPoints(startPoint, endPoint);
            }
        }

        public void ClearAllLines()
        {
            foreach (var lineView in _connectionLines.Values)
            {
                _canvas.Children.Remove(lineView);
            }
            _connectionLines.Clear();
        }

        public Point CalculateConnectionPoint(ControlInfo control)
        {
            return new Point(
                control.Left + control.Width / 2,
                control.Top + control.Height / 2
            );
        }



        // 事件
        public System.Action<string>? ConnectionLineDeleted;
    }
}