using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;
using DH.Client.App.Services.Performance;

namespace DH.Client.App.Data
{
    public class DataBus : IDataBus
    {
        private readonly ConcurrentDictionary<int, RingBuffer<CurvePoint>> _channelBuffers = new();

        private const int DefaultBufferSize = 16384;
        private const int MinPreviewPointsPerFrame = 4;
        private const int MaxPreviewPointsPerFrame = 512;
        private const int TargetPreviewPointsPerSecond = 1200;
        private readonly int _bufferSize;
        private long _previewOriginTicks;
        private readonly ConcurrentDictionary<int, long> _channelPreviewSampleCounts = new();
        private readonly ConcurrentDictionary<int, ChannelIngressStats> _channelIngressStats = new();

        public event EventHandler<int> ChannelAdded;
        public event EventHandler<int> ChannelRemoved;
        public event EventHandler<DataUpdateEventArgs> DataUpdated;
        public event EventHandler? PreviewTimelineReset;

        private readonly ConcurrentDictionary<int, Channel<IDataFrame>> _channels = new();
        private readonly ConcurrentDictionary<int, int> _channelSubscriberCounts = new();

        public DataBus(int bufferSize = DefaultBufferSize)
        {
            _bufferSize = bufferSize;
        }

        public async IAsyncEnumerable<IDataFrame> SubscribeChannel(int channelId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());
            _channelSubscriberCounts.AddOrUpdate(channelId, 1, static (_, count) => count + 1);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frame = await channel.Reader.ReadAsync(ct);
                    yield return frame;
                }
            }
            finally
            {
                _channelSubscriberCounts.AddOrUpdate(channelId, 0, static (_, count) => Math.Max(0, count - 1));
            }
        }

        public async ValueTask PublishFrameAsync(IDataFrame frame, CancellationToken ct = default)
        {
            if (frame == null)
                return;

            int channelId = frame.ChannelId;
            RecordIngress(frame);
            var channel = _channels.GetOrAdd(channelId, _ => Channel.CreateUnbounded<IDataFrame>());

            if (_channelSubscriberCounts.TryGetValue(channelId, out var subscriberCount) && subscriberCount > 0)
            {
                await channel.Writer.WriteAsync(frame, ct);
            }

            var points = ConvertFrameToCurvePoints(frame);
            PublishData(channelId, points);
        }

        public async IAsyncEnumerable<IDataFrame> SubscribeAll([EnumeratorCancellation] CancellationToken ct)
        {
            var mergedChannel = Channel.CreateUnbounded<IDataFrame>();
            var tasks = new List<Task>();

            foreach (var channelId in _channels.Keys)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await foreach (var frame in SubscribeChannel(channelId, ct))
                    {
                        await mergedChannel.Writer.WriteAsync(frame, ct);
                    }
                }, ct));
            }

            tasks.Add(Task.Run(async () =>
            {
                var knownChannels = new HashSet<int>(_channels.Keys);

                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(100, ct);

                    foreach (var channelId in _channels.Keys)
                    {
                        if (!knownChannels.Add(channelId))
                            continue;

                        _ = Task.Run(async () =>
                        {
                            await foreach (var frame in SubscribeChannel(channelId, ct))
                            {
                                await mergedChannel.Writer.WriteAsync(frame, ct);
                            }
                        }, ct);
                    }
                }
            }, ct));

            while (!ct.IsCancellationRequested)
            {
                var frame = await mergedChannel.Reader.ReadAsync(ct);
                yield return frame;
            }
        }

        public void EnsureChannel(int channelId)
        {
            _channelBuffers.GetOrAdd(channelId, _ =>
            {
                int cap = _bufferSize <= 0 ? DefaultBufferSize : _bufferSize;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand: false);
                OnChannelAdded(channelId);
                return newBuffer;
            });
        }

        private IReadOnlyList<CurvePoint> ConvertFrameToCurvePoints(IDataFrame frame)
        {
            var samples = frame.Samples;
            if (samples.IsEmpty)
                return Array.Empty<CurvePoint>();

            int sampleRate = frame.Header?.SampleRate ?? 1000;
            sampleRate = Math.Max(1, sampleRate);
            double defaultTimeInterval = 1.0 / sampleRate;
            double timeInterval = frame.Header?.SampleIntervalSeconds is > 0
                ? frame.Header.SampleIntervalSeconds.Value
                : defaultTimeInterval;
            double frameDurationSeconds = samples.Length > 1
                ? (samples.Length - 1) * timeInterval
                : timeInterval;
            long newTotalSampleCount = _channelPreviewSampleCounts.AddOrUpdate(
                frame.ChannelId,
                samples.Length,
                (_, existing) => existing + samples.Length);
            long frameStartSampleIndex = frame.Header?.StartSampleIndex ?? Math.Max(0, newTotalSampleCount - samples.Length);
            double frameStartTime = frameStartSampleIndex * defaultTimeInterval;

            int targetPointCount = (int)Math.Ceiling(frameDurationSeconds * TargetPreviewPointsPerSecond);
            int maxOutputPoints = Math.Clamp(targetPointCount, MinPreviewPointsPerFrame, MaxPreviewPointsPerFrame);
            maxOutputPoints = Math.Min(samples.Length, maxOutputPoints);
            var previewPoints = BuildEnvelopePreviewPoints(samples.Span, frameStartTime, timeInterval, maxOutputPoints);
            double frameEndTime = frameStartTime + (Math.Max(0, samples.Length - 1) * timeInterval);
            RenderPhaseTimingLogger.LogRealtimeChannelTimebase(
                frame.ChannelId,
                samples.Length,
                sampleRate,
                frameDurationSeconds,
                newTotalSampleCount,
                frameStartTime,
                frameEndTime,
                previewPoints.Count);
            return previewPoints;
        }

        private static IReadOnlyList<CurvePoint> BuildEnvelopePreviewPoints(
            ReadOnlySpan<float> samples,
            double startTime,
            double timeInterval,
            int maxOutputPoints)
        {
            if (samples.IsEmpty || maxOutputPoints <= 0)
            {
                return Array.Empty<CurvePoint>();
            }

            if (samples.Length <= maxOutputPoints)
            {
                var full = new CurvePoint[samples.Length];
                for (int i = 0; i < samples.Length; i++)
                {
                    full[i] = new CurvePoint(startTime + (i * timeInterval), samples[i]);
                }

                return full;
            }

            int bucketCount = Math.Max(1, Math.Min(samples.Length, maxOutputPoints / 2));
            var points = new List<CurvePoint>(Math.Min(maxOutputPoints, bucketCount * 4));

            for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
            {
                int bucketStart = (int)Math.Floor(bucketIndex * samples.Length / (double)bucketCount);
                int bucketEnd = (int)Math.Floor((bucketIndex + 1) * samples.Length / (double)bucketCount) - 1;

                bucketStart = Math.Clamp(bucketStart, 0, samples.Length - 1);
                bucketEnd = Math.Clamp(bucketEnd, bucketStart, samples.Length - 1);

                int minIndex = bucketStart;
                int maxIndex = bucketStart;
                float minValue = samples[bucketStart];
                float maxValue = minValue;

                for (int sampleIndex = bucketStart + 1; sampleIndex <= bucketEnd; sampleIndex++)
                {
                    float value = samples[sampleIndex];
                    if (value < minValue)
                    {
                        minValue = value;
                        minIndex = sampleIndex;
                    }

                    if (value > maxValue)
                    {
                        maxValue = value;
                        maxIndex = sampleIndex;
                    }
                }

                AppendEnvelopePointsInTimeOrder(
                    points,
                    samples,
                    startTime,
                    timeInterval,
                    bucketStart,
                    minIndex,
                    maxIndex,
                    bucketEnd);
            }

            if (points.Count > maxOutputPoints)
            {
                points.RemoveRange(maxOutputPoints, points.Count - maxOutputPoints);
            }

            return points;
        }

        private static void AppendEnvelopePoint(
            List<CurvePoint> points,
            ReadOnlySpan<float> samples,
            double startTime,
            double timeInterval,
            int sampleIndex)
        {
            double x = startTime + (sampleIndex * timeInterval);
            double y = samples[sampleIndex];

            if (points.Count > 0)
            {
                var last = points[^1];
                if (Math.Abs(last.X - x) < 1e-12 && Math.Abs(last.Y - y) < 1e-12)
                {
                    return;
                }
            }

            points.Add(new CurvePoint(x, y));
        }

        private static void AppendEnvelopePointsInTimeOrder(
            List<CurvePoint> points,
            ReadOnlySpan<float> samples,
            double startTime,
            double timeInterval,
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

                AppendEnvelopePoint(points, samples, startTime, timeInterval, currentIndex);
                lastIndex = currentIndex;
            }
        }

        public void PublishData(int channelId, IReadOnlyList<CurvePoint> data)
        {
            if (data == null || data.Count == 0)
                return;

            var buffer = _channelBuffers.GetOrAdd(channelId, _ =>
            {
                int cap = _bufferSize <= 0 ? DefaultBufferSize : _bufferSize;
                var newBuffer = new RingBuffer<CurvePoint>(cap, allowExpand: false);
                OnChannelAdded(channelId);
                return newBuffer;
            });

            buffer.AddRange(data);
            OnDataUpdated(channelId, data);
        }

        public IReadOnlyList<CurvePoint> GetLatestData(int channelId, int count = -1)
        {
            if (_channelBuffers.TryGetValue(channelId, out var buffer))
            {
                if (count <= 0)
                    return buffer.GetAll();
                return buffer.GetLatest(count);
            }

            return Array.Empty<CurvePoint>();
        }

        public IReadOnlyList<int> GetAvailableChannels()
        {
            return new List<int>(_channelBuffers.Keys);
        }

        public void RemoveChannel(int channelId)
        {
            if (_channelBuffers.TryRemove(channelId, out _))
            {
                OnChannelRemoved(channelId);
            }
        }

        public void ResetPreviewTimeline(bool clearBuffers = true)
        {
            Interlocked.Exchange(ref _previewOriginTicks, 0);
            _channelPreviewSampleCounts.Clear();
            _channelIngressStats.Clear();

            if (!clearBuffers)
            {
                return;
            }

            foreach (var buffer in _channelBuffers.Values)
            {
                buffer.Clear();
            }

            PreviewTimelineReset?.Invoke(this, EventArgs.Empty);
        }

        private void RecordIngress(IDataFrame frame)
        {
            int declaredSampleRate = Math.Max(1, frame.Header?.SampleRate ?? 1000);
            string producerTag = string.IsNullOrWhiteSpace(frame.Header?.ProducerTag)
                ? "unknown"
                : frame.Header.ProducerTag;
            int frameSamples = frame.Samples.Length;
            DateTime nowUtc = DateTime.UtcNow;
            var stats = _channelIngressStats.GetOrAdd(frame.ChannelId, static _ => new ChannelIngressStats());

            lock (stats.SyncRoot)
            {
                stats.TotalFrames++;
                stats.TotalSamples += frameSamples;

                double wallDeltaMs = stats.LastObservedAtUtc == DateTime.MinValue
                    ? 0.0
                    : (nowUtc - stats.LastObservedAtUtc).TotalMilliseconds;
                double frameTimestampDeltaMs = stats.LastFrameTimestampUtc == DateTime.MinValue
                    ? 0.0
                    : (frame.Timestamp - stats.LastFrameTimestampUtc).TotalMilliseconds;

                if (stats.SummaryWindowStartUtc == DateTime.MinValue)
                {
                    stats.SummaryWindowStartUtc = nowUtc;
                    stats.SummaryFrames = 0;
                    stats.SummarySamples = 0;
                }

                stats.SummaryFrames++;
                stats.SummarySamples += frameSamples;

                double summaryWallSeconds = Math.Max(0.0, (nowUtc - stats.SummaryWindowStartUtc).TotalSeconds);
                if (summaryWallSeconds >= 1.0)
                {
                    double effectiveFramesPerSecond = stats.SummaryFrames / Math.Max(1e-9, summaryWallSeconds);
                    double effectiveSamplesPerSecond = stats.SummarySamples / Math.Max(1e-9, summaryWallSeconds);
                    RenderPhaseTimingLogger.LogRealtimeChannelIngress(
                        frame.ChannelId,
                        frame.FrameId,
                        producerTag,
                        frameSamples,
                        declaredSampleRate,
                        frame.Timestamp.ToUniversalTime().ToString("O"),
                        wallDeltaMs,
                        frameTimestampDeltaMs,
                        stats.TotalFrames,
                        stats.TotalSamples,
                        summaryWallSeconds,
                        stats.SummaryFrames,
                        stats.SummarySamples,
                        effectiveFramesPerSecond,
                        effectiveSamplesPerSecond);

                    stats.SummaryWindowStartUtc = nowUtc;
                    stats.SummaryFrames = 0;
                    stats.SummarySamples = 0;
                }

                stats.LastObservedAtUtc = nowUtc;
                stats.LastFrameTimestampUtc = frame.Timestamp;
            }
        }

        private sealed class ChannelIngressStats
        {
            public object SyncRoot { get; } = new();
            public DateTime LastObservedAtUtc { get; set; }
            public DateTime LastFrameTimestampUtc { get; set; }
            public DateTime SummaryWindowStartUtc { get; set; }
            public long TotalFrames { get; set; }
            public long TotalSamples { get; set; }
            public long SummaryFrames { get; set; }
            public long SummarySamples { get; set; }
        }

        private void OnChannelAdded(int channelId)
        {
            ChannelAdded?.Invoke(this, channelId);
        }

        private void OnChannelRemoved(int channelId)
        {
            ChannelRemoved?.Invoke(this, channelId);
        }

        private void OnDataUpdated(int channelId, IReadOnlyList<CurvePoint> data)
        {
            DataUpdated?.Invoke(this, new DataUpdateEventArgs(channelId, data));
        }
    }

    public class DataUpdateEventArgs : EventArgs
    {
        public int ChannelId { get; }
        public IReadOnlyList<CurvePoint> Data { get; }

        public DataUpdateEventArgs(int channelId, IReadOnlyList<CurvePoint> data)
        {
            ChannelId = channelId;
            Data = data;
        }
    }
}
