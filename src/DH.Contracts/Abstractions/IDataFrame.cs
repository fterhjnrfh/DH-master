using DH.Contracts.Models;

namespace DH.Contracts.Abstractions;

public interface IDataFrame
{
    DateTime Timestamp { get; }
    int ChannelId { get; }
    ReadOnlyMemory<float> Samples { get; }
    int FrameId { get; }
    FrameHeader Header { get; }
}