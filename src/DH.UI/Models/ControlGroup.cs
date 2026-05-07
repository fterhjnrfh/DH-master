using System.Collections.Generic;
using Avalonia;

namespace NewAvalonia.Models
{
    public class ControlGroup
    {
        public string Id { get; set; } = System.Guid.NewGuid().ToString();
        public List<string> ControlIds { get; set; } = new List<string>();
        public List<ControlConnection> Connections { get; set; } = new List<ControlConnection>();
        public Rect BoundingBox { get; set; }
        public FunctionCombinationType FunctionType { get; set; } = FunctionCombinationType.None;
    }

    public enum FunctionCombinationType
    {
        None,
        SinWaveGenerator,
        SquareWaveGenerator,
        SimulatedSignalSource
    }
}