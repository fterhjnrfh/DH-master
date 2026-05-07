using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal static class SessionCatalogWriter
{
    public static string Write(
        string artifactRootPath,
        string sessionId,
        string sessionName,
        double sampleRateHz,
        IReadOnlyCollection<int> channelIds,
        PreviewSidecarArtifacts previewArtifacts,
        string rawIndexManifestPath,
        SdkRawCaptureManifest? captureManifest)
    {
        Directory.CreateDirectory(artifactRootPath);
        string catalogPath = Path.Combine(artifactRootPath, "session.catalog.db");
        if (File.Exists(catalogPath))
        {
            File.Delete(catalogPath);
        }

        DateTimeOffset startTime = captureManifest?.StartedAtUtc.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        DateTimeOffset? endTime = captureManifest?.StoppedAtUtc.ToUniversalTime();

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        connection.Open();

        ExecuteNonQuery(connection, transaction: null, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, transaction: null, "PRAGMA synchronous=NORMAL;");

        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE session_info (
                session_id TEXT NOT NULL,
                task_name TEXT NOT NULL,
                start_time_utc TEXT NOT NULL,
                end_time_utc TEXT NULL,
                storage_format TEXT NOT NULL,
                recovered_state INTEGER NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE sources (
                source_id INTEGER NOT NULL,
                device_name TEXT NOT NULL,
                channel_count INTEGER NOT NULL,
                sample_rate_hz REAL NOT NULL,
                time_axis_kind TEXT NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE preview_levels (
                level_name TEXT NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE segments (
                segment_id TEXT NOT NULL,
                stream_kind TEXT NOT NULL,
                path TEXT NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                start_sample INTEGER NULL,
                end_sample INTEGER NULL,
                is_closed INTEGER NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE segment_sources (
                segment_id TEXT NOT NULL,
                source_id INTEGER NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE segment_channels (
                segment_id TEXT NOT NULL,
                channel_start INTEGER NOT NULL,
                channel_end INTEGER NOT NULL
            );");
        ExecuteNonQuery(connection, transaction,
            @"CREATE TABLE preview_mapping (
                raw_segment_id TEXT NOT NULL,
                preview_level TEXT NOT NULL,
                preview_segment_id TEXT NOT NULL
            );");

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO session_info
                  (session_id, task_name, start_time_utc, end_time_utc, storage_format, recovered_state)
                  VALUES ($session_id, $task_name, $start_time_utc, $end_time_utc, $storage_format, $recovered_state);";
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$task_name", sessionName);
            command.Parameters.AddWithValue("$start_time_utc", startTime.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$end_time_utc", endTime?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            command.Parameters.AddWithValue("$storage_format", "tdms+preview-sidecar");
            command.Parameters.AddWithValue("$recovered_state", 0);
            command.ExecuteNonQuery();
        }

        foreach (var source in channelIds
            .GroupBy(ChannelNaming.GetDeviceId)
            .OrderBy(group => group.Key))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO sources
                  (source_id, device_name, channel_count, sample_rate_hz, time_axis_kind)
                  VALUES ($source_id, $device_name, $channel_count, $sample_rate_hz, $time_axis_kind);";
            command.Parameters.AddWithValue("$source_id", source.Key);
            command.Parameters.AddWithValue("$device_name", $"Device {source.Key:D2}");
            command.Parameters.AddWithValue("$channel_count", source.Count());
            command.Parameters.AddWithValue("$sample_rate_hz", sampleRateHz);
            command.Parameters.AddWithValue("$time_axis_kind", "SampleIndexMappedTime");
            command.ExecuteNonQuery();
        }

        foreach (string level in new[] { "L1", "L2", "L3", "L4" })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO preview_levels (level_name) VALUES ($level_name);";
            command.Parameters.AddWithValue("$level_name", level);
            command.ExecuteNonQuery();
        }

        RawIndexManifest rawManifest = ReadRawManifest(rawIndexManifestPath);
        PreviewIndexManifest previewManifest = ReadPreviewManifest(previewArtifacts.IndexManifestPath);

        foreach (RawIndexChannelFileEntry channel in rawManifest.Channels.OrderBy(entry => entry.ChannelId))
        {
            int sourceId = ChannelNaming.GetDeviceId(channel.ChannelId);
            string segmentId = $"raw-ch{channel.ChannelId:D4}";
            string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(rawIndexManifestPath)!, channel.RelativeFilePath));
            DateTimeOffset segmentStart = startTime + TimeSpan.FromSeconds(channel.StartSampleIndex / sampleRateHz);
            DateTimeOffset segmentEnd = startTime + TimeSpan.FromSeconds(Math.Max(channel.EndSampleIndex, channel.StartSampleIndex) / sampleRateHz);

            InsertSegment(
                connection,
                transaction,
                segmentId,
                "raw",
                path,
                segmentStart,
                segmentEnd,
                channel.StartSampleIndex,
                channel.EndSampleIndex,
                sourceId,
                channel.ChannelId,
                channel.ChannelId);
        }

        foreach (PreviewFileIndexEntry file in previewManifest.Files.OrderBy(entry => entry.LevelName).ThenBy(entry => entry.ChannelId))
        {
            int sourceId = ChannelNaming.GetDeviceId(file.ChannelId);
            string segmentId = $"preview-{file.LevelName.ToLowerInvariant()}-ch{file.ChannelId:D4}";
            string path = Path.GetFullPath(Path.Combine(previewArtifacts.RootPath, file.RelativeFilePath));
            DateTimeOffset segmentStart = startTime + TimeSpan.FromSeconds(file.StartSampleIndex / sampleRateHz);
            DateTimeOffset segmentEnd = startTime + TimeSpan.FromSeconds(Math.Max(file.EndSampleIndex, file.StartSampleIndex) / sampleRateHz);

            InsertSegment(
                connection,
                transaction,
                segmentId,
                "preview",
                path,
                segmentStart,
                segmentEnd,
                file.StartSampleIndex,
                file.EndSampleIndex,
                sourceId,
                file.ChannelId,
                file.ChannelId);

            using var mappingCommand = connection.CreateCommand();
            mappingCommand.Transaction = transaction;
            mappingCommand.CommandText =
                @"INSERT INTO preview_mapping
                  (raw_segment_id, preview_level, preview_segment_id)
                  VALUES ($raw_segment_id, $preview_level, $preview_segment_id);";
            mappingCommand.Parameters.AddWithValue("$raw_segment_id", $"raw-ch{file.ChannelId:D4}");
            mappingCommand.Parameters.AddWithValue("$preview_level", file.LevelName);
            mappingCommand.Parameters.AddWithValue("$preview_segment_id", segmentId);
            mappingCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return catalogPath;
    }

    private static void InsertSegment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string segmentId,
        string streamKind,
        string path,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        long startSampleIndex,
        long endSampleIndex,
        int sourceId,
        int channelStart,
        int channelEnd)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO segments
                  (segment_id, stream_kind, path, start_time, end_time, start_sample, end_sample, is_closed)
                  VALUES ($segment_id, $stream_kind, $path, $start_time, $end_time, $start_sample, $end_sample, $is_closed);";
            command.Parameters.AddWithValue("$segment_id", segmentId);
            command.Parameters.AddWithValue("$stream_kind", streamKind);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$start_time", startTime.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$end_time", endTime.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$start_sample", startSampleIndex);
            command.Parameters.AddWithValue("$end_sample", endSampleIndex);
            command.Parameters.AddWithValue("$is_closed", 1);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                @"INSERT INTO segment_sources (segment_id, source_id)
                  VALUES ($segment_id, $source_id);";
            command.Parameters.AddWithValue("$segment_id", segmentId);
            command.Parameters.AddWithValue("$source_id", sourceId);
            command.ExecuteNonQuery();
        }

        using var channelCommand = connection.CreateCommand();
        channelCommand.Transaction = transaction;
        channelCommand.CommandText =
            @"INSERT INTO segment_channels (segment_id, channel_start, channel_end)
              VALUES ($segment_id, $channel_start, $channel_end);";
        channelCommand.Parameters.AddWithValue("$segment_id", segmentId);
        channelCommand.Parameters.AddWithValue("$channel_start", channelStart);
        channelCommand.Parameters.AddWithValue("$channel_end", channelEnd);
        channelCommand.ExecuteNonQuery();
    }

    private static RawIndexManifest ReadRawManifest(string rawIndexManifestPath)
    {
        string json = File.ReadAllText(rawIndexManifestPath);
        return System.Text.Json.JsonSerializer.Deserialize<RawIndexManifest>(json)
            ?? throw new InvalidOperationException($"Failed to read raw index manifest: {rawIndexManifestPath}");
    }

    private static PreviewIndexManifest ReadPreviewManifest(string previewIndexManifestPath)
    {
        string json = File.ReadAllText(previewIndexManifestPath);
        return System.Text.Json.JsonSerializer.Deserialize<PreviewIndexManifest>(json)
            ?? throw new InvalidOperationException($"Failed to read preview index manifest: {previewIndexManifestPath}");
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }
}
