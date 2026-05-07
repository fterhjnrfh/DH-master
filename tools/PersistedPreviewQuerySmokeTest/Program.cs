using System.Text.Json;
using System.Diagnostics;
using DH.Client.App.Data.Query;
using DH.Client.App.Services.Performance;

string? outputDirectory = null;
string? sessionPath = null;
string? resultFile = null;
bool requireCatalog = false;
bool largeSessionMode = false;
bool rawOnly = false;
int repeatCount = 1;

for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--output-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        outputDirectory = args[++i];
        continue;
    }

    if (string.Equals(args[i], "--session-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        sessionPath = args[++i];
        continue;
    }

    if (string.Equals(args[i], "--result-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        resultFile = args[++i];
        continue;
    }

    if (string.Equals(args[i], "--require-catalog", StringComparison.OrdinalIgnoreCase))
    {
        requireCatalog = true;
        continue;
    }

    if (string.Equals(args[i], "--large-session-mode", StringComparison.OrdinalIgnoreCase))
    {
        largeSessionMode = true;
        continue;
    }

    if (string.Equals(args[i], "--raw-only", StringComparison.OrdinalIgnoreCase))
    {
        rawOnly = true;
        continue;
    }

    if (string.Equals(args[i], "--repeat", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        repeatCount = Math.Max(1, int.Parse(args[++i]));
    }
}

StreamWriter? resultWriter = null;
if (!string.IsNullOrWhiteSpace(resultFile))
{
    string resultPath = Path.GetFullPath(resultFile);
    string? resultDirectory = Path.GetDirectoryName(resultPath);
    if (!string.IsNullOrWhiteSpace(resultDirectory))
    {
        Directory.CreateDirectory(resultDirectory);
    }

    resultWriter = new StreamWriter(
        new FileStream(resultPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    {
        AutoFlush = true
    };
    Console.SetOut(resultWriter);
    Console.SetError(resultWriter);
}

if (string.IsNullOrWhiteSpace(sessionPath))
{
    Console.WriteLine("Missing --session-path");
    resultWriter?.Dispose();
    return 2;
}

if (!string.IsNullOrWhiteSpace(outputDirectory))
{
    Environment.SetEnvironmentVariable("DH_PERF_OUTPUT_DIR", outputDirectory);
}

sessionPath = Path.GetFullPath(sessionPath);
sessionPath = ResolveSessionPath(sessionPath);
var catalog = new FileSystemDataSessionCatalog();
var diagnostics = await catalog.OpenWithDiagnosticsAsync(sessionPath);
SessionDescriptor session = diagnostics.Session;
var runtime = new PersistedPreviewQueryRuntime(sessionPath, session);

if (requireCatalog && diagnostics.Source != FileSystemDataSessionCatalog.MetadataSource.Catalog)
{
    Console.WriteLine("CATALOG_REQUIRED_BUT_NOT_USED");
    Console.WriteLine($"MetadataSource={diagnostics.Source}");
    Console.WriteLine($"CatalogPath={diagnostics.Artifacts.CatalogPath ?? string.Empty}");
    Console.WriteLine($"ManifestPath={diagnostics.Artifacts.ManifestPath ?? string.Empty}");
    resultWriter?.Dispose();
    return 8;
}

string previewIndexPath = Path.Combine(sessionPath, "preview_levels", "preview.index.json");
if (!rawOnly && !File.Exists(previewIndexPath))
{
    Console.WriteLine($"Missing preview index: {previewIndexPath}");
    resultWriter?.Dispose();
    return 3;
}

JsonDocument? previewIndex = null;
JsonElement[] previewFiles = Array.Empty<JsonElement>();
double sampleRateHz = session.Sources.FirstOrDefault()?.SampleRateHz ?? 0.0;
long maxEndSampleIndex = 0L;
if (File.Exists(previewIndexPath))
{
    var previewIndexStream = File.OpenRead(previewIndexPath);
    previewIndex = await JsonDocument.ParseAsync(previewIndexStream);
    previewFiles = previewIndex.RootElement
        .GetProperty("Files")
        .EnumerateArray()
        .ToArray();
    sampleRateHz = previewIndex.RootElement.GetProperty("SampleRateHz").GetDouble();
    maxEndSampleIndex = previewFiles
        .Select(element => element.GetProperty("EndSampleIndex").GetInt64())
        .DefaultIfEmpty(0L)
        .Max();
}
else if (rawOnly)
{
    maxEndSampleIndex = ReadMaxEndSampleIndex(diagnostics.Artifacts.ManifestPath);
}

double totalDurationSeconds = sampleRateHz > 0
    ? maxEndSampleIndex / sampleRateHz
    : 0.0;

int[] allChannelIds = previewFiles
    .Select(element => element.GetProperty("ChannelId").GetInt32())
    .Distinct()
    .OrderBy(id => id)
    .ToArray();
if (allChannelIds.Length == 0)
{
    allChannelIds = session.Sources
        .SelectMany(source => Enumerable.Range(1, source.ChannelCount).Select(channelNumber => source.SourceId * 100 + channelNumber))
        .Distinct()
        .OrderBy(id => id)
        .ToArray();
}
int[] channelIds = allChannelIds
    .Take(4)
    .ToArray();
Dictionary<string, int> previewFilesByLevel = previewFiles
    .GroupBy(element => element.GetProperty("LevelName").GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
    .Where(group => !string.IsNullOrWhiteSpace(group.Key))
    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
bool requireRawIndex = Directory.Exists(Path.Combine(sessionPath, "raw_index"));
SessionArtifactValidationResult artifactValidation = await SessionArtifactValidator.ValidateAsync(
    diagnostics.Artifacts,
    requireRawIndex);
if (!artifactValidation.IsValid)
{
    Console.WriteLine("ARTIFACT_STRUCTURE_INVALID");
    foreach (string error in artifactValidation.Errors)
    {
        Console.WriteLine(error);
    }

    resultWriter?.Dispose();
    return 13;
}

if (rawOnly && channelIds.Length > 0)
{
    long coveredEndSample = ReadMinEndSampleIndexForChannels(diagnostics.Artifacts.ManifestPath, channelIds);
    if (coveredEndSample > 0)
    {
        maxEndSampleIndex = coveredEndSample;
        totalDurationSeconds = sampleRateHz > 0
            ? maxEndSampleIndex / sampleRateHz
            : 0.0;
    }
}

if (channelIds.Length == 0)
{
    Console.WriteLine("No channels found in preview index.");
    resultWriter?.Dispose();
    return 4;
}

Console.WriteLine($"PerformanceRunDirectory={PerformanceOutputPaths.CurrentRunDirectory}");
Console.WriteLine($"SessionPath={sessionPath}");
Console.WriteLine($"MetadataSource={diagnostics.Source}");
Console.WriteLine($"CatalogPath={diagnostics.Artifacts.CatalogPath ?? string.Empty}");
Console.WriteLine($"ManifestPath={diagnostics.Artifacts.ManifestPath ?? string.Empty}");
Console.WriteLine($"StorageFormat={session.StorageFormat}");
if (diagnostics.CatalogStructure is not null)
{
    Console.WriteLine($"CatalogRawSegments={diagnostics.CatalogStructure.RawSegmentCount}");
    Console.WriteLine($"CatalogPreviewSegments={diagnostics.CatalogStructure.PreviewSegmentCount}");
    Console.WriteLine($"CatalogPreviewMappings={diagnostics.CatalogStructure.PreviewMappingCount}");
    Console.WriteLine($"CatalogSegmentSources={diagnostics.CatalogStructure.SegmentSourceCount}");
    Console.WriteLine($"CatalogSegmentChannels={diagnostics.CatalogStructure.SegmentChannelCount}");
    foreach (var pair in diagnostics.CatalogStructure.PreviewSegmentsByLevel.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"CatalogPreviewLevel:{pair.Key}={pair.Value}");
    }
}
Console.WriteLine($"SessionId={session.SessionId}");
Console.WriteLine($"SampleRateHz={sampleRateHz:F3}");
Console.WriteLine($"TotalDurationSeconds={totalDurationSeconds:F3}");
Console.WriteLine($"Channels={string.Join(",", channelIds)}");
Console.WriteLine($"LargeSessionMode={(largeSessionMode ? 1 : 0)}");
Console.WriteLine($"RawOnly={(rawOnly ? 1 : 0)}");
Console.WriteLine($"RepeatCount={repeatCount}");
Console.WriteLine($"PreviewFilesValidated={artifactValidation.PreviewFileCount}");
Console.WriteLine($"PreviewBytesValidated={artifactValidation.PreviewBytes}");
Console.WriteLine($"RawIndexFilesValidated={artifactValidation.RawIndexFileCount}");
Console.WriteLine($"RawIndexBytesValidated={artifactValidation.RawIndexBytes}");
Console.WriteLine($"RawCaptureFileExists={(artifactValidation.RawCaptureFileExists ? 1 : 0)}");
Console.WriteLine("FastSegmentFiles=" + CountManifestFiles(diagnostics.Artifacts.ManifestPath, ".dhseg"));
Console.WriteLine("TdmsFiles=" + CountManifestFiles(diagnostics.Artifacts.ManifestPath, ".tdms"));

if (requireCatalog)
{
    if (diagnostics.CatalogStructure is null)
    {
        Console.WriteLine("CATALOG_STRUCTURE_MISSING");
        resultWriter?.Dispose();
        return 9;
    }

    int expectedRawSegments = allChannelIds.Length;
    int expectedPreviewSegments = previewFiles.Length;
    int expectedSegmentRows = expectedRawSegments + expectedPreviewSegments;
    if (diagnostics.CatalogStructure.RawSegmentCount != expectedRawSegments
        || diagnostics.CatalogStructure.PreviewSegmentCount != expectedPreviewSegments
        || diagnostics.CatalogStructure.PreviewMappingCount != expectedPreviewSegments
        || diagnostics.CatalogStructure.SegmentSourceCount != expectedSegmentRows
        || diagnostics.CatalogStructure.SegmentChannelCount != expectedSegmentRows)
    {
        Console.WriteLine("CATALOG_STRUCTURE_INVALID");
        Console.WriteLine($"ExpectedRawSegments={expectedRawSegments}");
        Console.WriteLine($"ExpectedPreviewSegments={expectedPreviewSegments}");
        Console.WriteLine($"ExpectedSegmentRows={expectedSegmentRows}");
        resultWriter?.Dispose();
        return 10;
    }

    foreach (var pair in previewFilesByLevel)
    {
        diagnostics.CatalogStructure.PreviewSegmentsByLevel.TryGetValue(pair.Key, out int actual);
        if (actual != pair.Value)
        {
            Console.WriteLine("CATALOG_PREVIEW_LEVEL_INVALID");
            Console.WriteLine($"Level={pair.Key}");
            Console.WriteLine($"Expected={pair.Value}");
            Console.WriteLine($"Actual={actual}");
            resultWriter?.Dispose();
            return 11;
        }
    }
}

var requests = rawOnly
    ? new[]
    {
        new { Label = "L0.Start2s", Level = PreviewLevel.L0, Start = 0.0, End = Math.Min(2.0, Math.Max(2.0, totalDurationSeconds)) },
        new { Label = "L0.Mid2s", Level = PreviewLevel.L0, Start = Math.Max(0.0, totalDurationSeconds * 0.5), End = Math.Max(2.0, totalDurationSeconds * 0.5 + 2.0) },
        new { Label = "L0.End2s", Level = PreviewLevel.L0, Start = Math.Max(0.0, totalDurationSeconds - 2.0), End = Math.Max(2.0, totalDurationSeconds) }
    }
    : new[]
    {
        new { Label = "L4.Full", Level = PreviewLevel.L4, Start = 0.0, End = Math.Max(2.0, totalDurationSeconds) },
        new { Label = "L3.Mid", Level = PreviewLevel.L3, Start = Math.Max(0.0, totalDurationSeconds * 0.25), End = Math.Max(2.0, totalDurationSeconds * 0.50) },
        new { Label = "L2.Near", Level = PreviewLevel.L2, Start = Math.Max(0.0, totalDurationSeconds - Math.Max(10.0, totalDurationSeconds * 0.10)), End = Math.Max(2.0, totalDurationSeconds - Math.Max(1.0, totalDurationSeconds * 0.01)) },
        new { Label = "L1.2s", Level = PreviewLevel.L1, Start = Math.Max(0.0, totalDurationSeconds - 2.0), End = Math.Max(2.0, totalDurationSeconds) },
        new { Label = "L0.2s", Level = PreviewLevel.L0, Start = Math.Max(0.0, totalDurationSeconds - 2.0), End = Math.Max(2.0, totalDurationSeconds) }
    };

for (int repeatIndex = 1; repeatIndex <= repeatCount; repeatIndex++)
{
    foreach (var item in requests)
    {
        string label = repeatCount > 1
            ? $"Repeat{repeatIndex}.{item.Label}"
            : item.Label;
        var request = new PreviewReadRequest
        {
            SessionId = session.SessionId,
            ViewId = label,
            ChannelIds = channelIds,
            WindowStart = item.Start,
            WindowEnd = item.End,
            PreviewLevel = item.Level,
            MaxPointsPerChannel = 4000,
            RequireEnvelopeSemantics = true,
            AllowDegradedResult = true,
            RequireCompleteWindow = false
        };

        var queryTiming = Stopwatch.StartNew();
        CurveWindowSnapshot snapshot = await runtime.QueryAsync(request);
        queryTiming.Stop();
        var validation = CurveQueryValidator.ValidateSnapshot(snapshot);
        if (!validation.IsValid)
        {
            Console.WriteLine($"{label}:SNAPSHOT_INVALID");
            foreach (var error in validation.Errors)
            {
                Console.WriteLine(error);
            }
            resultWriter?.Dispose();
            return 5;
        }

        Console.WriteLine($"{label}:PreviewLevel={snapshot.PreviewLevel}");
        Console.WriteLine($"{label}:BuildState={snapshot.BuildState}");
        Console.WriteLine($"{label}:IsComplete={snapshot.IsComplete}");
        Console.WriteLine($"{label}:QueryLatencyMs={queryTiming.Elapsed.TotalMilliseconds:F3}");
        Console.WriteLine($"{label}:WindowStart={snapshot.WindowStart:F6}");
        Console.WriteLine($"{label}:WindowEnd={snapshot.WindowEnd:F6}");
        Console.WriteLine($"{label}:TotalActualPoints={snapshot.TotalActualPoints}");
        Console.WriteLine($"{label}:MaxActualPointsPerChannel={snapshot.MaxActualPointsPerChannel}");

        foreach (int channelId in snapshot.ChannelIds.OrderBy(id => id))
        {
            snapshot.ChannelData.TryGetValue(channelId, out var points);
            Console.WriteLine($"{label}:Channel {channelId}: points={(points?.Count ?? 0)}");
        }

        if (largeSessionMode
            && string.Equals(item.Label, "L4.Full", StringComparison.OrdinalIgnoreCase)
            && queryTiming.Elapsed.TotalMilliseconds > 1000.0)
        {
            Console.WriteLine("L4_FULL_QUERY_OVER_1S");
            Console.WriteLine($"L4.Full:QueryLatencyMs={queryTiming.Elapsed.TotalMilliseconds:F3}");
            resultWriter?.Dispose();
            return 12;
        }

        if (item.Level != PreviewLevel.L0)
        {
            var statisticsRequest = new CurveStatisticsRequest
            {
                SessionId = session.SessionId,
                ViewId = $"{label}.Stats",
                ChannelIds = channelIds,
                WindowStart = item.Start,
                WindowEnd = item.End,
                PreviewLevel = item.Level
            };
            CurveStatisticsResult statistics = await runtime.QueryStatisticsAsync(statisticsRequest);
            if (statistics.BuildState == BuildState.Missing)
            {
                Console.WriteLine($"{label}.Stats:BuildState={statistics.BuildState}");
                resultWriter?.Dispose();
                return 6;
            }

            Console.WriteLine($"{label}.Stats:BuildState={statistics.BuildState}");
            Console.WriteLine($"{label}.Stats:IsComplete={statistics.IsComplete}");
            foreach (int channelId in channelIds.OrderBy(id => id))
            {
                if (!statistics.ChannelStatistics.TryGetValue(channelId, out var stat))
                {
                    Console.WriteLine($"{label}.Stats:Channel {channelId}: missing");
                    resultWriter?.Dispose();
                    return 7;
                }

                Console.WriteLine(
                    $"{label}.Stats:Channel {channelId}: count={stat.Count}, min={stat.Min:F6}, max={stat.Max:F6}, mean={stat.Mean:F6}, stddev={stat.StandardDeviation:F6}");
            }
        }
    }
}

resultWriter?.Dispose();
return 0;

static long ReadMaxEndSampleIndex(string? manifestPath)
{
    if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
    {
        return 0L;
    }

    using var stream = File.OpenRead(manifestPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("TdmsSegments", out JsonElement segments)
        || segments.ValueKind != JsonValueKind.Array)
    {
        return 0L;
    }

    long maxEnd = 0L;
    foreach (JsonElement segment in segments.EnumerateArray())
    {
        long start = segment.TryGetProperty("StartSample", out JsonElement startElement) && startElement.TryGetInt64(out long parsedStart)
            ? parsedStart
            : 0L;
        long samples = segment.TryGetProperty("SamplesPerChannel", out JsonElement samplesElement) && samplesElement.TryGetInt64(out long parsedSamples)
            ? parsedSamples
            : 0L;
        maxEnd = Math.Max(maxEnd, start + samples);
    }

    return maxEnd;
}

static long ReadMinEndSampleIndexForChannels(string? manifestPath, IReadOnlyCollection<int> channelIds)
{
    if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath) || channelIds.Count == 0)
    {
        return 0L;
    }

    using var stream = File.OpenRead(manifestPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("TdmsSegments", out JsonElement segments)
        || segments.ValueKind != JsonValueKind.Array)
    {
        return 0L;
    }

    var requested = channelIds.ToHashSet();
    var maxEndByChannel = requested.ToDictionary(id => id, _ => 0L);
    foreach (JsonElement segment in segments.EnumerateArray())
    {
        if (!segment.TryGetProperty("ChannelIds", out JsonElement segmentChannels)
            || segmentChannels.ValueKind != JsonValueKind.Array)
        {
            continue;
        }

        long start = segment.TryGetProperty("StartSample", out JsonElement startElement) && startElement.TryGetInt64(out long parsedStart)
            ? parsedStart
            : 0L;
        long samples = segment.TryGetProperty("SamplesPerChannel", out JsonElement samplesElement) && samplesElement.TryGetInt64(out long parsedSamples)
            ? parsedSamples
            : 0L;
        long end = start + samples;
        foreach (JsonElement channelElement in segmentChannels.EnumerateArray())
        {
            if (channelElement.TryGetInt32(out int channelId)
                && maxEndByChannel.ContainsKey(channelId))
            {
                maxEndByChannel[channelId] = Math.Max(maxEndByChannel[channelId], end);
            }
        }
    }

    return maxEndByChannel.Values
        .Where(value => value > 0)
        .DefaultIfEmpty(0L)
        .Min();
}

static string ResolveSessionPath(string inputPath)
{
    if (Directory.Exists(inputPath))
    {
        if (HasPreviewIndex(inputPath))
        {
            return inputPath;
        }

        string? artifacts = Directory
            .EnumerateDirectories(inputPath, "*.artifacts", SearchOption.TopDirectoryOnly)
            .Where(HasPreviewIndex)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (artifacts is not null)
        {
            return artifacts;
        }

        return inputPath;
    }

    if (!File.Exists(inputPath))
    {
        throw new FileNotFoundException("Input path does not exist.", inputPath);
    }

    string fileName = Path.GetFileName(inputPath);
    string directory = Path.GetDirectoryName(inputPath) ?? ".";

    if (fileName.EndsWith(".sdkraw.bin", StringComparison.OrdinalIgnoreCase))
    {
        string stem = fileName[..^".sdkraw.bin".Length];
        string? artifacts = Directory
            .EnumerateDirectories(directory, $"{stem}_converted_*.artifacts", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (artifacts is not null)
        {
            return artifacts;
        }
    }

    if (fileName.EndsWith(".tdms", StringComparison.OrdinalIgnoreCase))
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string artifactsPath = Path.Combine(directory, $"{stem}.artifacts");
        if (Directory.Exists(artifactsPath))
        {
            return artifactsPath;
        }
    }

    throw new InvalidOperationException($"Unable to resolve session artifacts from path: {inputPath}");
}

static bool HasPreviewIndex(string path)
    => File.Exists(Path.Combine(path, "preview_levels", "preview.index.json"));

static int CountManifestFiles(string? manifestPath, string extension)
{
    if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
    {
        return 0;
    }

    using var stream = File.OpenRead(manifestPath);
    using var document = JsonDocument.Parse(stream);
    if (!document.RootElement.TryGetProperty("TdmsFiles", out JsonElement tdmsFiles)
        || tdmsFiles.ValueKind != JsonValueKind.Array)
    {
        return 0;
    }

    return tdmsFiles
        .EnumerateArray()
        .Count(element =>
            element.ValueKind == JsonValueKind.String
            && (element.GetString() ?? string.Empty).EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
