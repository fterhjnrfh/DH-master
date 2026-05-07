using System;
using System.Collections.Generic;
using DH.Contracts.Models;

namespace DH.Client.App.Data.Query;

public sealed record PreviewLevelBuildRequest
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public IReadOnlyList<int> ChannelIds { get; init; } = Array.Empty<int>();
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
    public int MaxPointsPerChannel { get; init; }
    public bool RequireEnvelopeSemantics { get; init; } = true;
    public TimeAxisKind TimeAxisKind { get; init; } = TimeAxisKind.SampleIndexMappedTime;
    public long SourceVersion { get; init; }
    public long DataEpoch { get; init; }
}

public sealed record PreviewLevelBuildResult
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
    public int MaxPointsPerChannel { get; init; }
    public long SourceVersion { get; init; }
    public long DataEpoch { get; init; }
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public bool IsComplete { get; init; }
    public BuildState BuildState { get; init; } = BuildState.Ready;
    public TimeAxisKind TimeAxisKind { get; init; } = TimeAxisKind.SampleIndexMappedTime;
    public IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> ChannelData { get; init; } =
        new Dictionary<int, IReadOnlyList<CurvePoint>>();
    public int MaxActualPointsPerChannel { get; init; }
    public long TotalActualPoints { get; init; }
}

public sealed record PreviewLevelReadRequest
{
    public Guid SessionId { get; init; }
    public string? ViewId { get; init; }
    public IReadOnlyList<int> ChannelIds { get; init; } = Array.Empty<int>();
    public double WindowStart { get; init; }
    public double WindowEnd { get; init; }
    public PreviewLevel PreviewLevel { get; init; } = PreviewLevel.L1;
    public int MaxPointsPerChannel { get; init; }
    public long SourceVersion { get; init; }
    public long DataEpoch { get; init; }
}

public sealed record PreviewLevelReadResult
{
    public bool CacheHit { get; init; }
    public PreviewLevelBuildResult? Snapshot { get; init; }
}
