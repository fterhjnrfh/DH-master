using System;
using System.Threading;
using DH.Client.App.Services.Performance;

namespace DH.Client.App.Data;

public readonly record struct RealtimeFrameStamp(
    long FrameVersion,
    long DataEpoch,
    long DataVersion,
    DateTimeOffset AdvancedAt);

public sealed class RealtimeFrameCoordinator : IDisposable
{
    private readonly DataBus _dataBus;
    private long _latestDataVersion = 1;
    private long _presentedDataVersion = 0;
    private long _frameVersion = 1;
    private long _dataEpoch = 1;
    private long _updateCount;
    private long _frameAdvanceCount;
    private DateTimeOffset _lastAdvancedAt = DateTimeOffset.UtcNow;

    public RealtimeFrameCoordinator(DataBus dataBus)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _dataBus.DataUpdated += OnDataUpdated;
        _dataBus.PreviewTimelineReset += OnPreviewTimelineReset;
        _dataBus.ChannelRemoved += OnChannelRemoved;
    }

    public RealtimeFrameStamp GetCurrentFrameStamp()
    {
        return new RealtimeFrameStamp(
            Interlocked.Read(ref _frameVersion),
            Interlocked.Read(ref _dataEpoch),
            Interlocked.Read(ref _presentedDataVersion),
            _lastAdvancedAt);
    }

    public void AdvanceFrame()
    {
        long latestDataVersion = Interlocked.Read(ref _latestDataVersion);
        long presentedDataVersion = Interlocked.Read(ref _presentedDataVersion);
        if (latestDataVersion != presentedDataVersion)
        {
            Interlocked.Exchange(ref _presentedDataVersion, latestDataVersion);
        }

        long frameVersion = Interlocked.Increment(ref _frameVersion);
        long frameAdvanceCount = Interlocked.Increment(ref _frameAdvanceCount);
        _lastAdvancedAt = DateTimeOffset.UtcNow;

        RenderPhaseTimingLogger.LogFrameCoordinatorSummary(
            Interlocked.Read(ref _updateCount),
            frameAdvanceCount,
            frameVersion,
            Interlocked.Read(ref _dataEpoch),
            latestDataVersion,
            presentedDataVersion,
            latestDataVersion != presentedDataVersion);
    }

    public void Dispose()
    {
        _dataBus.DataUpdated -= OnDataUpdated;
        _dataBus.PreviewTimelineReset -= OnPreviewTimelineReset;
        _dataBus.ChannelRemoved -= OnChannelRemoved;
    }

    private void OnDataUpdated(object? sender, DataUpdateEventArgs e)
    {
        Interlocked.Increment(ref _updateCount);
        Interlocked.Increment(ref _latestDataVersion);
    }

    private void OnPreviewTimelineReset(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _dataEpoch);
        Interlocked.Increment(ref _latestDataVersion);
    }

    private void OnChannelRemoved(object? sender, int channelId)
    {
        Interlocked.Increment(ref _latestDataVersion);
    }
}
