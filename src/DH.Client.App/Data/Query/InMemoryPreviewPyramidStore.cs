using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public sealed class InMemoryPreviewPyramidStore : IPreviewPyramidStore
{
    private readonly ConcurrentDictionary<PreviewCacheKey, PreviewLevelBuildResult> _cache = new();

    public ValueTask<PreviewLevelReadResult> TryReadAsync(
        PreviewLevelReadRequest request,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<PreviewLevelReadResult>(ct);
        }

        var key = PreviewCacheKey.From(request);
        if (_cache.TryGetValue(key, out var snapshot))
        {
            return ValueTask.FromResult(new PreviewLevelReadResult
            {
                CacheHit = true,
                Snapshot = snapshot
            });
        }

        return ValueTask.FromResult(new PreviewLevelReadResult
        {
            CacheHit = false,
            Snapshot = null
        });
    }

    public ValueTask WriteAsync(
        PreviewLevelBuildResult snapshot,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        _cache[PreviewCacheKey.From(snapshot)] = snapshot;
        return ValueTask.CompletedTask;
    }

    private readonly record struct PreviewCacheKey(
        Guid SessionId,
        string ChannelSignature,
        long WindowStartMicros,
        long WindowEndMicros,
        PreviewLevel PreviewLevel,
        int MaxPointsPerChannel,
        long SourceVersion,
        long DataEpoch)
    {
        public static PreviewCacheKey From(PreviewLevelReadRequest request) => new(
            request.SessionId,
            string.Join(",", request.ChannelIds.OrderBy(static id => id)),
            Quantize(request.WindowStart),
            Quantize(request.WindowEnd),
            request.PreviewLevel,
            request.MaxPointsPerChannel,
            request.SourceVersion,
            request.DataEpoch);

        public static PreviewCacheKey From(PreviewLevelBuildResult snapshot) => new(
            snapshot.SessionId,
            string.Join(",", snapshot.ChannelData.Keys.OrderBy(static id => id)),
            Quantize(snapshot.WindowStart),
            Quantize(snapshot.WindowEnd),
            snapshot.PreviewLevel,
            snapshot.MaxPointsPerChannel,
            snapshot.SourceVersion,
            snapshot.DataEpoch);

        private static long Quantize(double seconds) => (long)Math.Round(seconds * 1_000_000.0);
    }
}
