using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DH.Client.App.Services.Performance;
using DH.Client.App.Services.Storage;
using DH.Contracts;
using DH.Contracts.Models;

namespace DH.Client.App.Data.Query;

public sealed class PersistedPreviewQueryRuntime :
    ICurveQueryService,
    ICurveFrameProvider,
    ICurveStatisticsService
{
    private const int PreviewBucketRecordSize = 60;
    private const int RawIndexEntrySize = 40;
    private const int FastSegmentHeaderBytes = 4096;
    private const double RawStatisticsMaxWindowSeconds = 2.0;

    private readonly string _sessionPath;
    private readonly SessionDescriptor _session;
    private readonly ISessionArtifactLocator _artifactLocator;
    private PersistedPreviewIndexManifest? _indexManifest;
    private IReadOnlyList<string>? _tdmsFiles;
    private IReadOnlyList<TdmsSegmentTimelineEntry>? _tdmsSegmentTimeline;
    private RawIndexManifestModel? _rawIndexManifest;
    private FastSegmentTimeline? _fastSegmentTimeline;

    public PersistedPreviewQueryRuntime(
        string sessionPath,
        SessionDescriptor session,
        ISessionArtifactLocator? artifactLocator = null)
    {
        _sessionPath = string.IsNullOrWhiteSpace(sessionPath)
            ? throw new ArgumentException("Session path is required.", nameof(sessionPath))
            : Path.GetFullPath(sessionPath);
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _artifactLocator = artifactLocator ?? new FileSystemSessionArtifactLocator();
    }

    public CurveFrameVersion GetLatestVersion(Guid sessionId)
    {
        if (sessionId != _session.SessionId)
        {
            return new CurveFrameVersion(sessionId, 0, 0, DateTimeOffset.MinValue);
        }

        return new CurveFrameVersion(
            _session.SessionId,
            1,
            1,
            _session.EndTime ?? _session.StartTime);
    }

    public ValueTask<CurveWindowSnapshot> GetLatestAsync(
        PreviewReadRequest request,
        CancellationToken ct = default)
    {
        return QueryAsync(request, ct);
    }

    public async ValueTask<CurveWindowSnapshot> QueryAsync(
        PreviewReadRequest request,
        CancellationToken ct = default)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        var validation = CurveQueryValidator.ValidateRequest(request);
        if (!validation.IsValid || request.SessionId != _session.SessionId)
        {
            return CreateMissingSnapshot(request);
        }

        if (request.PreviewLevel == PreviewLevel.L0)
        {
            return await QueryRawTdmsAsync(request, ct);
        }

        PersistedPreviewIndexManifest manifest = await EnsureIndexManifestAsync(ct);
        double sampleRateHz = manifest.SampleRateHz > 0
            ? manifest.SampleRateHz
            : _session.Sources.FirstOrDefault()?.SampleRateHz ?? 0.0;
        if (sampleRateHz <= 0)
        {
            return CreateMissingSnapshot(request);
        }

        long requestedStartSample = Math.Max(0L, (long)Math.Floor(request.WindowStart * sampleRateHz));
        long requestedEndSampleExclusive = Math.Max(requestedStartSample + 1L, (long)Math.Ceiling(request.WindowEnd * sampleRateHz));

        var channelData = new Dictionary<int, IReadOnlyList<CurvePoint>>();
        int maxActualPointsPerChannel = 0;
        long totalActualPoints = 0;
        bool allChannelsComplete = true;
        bool anyData = false;

        foreach (int channelId in request.ChannelIds.Distinct().OrderBy(id => id))
        {
            PersistedPreviewIndexEntry? entry = manifest.Files
                .Where(file => file.ChannelId == channelId && file.LevelName.Equals(request.PreviewLevel.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativeFilePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (entry is null)
            {
                allChannelsComplete = false;
                channelData[channelId] = Array.Empty<CurvePoint>();
                continue;
            }

            var points = ReadPreviewWindowPoints(manifest.RootPath, entry, requestedStartSample, requestedEndSampleExclusive, sampleRateHz);
            channelData[channelId] = points;

            int actualPoints = points.Count;
            maxActualPointsPerChannel = Math.Max(maxActualPointsPerChannel, actualPoints);
            totalActualPoints += actualPoints;
            anyData |= actualPoints > 0;

            bool channelComplete =
                entry.StartSampleIndex >= 0 &&
                entry.StartSampleIndex <= requestedStartSample &&
                entry.EndSampleIndex >= requestedEndSampleExclusive - 1;
            allChannelsComplete &= channelComplete;
        }

        BuildState buildState = !anyData
            ? BuildState.Missing
            : (allChannelsComplete ? BuildState.Ready : BuildState.Degraded);
        var snapshot = new CurveWindowSnapshot
        {
            SessionId = _session.SessionId,
            ViewId = request.ViewId,
            ChannelIds = request.ChannelIds,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            Version = 1,
            DataEpoch = 1,
            SourceVersion = 1,
            SegmentRange = null,
            IsPreview = true,
            IsComplete = allChannelsComplete,
            BuildState = buildState,
            Recovered = _session.Recovered,
            TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
            ChannelData = channelData,
            MaxActualPointsPerChannel = maxActualPointsPerChannel,
            TotalActualPoints = totalActualPoints
        };
        timing.Stop();
        RenderPhaseTimingLogger.LogPersistedPreviewQuery(
            _session.SessionId.ToString("N"),
            request.ViewId,
            request.PreviewLevel.ToString(),
            request.ChannelIds.Count,
            indexHit: true,
            timing.Elapsed.TotalMilliseconds,
            snapshot.TotalActualPoints,
            snapshot.IsComplete);
        return snapshot;
    }

    public async ValueTask<CurveStatisticsResult> QueryStatisticsAsync(
        CurveStatisticsRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request.SessionId != _session.SessionId
            || request.WindowEnd <= request.WindowStart
            || request.ChannelIds.Count == 0)
        {
            return CreateMissingStatistics(request);
        }

        PersistedPreviewIndexManifest manifest = await EnsureIndexManifestAsync(ct);
        double sampleRateHz = manifest.SampleRateHz > 0
            ? manifest.SampleRateHz
            : _session.Sources.FirstOrDefault()?.SampleRateHz ?? 0.0;
        if (sampleRateHz <= 0)
        {
            return CreateMissingStatistics(request);
        }

        PreviewLevel level = SelectStatisticsPreviewLevel(manifest, request.PreviewLevel);
        long requestedStartSample = Math.Max(0L, (long)Math.Floor(request.WindowStart * sampleRateHz));
        long requestedEndSampleExclusive = Math.Max(requestedStartSample + 1L, (long)Math.Ceiling(request.WindowEnd * sampleRateHz));
        RawIndexManifestModel? rawIndexManifest = null;
        bool useRawStatistics = request.WindowEnd - request.WindowStart <= RawStatisticsMaxWindowSeconds;
        if (useRawStatistics)
        {
            rawIndexManifest = await EnsureRawIndexManifestAsync(ct);
            useRawStatistics = rawIndexManifest is not null && rawIndexManifest.Channels.Count > 0;
        }

        var statistics = new Dictionary<int, CurveChannelStatistics>();
        bool anyData = false;
        bool allComplete = true;
        foreach (int channelId in request.ChannelIds.Distinct().OrderBy(id => id))
        {
            if (useRawStatistics && rawIndexManifest is not null)
            {
                CurveChannelStatistics rawStatistics = ReadRawWindowStatistics(
                    rawIndexManifest,
                    channelId,
                    request.WindowStart,
                    request.WindowEnd);
                statistics[channelId] = rawStatistics;
                anyData |= rawStatistics.Count > 0;
                allComplete &= rawStatistics.IsComplete;
                continue;
            }

            PersistedPreviewIndexEntry? entry = manifest.Files
                .Where(file => file.ChannelId == channelId && file.LevelName.Equals(level.ToString(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.RelativeFilePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (entry is null)
            {
                allComplete = false;
                statistics[channelId] = CreateEmptyChannelStatistics(channelId, complete: false);
                continue;
            }

            CurveChannelStatistics channelStatistics = ReadPreviewWindowStatistics(
                manifest.RootPath,
                entry,
                requestedStartSample,
                requestedEndSampleExclusive,
                channelId);
            statistics[channelId] = channelStatistics;
            anyData |= channelStatistics.Count > 0;
            allComplete &= channelStatistics.IsComplete;
        }

        return new CurveStatisticsResult
        {
            SessionId = _session.SessionId,
            ViewId = request.ViewId,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = useRawStatistics ? PreviewLevel.L0 : level,
            ChannelStatistics = statistics,
            IsComplete = allComplete,
            BuildState = !anyData
                ? BuildState.Missing
                : (allComplete ? BuildState.Ready : BuildState.Degraded)
        };
    }

    private async ValueTask<CurveWindowSnapshot> QueryRawTdmsAsync(
        PreviewReadRequest request,
        CancellationToken ct)
    {
        var timing = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<string> rawFiles = await EnsureTdmsFilesAsync(ct);
        RawIndexManifestModel? rawIndexManifest = await EnsureRawIndexManifestAsync(ct);
        if ((rawIndexManifest is null || rawIndexManifest.Channels.Count == 0) && rawFiles.Count == 0)
        {
            return CreateMissingSnapshot(request);
        }

        double sampleRateHz = _session.Sources.FirstOrDefault()?.SampleRateHz ?? 0.0;
        if (sampleRateHz <= 0)
        {
            return CreateMissingSnapshot(request);
        }

        var channelData = new Dictionary<int, IReadOnlyList<CurvePoint>>();
        int maxActualPointsPerChannel = 0;
        long totalActualPoints = 0;
        bool allChannelsComplete = true;
        bool anyData = false;
        bool tdmsFallbackUsed = false;
        FastSegmentTimeline? fastSegmentTimeline = EnsureFastSegmentTimeline(rawFiles, sampleRateHz);
        IReadOnlyList<TdmsSegmentTimelineEntry> tdmsSegmentTimeline = await EnsureTdmsSegmentTimelineAsync(sampleRateHz, ct);
        bool hasFastSegments = fastSegmentTimeline is not null;
        bool rawIndexHit = !hasFastSegments && rawIndexManifest is not null && rawIndexManifest.Channels.Count > 0;
        IReadOnlyList<string> tdmsFiles = hasFastSegments
            ? rawFiles.Where(path => !IsFastSegmentFile(path)).ToArray()
            : rawFiles;

        foreach (int channelId in request.ChannelIds.Distinct().OrderBy(id => id))
        {
            IReadOnlyList<CurvePoint> points;
            if (fastSegmentTimeline is not null)
            {
                points = ReadFastSegmentWindowPoints(fastSegmentTimeline, channelId, request.WindowStart, request.WindowEnd);
            }
            else if (rawIndexManifest is not null)
            {
                points = ReadRawWindowPoints(rawIndexManifest, channelId, request.WindowStart, request.WindowEnd);
                if (points.Count == 0 && tdmsFiles.Count > 0)
                {
                    tdmsFallbackUsed = true;
                    points = tdmsSegmentTimeline.Count > 0
                        ? ReadTdmsSegmentWindowPoints(tdmsSegmentTimeline, channelId, request.WindowStart, request.WindowEnd, sampleRateHz, request.MaxPointsPerChannel, request.RequireEnvelopeSemantics)
                        : ReadRawWindowPoints(tdmsFiles, channelId, request.WindowStart, request.WindowEnd);
                }
            }
            else
            {
                tdmsFallbackUsed = true;
                points = tdmsSegmentTimeline.Count > 0
                    ? ReadTdmsSegmentWindowPoints(tdmsSegmentTimeline, channelId, request.WindowStart, request.WindowEnd, sampleRateHz, request.MaxPointsPerChannel, request.RequireEnvelopeSemantics)
                    : ReadRawWindowPoints(tdmsFiles, channelId, request.WindowStart, request.WindowEnd);
            }
            channelData[channelId] = points;

            int actualPoints = points.Count;
            maxActualPointsPerChannel = Math.Max(maxActualPointsPerChannel, actualPoints);
            totalActualPoints += actualPoints;
            anyData |= actualPoints > 0;
            allChannelsComplete &= actualPoints > 0;
        }

        BuildState buildState = !anyData
            ? BuildState.Missing
            : (allChannelsComplete ? BuildState.Ready : BuildState.Degraded);
        var snapshot = new CurveWindowSnapshot
        {
            SessionId = _session.SessionId,
            ViewId = request.ViewId,
            ChannelIds = request.ChannelIds,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = PreviewLevel.L0,
            Version = 1,
            DataEpoch = 1,
            SourceVersion = 1,
            SegmentRange = rawFiles,
            IsPreview = false,
            IsComplete = allChannelsComplete,
            BuildState = buildState,
            Recovered = _session.Recovered,
            TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
            ChannelData = channelData,
            MaxActualPointsPerChannel = maxActualPointsPerChannel,
            TotalActualPoints = totalActualPoints
        };
        timing.Stop();
        RenderPhaseTimingLogger.LogPersistedRawQuery(
            _session.SessionId.ToString("N"),
            request.ViewId,
            request.ChannelIds.Count,
            rawIndexHit,
            tdmsFallbackUsed,
            timing.Elapsed.TotalMilliseconds,
            snapshot.TotalActualPoints,
            snapshot.IsComplete);
        return snapshot;
    }

    private async ValueTask<PersistedPreviewIndexManifest> EnsureIndexManifestAsync(CancellationToken ct)
    {
        if (_indexManifest is not null)
        {
            return _indexManifest;
        }

        SessionArtifactPaths artifacts = await _artifactLocator.DiscoverAsync(_sessionPath, ct);
        if (string.IsNullOrWhiteSpace(artifacts.PreviewIndexManifestPath))
        {
            throw new FileNotFoundException("No preview.index.json was found.", _sessionPath);
        }

        await using var stream = File.OpenRead(artifacts.PreviewIndexManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<PersistedPreviewIndexManifest>(stream, cancellationToken: ct)
            ?? throw new InvalidDataException("Failed to deserialize preview.index.json.");

        manifest.RootPath = Path.GetDirectoryName(artifacts.PreviewIndexManifestPath) ?? _sessionPath;
        _indexManifest = manifest;
        return manifest;
    }

    private async ValueTask<IReadOnlyList<string>> EnsureTdmsFilesAsync(CancellationToken ct)
    {
        if (_tdmsFiles is not null)
        {
            return _tdmsFiles;
        }

        SessionArtifactPaths artifacts = await _artifactLocator.DiscoverAsync(_sessionPath, ct);
        string manifestPath = artifacts.ManifestPath
            ?? throw new FileNotFoundException("No session manifest was found.", _sessionPath);

        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("TdmsFiles", out JsonElement tdmsFilesElement)
            || tdmsFilesElement.ValueKind != JsonValueKind.Array)
        {
            _tdmsFiles = Array.Empty<string>();
            return _tdmsFiles;
        }

        _tdmsFiles = tdmsFilesElement
            .EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Select(path => Path.GetFullPath(path!))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return _tdmsFiles;
    }

    private async ValueTask<IReadOnlyList<TdmsSegmentTimelineEntry>> EnsureTdmsSegmentTimelineAsync(
        double sampleRateHz,
        CancellationToken ct)
    {
        if (_tdmsSegmentTimeline is not null)
        {
            return _tdmsSegmentTimeline;
        }

        IReadOnlyList<string> tdmsFiles = await EnsureTdmsFilesAsync(ct);
        if (tdmsFiles.Count == 0)
        {
            _tdmsSegmentTimeline = Array.Empty<TdmsSegmentTimelineEntry>();
            return _tdmsSegmentTimeline;
        }

        SessionArtifactPaths artifacts = await _artifactLocator.DiscoverAsync(_sessionPath, ct);
        var fromManifest = artifacts.ManifestPath is { } manifestPath && File.Exists(manifestPath)
            ? await ReadTdmsSegmentTimelineFromManifestAsync(manifestPath, sampleRateHz, ct)
            : Array.Empty<TdmsSegmentTimelineEntry>();

        _tdmsSegmentTimeline = fromManifest.Count > 0
            ? fromManifest
            : BuildTdmsSegmentTimelineFromFiles(tdmsFiles, sampleRateHz);
        return _tdmsSegmentTimeline;
    }

    private static async ValueTask<IReadOnlyList<TdmsSegmentTimelineEntry>> ReadTdmsSegmentTimelineFromManifestAsync(
        string manifestPath,
        double fallbackSampleRateHz,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(manifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("TdmsSegments", out JsonElement segmentsElement)
            || segmentsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<TdmsSegmentTimelineEntry>();
        }

        var entries = new List<TdmsSegmentTimelineEntry>();
        foreach (JsonElement element in segmentsElement.EnumerateArray())
        {
            string? path = GetStringProperty(element, "Path");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            int sourceId = GetIntProperty(element, "SourceId") ?? TryParseSourceSegment(path).sourceId;
            int segmentIndex = GetIntProperty(element, "SegmentIndex") ?? TryParseSourceSegment(path).segmentIndex;
            long startSample = GetLongProperty(element, "StartSample") ?? Math.Max(0, segmentIndex - 1) * (long)Math.Max(0, GetIntProperty(element, "SamplesPerChannel") ?? 0);
            int samplesPerChannel = GetIntProperty(element, "SamplesPerChannel") ?? 0;
            double sampleRateHz = GetDoubleProperty(element, "SampleRateHz") ?? fallbackSampleRateHz;
            int[] channelIds = GetIntArrayProperty(element, "ChannelIds");
            bool compressionEnabled = GetBoolProperty(element, "CompressionEnabled") ?? false;
            CompressionType compressionType = ParseEnumProperty(element, "CompressionType", CompressionType.None);
            PreprocessType preprocessType = ParseEnumProperty(element, "PreprocessType", PreprocessType.None);
            int[] channelPayloadBytes = GetIntArrayProperty(element, "ChannelPayloadBytes");
            if (sourceId < 0 || segmentIndex <= 0 || samplesPerChannel <= 0 || sampleRateHz <= 0 || channelIds.Length == 0)
            {
                continue;
            }

            entries.Add(new TdmsSegmentTimelineEntry(
                Path.GetFullPath(path),
                sourceId,
                segmentIndex,
                startSample,
                samplesPerChannel,
                sampleRateHz,
                channelIds,
                compressionEnabled,
                compressionType,
                preprocessType,
                channelPayloadBytes));
        }

        return entries
            .OrderBy(entry => entry.SourceId)
            .ThenBy(entry => entry.SegmentIndex)
            .ToArray();
    }

    private static IReadOnlyList<TdmsSegmentTimelineEntry> BuildTdmsSegmentTimelineFromFiles(
        IReadOnlyList<string> tdmsFiles,
        double sampleRateHz)
    {
        var groups = tdmsFiles
            .Select(path => (path, parsed: TryParseSourceSegment(path)))
            .Where(item => item.parsed.sourceId >= 0 && item.parsed.segmentIndex > 0)
            .GroupBy(item => item.parsed.sourceId)
            .ToArray();
        if (groups.Length == 0 || sampleRateHz <= 0)
        {
            return Array.Empty<TdmsSegmentTimelineEntry>();
        }

        var entries = new List<TdmsSegmentTimelineEntry>();
        foreach (var group in groups)
        {
            int sourceId = group.Key;
            foreach (var item in group.OrderBy(item => item.parsed.segmentIndex))
            {
                int samplesPerChannel = TryReadTdmsSegmentSamplesPerChannel(item.path, sourceId);
                if (samplesPerChannel <= 0)
                {
                    continue;
                }

                int[] channelIds = Enumerable
                    .Range(1, 16)
                    .Select(channelNumber => ChannelNaming.MakeChannelId(sourceId, channelNumber))
                    .ToArray();
                long startSample = Math.Max(0, item.parsed.segmentIndex - 1) * (long)samplesPerChannel;
                entries.Add(new TdmsSegmentTimelineEntry(
                    Path.GetFullPath(item.path),
                    sourceId,
                    item.parsed.segmentIndex,
                    startSample,
                    samplesPerChannel,
                    sampleRateHz,
                    channelIds,
                    false,
                    CompressionType.None,
                    PreprocessType.None,
                    Array.Empty<int>()));
            }
        }

        return entries
            .OrderBy(entry => entry.SourceId)
            .ThenBy(entry => entry.SegmentIndex)
            .ToArray();
    }

    private async ValueTask<RawIndexManifestModel?> EnsureRawIndexManifestAsync(CancellationToken ct)
    {
        if (_rawIndexManifest is not null)
        {
            return _rawIndexManifest;
        }

        string rawIndexManifestPath = Path.Combine(_sessionPath, "raw_index", "raw.index.json");
        if (!File.Exists(rawIndexManifestPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(rawIndexManifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<RawIndexManifestModel>(stream, cancellationToken: ct);
        if (manifest is null)
        {
            return null;
        }

        manifest.RootPath = Path.GetDirectoryName(rawIndexManifestPath) ?? _sessionPath;
        _rawIndexManifest = manifest;
        return manifest;
    }

    private static IReadOnlyList<CurvePoint> ReadPreviewWindowPoints(
        string previewRootPath,
        PersistedPreviewIndexEntry entry,
        long requestedStartSample,
        long requestedEndSampleExclusive,
        double sampleRateHz)
    {
        if (entry.BucketCount <= 0 || string.IsNullOrWhiteSpace(entry.RelativeFilePath))
        {
            return Array.Empty<CurvePoint>();
        }

        long clampedStart = Math.Max(requestedStartSample, entry.StartSampleIndex);
        long clampedEndInclusive = Math.Min(requestedEndSampleExclusive - 1, entry.EndSampleIndex);
        if (clampedEndInclusive < clampedStart)
        {
            return Array.Empty<CurvePoint>();
        }

        long entryStartBucket = entry.StartSampleIndex / entry.BucketSampleSpan;
        long firstBucket = clampedStart / entry.BucketSampleSpan;
        long lastBucket = clampedEndInclusive / entry.BucketSampleSpan;
        long bucketCountToRead = (lastBucket - firstBucket) + 1;
        if (bucketCountToRead <= 0)
        {
            return Array.Empty<CurvePoint>();
        }

        long bucketOffset = firstBucket - entryStartBucket;
        string filePath = Path.Combine(previewRootPath, entry.RelativeFilePath);
        if (!File.Exists(filePath))
        {
            return Array.Empty<CurvePoint>();
        }

        var points = new List<CurvePoint>((int)Math.Min(int.MaxValue, bucketCountToRead * 2));
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        stream.Seek(bucketOffset * PreviewBucketRecordSize, SeekOrigin.Begin);

        for (long i = 0; i < bucketCountToRead; i++)
        {
            long startSampleIndex = reader.ReadInt64();
            long endSampleIndex = reader.ReadInt64();
            int sampleCount = reader.ReadInt32();
            double minValue = reader.ReadDouble();
            double maxValue = reader.ReadDouble();
            int minOffsetInBucket = reader.ReadInt32();
            int maxOffsetInBucket = reader.ReadInt32();
            _ = reader.ReadDouble(); // sum
            _ = reader.ReadDouble(); // sumSquares

            if (endSampleIndex < requestedStartSample || startSampleIndex >= requestedEndSampleExclusive)
            {
                continue;
            }

            anyAdd(points, BuildPreviewPoint(startSampleIndex, minOffsetInBucket, minValue, sampleRateHz));
            anyAdd(points, BuildPreviewPoint(startSampleIndex, maxOffsetInBucket, maxValue, sampleRateHz));
        }

        points.Sort(static (a, b) => a.X.CompareTo(b.X));
        return points;

        static CurvePoint BuildPreviewPoint(long bucketStartSampleIndex, int offsetInBucket, double value, double sampleRateHz)
        {
            long sampleIndex = bucketStartSampleIndex + Math.Max(0, offsetInBucket);
            return new CurvePoint(sampleIndex / sampleRateHz, value);
        }

        static void anyAdd(List<CurvePoint> target, CurvePoint point)
        {
            if (target.Count > 0)
            {
                CurvePoint last = target[^1];
                if (Math.Abs(last.X - point.X) < 1e-12 && Math.Abs(last.Y - point.Y) < 1e-12)
                {
                    return;
                }
            }

            target.Add(point);
        }
    }

    private static CurveChannelStatistics ReadPreviewWindowStatistics(
        string previewRootPath,
        PersistedPreviewIndexEntry entry,
        long requestedStartSample,
        long requestedEndSampleExclusive,
        int channelId)
    {
        if (entry.BucketCount <= 0 || string.IsNullOrWhiteSpace(entry.RelativeFilePath))
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        long clampedStart = Math.Max(requestedStartSample, entry.StartSampleIndex);
        long clampedEndInclusive = Math.Min(requestedEndSampleExclusive - 1, entry.EndSampleIndex);
        if (clampedEndInclusive < clampedStart)
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        long entryStartBucket = entry.StartSampleIndex / entry.BucketSampleSpan;
        long firstBucket = clampedStart / entry.BucketSampleSpan;
        long lastBucket = clampedEndInclusive / entry.BucketSampleSpan;
        long bucketCountToRead = (lastBucket - firstBucket) + 1;
        if (bucketCountToRead <= 0)
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        string filePath = Path.Combine(previewRootPath, entry.RelativeFilePath);
        if (!File.Exists(filePath))
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        long bucketOffset = firstBucket - entryStartBucket;
        long count = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        double sumSquares = 0.0;

        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        stream.Seek(bucketOffset * PreviewBucketRecordSize, SeekOrigin.Begin);

        for (long i = 0; i < bucketCountToRead; i++)
        {
            long startSampleIndex = reader.ReadInt64();
            long endSampleIndex = reader.ReadInt64();
            int sampleCount = reader.ReadInt32();
            double minValue = reader.ReadDouble();
            double maxValue = reader.ReadDouble();
            _ = reader.ReadInt32(); // minOffsetInBucket
            _ = reader.ReadInt32(); // maxOffsetInBucket
            double bucketSum = reader.ReadDouble();
            double bucketSumSquares = reader.ReadDouble();

            if (endSampleIndex < requestedStartSample || startSampleIndex >= requestedEndSampleExclusive)
            {
                continue;
            }

            count += sampleCount;
            min = Math.Min(min, minValue);
            max = Math.Max(max, maxValue);
            sum += bucketSum;
            sumSquares += bucketSumSquares;
        }

        if (count <= 0)
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        double mean = sum / count;
        double variance = Math.Max(0.0, (sumSquares / count) - (mean * mean));
        bool complete =
            entry.StartSampleIndex >= 0 &&
            entry.StartSampleIndex <= requestedStartSample &&
            entry.EndSampleIndex >= requestedEndSampleExclusive - 1;
        return new CurveChannelStatistics
        {
            ChannelId = channelId,
            Count = count,
            Min = min,
            Max = max,
            Sum = sum,
            SumSquares = sumSquares,
            Mean = mean,
            StandardDeviation = Math.Sqrt(variance),
            IsComplete = complete
        };
    }

    private static CurveChannelStatistics ReadRawWindowStatistics(
        RawIndexManifestModel manifest,
        int channelId,
        double windowStart,
        double windowEnd)
    {
        IReadOnlyList<CurvePoint> points = ReadRawWindowPoints(manifest, channelId, windowStart, windowEnd);
        if (points.Count == 0)
        {
            return CreateEmptyChannelStatistics(channelId, complete: false);
        }

        long count = 0;
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        double sum = 0.0;
        double sumSquares = 0.0;
        foreach (CurvePoint point in points)
        {
            count++;
            double value = point.Y;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            sum += value;
            sumSquares += value * value;
        }

        double mean = sum / count;
        double variance = Math.Max(0.0, (sumSquares / count) - (mean * mean));
        return new CurveChannelStatistics
        {
            ChannelId = channelId,
            Count = count,
            Min = min,
            Max = max,
            Sum = sum,
            SumSquares = sumSquares,
            Mean = mean,
            StandardDeviation = Math.Sqrt(variance),
            IsComplete = true
        };
    }

    private static PreviewLevel SelectStatisticsPreviewLevel(
        PersistedPreviewIndexManifest manifest,
        PreviewLevel requestedLevel)
    {
        if (requestedLevel == PreviewLevel.L0)
        {
            requestedLevel = PreviewLevel.L1;
        }

        var levels = manifest.Files
            .Select(file => new
            {
                Level = Enum.TryParse(file.LevelName, ignoreCase: true, out PreviewLevel parsed)
                    ? parsed
                    : PreviewLevel.L4,
                file.BucketSampleSpan
            })
            .Where(item => item.Level != PreviewLevel.L0 && item.BucketSampleSpan > 0)
            .GroupBy(item => item.Level)
            .Select(group => new
            {
                Level = group.Key,
                BucketSampleSpan = group.Min(item => item.BucketSampleSpan)
            })
            .OrderBy(item => item.BucketSampleSpan)
            .ToArray();
        if (levels.Length == 0)
        {
            return requestedLevel;
        }

        return levels[0].Level;
    }

    private static IReadOnlyList<CurvePoint> ReadRawWindowPoints(
        IReadOnlyList<string> tdmsFiles,
        int channelId,
        double windowStart,
        double windowEnd)
    {
        string channelName = ChannelNaming.TdmsChannelName(channelId);
        foreach (string filePath in tdmsFiles)
        {
            try
            {
                var raw = TdmsReaderUtil.ReadChannelData(filePath, "Session", channelName);
                if (raw.Length == 0)
                {
                    continue;
                }

                var properties = TdmsReaderUtil.ReadChannelProperties(filePath, "Session", channelName);
                double increment = TryGetDouble(properties, "wf_increment") ?? 1.0;
                double offset = TryGetDouble(properties, "wf_start_offset") ?? 0.0;

                int startIndex = Math.Max(0, (int)Math.Floor((windowStart - offset) / increment));
                int endIndexExclusive = Math.Min(raw.Length, (int)Math.Ceiling((windowEnd - offset) / increment));
                if (endIndexExclusive <= startIndex)
                {
                    return Array.Empty<CurvePoint>();
                }

                int count = endIndexExclusive - startIndex;
                var points = new CurvePoint[count];
                for (int i = 0; i < count; i++)
                {
                    int sampleIndex = startIndex + i;
                    points[i] = new CurvePoint(offset + sampleIndex * increment, raw[sampleIndex]);
                }

                return points;
            }
            catch
            {
                // try next tdms file
            }
        }

        return Array.Empty<CurvePoint>();
    }

    private FastSegmentTimeline? EnsureFastSegmentTimeline(
        IReadOnlyList<string> segmentFiles,
        double sampleRateHz)
    {
        if (_fastSegmentTimeline is not null)
        {
            return _fastSegmentTimeline;
        }

        _fastSegmentTimeline = TryBuildFastSegmentTimeline(segmentFiles, sampleRateHz);
        return _fastSegmentTimeline;
    }

    private static FastSegmentTimeline? TryBuildFastSegmentTimeline(
        IReadOnlyList<string> segmentFiles,
        double sampleRateHz)
    {
        if (sampleRateHz <= 0)
        {
            return null;
        }

        var segments = new List<FastSegmentTimelineEntry>();
        var sourceSampleCursors = new Dictionary<int, long>();

        foreach (string filePath in segmentFiles
            .Where(IsFastSegmentFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!TryReadFastSegmentHeader(filePath, out FastSegmentHeader header)
                || header.SampleRateHz <= 0)
            {
                continue;
            }

            sourceSampleCursors.TryGetValue(header.SourceId, out long sourceSampleCursor);
            long segmentStartSample = sourceSampleCursor;
            long segmentEndSampleExclusive = segmentStartSample + header.SamplesPerChannel;
            sourceSampleCursors[header.SourceId] = segmentEndSampleExclusive;

            segments.Add(new FastSegmentTimelineEntry(
                filePath,
                header,
                segmentStartSample,
                segmentEndSampleExclusive));
        }

        if (segments.Count == 0)
        {
            return null;
        }

        return new FastSegmentTimeline(sampleRateHz, segments);
    }

    private static IReadOnlyList<CurvePoint> ReadFastSegmentWindowPoints(
        FastSegmentTimeline timeline,
        int channelId,
        double windowStart,
        double windowEnd)
    {
        if (timeline.SampleRateHz <= 0 || windowEnd <= windowStart)
        {
            return Array.Empty<CurvePoint>();
        }

        int sourceId = ChannelNaming.GetDeviceId(channelId);
        long requestedStartSample = Math.Max(0L, (long)Math.Floor(windowStart * timeline.SampleRateHz));
        long requestedEndSampleExclusive = Math.Max(requestedStartSample + 1L, (long)Math.Ceiling(windowEnd * timeline.SampleRateHz));
        var points = new List<CurvePoint>();

        foreach (FastSegmentTimelineEntry segment in timeline.Segments
            .Where(segment => segment.Header.SourceId == sourceId)
            .OrderBy(segment => segment.StartSampleIndex))
        {
            FastSegmentHeader header = segment.Header;
            long segmentStartSample = segment.StartSampleIndex;
            long segmentEndSampleExclusive = segment.EndSampleIndexExclusive;

            if (segmentEndSampleExclusive <= requestedStartSample)
            {
                continue;
            }

            if (segmentStartSample >= requestedEndSampleExclusive)
            {
                break;
            }

            int channelOffset = Array.IndexOf(header.ChannelIds, channelId);
            if (channelOffset < 0)
            {
                continue;
            }

            int localStart = (int)Math.Max(0, requestedStartSample - segmentStartSample);
            int localEndExclusive = (int)Math.Min(header.SamplesPerChannel, requestedEndSampleExclusive - segmentStartSample);
            if (localEndExclusive <= localStart)
            {
                continue;
            }

            using var stream = File.Open(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long channelDataOffset = header.HeaderBytes + ((long)channelOffset * header.SamplesPerChannel * sizeof(float));
            stream.Seek(channelDataOffset + ((long)localStart * sizeof(float)), SeekOrigin.Begin);

            int sampleCount = localEndExclusive - localStart;
            byte[] bytes = new byte[sampleCount * sizeof(float)];
            int read = stream.Read(bytes, 0, bytes.Length);
            int floatsRead = read / sizeof(float);
            for (int i = 0; i < floatsRead; i++)
            {
                float y = BitConverter.ToSingle(bytes, i * sizeof(float));
                long absoluteSampleIndex = segmentStartSample + localStart + i;
                points.Add(new CurvePoint(absoluteSampleIndex / timeline.SampleRateHz, y));
            }
        }

        return points;
    }

    private static bool TryReadFastSegmentHeader(string filePath, out FastSegmentHeader header)
    {
        header = default;
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            byte[] bytes = new byte[FastSegmentHeaderBytes];
            using var stream = File.OpenRead(filePath);
            if (stream.Length < FastSegmentHeaderBytes || stream.Read(bytes, 0, bytes.Length) != bytes.Length)
            {
                return false;
            }

            bool magicOk = bytes[0] == (byte)'D'
                && bytes[1] == (byte)'H'
                && bytes[2] == (byte)'F'
                && bytes[3] == (byte)'S'
                && bytes[4] == (byte)'E'
                && bytes[5] == (byte)'G'
                && bytes[6] == (byte)'1';
            if (!magicOk)
            {
                return false;
            }

            int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
            int sourceId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
            int segmentIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(20));
            int samplesPerChannel = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24));
            double sampleRateHz = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(32));
            long payloadBytes = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(40));
            int headerBytes = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(48));
            if (version != 1
                || sourceId < 0
                || segmentIndex < 0
                || channelCount <= 0
                || samplesPerChannel <= 0
                || sampleRateHz <= 0
                || headerBytes != FastSegmentHeaderBytes
                || payloadBytes != (long)channelCount * samplesPerChannel * sizeof(float)
                || stream.Length < headerBytes + payloadBytes)
            {
                return false;
            }

            int[] channelIds = new int[channelCount];
            int channelOffset = 64;
            for (int i = 0; i < channelIds.Length; i++)
            {
                if (channelOffset + sizeof(int) > bytes.Length)
                {
                    return false;
                }

                channelIds[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(channelOffset));
                channelOffset += sizeof(int);
            }

            header = new FastSegmentHeader(sourceId, segmentIndex, channelIds, samplesPerChannel, sampleRateHz, headerBytes);
            return true;
        }
        catch
        {
            header = default;
            return false;
        }
    }

    private static bool IsFastSegmentFile(string filePath)
        => string.Equals(Path.GetExtension(filePath), ".dhseg", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CurvePoint> ReadTdmsSegmentWindowPoints(
        IReadOnlyList<TdmsSegmentTimelineEntry> timeline,
        int channelId,
        double windowStart,
        double windowEnd,
        double fallbackSampleRateHz,
        int maxPointsPerChannel,
        bool requireEnvelopeSemantics)
    {
        if (windowEnd <= windowStart)
        {
            return Array.Empty<CurvePoint>();
        }

        int sourceId = ChannelNaming.GetDeviceId(channelId);
        var sourceSegments = timeline
            .Where(segment => segment.SourceId == sourceId && segment.ChannelIds.Contains(channelId))
            .OrderBy(segment => segment.StartSample)
            .ToArray();
        if (sourceSegments.Length == 0)
        {
            return Array.Empty<CurvePoint>();
        }

        double sampleRateHz = sourceSegments.FirstOrDefault()?.SampleRateHz > 0
            ? sourceSegments.First().SampleRateHz
            : fallbackSampleRateHz;
        if (sampleRateHz <= 0)
        {
            return Array.Empty<CurvePoint>();
        }

        long requestedStartSample = Math.Max(0L, (long)Math.Floor(windowStart * sampleRateHz));
        long requestedEndSampleExclusive = Math.Max(requestedStartSample + 1L, (long)Math.Ceiling(windowEnd * sampleRateHz));
        var ranges = new List<TdmsSegmentReadRange>();
        long totalSampleCount = 0L;

        foreach (TdmsSegmentTimelineEntry segment in sourceSegments)
        {
            if (segment.EndSampleExclusive <= requestedStartSample)
            {
                continue;
            }

            if (segment.StartSample >= requestedEndSampleExclusive)
            {
                break;
            }

            int localStart = (int)Math.Max(0, requestedStartSample - segment.StartSample);
            int localEndExclusive = (int)Math.Min(segment.SamplesPerChannel, requestedEndSampleExclusive - segment.StartSample);
            if (localEndExclusive <= localStart)
            {
                continue;
            }

            int count = localEndExclusive - localStart;
            totalSampleCount += count;
            ranges.Add(new TdmsSegmentReadRange(segment, localStart, count));
        }

        if (ranges.Count == 0 || totalSampleCount <= 0)
        {
            return Array.Empty<CurvePoint>();
        }

        int targetPoints = maxPointsPerChannel > 0
            ? maxPointsPerChannel
            : 4000;
        int sampleStride = totalSampleCount > targetPoints
            ? (int)Math.Max(1L, totalSampleCount / Math.Max(1, targetPoints / (requireEnvelopeSemantics ? 2 : 1)))
            : 1;
        int capacity = (int)Math.Min(int.MaxValue, totalSampleCount / sampleStride + 4);
        var points = new List<CurvePoint>(Math.Max(1, capacity));

        foreach (var range in ranges)
        {
            if (TryReadManualTdmsSegmentWindowPoints(
                range.Segment,
                channelId,
                range.LocalStart,
                range.SampleCount,
                sampleRateHz,
                sampleStride,
                requireEnvelopeSemantics,
                points))
            {
                continue;
            }

            string groupName = $"source_{sourceId:D4}";
            string channelName = $"AI{channelId:D4}";
            double[] raw;
            try
            {
                raw = TdmsReaderUtil.ReadChannelData(range.Segment.FilePath, groupName, channelName);
            }
            catch
            {
                continue;
            }

            int end = Math.Min(range.LocalStart + range.SampleCount, raw.Length);
            AppendSampledPoints(raw, range.Segment.StartSample, range.LocalStart, end, sampleRateHz, sampleStride, requireEnvelopeSemantics, points);
        }

        return points;
    }

    private static bool TryReadManualTdmsSegmentWindowPoints(
        TdmsSegmentTimelineEntry segment,
        int channelId,
        int localStart,
        int sampleCount,
        double sampleRateHz,
        int sampleStride,
        bool requireEnvelopeSemantics,
        List<CurvePoint> points)
    {
        int channelOffset = Array.IndexOf(segment.ChannelIds, channelId);
        if (channelOffset < 0 || sampleCount <= 0 || sampleRateHz <= 0)
        {
            return false;
        }

        if (segment.CompressionEnabled)
        {
            return TryReadCompressedTdmsSegmentWindowPoints(
                segment,
                channelOffset,
                localStart,
                sampleCount,
                sampleRateHz,
                sampleStride,
                requireEnvelopeSemantics,
                points);
        }

        long rawDataOffset = TryReadTdmsRawDataOffset(segment.FilePath);
        if (rawDataOffset < 0)
        {
            return false;
        }

        long channelByteOffset = checked(rawDataOffset + (long)channelOffset * segment.SamplesPerChannel * sizeof(float));
        long startByteOffset = checked(channelByteOffset + (long)localStart * sizeof(float));
        int bytesToRead = checked(sampleCount * sizeof(float));
        byte[] bytes = new byte[bytesToRead];

        try
        {
            using var stream = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.RandomAccess);
            stream.Seek(startByteOffset, SeekOrigin.Begin);
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            if (offset < bytes.Length)
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        float[] raw = new float[sampleCount];
        Buffer.BlockCopy(bytes, 0, raw, 0, bytes.Length);
        AppendSampledPoints(raw, segment.StartSample + localStart, 0, sampleCount, sampleRateHz, sampleStride, requireEnvelopeSemantics, points);
        return true;
    }

    private static bool TryReadCompressedTdmsSegmentWindowPoints(
        TdmsSegmentTimelineEntry segment,
        int channelOffset,
        int localStart,
        int sampleCount,
        double sampleRateHz,
        int sampleStride,
        bool requireEnvelopeSemantics,
        List<CurvePoint> points)
    {
        if (segment.ChannelPayloadBytes.Length != segment.ChannelIds.Length)
        {
            return false;
        }

        long rawDataOffset = TryReadTdmsRawDataOffset(segment.FilePath);
        if (rawDataOffset < 0)
        {
            return false;
        }

        long channelByteOffset = rawDataOffset;
        for (int i = 0; i < channelOffset; i++)
        {
            channelByteOffset += segment.ChannelPayloadBytes[i];
        }

        int payloadBytes = segment.ChannelPayloadBytes[channelOffset];
        if (payloadBytes <= 0)
        {
            return false;
        }

        byte[] payload = new byte[payloadBytes];
        try
        {
            using var stream = new FileStream(segment.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 1024, FileOptions.RandomAccess);
            stream.Seek(channelByteOffset, SeekOrigin.Begin);
            int offset = 0;
            while (offset < payload.Length)
            {
                int read = stream.Read(payload, offset, payload.Length - offset);
                if (read == 0)
                {
                    return false;
                }

                offset += read;
            }
        }
        catch
        {
            return false;
        }

        try
        {
            float[] raw = FloatSampleCompressionCodec.Decode(
                payload,
                payloadBytes,
                segment.SamplesPerChannel,
                segment.CompressionType,
                segment.PreprocessType);
            int end = Math.Min(localStart + sampleCount, raw.Length);
            AppendSampledPoints(raw, segment.StartSample, localStart, end, sampleRateHz, sampleStride, requireEnvelopeSemantics, points);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long TryReadTdmsRawDataOffset(string filePath)
    {
        Span<byte> header = stackalloc byte[28];
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            int read = stream.Read(header);
            if (read < header.Length
                || header[0] != (byte)'T'
                || header[1] != (byte)'D'
                || header[2] != (byte)'S'
                || header[3] != (byte)'m')
            {
                return -1;
            }

            ulong rawDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8));
            return checked(28L + (long)rawDataOffset);
        }
        catch
        {
            return -1;
        }
    }

    private static void AppendSampledPoints(
        IReadOnlyList<double> raw,
        long segmentStartSample,
        int localStart,
        int localEndExclusive,
        double sampleRateHz,
        int sampleStride,
        bool requireEnvelopeSemantics,
        List<CurvePoint> points)
    {
        int end = Math.Min(localEndExclusive, raw.Count);
        for (int i = localStart; i < end; i += sampleStride)
        {
            if (requireEnvelopeSemantics && sampleStride > 1)
            {
                AppendEnvelopeBucket(raw, segmentStartSample, i, Math.Min(i + sampleStride, end), sampleRateHz, points);
                continue;
            }

            long absoluteSampleIndex = segmentStartSample + i;
            points.Add(new CurvePoint(absoluteSampleIndex / sampleRateHz, raw[i]));
        }
    }

    private static void AppendSampledPoints(
        IReadOnlyList<float> raw,
        long segmentStartSample,
        int localStart,
        int localEndExclusive,
        double sampleRateHz,
        int sampleStride,
        bool requireEnvelopeSemantics,
        List<CurvePoint> points)
    {
        int end = Math.Min(localEndExclusive, raw.Count);
        for (int i = localStart; i < end; i += sampleStride)
        {
            if (requireEnvelopeSemantics && sampleStride > 1)
            {
                AppendEnvelopeBucket(raw, segmentStartSample, i, Math.Min(i + sampleStride, end), sampleRateHz, points);
                continue;
            }

            long absoluteSampleIndex = segmentStartSample + i;
            points.Add(new CurvePoint(absoluteSampleIndex / sampleRateHz, raw[i]));
        }
    }

    private static void AppendEnvelopeBucket(
        IReadOnlyList<double> raw,
        long segmentStartSample,
        int start,
        int endExclusive,
        double sampleRateHz,
        List<CurvePoint> points)
    {
        if (endExclusive <= start)
        {
            return;
        }

        int minIndex = start;
        int maxIndex = start;
        double min = raw[start];
        double max = raw[start];
        for (int i = start + 1; i < endExclusive; i++)
        {
            double value = raw[i];
            if (value < min)
            {
                min = value;
                minIndex = i;
            }

            if (value > max)
            {
                max = value;
                maxIndex = i;
            }
        }

        AppendEnvelopePointsInTimeOrder(segmentStartSample, sampleRateHz, minIndex, min, maxIndex, max, points);
    }

    private static void AppendEnvelopeBucket(
        IReadOnlyList<float> raw,
        long segmentStartSample,
        int start,
        int endExclusive,
        double sampleRateHz,
        List<CurvePoint> points)
    {
        if (endExclusive <= start)
        {
            return;
        }

        int minIndex = start;
        int maxIndex = start;
        float min = raw[start];
        float max = raw[start];
        for (int i = start + 1; i < endExclusive; i++)
        {
            float value = raw[i];
            if (value < min)
            {
                min = value;
                minIndex = i;
            }

            if (value > max)
            {
                max = value;
                maxIndex = i;
            }
        }

        AppendEnvelopePointsInTimeOrder(segmentStartSample, sampleRateHz, minIndex, min, maxIndex, max, points);
    }

    private static void AppendEnvelopePointsInTimeOrder(
        long segmentStartSample,
        double sampleRateHz,
        int minIndex,
        double min,
        int maxIndex,
        double max,
        List<CurvePoint> points)
    {
        if (minIndex <= maxIndex)
        {
            points.Add(new CurvePoint((segmentStartSample + minIndex) / sampleRateHz, min));
            if (maxIndex != minIndex)
            {
                points.Add(new CurvePoint((segmentStartSample + maxIndex) / sampleRateHz, max));
            }
        }
        else
        {
            points.Add(new CurvePoint((segmentStartSample + maxIndex) / sampleRateHz, max));
            points.Add(new CurvePoint((segmentStartSample + minIndex) / sampleRateHz, min));
        }
    }

    private static int TryReadTdmsSegmentSamplesPerChannel(string filePath, int sourceId)
    {
        try
        {
            string groupName = $"source_{sourceId:D4}";
            var structure = TdmsReaderUtil.ListGroupsAndChannels(filePath);
            if (!structure.TryGetValue(groupName, out string[] channels) || channels.Length == 0)
            {
                return 0;
            }

            double[] data = TdmsReaderUtil.ReadChannelData(filePath, groupName, channels[0]);
            return data.Length;
        }
        catch
        {
            return 0;
        }
    }

    private static (int sourceId, int segmentIndex) TryParseSourceSegment(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        var match = System.Text.RegularExpressions.Regex.Match(
            name,
            @"source_(\d+)_seg(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, out int sourceId)
            || !int.TryParse(match.Groups[2].Value, out int segmentIndex))
        {
            return (-1, -1);
        }

        return (sourceId, segmentIndex);
    }

    private static string? GetStringProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetIntProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    private static long? GetLongProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed)
            ? parsed
            : null;

    private static double? GetDoubleProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static bool? GetBoolProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    private static TEnum ParseEnumProperty<TEnum>(JsonElement element, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        string? value = GetStringProperty(element, name);
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;
    }

    private static int[] GetIntArrayProperty(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        return value
            .EnumerateArray()
            .Where(item => item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .ToArray();
    }

    private static IReadOnlyList<CurvePoint> ReadRawWindowPoints(
        RawIndexManifestModel manifest,
        int channelId,
        double windowStart,
        double windowEnd)
    {
        if (manifest.SampleRateHz <= 0 || string.IsNullOrWhiteSpace(manifest.CaptureFilePath) || !File.Exists(manifest.CaptureFilePath))
        {
            return Array.Empty<CurvePoint>();
        }

        RawIndexChannelEntryModel? channelEntry = manifest.Channels
            .FirstOrDefault(entry => entry.ChannelId == channelId);
        if (channelEntry is null)
        {
            return Array.Empty<CurvePoint>();
        }

        string indexPath = Path.Combine(manifest.RootPath, channelEntry.RelativeFilePath);
        if (!File.Exists(indexPath))
        {
            return Array.Empty<CurvePoint>();
        }

        long requestedStartSample = Math.Max(0L, (long)Math.Floor(windowStart * manifest.SampleRateHz));
        long requestedEndSampleExclusive = Math.Max(requestedStartSample + 1L, (long)Math.Ceiling(windowEnd * manifest.SampleRateHz));

        var points = new List<CurvePoint>();
        using var captureStream = File.Open(manifest.CaptureFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var captureReader = new BinaryReader(captureStream);
        using var indexStream = File.Open(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var indexReader = new BinaryReader(indexStream);

        long firstEntryIndex = FindFirstRawIndexEntryAtOrAfter(indexStream, indexReader, channelEntry.EntryCount, requestedStartSample);
        for (long i = firstEntryIndex; i < channelEntry.EntryCount; i++)
        {
            RawIndexEntry entry = ReadRawIndexEntry(indexStream, indexReader, i);

            if (entry.StartSampleIndex >= requestedEndSampleExclusive)
            {
                break;
            }

            if (entry.EndSampleIndex < requestedStartSample)
            {
                continue;
            }

            captureStream.Seek(entry.PayloadOffset, SeekOrigin.Begin);
            byte[] payloadBytes = captureReader.ReadBytes(entry.InterleavedFloatCount * sizeof(float));
            var interleaved = new float[entry.InterleavedFloatCount];
            Buffer.BlockCopy(payloadBytes, 0, interleaved, 0, payloadBytes.Length);

            int localStart = (int)Math.Max(0, requestedStartSample - entry.StartSampleIndex);
            int localEndExclusive = (int)Math.Min(entry.SampleCount, requestedEndSampleExclusive - entry.StartSampleIndex);
            for (int sampleIndex = localStart; sampleIndex < localEndExclusive; sampleIndex++)
            {
                int interleavedIndex = sampleIndex * entry.ChannelCount + entry.ChannelOffset;
                if (interleavedIndex < 0 || interleavedIndex >= interleaved.Length)
                {
                    continue;
                }

                long absoluteSampleIndex = entry.StartSampleIndex + sampleIndex;
                points.Add(new CurvePoint(absoluteSampleIndex / manifest.SampleRateHz, interleaved[interleavedIndex]));
            }
        }

        return points;
    }

    private static long FindFirstRawIndexEntryAtOrAfter(
        Stream indexStream,
        BinaryReader indexReader,
        long entryCount,
        long requestedStartSample)
    {
        long low = 0;
        long high = entryCount;
        while (low < high)
        {
            long mid = low + ((high - low) / 2);
            RawIndexEntry entry = ReadRawIndexEntry(indexStream, indexReader, mid);
            if (entry.EndSampleIndex < requestedStartSample)
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

    private static RawIndexEntry ReadRawIndexEntry(
        Stream indexStream,
        BinaryReader indexReader,
        long entryIndex)
    {
        indexStream.Seek(entryIndex * RawIndexEntrySize, SeekOrigin.Begin);
        return new RawIndexEntry(
            indexReader.ReadInt64(),
            indexReader.ReadInt64(),
            indexReader.ReadInt64(),
            indexReader.ReadInt32(),
            indexReader.ReadInt32(),
            indexReader.ReadInt32(),
            indexReader.ReadInt32());
    }

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> properties, string key)
    {
        if (!properties.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out double parsed) => parsed,
            _ => null
        };
    }

    private static CurveWindowSnapshot CreateMissingSnapshot(PreviewReadRequest request)
    {
        return new CurveWindowSnapshot
        {
            SessionId = request.SessionId,
            ViewId = request.ViewId,
            ChannelIds = request.ChannelIds,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            Version = 0,
            DataEpoch = 0,
            SourceVersion = 0,
            SegmentRange = null,
            IsPreview = request.PreviewLevel != PreviewLevel.L0,
            IsComplete = false,
            BuildState = BuildState.Missing,
            Recovered = false,
            TimeAxisKind = request.PreferredTimeAxisKind ?? TimeAxisKind.SampleIndexMappedTime,
            ChannelData = new Dictionary<int, IReadOnlyList<CurvePoint>>(),
            MaxActualPointsPerChannel = 0,
            TotalActualPoints = 0
        };
    }

    private static CurveStatisticsResult CreateMissingStatistics(CurveStatisticsRequest request)
    {
        return new CurveStatisticsResult
        {
            SessionId = request.SessionId,
            ViewId = request.ViewId,
            WindowStart = request.WindowStart,
            WindowEnd = request.WindowEnd,
            PreviewLevel = request.PreviewLevel,
            ChannelStatistics = request.ChannelIds
                .Distinct()
                .OrderBy(id => id)
                .ToDictionary(id => id, id => CreateEmptyChannelStatistics(id, complete: false)),
            IsComplete = false,
            BuildState = BuildState.Missing
        };
    }

    private static CurveChannelStatistics CreateEmptyChannelStatistics(int channelId, bool complete)
    {
        return new CurveChannelStatistics
        {
            ChannelId = channelId,
            Count = 0,
            Min = double.NaN,
            Max = double.NaN,
            Sum = 0.0,
            SumSquares = 0.0,
            Mean = double.NaN,
            StandardDeviation = double.NaN,
            IsComplete = complete
        };
    }

    private sealed class PersistedPreviewIndexManifest
    {
        public int Version { get; init; }

        public string SessionName { get; init; } = string.Empty;

        public double SampleRateHz { get; init; }

        public DateTime GeneratedAtUtc { get; init; }

        public List<PersistedPreviewIndexEntry> Files { get; init; } = new();

        public string RootPath { get; set; } = string.Empty;
    }

    private sealed class PersistedPreviewIndexEntry
    {
        public string LevelName { get; init; } = string.Empty;

        public int ChannelId { get; init; }

        public long BucketSampleSpan { get; init; }

        public string RelativeFilePath { get; init; } = string.Empty;

        public long BucketCount { get; init; }

        public long StartSampleIndex { get; init; }

        public long EndSampleIndex { get; init; }
    }

    private sealed class RawIndexManifestModel
    {
        public int Version { get; init; }

        public string CaptureFilePath { get; init; } = string.Empty;

        public double SampleRateHz { get; init; }

        public List<RawIndexChannelEntryModel> Channels { get; init; } = new();

        public string RootPath { get; set; } = string.Empty;
    }

    private sealed class RawIndexChannelEntryModel
    {
        public int ChannelId { get; init; }

        public string RelativeFilePath { get; init; } = string.Empty;

        public long EntryCount { get; init; }

        public long StartSampleIndex { get; init; }

        public long EndSampleIndex { get; init; }
    }

    private readonly record struct RawIndexEntry(
        long StartSampleIndex,
        long EndSampleIndex,
        long PayloadOffset,
        int InterleavedFloatCount,
        int ChannelCount,
        int ChannelOffset,
        int SampleCount);

    private readonly record struct FastSegmentHeader(
        int SourceId,
        int SegmentIndex,
        int[] ChannelIds,
        int SamplesPerChannel,
        double SampleRateHz,
        int HeaderBytes);

    private sealed record FastSegmentTimeline(
        double SampleRateHz,
        IReadOnlyList<FastSegmentTimelineEntry> Segments);

    private sealed record FastSegmentTimelineEntry(
        string FilePath,
        FastSegmentHeader Header,
        long StartSampleIndex,
        long EndSampleIndexExclusive);

    private sealed record TdmsSegmentTimelineEntry(
        string FilePath,
        int SourceId,
        int SegmentIndex,
        long StartSample,
        int SamplesPerChannel,
        double SampleRateHz,
        int[] ChannelIds,
        bool CompressionEnabled,
        CompressionType CompressionType,
        PreprocessType PreprocessType,
        int[] ChannelPayloadBytes)
    {
        public long EndSampleExclusive => StartSample + SamplesPerChannel;
    }

    private sealed record TdmsSegmentReadRange(
        TdmsSegmentTimelineEntry Segment,
        int LocalStart,
        int SampleCount);
}
