using System;
using System.Collections.Concurrent;
using System.Threading;

namespace DH.Driver.SDK;

internal static class SdkRawFloatBufferPool
{
    private const int MaxRetainedBuffersPerSize = 8;

    private sealed class BufferBucket
    {
        public ConcurrentStack<float[]> Buffers { get; } = new();

        public int RetainedCount;
    }

    private static readonly ConcurrentDictionary<int, BufferBucket> Buckets = new();

    public static float[] Rent(int floatCount)
    {
        if (floatCount <= 0)
        {
            return Array.Empty<float>();
        }

        var bucket = Buckets.GetOrAdd(floatCount, static _ => new BufferBucket());
        if (bucket.Buffers.TryPop(out var buffer))
        {
            Interlocked.Decrement(ref bucket.RetainedCount);
            return buffer;
        }

        return new float[floatCount];
    }

    public static void Return(float[]? buffer)
    {
        if (buffer == null || buffer.Length == 0)
        {
            return;
        }

        var bucket = Buckets.GetOrAdd(buffer.Length, static _ => new BufferBucket());
        while (true)
        {
            int current = Volatile.Read(ref bucket.RetainedCount);
            if (current >= MaxRetainedBuffersPerSize)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref bucket.RetainedCount, current + 1, current) == current)
            {
                bucket.Buffers.Push(buffer);
                return;
            }
        }
    }
}

/// <summary>
/// Raw SDK callback block preserved in its original interleaved layout.
/// </summary>
public sealed class SdkRawBlock : IDisposable
{
    private float[] _interleavedSamples = Array.Empty<float>();
    private int _payloadFloatCount;
    private int _released;

    public long SampleTime { get; init; }

    public int MessageType { get; init; }

    public int GroupId { get; init; }

    public int MachineId { get; init; }

    public long TotalDataCount { get; init; }

    public int DataCountPerChannel { get; init; }

    public int BufferCountBytes { get; init; }

    public int BlockIndex { get; init; }

    public int ChannelCount { get; init; }

    public float SampleRateHz { get; init; }

    public DateTime ReceivedAtUtc { get; init; }

    public float[] InterleavedSamples
    {
        get => _interleavedSamples;
        init => _interleavedSamples = value ?? Array.Empty<float>();
    }

    public int PayloadFloatCount
    {
        get => _payloadFloatCount > 0 ? _payloadFloatCount : _interleavedSamples.Length;
        init => _payloadFloatCount = value;
    }

    public bool ReturnBufferToPool { get; init; }

    public int PayloadBytes => PayloadFloatCount * sizeof(float);

    public ReadOnlySpan<float> PayloadSpan => _interleavedSamples.AsSpan(0, PayloadFloatCount);

    public void ReleasePayload()
    {
        if (!ReturnBufferToPool)
        {
            return;
        }

        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        var buffer = _interleavedSamples;
        _interleavedSamples = Array.Empty<float>();
        SdkRawFloatBufferPool.Return(buffer);
    }

    public void Dispose()
    {
        ReleasePayload();
    }
}
