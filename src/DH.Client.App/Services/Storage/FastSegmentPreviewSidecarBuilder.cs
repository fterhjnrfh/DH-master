using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

public sealed class FastSegmentPreviewBuildProgress
{
    public int FilesProcessed { get; init; }

    public int TotalFiles { get; init; }

    public long PayloadBytesProcessed { get; init; }
}

public sealed class FastSegmentPreviewBuildResult
{
    public string SessionPath { get; init; } = string.Empty;

    public string ArtifactRootPath { get; init; } = string.Empty;

    public string PreviewIndexPath { get; init; } = string.Empty;

    public int FastSegmentFileCount { get; init; }

    public int TdmsSegmentFileCount { get; init; }

    public int ChannelCount { get; init; }

    public IReadOnlyList<string> Levels { get; init; } = Array.Empty<string>();

    public long PayloadBytesProcessed { get; init; }

    public TimeSpan Elapsed { get; init; }
}

public sealed class FastSegmentPreviewSidecarBuilder
{
    private const int FastSegmentHeaderBytes = 4096;
    private const int ReadBufferFloatCount = 262_144;

    public FastSegmentPreviewBuildResult Build(
        string sessionOrArtifactPath,
        Action<FastSegmentPreviewBuildProgress>? progressCallback = null)
        => Build(sessionOrArtifactPath, levelNames: null, progressCallback);

    public FastSegmentPreviewBuildResult Build(
        string sessionOrArtifactPath,
        IReadOnlyCollection<string>? levelNames,
        Action<FastSegmentPreviewBuildProgress>? progressCallback = null)
    {
        if (string.IsNullOrWhiteSpace(sessionOrArtifactPath))
        {
            throw new ArgumentException("Session path is required.", nameof(sessionOrArtifactPath));
        }

        string artifactRootPath = ResolveArtifactRootPath(sessionOrArtifactPath);
        string manifestPath = Path.Combine(artifactRootPath, "session.manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("session.manifest.json was not found.", manifestPath);
        }

        FastSegmentManifest manifest = ReadManifest(manifestPath);
        if (manifest.FastSegmentFiles.Count == 0 && manifest.TdmsSegmentFiles.Count == 0)
        {
            throw new InvalidOperationException("No .dhseg or TDMS segment files were listed in the session manifest.");
        }

        var headers = manifest.FastSegmentFiles
            .Select(path => TryReadFastSegmentHeader(path, out FastSegmentHeader header)
                ? new FastSegmentFile(path, header)
                : null)
            .Where(file => file is not null)
            .Select(file => file!)
            .OrderBy(file => file.Header.SourceId)
            .ThenBy(file => file.Header.SegmentIndex)
            .ToArray();
        var tdmsSegments = manifest.TdmsSegmentFiles
            .OrderBy(file => file.SourceId)
            .ThenBy(file => file.SegmentIndex)
            .ThenBy(file => file.StartSample)
            .ToArray();
        if (headers.Length == 0 && tdmsSegments.Length == 0)
        {
            throw new InvalidDataException("No valid .dhseg headers or TDMS segment entries were found.");
        }

        int[] channelIds = manifest.ChannelIds.Count > 0
            ? manifest.ChannelIds.OrderBy(static id => id).ToArray()
            : headers.Length > 0
                ? headers.SelectMany(file => file.Header.ChannelIds).Distinct().OrderBy(static id => id).ToArray()
                : tdmsSegments.SelectMany(file => file.ChannelIds).Distinct().OrderBy(static id => id).ToArray();
        double sampleRateHz = manifest.SampleRateHz > 0
            ? manifest.SampleRateHz
            : headers.Length > 0
                ? headers[0].Header.SampleRateHz
                : tdmsSegments[0].SampleRateHz;
        string sessionName = string.IsNullOrWhiteSpace(manifest.TaskName)
            ? Path.GetFileName(Directory.GetParent(artifactRootPath)?.FullName ?? artifactRootPath)
            : manifest.TaskName;
        IReadOnlyList<PreviewLevelSpec> previewLevels = ResolvePreviewLevels(levelNames);
        ResetPreviewRoot(artifactRootPath);

        var stopwatch = Stopwatch.StartNew();
        using var previewWriter = new PreviewSidecarWriter(
            artifactRootPath,
            sessionName,
            sampleRateHz,
            channelIds,
            previewLevels);

        long processedPayloadBytes = 0;
        int totalFiles = headers.Length + tdmsSegments.Length;
        int filesProcessed = 0;
        for (int i = 0; i < headers.Length; i++)
        {
            FastSegmentFile file = headers[i];
            WriteFilePreview(file, previewWriter);
            processedPayloadBytes += file.Header.PayloadBytes;
            filesProcessed++;
            progressCallback?.Invoke(new FastSegmentPreviewBuildProgress
            {
                FilesProcessed = filesProcessed,
                TotalFiles = totalFiles,
                PayloadBytesProcessed = processedPayloadBytes
            });
        }

        for (int i = 0; i < tdmsSegments.Length; i++)
        {
            TdmsSegmentFile file = tdmsSegments[i];
            WriteTdmsSegmentPreview(file, previewWriter);
            processedPayloadBytes += file.PayloadBytes;
            filesProcessed++;
            progressCallback?.Invoke(new FastSegmentPreviewBuildProgress
            {
                FilesProcessed = filesProcessed,
                TotalFiles = totalFiles,
                PayloadBytesProcessed = processedPayloadBytes
            });
        }

        PreviewSidecarArtifacts artifacts = previewWriter.Complete();
        string[] previewFiles = artifacts.DataFilePaths
            .Concat(new[] { artifacts.IndexManifestPath })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        UpdateManifestPreviewFiles(manifestPath, previewFiles);

        stopwatch.Stop();
        return new FastSegmentPreviewBuildResult
        {
            SessionPath = Directory.GetParent(artifactRootPath)?.FullName ?? artifactRootPath,
            ArtifactRootPath = artifactRootPath,
            PreviewIndexPath = artifacts.IndexManifestPath,
            FastSegmentFileCount = headers.Length,
            TdmsSegmentFileCount = tdmsSegments.Length,
            ChannelCount = channelIds.Length,
            Levels = previewLevels.Select(static level => level.LevelName).ToArray(),
            PayloadBytesProcessed = processedPayloadBytes,
            Elapsed = stopwatch.Elapsed
        };
    }

    private static void WriteFilePreview(
        FastSegmentFile file,
        PreviewSidecarWriter previewWriter)
    {
        FastSegmentHeader header = file.Header;
        float[] buffer = ArrayPool<float>.Shared.Rent((int)Math.Min(ReadBufferFloatCount, Math.Max(1, header.SamplesPerChannel)));
        try
        {
            using var stream = File.Open(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] byteBuffer = new byte[buffer.Length * sizeof(float)];
            for (int channelOffset = 0; channelOffset < header.ChannelIds.Length; channelOffset++)
            {
                int channelId = header.ChannelIds[channelOffset];
                long channelOffsetBytes = header.HeaderBytes + ((long)channelOffset * header.SamplesPerChannel * sizeof(float));
                stream.Seek(channelOffsetBytes, SeekOrigin.Begin);

                int remaining = header.SamplesPerChannel;
                while (remaining > 0)
                {
                    int floatsToRead = Math.Min(buffer.Length, remaining);
                    int bytesToRead = floatsToRead * sizeof(float);
                    int bytesRead = ReadExactlyOrLess(stream, byteBuffer, bytesToRead);
                    int floatsRead = bytesRead / sizeof(float);
                    if (floatsRead <= 0)
                    {
                        break;
                    }

                    Buffer.BlockCopy(byteBuffer, 0, buffer, 0, floatsRead * sizeof(float));
                    previewWriter.Write(channelId, buffer.AsSpan(0, floatsRead));
                    remaining -= floatsRead;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static void WriteTdmsSegmentPreview(
        TdmsSegmentFile file,
        PreviewSidecarWriter previewWriter)
    {
        long rawDataOffset = TryReadTdmsRawDataOffset(file.Path);
        if (rawDataOffset < 0)
        {
            throw new InvalidDataException($"Unable to locate TDMS raw data offset: {file.Path}");
        }

        float[] buffer = ArrayPool<float>.Shared.Rent((int)Math.Min(ReadBufferFloatCount, Math.Max(1, file.SamplesPerChannel)));
        byte[] byteBuffer = new byte[buffer.Length * sizeof(float)];
        try
        {
            using var stream = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                1024 * 1024,
                FileOptions.SequentialScan);
            long expectedDataBytes = file.CompressionEnabled
                ? file.ChannelPayloadBytes.Sum(static bytes => (long)bytes)
                : (long)file.ChannelIds.Length * file.SamplesPerChannel * sizeof(float);
            if (stream.Length < rawDataOffset + expectedDataBytes)
            {
                throw new InvalidDataException($"TDMS segment payload is shorter than manifest metadata: {file.Path}");
            }

            for (int channelOffset = 0; channelOffset < file.ChannelIds.Length; channelOffset++)
            {
                int channelId = file.ChannelIds[channelOffset];
                if (file.CompressionEnabled)
                {
                    float[] samples = ReadCompressedTdmsChannel(file, rawDataOffset, channelOffset);
                    if (samples.Length > 0)
                    {
                        previewWriter.Write(channelId, samples);
                    }

                    continue;
                }

                long channelOffsetBytes = rawDataOffset + ((long)channelOffset * file.SamplesPerChannel * sizeof(float));
                stream.Seek(channelOffsetBytes, SeekOrigin.Begin);

                int remaining = file.SamplesPerChannel;
                while (remaining > 0)
                {
                    int floatsToRead = Math.Min(buffer.Length, remaining);
                    int bytesToRead = floatsToRead * sizeof(float);
                    int bytesRead = ReadExactlyOrLess(stream, byteBuffer, bytesToRead);
                    int floatsRead = bytesRead / sizeof(float);
                    if (floatsRead <= 0)
                    {
                        break;
                    }

                    Buffer.BlockCopy(byteBuffer, 0, buffer, 0, floatsRead * sizeof(float));
                    previewWriter.Write(channelId, buffer.AsSpan(0, floatsRead));
                    remaining -= floatsRead;
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    private static float[] ReadCompressedTdmsChannel(
        TdmsSegmentFile file,
        long rawDataOffset,
        int channelOffset)
    {
        if (file.ChannelPayloadBytes.Length != file.ChannelIds.Length)
        {
            throw new InvalidDataException($"Compressed TDMS segment is missing channel payload sizes: {file.Path}");
        }

        long channelOffsetBytes = rawDataOffset;
        for (int i = 0; i < channelOffset; i++)
        {
            channelOffsetBytes += file.ChannelPayloadBytes[i];
        }

        int payloadBytes = file.ChannelPayloadBytes[channelOffset];
        if (payloadBytes <= 0)
        {
            return Array.Empty<float>();
        }

        byte[] payload = new byte[payloadBytes];
        using var stream = new FileStream(
            file.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess);
        stream.Seek(channelOffsetBytes, SeekOrigin.Begin);
        int read = ReadExactlyOrLess(stream, payload, payload.Length);
        if (read != payload.Length)
        {
            throw new InvalidDataException($"Compressed TDMS channel payload is shorter than manifest metadata: {file.Path}");
        }

        return FloatSampleCompressionCodec.Decode(
            payload,
            payloadBytes,
            file.SamplesPerChannel,
            file.CompressionType,
            file.PreprocessType);
    }

    private static int ReadExactlyOrLess(Stream stream, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static IReadOnlyList<PreviewLevelSpec> ResolvePreviewLevels(
        IReadOnlyCollection<string>? levelNames)
    {
        IReadOnlyList<PreviewLevelSpec> defaults = PreviewSidecarWriter.CreateDefaultLevels();
        if (levelNames is null || levelNames.Count == 0)
        {
            return defaults;
        }

        var requested = new HashSet<string>(
            levelNames
                .Select(static level => level.Trim())
                .Where(static level => !string.IsNullOrWhiteSpace(level)),
            StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0)
        {
            return defaults;
        }

        var selected = defaults
            .Where(level => requested.Contains(level.LevelName))
            .ToArray();
        var known = selected
            .Select(static level => level.LevelName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] unknown = requested
            .Where(level => !known.Contains(level))
            .OrderBy(level => level, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"Unknown preview level(s): {string.Join(",", unknown)}. Valid values: L1,L2,L3,L4.");
        }

        return selected;
    }

    private static void ResetPreviewRoot(string artifactRootPath)
    {
        string previewRootPath = Path.Combine(artifactRootPath, "preview_levels");
        if (Directory.Exists(previewRootPath))
        {
            Directory.Delete(previewRootPath, recursive: true);
        }
    }

    private static string ResolveArtifactRootPath(string inputPath)
    {
        string fullPath = Path.GetFullPath(inputPath);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(fullPath);
        }

        if (File.Exists(Path.Combine(fullPath, "session.manifest.json")))
        {
            return fullPath;
        }

        string? artifactPath = Directory
            .EnumerateDirectories(fullPath, "*.artifacts", SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "session.manifest.json")))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (artifactPath is not null)
        {
            return artifactPath;
        }

        throw new FileNotFoundException("No session artifacts directory with session.manifest.json was found.", fullPath);
    }

    private static FastSegmentManifest ReadManifest(string manifestPath)
    {
        using var stream = File.OpenRead(manifestPath);
        using var document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        string taskName = root.TryGetProperty("TaskName", out JsonElement taskNameElement)
            ? taskNameElement.GetString() ?? string.Empty
            : string.Empty;
        double sampleRateHz = root.TryGetProperty("SampleRateHz", out JsonElement sampleRateElement)
            ? sampleRateElement.GetDouble()
            : 0d;

        var files = new List<string>();
        if (root.TryGetProperty("TdmsFiles", out JsonElement filesElement)
            && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement fileElement in filesElement.EnumerateArray())
            {
                string? path = fileElement.ValueKind == JsonValueKind.String
                    ? fileElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(path)
                    && path.EndsWith(".dhseg", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(path))
                {
                    files.Add(Path.GetFullPath(path));
                }
            }
        }

        var channelIds = new SortedSet<int>();
        if (root.TryGetProperty("Sources", out JsonElement sourcesElement)
            && sourcesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement sourceElement in sourcesElement.EnumerateArray())
            {
                int sourceId = sourceElement.TryGetProperty("SourceId", out JsonElement sourceIdElement)
                    ? sourceIdElement.GetInt32()
                    : -1;
                int channelCount = sourceElement.TryGetProperty("ChannelCount", out JsonElement channelCountElement)
                    ? channelCountElement.GetInt32()
                    : 0;
                for (int channelNumber = 1; sourceId >= 0 && channelNumber <= channelCount; channelNumber++)
                {
                    channelIds.Add(ChannelNaming.MakeChannelId(sourceId, channelNumber));
                }
            }
        }

        var tdmsSegments = new List<TdmsSegmentFile>();
        if (root.TryGetProperty("TdmsSegments", out JsonElement segmentsElement)
            && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement segmentElement in segmentsElement.EnumerateArray())
            {
                TdmsSegmentFile? segment = TryReadTdmsSegmentManifestEntry(segmentElement);
                if (segment is not null)
                {
                    tdmsSegments.Add(segment);
                    foreach (int channelId in segment.ChannelIds)
                    {
                        channelIds.Add(channelId);
                    }
                }
            }
        }

        return new FastSegmentManifest(taskName, sampleRateHz, files, tdmsSegments, channelIds);
    }

    private static TdmsSegmentFile? TryReadTdmsSegmentManifestEntry(JsonElement segmentElement)
    {
        if (!segmentElement.TryGetProperty("Path", out JsonElement pathElement)
            || pathElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        int sourceId = segmentElement.TryGetProperty("SourceId", out JsonElement sourceElement)
            ? sourceElement.GetInt32()
            : -1;
        int segmentIndex = segmentElement.TryGetProperty("SegmentIndex", out JsonElement indexElement)
            ? indexElement.GetInt32()
            : -1;
        long startSample = segmentElement.TryGetProperty("StartSample", out JsonElement startElement)
            ? startElement.GetInt64()
            : 0L;
        int samplesPerChannel = segmentElement.TryGetProperty("SamplesPerChannel", out JsonElement samplesElement)
            ? samplesElement.GetInt32()
            : 0;
        double sampleRateHz = segmentElement.TryGetProperty("SampleRateHz", out JsonElement sampleRateElement)
            ? sampleRateElement.GetDouble()
            : 0d;
        var channelIds = new List<int>();
        if (segmentElement.TryGetProperty("ChannelIds", out JsonElement channelsElement)
            && channelsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement channelElement in channelsElement.EnumerateArray())
            {
                if (channelElement.ValueKind == JsonValueKind.Number)
                {
                    channelIds.Add(channelElement.GetInt32());
                }
            }
        }
        bool compressionEnabled = segmentElement.TryGetProperty("CompressionEnabled", out JsonElement compressionEnabledElement)
            && compressionEnabledElement.ValueKind is JsonValueKind.True;
        CompressionType compressionType = TryReadEnum(segmentElement, "CompressionType", CompressionType.None);
        PreprocessType preprocessType = TryReadEnum(segmentElement, "PreprocessType", PreprocessType.None);
        int[] channelPayloadBytes = TryReadIntArray(segmentElement, "ChannelPayloadBytes");

        if (sourceId < 0
            || segmentIndex < 0
            || samplesPerChannel <= 0
            || sampleRateHz <= 0
            || channelIds.Count == 0)
        {
            return null;
        }

        return new TdmsSegmentFile(
            Path.GetFullPath(path),
            sourceId,
            segmentIndex,
            startSample,
            samplesPerChannel,
            sampleRateHz,
            channelIds.ToArray(),
            compressionEnabled,
            compressionType,
            preprocessType,
            channelPayloadBytes);
    }

    private static TEnum TryReadEnum<TEnum>(JsonElement element, string name, TEnum fallback)
        where TEnum : struct, Enum
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            return fallback;
        }

        return Enum.TryParse(value.GetString(), ignoreCase: true, out TEnum parsed)
            ? parsed
            : fallback;
    }

    private static int[] TryReadIntArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<int>();
        }

        return value.EnumerateArray()
            .Where(item => item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .ToArray();
    }

    private static void UpdateManifestPreviewFiles(
        string manifestPath,
        IReadOnlyList<string> previewFiles)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var stream = File.Create(manifestPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("PreviewFiles"))
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WritePropertyName("PreviewFiles");
        writer.WriteStartArray();
        foreach (string file in previewFiles)
        {
            writer.WriteStringValue(Path.GetFullPath(file));
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
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

            header = new FastSegmentHeader(
                sourceId,
                segmentIndex,
                channelIds,
                samplesPerChannel,
                sampleRateHz,
                payloadBytes,
                headerBytes);
            return true;
        }
        catch
        {
            header = default;
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

    private sealed record FastSegmentManifest(
        string TaskName,
        double SampleRateHz,
        IReadOnlyList<string> FastSegmentFiles,
        IReadOnlyList<TdmsSegmentFile> TdmsSegmentFiles,
        IReadOnlyCollection<int> ChannelIds);

    private sealed record FastSegmentFile(
        string Path,
        FastSegmentHeader Header);

    private sealed record TdmsSegmentFile(
        string Path,
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
        public long PayloadBytes => (long)SamplesPerChannel * ChannelIds.Length * sizeof(float);
    }

    private readonly record struct FastSegmentHeader(
        int SourceId,
        int SegmentIndex,
        int[] ChannelIds,
        int SamplesPerChannel,
        double SampleRateHz,
        long PayloadBytes,
        int HeaderBytes);
}
