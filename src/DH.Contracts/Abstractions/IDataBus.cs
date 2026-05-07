namespace DH.Contracts.Abstractions;

public interface IDataBus
{
    IAsyncEnumerable<IDataFrame> SubscribeChannel(int channelId, CancellationToken ct = default);
    ValueTask PublishFrameAsync(IDataFrame frame, CancellationToken ct = default);
    IAsyncEnumerable<IDataFrame> SubscribeAll(CancellationToken token);
    void EnsureChannel(int channelId);
}