using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts.Models;
using DH.Client.App.Services.Performance;

namespace DH.Client.App.Data.Query;

/// <summary>
/// Phase 1/2 bridge implementation:
/// wraps current DataBus + RealtimeDisplayCache and exposes the frozen query interfaces
/// without changing current rendering behavior.
/// </summary>
public sealed class RealtimeCurveQueryRuntime :
    ICurveQueryService,
    ICurveFrameProvider,
    IDisposable
{
    private const double PreviewSourceVersionCoalesceMs = 12.0;

    private readonly DataBus _dataBus;
    private readonly RealtimeDisplayCache? _displayCache;
    private readonly IPreviewLevelBuilder _previewLevelBuilder;
    private readonly IPreviewPyramidStoreProvider _previewPyramidStoreProvider;
    private readonly Guid _sessionId;
    private readonly int _defaultHistoryPointBudget;
    private readonly Timer _previewInvalidationTimer;
    private long _version = 1;
    private long _dataEpoch = 1;
    private long _latestRawSourceVersion = 1;
    private long _appliedRawSourceVersion = 1;
    private long _previewSourceVersion = 1;
    private DateTimeOffset _updatedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastPreviewSourceVersionUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastRawUpdateUtc = DateTimeOffset.UtcNow;

    public RealtimeCurveQueryRuntime(
        DataBus dataBus,
        RealtimeDisplayCache? displayCache,
        IPreviewLevelBuilder? previewLevelBuilder = null,
        IPreviewPyramidStore? previewPyramidStore = null,
        Guid? sessionId = null,
        int defaultHistoryPointBudget = 8192,
        IPreviewPyramidStoreProvider? previewPyramidStoreProvider = null)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _displayCache = displayCache;
        _previewLevelBuilder = previewLevelBuilder ?? new RealtimePreviewLevelBuilder(dataBus, displayCache, defaultHistoryPointBudget);
        _sessionId = sessionId ?? Guid.NewGuid();
        _previewPyramidStoreProvider = previewPyramidStoreProvider
            ?? (previewPyramidStore is not null
                ? new FixedPreviewPyramidStoreProvider(_sessionId, previewPyramidStore)
                : new InMemoryPreviewPyramidStoreProvider());
        _defaultHistoryPointBudget = Math.Max(256, defaultHistoryPointBudget);
        _previewInvalidationTimer = new Timer(
            OnPreviewInvalidationTimerTick,
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _dataBus.DataUpdated += OnDataUpdated;
        _dataBus.PreviewTimelineReset += OnPreviewTimelineReset;
        _dataBus.ChannelRemoved += OnChannelRemoved;
    }

    public CurveFrameVersion GetLatestVersion(Guid sessionId)
    {
        if (sessionId != _sessionId)
        {
            return new CurveFrameVersion(sessionId, 0, 0, DateTimeOffset.MinValue);
        }

        var version = new CurveFrameVersion(
            _sessionId,
            Interlocked.Read(ref _version),
            Interlocked.Read(ref _dataEpoch),
            _updatedAt);

        RenderPhaseTimingLogger.LogCurveQueryVersion(
            _sessionId.ToString("N"),
            version.Version,
            version.DataEpoch);

        return version;
    }

    public long GetLatestPreviewSourceVersionForDiagnostics()
    {
        return Interlocked.Read(ref _previewSourceVersion);
    }

    public ValueTask<CurveWindowSnapshot> GetLatestAsync(
        PreviewReadRequest request,
        CancellationToken ct = default)
    {
        return QueryAsync(request, ct);
    }

    public ValueTask<CurveWindowSnapshot> QueryAsync(
        PreviewReadRequest request,
        CancellationToken ct = default)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<CurveWindowSnapshot>(ct);
        }

        var requestValidation = CurveQueryValidator.ValidateRequest(request);
        if (!requestValidation.IsValid)
        {
            RenderPhaseTimingLogger.LogCurveQueryValidation(
                "request",
                request.SessionId.ToString("N"),
                string.Join("; ", requestValidation.Errors));
            return ValueTask.FromResult(CreateMissingSnapshot(request, BuildState.Missing));
        }

        if (request.SessionId != _sessionId)
        {
            return ValueTask.FromResult(CreateMissingSnapshot(request, BuildState.Missing));
        }

        if (request.ChannelIds is null || request.ChannelIds.Count == 0)
        {
            return ValueTask.FromResult(CreateMissingSnapshot(request, BuildState.Missing));
        }

        double windowStart = request.WindowStart;
        double windowEnd = request.WindowEnd;
        if (windowEnd <= windowStart)
        {
            return ValueTask.FromResult(CreateMissingSnapshot(request, BuildState.Missing));
        }

        var channels = request.ChannelIds
            .Where(static id => id > 0)
            .Distinct()
            .ToArray();
        if (channels.Length == 0)
        {
            return ValueTask.FromResult(CreateMissingSnapshot(request, BuildState.Missing));
        }

        if (request.PreviewLevel != PreviewLevel.L0)
        {
            return QueryPreviewAsync(request, channels, ct);
        }

        int historyCount = Math.Max(_defaultHistoryPointBudget, Math.Max(1, request.MaxPointsPerChannel));
        bool requireEnvelope = request.RequireEnvelopeSemantics;
        var channelData = new Dictionary<int, IReadOnlyList<CurvePoint>>(channels.Length);
        int maxActualPointsPerChannel = 0;
        long totalActualPoints = 0;
        bool anyData = false;
        bool allChannelsComplete = true;

        foreach (int channelId in channels)
        {
            var raw = _displayCache?.GetLatestData(channelId, historyCount)
                ?? _dataBus.GetLatestData(channelId, historyCount);

            int actualCount = PreviewProjection.CountPointsInWindow(raw, windowStart, windowEnd);
            maxActualPointsPerChannel = Math.Max(maxActualPointsPerChannel, actualCount);
            totalActualPoints += actualCount;
            anyData |= actualCount > 0;

            bool channelComplete = PreviewProjection.IsWindowCovered(raw, windowStart, windowEnd);
            allChannelsComplete &= channelComplete;

            channelData[channelId] = PreviewProjection.ProjectWindowWithLimit(
                raw,
                windowStart,
                windowEnd,
                Math.Max(1, request.MaxPointsPerChannel),
                requireEnvelope).Points;
        }

        BuildState buildState;
        if (!anyData)
        {
            buildState = BuildState.Missing;
        }
        else if (allChannelsComplete)
        {
            buildState = BuildState.Ready;
        }
        else
        {
            buildState = BuildState.Degraded;
        }

        var snapshot = new CurveWindowSnapshot
        {
            SessionId = _sessionId,
            ViewId = request.ViewId,
            ChannelIds = channels,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            PreviewLevel = request.PreviewLevel,
            Version = Interlocked.Read(ref _version),
            DataEpoch = Interlocked.Read(ref _dataEpoch),
            SourceVersion = Interlocked.Read(ref _previewSourceVersion),
            SegmentRange = null,
            IsPreview = request.PreviewLevel != PreviewLevel.L0,
            IsComplete = allChannelsComplete,
            BuildState = buildState,
            Recovered = false,
            TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
            ChannelData = channelData,
            MaxActualPointsPerChannel = maxActualPointsPerChannel,
            TotalActualPoints = totalActualPoints
        };

        var snapshotValidation = CurveQueryValidator.ValidateSnapshot(snapshot);
        if (!snapshotValidation.IsValid)
        {
            RenderPhaseTimingLogger.LogCurveQueryValidation(
                "snapshot",
                _sessionId.ToString("N"),
                string.Join("; ", snapshotValidation.Errors));
        }
        timing.Stop();
        RenderPhaseTimingLogger.LogCurveQuerySnapshot(
            _sessionId.ToString("N"),
            snapshot.ViewId,
            snapshot.ChannelIds.Count,
            snapshot.PreviewLevel.ToString(),
            snapshot.Version,
            snapshot.DataEpoch,
            snapshot.SourceVersion,
            snapshot.BuildState.ToString(),
            snapshot.IsPreview,
            snapshot.IsComplete,
            snapshot.TimeAxisKind.ToString(),
            snapshot.TotalActualPoints,
            timing.Elapsed.TotalMilliseconds);

        return ValueTask.FromResult(snapshot);
    }

    public void Dispose()
    {
        _dataBus.DataUpdated -= OnDataUpdated;
        _dataBus.PreviewTimelineReset -= OnPreviewTimelineReset;
        _dataBus.ChannelRemoved -= OnChannelRemoved;
        _previewInvalidationTimer.Dispose();
        _previewPyramidStoreProvider.TryRemove(_sessionId);
    }

    private void OnDataUpdated(object? sender, DataUpdateEventArgs e)
    {
        Interlocked.Increment(ref _version);
        Interlocked.Increment(ref _latestRawSourceVersion);
        _updatedAt = DateTimeOffset.UtcNow;
        _lastRawUpdateUtc = _updatedAt;
        ArmPreviewInvalidationTimer();
    }

    private void OnPreviewTimelineReset(object? sender, EventArgs e)
    {
        Interlocked.Increment(ref _dataEpoch);
        Interlocked.Increment(ref _version);
        Interlocked.Increment(ref _latestRawSourceVersion);
        Interlocked.Exchange(ref _appliedRawSourceVersion, Interlocked.Read(ref _latestRawSourceVersion));
        Interlocked.Increment(ref _previewSourceVersion);
        _updatedAt = DateTimeOffset.UtcNow;
        _lastPreviewSourceVersionUtc = _updatedAt;
        _lastRawUpdateUtc = _updatedAt;
        _previewInvalidationTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnChannelRemoved(object? sender, int channelId)
    {
        Interlocked.Increment(ref _version);
        Interlocked.Increment(ref _latestRawSourceVersion);
        _updatedAt = DateTimeOffset.UtcNow;
        _lastRawUpdateUtc = _updatedAt;
        ArmPreviewInvalidationTimer();
    }

    private CurveWindowSnapshot CreateMissingSnapshot(
        PreviewReadRequest request,
        BuildState state)
    {
        return new CurveWindowSnapshot
        {
            SessionId = request.SessionId,
            ViewId = request.ViewId,
            ChannelIds = request.ChannelIds ?? Array.Empty<int>(),
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            Version = 0,
            DataEpoch = 0,
            SourceVersion = 0,
            SegmentRange = null,
            IsPreview = request.PreviewLevel != PreviewLevel.L0,
            IsComplete = false,
            BuildState = state,
            Recovered = false,
            TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
            ChannelData = new Dictionary<int, IReadOnlyList<CurvePoint>>(),
            MaxActualPointsPerChannel = 0,
            TotalActualPoints = 0
        };
    }

    private ValueTask<CurveWindowSnapshot> QueryPreviewAsync(
        PreviewReadRequest request,
        int[] channels,
        CancellationToken ct)
    {
        IPreviewPyramidStore previewStore = _previewPyramidStoreProvider.GetOrCreate(_sessionId);
        var readRequest = new PreviewLevelReadRequest
        {
            SessionId = _sessionId,
            ViewId = request.ViewId,
            ChannelIds = channels,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            MaxPointsPerChannel = Math.Max(1, request.MaxPointsPerChannel),
            SourceVersion = Interlocked.Read(ref _previewSourceVersion),
            DataEpoch = Interlocked.Read(ref _dataEpoch)
        };

        var cacheRead = previewStore.TryReadAsync(readRequest, ct);
        PreviewLevelReadResult cacheResult = cacheRead.IsCompletedSuccessfully
            ? cacheRead.Result
            : cacheRead.AsTask().GetAwaiter().GetResult();

        if (!cacheResult.CacheHit || cacheResult.Snapshot is null)
        {
            var buildRequest = new PreviewLevelBuildRequest
            {
                SessionId = _sessionId,
                ViewId = request.ViewId,
                ChannelIds = channels,
                WindowStart = request.WindowStart,
                WindowEnd = request.WindowEnd,
                PreviewLevel = request.PreviewLevel,
                MaxPointsPerChannel = Math.Max(1, request.MaxPointsPerChannel),
                RequireEnvelopeSemantics = request.RequireEnvelopeSemantics,
                TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
                SourceVersion = readRequest.SourceVersion,
                DataEpoch = readRequest.DataEpoch
            };

            var buildTask = _previewLevelBuilder.BuildAsync(buildRequest, ct);
            var built = buildTask.IsCompletedSuccessfully
                ? buildTask.Result
                : buildTask.AsTask().GetAwaiter().GetResult();
            previewStore.WriteAsync(built, ct).GetAwaiter().GetResult();
            cacheResult = new PreviewLevelReadResult
            {
                CacheHit = false,
                Snapshot = built
            };
        }

        var preview = cacheResult.Snapshot!;
        RenderPhaseTimingLogger.LogPreviewQuery(
            _sessionId.ToString("N"),
            request.ViewId,
            request.PreviewLevel.ToString(),
            channels.Length,
            cacheResult.CacheHit,
            preview.BuildState.ToString(),
            preview.IsComplete,
            preview.TotalActualPoints);

        return ValueTask.FromResult(new CurveWindowSnapshot
        {
            SessionId = _sessionId,
            ViewId = request.ViewId,
            ChannelIds = channels,
            WindowStart = preview.WindowStart,
            WindowEnd = preview.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            Version = Interlocked.Read(ref _version),
            DataEpoch = preview.DataEpoch,
            SourceVersion = preview.SourceVersion,
            SegmentRange = null,
            IsPreview = true,
            IsComplete = preview.IsComplete,
            BuildState = preview.BuildState,
            Recovered = false,
            TimeAxisKind = preview.TimeAxisKind,
            ChannelData = preview.ChannelData,
            MaxActualPointsPerChannel = preview.MaxActualPointsPerChannel,
            TotalActualPoints = preview.TotalActualPoints
        });
    }

    private void ArmPreviewInvalidationTimer()
    {
        _previewInvalidationTimer.Change(
            TimeSpan.FromMilliseconds(PreviewSourceVersionCoalesceMs),
            Timeout.InfiniteTimeSpan);
    }

    private void OnPreviewInvalidationTimerTick(object? state)
    {
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        TimeSpan quietPeriod = nowUtc - _lastRawUpdateUtc;
        if (quietPeriod.TotalMilliseconds < PreviewSourceVersionCoalesceMs)
        {
            TimeSpan remaining = TimeSpan.FromMilliseconds(PreviewSourceVersionCoalesceMs) - quietPeriod;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            _previewInvalidationTimer.Change(remaining, Timeout.InfiniteTimeSpan);
            return;
        }

        long latestRawSourceVersion = Interlocked.Read(ref _latestRawSourceVersion);
        long appliedRawSourceVersion = Interlocked.Read(ref _appliedRawSourceVersion);
        if (latestRawSourceVersion == appliedRawSourceVersion)
        {
            return;
        }

        Interlocked.Exchange(ref _appliedRawSourceVersion, latestRawSourceVersion);
        Interlocked.Increment(ref _previewSourceVersion);
        _lastPreviewSourceVersionUtc = nowUtc;
    }

    private sealed class FixedPreviewPyramidStoreProvider : IPreviewPyramidStoreProvider
    {
        private readonly Guid _sessionId;
        private readonly IPreviewPyramidStore _store;

        public FixedPreviewPyramidStoreProvider(Guid sessionId, IPreviewPyramidStore store)
        {
            _sessionId = sessionId;
            _store = store;
        }

        public IPreviewPyramidStore GetOrCreate(Guid sessionId)
        {
            if (sessionId != _sessionId)
            {
                throw new InvalidOperationException("Preview store provider session mismatch.");
            }

            return _store;
        }

        public bool TryRemove(Guid sessionId)
        {
            return sessionId == _sessionId;
        }
    }
}
