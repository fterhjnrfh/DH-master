using System;

namespace NewAvalonia.Models
{
    public class ControlConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string SourceControlId { get; set; } = string.Empty;
        public string TargetControlId { get; set; } = string.Empty;
        public string SourcePointId { get; set; } = string.Empty;
        public string TargetPointId { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}