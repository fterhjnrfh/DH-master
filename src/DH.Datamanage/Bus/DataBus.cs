using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using DH.Contracts.Abstractions;
using System.Threading.Channels;

namespace DH.Datamanage.Bus;

public class DataBus : IDataBus
{
    private readonly ConcurrentDictionary<int, Channel<IDataFrame>> _channels = new();

    private readonly Channel<IDataFrame> _all = Channel.CreateUnbounded<IDataFrame>(
           new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    // Publish a data frame to the appropriate channel
    public ValueTask PublishFrameAsync(IDataFrame frame, CancellationToken ct = default)
    {
        var ch = _channels.GetOrAdd(frame.ChannelId, _ => Channel.CreateUnbounded<IDataFrame>());
        ch.Writer.TryWrite(frame);
        return ValueTask.CompletedTask;
    }

    // Subscribe to a channel to receive data frames
    public async IAsyncEnumerable<IDataFrame> SubscribeChannel(int channelId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var ch = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
        while (!ct.IsCancellationRequested && await ch.Reader.WaitToReadAsync(ct))
        {
            while (ch.Reader.TryRead(out var item))
                yield return item;
        }
    }

    public async IAsyncEnumerable<IDataFrame> SubscribeAll([EnumeratorCancellation] CancellationToken token)
    {
        var reader = _all.Reader;
        while (!token.IsCancellationRequested && await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var frame))
                yield return frame;
        }
    }

    public void EnsureChannel(int channelId)
    {
        _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
    }
}