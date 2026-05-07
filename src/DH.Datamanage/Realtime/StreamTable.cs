using System.Collections.Concurrent;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;
using DH.Contracts;

namespace DH.Datamanage.Realtime;

public class StreamTable
{
    public record Subscriber(int ChannelId, CancellationToken Token);

    private readonly IDataBus _bus;
    private readonly ConcurrentDictionary<int, ChannelInfo> _channels = new();

    public StreamTable(IDataBus bus) { _bus = bus; }

    public ChannelInfo EnsureChannel(int id, string name = "")
    {
        return _channels.GetOrAdd(id, _ => new ChannelInfo
        {
            ChannelId = id,
            Name = string.IsNullOrEmpty(name) ? ChannelNaming.ChannelName(id) : name,
            Online = false
        });
    }

    public IEnumerable<ChannelInfo> ListChannels() => _channels.Values.OrderBy(c => c.ChannelId);

    public IAsyncEnumerable<IDataFrame> Subscribe(int channelId, CancellationToken ct) => _bus.SubscribeChannel(channelId, ct);

    public ValueTask PublishAsync(IDataFrame frame, CancellationToken ct) => _bus.PublishFrameAsync(frame, ct);
}