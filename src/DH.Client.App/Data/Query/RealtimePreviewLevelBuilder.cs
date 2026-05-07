using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts.Models;
using DH.Client.App.Services.Performance;

namespace DH.Client.App.Data.Query;

public sealed class RealtimePreviewLevelBuilder : IPreviewLevelBuilder
{
    private readonly DataBus _dataBus;
    private readonly RealtimeDisplayCache? _displayCache;
    private readonly int _defaultHistoryPointBudget;

    public RealtimePreviewLevelBuilder(
        DataBus dataBus,
        RealtimeDisplayCache? displayCache,
        int defaultHistoryPointBudget = 8192)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _displayCache = displayCache;
        _defaultHistoryPointBudget = Math.Max(256, defaultHistoryPointBudget);
    }

    public ValueTask<PreviewLevelBuildResult> BuildAsync(
        PreviewLevelBuildRequest request,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<PreviewLevelBuildResult>(ct);
        }

        var timing = System.Diagnostics.Stopwatch.StartNew();
        int[] channels = request.ChannelIds
            .Where(static id => id > 0)
            .Distinct()
            .ToArray();

        int historyCount = Math.Max(_defaultHistoryPointBudget, Math.Max(1, request.MaxPointsPerChannel));
        var channelData = new Dictionary<int, IReadOnlyList<CurvePoint>>(channels.Length);
        int maxActualPointsPerChannel = 0;
        long totalActualPoints = 0;
        bool anyData = false;
        bool allChannelsComplete = true;

        foreach (int channelId in channels)
        {
            var raw = _displayCache?.GetLatestData(channelId, historyCount)
                ?? _dataBus.GetLatestData(channelId, historyCount);
            var projected = PreviewProjection.ProjectWindowWithLimit(
                raw,
                request.WindowStart,
                request.WindowEnd,
                Math.Max(1, request.MaxPointsPerChannel),
                request.RequireEnvelopeSemantics);

            channelData[channelId] = projected.Points;
            maxActualPointsPerChannel = Math.Max(maxActualPointsPerChannel, projected.ActualPointCount);
            totalActualPoints += projected.ActualPointCount;
            anyData |= projected.ActualPointCount > 0;
            allChannelsComplete &= PreviewProjection.IsWindowCovered(raw, request.WindowStart, request.WindowEnd);
        }

        BuildState buildState = !anyData
            ? BuildState.Missing
            : (allChannelsComplete ? BuildState.Ready : BuildState.Degraded);

        var result = new PreviewLevelBuildResult
        {
            SessionId = request.SessionId,
            ViewId = request.ViewId,
            PreviewLevel = request.PreviewLevel,
            MaxPointsPerChannel = Math.Max(1, request.MaxPointsPerChannel),
            SourceVersion = request.SourceVersion,
            DataEpoch = request.DataEpoch,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            IsComplete = allChannelsComplete,
            BuildState = buildState,
            TimeAxisKind = request.TimeAxisKind,
            ChannelData = channelData,
            MaxActualPointsPerChannel = maxActualPointsPerChannel,
            TotalActualPoints = totalActualPoints
        };

        timing.Stop();
        RenderPhaseTimingLogger.LogPreviewBuild(
            request.SessionId.ToString("N"),
            request.ViewId,
            request.PreviewLevel.ToString(),
            channels.Length,
            cacheHit: false,
            buildState.ToString(),
            "sync",
            timing.Elapsed.TotalMilliseconds);

        return ValueTask.FromResult(result);
    }
}
