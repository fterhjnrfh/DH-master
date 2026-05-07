using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using DH.Contracts.Models;

namespace DH.Client.App.Data;

/// <summary>
/// Shared cache for explicit-state sweep snapshots.
/// Views keep their own sweep state, but when multiple views currently ask for
/// the exact same channel set + window + point budget we can reuse the result.
/// </summary>
public sealed class RealtimeSweepSnapshotCache : IDisposable
{
    private const long VersionCoalesceIntervalTicks = TimeSpan.TicksPerMillisecond * 12;
    private readonly DataBus _dataBus;
    private readonly RealtimeDisplayCache _displayCache;
    private readonly ConcurrentDictionary<CacheKey, SweepSnapshot> _snapshotCache = new();
    private readonly ConcurrentDictionary<LatestTimestampCacheKey, LatestTimestampSnapshot> _latestTimestampCache = new();
    private long _globalVersion = 1;
    private long _lastVersionBumpTicks;

    public RealtimeSweepSnapshotCache(DataBus dataBus, RealtimeDisplayCache displayCache)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _displayCache = displayCache ?? throw new ArgumentNullException(nameof(displayCache));

        _dataBus.DataUpdated += OnDataUpdated;
        _dataBus.ChannelRemoved += OnChannelRemoved;
        _dataBus.PreviewTimelineReset += OnPreviewTimelineReset;
    }

    public SweepSnapshot GetSweepSnapshot(
        IReadOnlyList<int> channels,
        int historyCount,
        double windowStartSeconds,
        double windowSeconds,
        int maxPointsPerChannel)
    {
        if (channels == null || channels.Count == 0)
        {
            return SweepSnapshot.Empty;
        }

        int normalizedHistoryCount = Math.Max(1, historyCount);
        int normalizedMaxPoints = Math.Max(1, maxPointsPerChannel);
        double normalizedWindowStartSeconds = Math.Max(0.0, windowStartSeconds);
        double normalizedWindowSeconds = Math.Max(0.001, windowSeconds);
        string signature = string.Join(",", channels);
        long versionStamp = CreateVersionStamp(channels);
        var key = new CacheKey(
            signature,
            normalizedHistoryCount,
            QuantizeSeconds(normalizedWindowStartSeconds),
            QuantizeSeconds(normalizedWindowSeconds),
            normalizedMaxPoints);

        if (_snapshotCache.TryGetValue(key, out var cached) && cached.VersionStamp == versionStamp)
        {
            return cached;
        }

        var rawData = new Dictionary<int, IReadOnlyList<CurvePoint>>(channels.Count);

        foreach (int channelId in channels)
        {
            var data = _displayCache.GetLatestData(channelId, normalizedHistoryCount);
            rawData[channelId] = data;
        }

        double windowEndSeconds = normalizedWindowStartSeconds + normalizedWindowSeconds;
        var windowData = new Dictionary<int, IReadOnlyList<CurvePoint>>(channels.Count);
        bool hasAnyData = false;
        double windowMaxAbsY = 1.0;
        int maxActualPointsPerChannel = 0;
        int totalActualPoints = 0;

        foreach (int channelId in channels)
        {
            var projected = ProjectSweepWindowWithLimit(
                rawData[channelId],
                normalizedWindowStartSeconds,
                windowEndSeconds,
                normalizedMaxPoints);
            windowData[channelId] = projected.Points;
            if (projected.ActualPointCount > 0)
            {
                hasAnyData = true;
            }

            windowMaxAbsY = Math.Max(windowMaxAbsY, projected.MaxAbsY);
            maxActualPointsPerChannel = Math.Max(maxActualPointsPerChannel, projected.ActualPointCount);
            totalActualPoints += projected.ActualPointCount;
        }

        if (!hasAnyData)
        {
            var empty = SweepSnapshot.Empty with
            {
                VersionStamp = versionStamp,
                WindowStartSeconds = normalizedWindowStartSeconds,
                WindowEndSeconds = windowEndSeconds,
                WindowMaxAbsY = 1.0,
                MaxActualPointsPerChannel = 0,
                TotalActualPoints = 0
            };
            _snapshotCache[key] = empty;
            return empty;
        }

        var snapshot = new SweepSnapshot(
            VersionStamp: versionStamp,
            WindowStartSeconds: normalizedWindowStartSeconds,
            WindowEndSeconds: windowEndSeconds,
            WindowMaxAbsY: windowMaxAbsY,
            MaxActualPointsPerChannel: maxActualPointsPerChannel,
            TotalActualPoints: totalActualPoints,
            WindowData: windowData);
        _snapshotCache[key] = snapshot;
        return snapshot;
    }

    public bool TryGetLatestSeconds(
        IReadOnlyList<int> channels,
        int historyCount,
        out double latestSeconds)
    {
        latestSeconds = double.NegativeInfinity;
        if (channels == null || channels.Count == 0)
        {
            return false;
        }

        int normalizedHistoryCount = Math.Max(1, historyCount);
        string signature = string.Join(",", channels);
        long versionStamp = CreateVersionStamp(channels);
        var key = new LatestTimestampCacheKey(signature, normalizedHistoryCount);

        if (_latestTimestampCache.TryGetValue(key, out var cached) && cached.VersionStamp == versionStamp)
        {
            latestSeconds = cached.LatestSeconds;
            return !double.IsNegativeInfinity(latestSeconds);
        }

        foreach (int channelId in channels)
        {
            if (_displayCache.TryGetLatestTimestamp(channelId, out var channelLatestSeconds))
            {
                latestSeconds = Math.Max(latestSeconds, channelLatestSeconds);
            }
        }

        if (double.IsNegativeInfinity(latestSeconds))
        {
            foreach (int channelId in channels)
            {
                var latestData = _dataBus.GetLatestData(channelId, 1);
                if (latestData.Count > 0)
                {
                    latestSeconds = Math.Max(latestSeconds, latestData[latestData.Count - 1].X);
                }
            }
        }

        _latestTimestampCache[key] = new LatestTimestampSnapshot(versionStamp, latestSeconds);
        return !double.IsNegativeInfinity(latestSeconds);
    }

    public void Clear()
    {
        _snapshotCache.Clear();
        _latestTimestampCache.Clear();
        Interlocked.Exchange(ref _globalVersion, 1);
        Interlocked.Exchange(ref _lastVersionBumpTicks, 0);
    }

    private long CreateVersionStamp(IReadOnlyList<int> channels)
    {
        return Volatile.Read(ref _globalVersion);
    }

    private void OnDataUpdated(object? sender, DataUpdateEventArgs e)
    {
        TryBumpGlobalVersion();
    }

    private void OnChannelRemoved(object? sender, int channelId)
    {
        ForceBumpGlobalVersion();
        foreach (var key in _snapshotCache.Keys)
        {
            if (ChannelSignatureContains(key.ChannelSignature, channelId))
            {
                _snapshotCache.TryRemove(key, out _);
            }
        }
    }

    private void OnPreviewTimelineReset(object? sender, EventArgs e)
    {
        Clear();
    }

    public void Dispose()
    {
        _dataBus.DataUpdated -= OnDataUpdated;
        _dataBus.ChannelRemoved -= OnChannelRemoved;
        _dataBus.PreviewTimelineReset -= OnPreviewTimelineReset;
        Clear();
    }

    private void TryBumpGlobalVersion()
    {
        long nowTicks = DateTime.UtcNow.Ticks;

        while (true)
        {
            long lastTicks = Volatile.Read(ref _lastVersionBumpTicks);
            if (lastTicks != 0 && nowTicks - lastTicks < VersionCoalesceIntervalTicks)
            {
                return;
            }

            long observed = Interlocked.CompareExchange(ref _lastVersionBumpTicks, nowTicks, lastTicks);
            if (observed == lastTicks)
            {
                Interlocked.Increment(ref _globalVersion);
                return;
            }
        }
    }

    private void ForceBumpGlobalVersion()
    {
        Interlocked.Exchange(ref _lastVersionBumpTicks, DateTime.UtcNow.Ticks);
        Interlocked.Increment(ref _globalVersion);
    }

    private static long QuantizeSeconds(double seconds)
    {
        return (long)Math.Round(seconds * 1_000_000.0);
    }

    private static ProjectedSweepWindow ProjectSweepWindowWithLimit(
        IReadOnlyList<CurvePoint> source,
        double windowStartSeconds,
        double windowEndSeconds,
        int maxPoints)
    {
        if (source == null || source.Count == 0)
        {
            return ProjectedSweepWindow.Empty;
        }

        int startIndex = FindFirstPointAtOrAfter(source, windowStartSeconds);
        if (startIndex >= source.Count)
        {
            return ProjectedSweepWindow.Empty;
        }

        int endIndex = FindFirstPointAtOrAfter(source, windowEndSeconds);
        int count = endIndex - startIndex;
        if (count <= 0)
        {
            return ProjectedSweepWindow.Empty;
        }

        if (maxPoints > 1 && count > maxPoints)
        {
            return BuildEnvelopeWindowPoints(source, startIndex, endIndex - 1, windowStartSeconds, maxPoints);
        }

        double maxAbsY = 1.0;
        if (windowStartSeconds <= 0.0 && startIndex == 0 && endIndex == source.Count)
        {
            for (int i = startIndex; i < endIndex; i++)
            {
                maxAbsY = Math.Max(maxAbsY, Math.Abs(source[i].Y));
            }

            return new ProjectedSweepWindow(source, count, maxAbsY);
        }

        var projected = new CurvePoint[count];
        for (int i = 0; i < count; i++)
        {
            var point = source[startIndex + i];
            projected[i] = new CurvePoint(point.X - windowStartSeconds, point.Y);
            maxAbsY = Math.Max(maxAbsY, Math.Abs(point.Y));
        }

        return new ProjectedSweepWindow(projected, count, maxAbsY);
    }

    private static int FindFirstPointAtOrAfter(IReadOnlyList<CurvePoint> data, double targetSeconds)
    {
        int low = 0;
        int high = data.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (data[mid].X < targetSeconds)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static ProjectedSweepWindow BuildEnvelopeWindowPoints(
        IReadOnlyList<CurvePoint> source,
        int startIndex,
        int endIndex,
        double windowStartSeconds,
        int maxPoints)
    {
        if (source == null || source.Count == 0 || endIndex < startIndex || maxPoints <= 0)
        {
            return ProjectedSweepWindow.Empty;
        }

        int count = endIndex - startIndex + 1;
        if (count <= maxPoints)
        {
            double maxAbsY = 1.0;
            var projected = new CurvePoint[count];
            for (int i = 0; i < count; i++)
            {
                var point = source[startIndex + i];
                projected[i] = new CurvePoint(point.X - windowStartSeconds, point.Y);
                maxAbsY = Math.Max(maxAbsY, Math.Abs(point.Y));
            }

            return new ProjectedSweepWindow(projected, count, maxAbsY);
        }

        int bucketCount = Math.Max(1, Math.Min(count, maxPoints / 2));
        var points = new List<CurvePoint>(Math.Min(maxPoints, bucketCount * 4));
        double windowMaxAbsY = 1.0;

        for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            int bucketStart = startIndex + (int)Math.Floor(bucketIndex * count / (double)bucketCount);
            int bucketEnd = startIndex + (int)Math.Floor((bucketIndex + 1) * count / (double)bucketCount) - 1;

            bucketStart = Math.Clamp(bucketStart, startIndex, endIndex);
            bucketEnd = Math.Clamp(bucketEnd, bucketStart, endIndex);

            int minIndex = bucketStart;
            int maxIndex = bucketStart;
            double minValue = source[bucketStart].Y;
            double maxValue = minValue;
            windowMaxAbsY = Math.Max(windowMaxAbsY, Math.Abs(minValue));

            for (int pointIndex = bucketStart + 1; pointIndex <= bucketEnd; pointIndex++)
            {
                double value = source[pointIndex].Y;
                windowMaxAbsY = Math.Max(windowMaxAbsY, Math.Abs(value));

                if (value < minValue)
                {
                    minValue = value;
                    minIndex = pointIndex;
                }

                if (value > maxValue)
                {
                    maxValue = value;
                    maxIndex = pointIndex;
                }
            }

            AppendEnvelopePointsInTimeOrder(
                points,
                source,
                windowStartSeconds,
                bucketStart,
                minIndex,
                maxIndex,
                bucketEnd);
        }

        if (points.Count > maxPoints)
        {
            points.RemoveRange(maxPoints, points.Count - maxPoints);
        }

        return new ProjectedSweepWindow(points, points.Count, windowMaxAbsY);
    }

    private static void AppendEnvelopePoint(
        List<CurvePoint> points,
        CurvePoint sourcePoint,
        double windowStartSeconds)
    {
        var projected = new CurvePoint(sourcePoint.X - windowStartSeconds, sourcePoint.Y);

        if (points.Count > 0)
        {
            var last = points[^1];
            if (Math.Abs(last.X - projected.X) < 1e-12 && Math.Abs(last.Y - projected.Y) < 1e-12)
            {
                return;
            }
        }

        points.Add(projected);
    }

    private static void AppendEnvelopePointsInTimeOrder(
        List<CurvePoint> points,
        IReadOnlyList<CurvePoint> source,
        double windowStartSeconds,
        int bucketStart,
        int minIndex,
        int maxIndex,
        int bucketEnd)
    {
        Span<int> indices = stackalloc int[4];
        indices[0] = bucketStart;
        indices[1] = minIndex;
        indices[2] = maxIndex;
        indices[3] = bucketEnd;
        indices.Sort();

        int? lastIndex = null;
        for (int i = 0; i < indices.Length; i++)
        {
            int currentIndex = indices[i];
            if (lastIndex == currentIndex)
            {
                continue;
            }

            AppendEnvelopePoint(points, source[currentIndex], windowStartSeconds);
            lastIndex = currentIndex;
        }
    }

    private static bool ChannelSignatureContains(string channelSignature, int channelId)
    {
        if (string.IsNullOrEmpty(channelSignature))
        {
            return false;
        }

        string token = channelId.ToString();
        if (channelSignature.Equals(token, StringComparison.Ordinal))
        {
            return true;
        }

        return channelSignature.StartsWith(token + ",", StringComparison.Ordinal)
            || channelSignature.EndsWith("," + token, StringComparison.Ordinal)
            || channelSignature.Contains("," + token + ",", StringComparison.Ordinal);
    }

    private readonly record struct CacheKey(
        string ChannelSignature,
        int HistoryCount,
        long WindowStartMicros,
        long WindowLengthMicros,
        int MaxPointsPerChannel);

    private readonly record struct LatestTimestampCacheKey(
        string ChannelSignature,
        int HistoryCount);

    private readonly record struct ProjectedSweepWindow(
        IReadOnlyList<CurvePoint> Points,
        int ActualPointCount,
        double MaxAbsY)
    {
        public static ProjectedSweepWindow Empty { get; } =
            new(Array.Empty<CurvePoint>(), 0, 1.0);
    }
}

public readonly record struct LatestTimestampSnapshot(
    long VersionStamp,
    double LatestSeconds);

public readonly record struct SweepSnapshot(
    long VersionStamp,
    double WindowStartSeconds,
    double WindowEndSeconds,
    double WindowMaxAbsY,
    int MaxActualPointsPerChannel,
    int TotalActualPoints,
    IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> WindowData)
{
    public static SweepSnapshot Empty => new(
        VersionStamp: 0,
        WindowStartSeconds: 0.0,
        WindowEndSeconds: 0.0,
        WindowMaxAbsY: 1.0,
        MaxActualPointsPerChannel: 0,
        TotalActualPoints: 0,
        WindowData: new Dictionary<int, IReadOnlyList<CurvePoint>>());
}
