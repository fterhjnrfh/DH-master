using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public static class SessionArtifactValidator
{
    private const int PreviewBucketRecordSize = 60;
    private const int RawIndexEntrySize = 40;

    public static async ValueTask<SessionArtifactValidationResult> ValidateAsync(
        SessionArtifactPaths artifacts,
        bool requireRawIndex,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var errors = new List<string>();
        int previewFileCount = 0;
        long previewBytes = 0;
        if (!string.IsNullOrWhiteSpace(artifacts.PreviewIndexManifestPath))
        {
            PreviewArtifactValidation previewValidation = await ValidatePreviewFilesAsync(
                artifacts.PreviewIndexManifestPath,
                ct);
            errors.AddRange(previewValidation.Errors);
            previewFileCount = previewValidation.PreviewFileCount;
            previewBytes = previewValidation.PreviewBytes;
        }

        string rawIndexManifestPath = Path.Combine(artifacts.SessionPath, "raw_index", "raw.index.json");
        RawArtifactValidation rawValidation = await ValidateRawIndexFilesAsync(
            rawIndexManifestPath,
            requireRawIndex,
            ct);
        errors.AddRange(rawValidation.Errors);

        return new SessionArtifactValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            PreviewFileCount = previewFileCount,
            PreviewBytes = previewBytes,
            RawIndexFileCount = rawValidation.RawIndexFileCount,
            RawIndexBytes = rawValidation.RawIndexBytes,
            RawCaptureFileExists = rawValidation.RawCaptureFileExists
        };
    }

    private static async ValueTask<PreviewArtifactValidation> ValidatePreviewFilesAsync(
        string previewIndexManifestPath,
        CancellationToken ct)
    {
        var errors = new List<string>();
        if (!File.Exists(previewIndexManifestPath))
        {
            errors.Add($"Missing preview index manifest: {previewIndexManifestPath}");
            return new PreviewArtifactValidation(errors, 0, 0);
        }

        string previewRootPath = Path.GetDirectoryName(previewIndexManifestPath) ?? string.Empty;
        await using var stream = File.OpenRead(previewIndexManifestPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("Files", out JsonElement filesElement)
            || filesElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("preview.index.json does not contain a Files array.");
            return new PreviewArtifactValidation(errors, 0, 0);
        }

        int previewFileCount = 0;
        long previewBytes = 0;
        foreach (JsonElement file in filesElement.EnumerateArray())
        {
            string? relativePath = file.TryGetProperty("RelativeFilePath", out JsonElement relativePathElement)
                ? relativePathElement.GetString()
                : null;
            long bucketCount = file.TryGetProperty("BucketCount", out JsonElement bucketCountElement)
                ? bucketCountElement.GetInt64()
                : -1L;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                errors.Add("Preview file has empty RelativeFilePath.");
                continue;
            }

            string fullPath = Path.Combine(previewRootPath, relativePath);
            if (!File.Exists(fullPath))
            {
                errors.Add($"Missing preview file: {fullPath}");
                continue;
            }

            long expectedBytes = bucketCount * PreviewBucketRecordSize;
            long actualBytes = new FileInfo(fullPath).Length;
            if (bucketCount < 0 || actualBytes != expectedBytes)
            {
                errors.Add($"Preview file size mismatch: {fullPath}, expected={expectedBytes}, actual={actualBytes}");
            }

            previewFileCount++;
            previewBytes += actualBytes;
        }

        return new PreviewArtifactValidation(errors, previewFileCount, previewBytes);
    }

    private static async ValueTask<RawArtifactValidation> ValidateRawIndexFilesAsync(
        string rawIndexManifestPath,
        bool requireRawIndex,
        CancellationToken ct)
    {
        var errors = new List<string>();
        if (!File.Exists(rawIndexManifestPath))
        {
            if (requireRawIndex)
            {
                errors.Add($"Missing raw index manifest: {rawIndexManifestPath}");
            }

            return new RawArtifactValidation(errors, 0, 0, false);
        }

        await using var rawIndexStream = File.OpenRead(rawIndexManifestPath);
        using var rawIndexDocument = await JsonDocument.ParseAsync(rawIndexStream, cancellationToken: ct);
        JsonElement root = rawIndexDocument.RootElement;
        string? captureFilePath = root.TryGetProperty("CaptureFilePath", out JsonElement captureFilePathElement)
            ? captureFilePathElement.GetString()
            : null;
        bool rawCaptureFileExists = !string.IsNullOrWhiteSpace(captureFilePath) && File.Exists(captureFilePath);
        if (!rawCaptureFileExists)
        {
            errors.Add($"Missing raw capture file: {captureFilePath ?? string.Empty}");
        }

        if (!root.TryGetProperty("Channels", out JsonElement channelsElement)
            || channelsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("raw.index.json does not contain a Channels array.");
            return new RawArtifactValidation(errors, 0, 0, rawCaptureFileExists);
        }

        string rawIndexRootPath = Path.GetDirectoryName(rawIndexManifestPath) ?? string.Empty;
        int rawIndexFileCount = 0;
        long rawIndexBytes = 0;
        foreach (JsonElement channel in channelsElement.EnumerateArray())
        {
            string? relativePath = channel.TryGetProperty("RelativeFilePath", out JsonElement relativePathElement)
                ? relativePathElement.GetString()
                : null;
            long entryCount = channel.TryGetProperty("EntryCount", out JsonElement entryCountElement)
                ? entryCountElement.GetInt64()
                : -1L;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                errors.Add("Raw index channel has empty RelativeFilePath.");
                continue;
            }

            string fullPath = Path.Combine(rawIndexRootPath, relativePath);
            if (!File.Exists(fullPath))
            {
                errors.Add($"Missing raw index file: {fullPath}");
                continue;
            }

            long expectedBytes = entryCount * RawIndexEntrySize;
            long actualBytes = new FileInfo(fullPath).Length;
            if (entryCount < 0 || actualBytes != expectedBytes)
            {
                errors.Add($"Raw index file size mismatch: {fullPath}, expected={expectedBytes}, actual={actualBytes}");
            }

            rawIndexFileCount++;
            rawIndexBytes += actualBytes;
        }

        return new RawArtifactValidation(errors, rawIndexFileCount, rawIndexBytes, rawCaptureFileExists);
    }

    private sealed record PreviewArtifactValidation(
        IReadOnlyList<string> Errors,
        int PreviewFileCount,
        long PreviewBytes);

    private sealed record RawArtifactValidation(
        IReadOnlyList<string> Errors,
        int RawIndexFileCount,
        long RawIndexBytes,
        bool RawCaptureFileExists);
}

public sealed record SessionArtifactValidationResult
{
    public bool IsValid { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public int PreviewFileCount { get; init; }

    public long PreviewBytes { get; init; }

    public int RawIndexFileCount { get; init; }

    public long RawIndexBytes { get; init; }

    public bool RawCaptureFileExists { get; init; }
}
