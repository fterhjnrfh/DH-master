using Avalonia;

namespace NewAvalonia.Models
{
    public class ConnectionPoint
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public string ControlId { get; set; } = string.Empty;
        public Point Position { get; set; }
        public bool IsConnected { get; set; }
        public ConnectionPointType Type { get; set; } = ConnectionPointType.Bidirectional;
    }

    public enum ConnectionPointType
    {
        Input,
        Output,
        Bidirectional
    }
}