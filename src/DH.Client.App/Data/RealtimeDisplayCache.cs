using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DH.Contracts.Models;

namespace DH.Client.App.Data;

/// <summary>
/// Shared display-side cache that keeps per-channel preview snapshots up to a fixed
/// retained-point budget. Snapshots are updated when data arrives, so views mostly
/// read prepared data instead of pulling from DataBus during every refresh.
/// </summary>
public sealed class RealtimeDisplayCache : IDisposable
{
    private readonly DataBus _dataBus;
    private readonly int _maxRetainedPoints;
    private readonly ConcurrentDictionary<int, ChannelSnapshotBuffer> _channelBuffers = new();

    public RealtimeDisplayCache(DataBus dataBus, int maxRetainedPoints = 8192)
    {
        _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
        _maxRetainedPoints = Math.Max(256, maxRetainedPoints);
        _dataBus.DataUpdated += OnDataUpdated;
        _dataBus.ChannelRemoved += OnChannelRemoved;
        _dataBus.PreviewTimelineReset += OnPreviewTimelineReset;
    }

    public IReadOnlyList<CurvePoint> GetLatestData(int channelId, int count = -1)
    {
        if (!_channelBuffers.TryGetValue(channelId, out var buffer))
        {
            return Array.Empty<CurvePoint>();
        }

        return buffer.GetLatest(count);
    }

    public bool TryGetLatestTimestamp(int channelId, out double latestTimestampSeconds)
    {
        latestTimestampSeconds = 0.0;
        if (!_channelBuffers.TryGetValue(channelId, out var buffer))
        {
            return false;
        }

        return buffer.TryGetLatestTimestamp(out latestTimestampSeconds);
    }

    public void Clear()
    {
        _channelBuffers.Clear();
    }

    private void OnDataUpdated(object? sender, DataUpdateEventArgs e)
    {
        if (e.Data == null || e.Data.Count == 0)
        {
            return;
        }

        var buffer = _channelBuffers.GetOrAdd(
            e.ChannelId,
            _ => new ChannelSnapshotBuffer(_maxRetainedPoints));
        buffer.Append(e.Data);
    }

    private void OnChannelRemoved(object? sender, int channelId)
    {
        _channelBuffers.TryRemove(channelId, out _);
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

    private sealed class ChannelSnapshotBuffer
    {
        private readonly object _gate = new();
        private readonly int _maxRetainedPoints;
        private CurvePoint[] _snapshot = Array.Empty<CurvePoint>();
        private double _latestTimestampSeconds;
        private bool _hasLatestTimestamp;

        public ChannelSnapshotBuffer(int maxRetainedPoints)
        {
            _maxRetainedPoints = Math.Max(256, maxRetainedPoints);
        }

        public void Append(IReadOnlyList<CurvePoint> incoming)
        {
            lock (_gate)
            {
                int incomingCount = incoming.Count;
                if (incomingCount <= 0)
                {
                    return;
                }

                if (incomingCount >= _maxRetainedPoints)
                {
                    _snapshot = CopyTail(incoming, _maxRetainedPoints);
                    _latestTimestampSeconds = incoming[incomingCount - 1].X;
                    _hasLatestTimestamp = true;
                    return;
                }

                int retainedExisting = Math.Min(_snapshot.Length, _maxRetainedPoints - incomingCount);
                int newLength = retainedExisting + incomingCount;
                var next = new CurvePoint[newLength];

                if (retainedExisting > 0)
                {
                    Array.Copy(
                        _snapshot,
                        _snapshot.Length - retainedExisting,
                        next,
                        0,
                        retainedExisting);
                }

                for (int i = 0; i < incomingCount; i++)
                {
                    next[retainedExisting + i] = incoming[i];
                }

                _snapshot = next;
                _latestTimestampSeconds = incoming[incomingCount - 1].X;
                _hasLatestTimestamp = true;
            }
        }

        public IReadOnlyList<CurvePoint> GetLatest(int count)
        {
            lock (_gate)
            {
                if (_snapshot.Length == 0)
                {
                    return Array.Empty<CurvePoint>();
                }

                if (count <= 0 || count >= _snapshot.Length)
                {
                    return _snapshot;
                }

                var latest = new CurvePoint[count];
                Array.Copy(_snapshot, _snapshot.Length - count, latest, 0, count);
                return latest;
            }
        }

        public bool TryGetLatestTimestamp(out double latestTimestampSeconds)
        {
            lock (_gate)
            {
                latestTimestampSeconds = _latestTimestampSeconds;
                return _hasLatestTimestamp;
            }
        }

        private static CurvePoint[] CopyTail(IReadOnlyList<CurvePoint> source, int count)
        {
            int take = Math.Min(count, source.Count);
            var result = new CurvePoint[take];
            int start = source.Count - take;
            for (int i = 0; i < take; i++)
            {
                result[i] = source[start + i];
            }

            return result;
        }
    }
}
