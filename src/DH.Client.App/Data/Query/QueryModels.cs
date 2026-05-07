using System;
using System.Collections.Generic;
using DH.Contracts.Models;

namespace DH.Client.App.Data.Query;

public sealed record SessionDescriptor
{
    public Guid SessionId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset? EndTime { get; init; }
    public string StorageFormat { get; init; } = string.Empty;
    public bool Recovered { get; init; }
    public IReadOnlyList<SourceDescriptor> Sources { get; init; } = Array.Empty<SourceDescriptor>();
    public IReadOnlyList<PreviewLevel> PreviewLevels { get; init; } = Array.Empty<PreviewLevel>();
}

public sealed record SourceDescriptor
{
    public int SourceId { get; init; }
    public string DeviceName { get; init; } = string.Empty;
    public int ChannelCount { get; init; }
    public double SampleRateHz { get; init; }
    public TimeAxisKind TimeAxisKind { get; init; } = TimeAxisKind.SampleIndexMappedTime;
}

public sealed record SegmentDescriptor
{
    public string SegmentId { get; init; } = string.Empty;
    public string StreamKind { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public DateTimeOffset StartTime { get; init; }
    public DateTimeOffset EndTime { get; init; }
    public long? StartSampleIndex { get; init; }
    public long? EndSampleIndex { get; init; }
    public IReadOnlyList<int> SourceIds { get; init; } = Array.Empty<int>();
    public string ChannelRange { get; init; } = string.Empty;
    public bool IsClosed { get; init; }
}

public sealed record PreviewReadRequest
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public IReadOnlyList<int>? SourceIds { get; init; }
    public IReadOnlyList<int> ChannelIds { get; init; } = Array.Empty<int>();
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
    public int MaxPointsPerChannel { get; init; }
    public bool RequireEnvelopeSemantics { get; init; } = true;
    public TimeAxisKind? PreferredTimeAxisKind { get; init; }
    public bool AllowDegradedResult { get; init; } = true;
    public bool RequireCompleteWindow { get; init; }
    public long? RequestedSourceVersion { get; init; }
}

public sealed record CurveWindowSnapshot
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public IReadOnlyList<int> ChannelIds { get; init; } = Array.Empty<int>();
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
    public long Version { get; init; }
    public long DataEpoch { get; init; }
    public long SourceVersion { get; init; }
    public IReadOnlyList<string>? SegmentRange { get; init; }
    public bool IsPreview { get; init; }
    public bool IsComplete { get; init; }
    public BuildState BuildState { get; init; } = BuildState.Ready;
    public bool Recovered { get; init; }
    public TimeAxisKind TimeAxisKind { get; init; } = TimeAxisKind.SampleIndexMappedTime;
    public IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> ChannelData { get; init; } =
        new Dictionary<int, IReadOnlyList<CurvePoint>>();
    public int MaxActualPointsPerChannel { get; init; }
    public long TotalActualPoints { get; init; }
}

public readonly record struct CurveFrameVersion(
    Guid SessionId,
    long Version,
    long DataEpoch,
    DateTimeOffset UpdatedAt);

public sealed record RawReadRequest
{
    public Guid SessionId { get; init; }
    public IReadOnlyList<string>? SegmentIds { get; init; }
    public IReadOnlyList<int>? ChannelIds { get; init; }
    public double? WindowStart { get; init; }
    public double? WindowEnd { get; init; }
    public long? StartSampleIndex { get; init; }
    public long? EndSampleIndex { get; init; }
}

public sealed record RawSegmentReadResult
{
    public Guid SessionId { get; init; }
    public IReadOnlyList<string> SegmentIds { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> ChannelData { get; init; } =
        new Dictionary<int, IReadOnlyList<CurvePoint>>();
    public bool IsComplete { get; init; }
    public TimeAxisKind TimeAxisKind { get; init; } = TimeAxisKind.SampleIndexMappedTime;
}

public sealed record CurveStatisticsRequest
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public IReadOnlyList<int> ChannelIds { get; init; } = Array.Empty<int>();
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
}

public sealed record CurveChannelStatistics
{
    public int ChannelId { get; init; }
    public long Count { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Sum { get; init; }
    public double SumSquares { get; init; }
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record CurveStatisticsResult
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; }
    public IReadOnlyDictionary<int, CurveChannelStatistics> ChannelStatistics { get; init; } =
        new Dictionary<int, CurveChannelStatistics>();
    public bool IsComplete { get; init; }
    public BuildState BuildState { get; init; } = BuildState.Ready;
}
