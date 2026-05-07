using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DH.Client.App.Data.Query;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal static class PersistedPreviewSessionManifestWriter
{
    internal sealed record ManifestWriteResult(
        Guid SessionId,
        string ManifestPath);

    public static ManifestWriteResult Write(
        string artifactRootPath,
        string sessionName,
        double sampleRateHz,
        IReadOnlyCollection<int> channelIds,
        IReadOnlyCollection<string> tdmsFiles,
        IReadOnlyCollection<string> previewFiles,
        SdkRawCaptureManifest? captureManifest)
    {
        Directory.CreateDirectory(artifactRootPath);
        Guid sessionId = Guid.NewGuid();

        var sourceDescriptors = channelIds
            .GroupBy(ChannelNaming.GetDeviceId)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                SourceId = group.Key,
                DeviceName = $"Device {group.Key:D2}",
                ChannelCount = group.Count(),
                SampleRateHz = sampleRateHz,
                TimeAxisKind = nameof(TimeAxisKind.SampleIndexMappedTime)
            })
            .ToArray();

        bool hasFastSegments = tdmsFiles.Any(static path => path.EndsWith(".dhseg", StringComparison.OrdinalIgnoreCase));
        var payload = new
        {
            SessionId = sessionId,
            TaskName = sessionName,
            StartTime = captureManifest?.StartedAtUtc.ToUniversalTime() ?? DateTime.UtcNow,
            EndTime = captureManifest?.StoppedAtUtc.ToUniversalTime(),
            StorageFormat = hasFastSegments ? "dh-fast-segment+preview-sidecar" : "tdms+preview-sidecar",
            RecoveredState = false,
            PreviewLevels = new[] { "L1", "L2", "L3", "L4" },
            Sources = sourceDescriptors,
            TdmsFiles = tdmsFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetFullPath(path))
                .ToArray(),
            PreviewFiles = previewFiles
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.GetFullPath(path))
                .ToArray(),
            TdmsSegments = captureManifest?.TdmsSegments
                .OrderBy(segment => segment.SourceId)
                .ThenBy(segment => segment.SegmentIndex)
                .Select(segment => new
                {
                    Path = Path.GetFullPath(segment.Path),
                    segment.SourceId,
                    segment.SegmentIndex,
                    segment.StartSample,
                    segment.SamplesPerChannel,
                    segment.EndSampleExclusive,
                    segment.SampleRateHz,
                    segment.ChannelIds,
                    segment.CompressionEnabled,
                    segment.CompressionType,
                    segment.PreprocessType,
                    segment.CompressionOriginalPayloadBytes,
                    segment.CompressionPayloadBytes,
                    segment.ChannelPayloadBytes
                })
                .ToArray() ?? Array.Empty<object>(),
            CaptureFileName = captureManifest?.CaptureFileName ?? string.Empty,
            CaptureFilePath = !string.IsNullOrWhiteSpace(captureManifest?.CaptureFileName)
                ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(artifactRootPath) ?? artifactRootPath, captureManifest.CaptureFileName))
                : string.Empty,
            SampleRateHz = sampleRateHz
        };

        string manifestPath = Path.Combine(artifactRootPath, "session.manifest.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(payload, options));
        return new ManifestWriteResult(sessionId, manifestPath);
    }
}
