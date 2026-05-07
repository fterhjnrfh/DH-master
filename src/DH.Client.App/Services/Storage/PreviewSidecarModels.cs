using System;
using System.Collections.Generic;

namespace DH.Client.App.Services.Storage;

internal sealed record PreviewLevelSpec(
    string LevelName,
    long BucketSampleSpan);

internal sealed class PreviewBucketRecord
{
    public long StartSampleIndex { get; init; }

    public long EndSampleIndex { get; init; }

    public int SampleCount { get; init; }

    public double MinValue { get; init; }

    public double MaxValue { get; init; }

    public int MinOffsetInBucket { get; init; }

    public int MaxOffsetInBucket { get; init; }

    public double Sum { get; init; }

    public double SumSquares { get; init; }
}

internal sealed class PreviewFileIndexEntry
{
    public string LevelName { get; init; } = string.Empty;

    public int ChannelId { get; init; }

    public long BucketSampleSpan { get; init; }

    public string RelativeFilePath { get; init; } = string.Empty;

    public long BucketCount { get; set; }

    public long StartSampleIndex { get; set; }

    public long EndSampleIndex { get; set; }
}

internal sealed class PreviewIndexManifest
{
    public int Version { get; init; } = 1;

    public string SessionName { get; init; } = string.Empty;

    public double SampleRateHz { get; init; }

    public DateTime GeneratedAtUtc { get; init; }

    public IReadOnlyList<PreviewFileIndexEntry> Files { get; init; } = Array.Empty<PreviewFileIndexEntry>();
}

internal sealed class PreviewSidecarArtifacts
{
    public string RootPath { get; init; } = string.Empty;

    public string IndexManifestPath { get; init; } = string.Empty;

    public IReadOnlyList<string> DataFilePaths { get; init; } = Array.Empty<string>();
}
