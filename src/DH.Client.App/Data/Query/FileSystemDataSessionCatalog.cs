using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DH.Client.App.Services.Storage;
using Microsoft.Data.Sqlite;

namespace DH.Client.App.Data.Query;

public sealed class FileSystemDataSessionCatalog : IDataSessionCatalog
{
    private readonly ISessionArtifactLocator _artifactLocator;

    public enum MetadataSource
    {
        Manifest = 0,
        Catalog = 1,
        RecoveredTdms = 2
    }

    public sealed record OpenDiagnostics(
        SessionDescriptor Session,
        SessionArtifactPaths Artifacts,
        MetadataSource Source,
        CatalogStructureDiagnostics? CatalogStructure,
        SessionArtifactValidationResult ArtifactValidation);

    public sealed record CatalogStructureDiagnostics(
        int RawSegmentCount,
        int PreviewSegmentCount,
        int PreviewMappingCount,
        int SegmentSourceCount,
        int SegmentChannelCount,
        IReadOnlyDictionary<string, int> PreviewSegmentsByLevel);

    public FileSystemDataSessionCatalog(ISessionArtifactLocator? artifactLocator = null)
    {
        _artifactLocator = artifactLocator ?? new FileSystemSessionArtifactLocator();
    }

    public async ValueTask<SessionDescriptor> OpenAsync(
        string sessionPath,
        CancellationToken ct = default)
    {
        OpenDiagnostics diagnostics = await OpenWithDiagnosticsAsync(sessionPath, ct);
        return diagnostics.Session;
    }

    public async ValueTask<OpenDiagnostics> OpenWithDiagnosticsAsync(
        string sessionPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        SessionArtifactPaths artifacts = await _artifactLocator.DiscoverAsync(sessionPath, ct);
        SessionArtifactValidationResult artifactValidation = await SessionArtifactValidator.ValidateAsync(
            artifacts,
            requireRawIndex: false,
            ct);
        if (!artifactValidation.IsValid)
        {
            throw new InvalidDataException(
                "Session artifacts are incomplete or inconsistent: "
                + string.Join("; ", artifactValidation.Errors));
        }

        if (!string.IsNullOrWhiteSpace(artifacts.CatalogPath) && File.Exists(artifacts.CatalogPath))
        {
            SessionDescriptor? fromCatalog = await TryOpenFromCatalogAsync(artifacts.CatalogPath, sessionPath, ct);
            if (fromCatalog is not null)
            {
                CatalogStructureDiagnostics structure = await ReadCatalogStructureAsync(artifacts.CatalogPath, ct);
                return new OpenDiagnostics(fromCatalog, artifacts, MetadataSource.Catalog, structure, artifactValidation);
            }
        }

        if (string.IsNullOrWhiteSpace(artifacts.ManifestPath))
        {
            SessionDescriptor? recoveredSession = TryOpenRecoveredTdmsSession(artifacts.SessionPath);
            if (recoveredSession is not null)
            {
                return new OpenDiagnostics(
                    recoveredSession,
                    artifacts,
                    MetadataSource.RecoveredTdms,
                    CatalogStructure: null,
                    artifactValidation);
            }

            throw new FileNotFoundException(
                "No manifest file was found under the session path.",
                sessionPath);
        }

        await using var stream = File.OpenRead(artifacts.ManifestPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        JsonElement root = document.RootElement;

        SessionDescriptor session = new SessionDescriptor
        {
            SessionId = GetGuid(root, "SessionId") ?? Guid.Empty,
            TaskName = GetString(root, "TaskName")
                ?? Path.GetFileName(artifacts.SessionPath),
            StartTime = GetDateTimeOffset(root, "StartTime")
                ?? DateTimeOffset.MinValue,
            EndTime = GetDateTimeOffset(root, "EndTime"),
            StorageFormat = GetString(root, "StorageFormat")
                ?? string.Empty,
            Recovered = GetBool(root, "Recovered")
                ?? GetBool(root, "RecoveredState")
                ?? false,
            Sources = GetSources(root),
            PreviewLevels = GetPreviewLevels(root)
        };
        return new OpenDiagnostics(session, artifacts, MetadataSource.Manifest, CatalogStructure: null, artifactValidation);
    }

    private static SessionDescriptor? TryOpenRecoveredTdmsSession(string sessionPath)
    {
        string[] tdmsFiles = FindRecoveredTdmsFiles(sessionPath);
        if (tdmsFiles.Length == 0)
        {
            return null;
        }

        var sources = new List<SourceDescriptor>();
        foreach (var group in tdmsFiles
            .Select(path => (Path: path, Parsed: TryParseSourceSegment(path)))
            .Where(item => item.Parsed.SourceId >= 0)
            .GroupBy(item => item.Parsed.SourceId)
            .OrderBy(group => group.Key))
        {
            int channelCount = 0;
            double sampleRateHz = 0.0;
            foreach (string filePath in group.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (TryReadTdmsStructure(filePath, group.Key, out int parsedChannelCount, out double parsedSampleRateHz))
                {
                    channelCount = Math.Max(channelCount, parsedChannelCount);
                    if (sampleRateHz <= 0 && parsedSampleRateHz > 0)
                    {
                        sampleRateHz = parsedSampleRateHz;
                    }
                }

                if (channelCount > 0 && sampleRateHz > 0)
                {
                    break;
                }
            }

            if (channelCount <= 0)
            {
                continue;
            }

            sources.Add(new SourceDescriptor
            {
                SourceId = group.Key,
                DeviceName = $"Device {group.Key:D2}",
                ChannelCount = channelCount,
                SampleRateHz = sampleRateHz > 0 ? sampleRateHz : 1_000_000.0,
                TimeAxisKind = TimeAxisKind.SampleIndexMappedTime
            });
        }

        if (sources.Count == 0)
        {
            return null;
        }

        DateTimeOffset startTime = tdmsFiles
            .Select(path => new FileInfo(path).CreationTimeUtc)
            .DefaultIfEmpty(DateTime.UtcNow)
            .Min();

        string taskName = ResolveRecoveredTaskName(sessionPath);
        return new SessionDescriptor
        {
            SessionId = Guid.NewGuid(),
            TaskName = taskName,
            StartTime = startTime,
            EndTime = null,
            StorageFormat = "tdms-source-segment-recovered",
            Recovered = true,
            Sources = sources,
            PreviewLevels = Array.Empty<PreviewLevel>()
        };
    }

    private static string ResolveRecoveredTaskName(string sessionPath)
    {
        string fullPath = Path.GetFullPath(sessionPath);
        string leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (leaf.EndsWith(".artifacts", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileName(Path.GetDirectoryName(fullPath) ?? fullPath);
        }

        return leaf;
    }

    private static string[] FindRecoveredTdmsFiles(string sessionPath)
    {
        string? rawRoot = FindRawRoot(sessionPath);
        if (string.IsNullOrWhiteSpace(rawRoot) || !Directory.Exists(rawRoot))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(rawRoot, "source_*_seg*.tdms", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindRawRoot(string sessionPath)
    {
        string fullPath = Path.GetFullPath(sessionPath);
        string leaf = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(leaf, "raw", StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (leaf.EndsWith(".artifacts", StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(fullPath);
            string siblingRaw = !string.IsNullOrWhiteSpace(parent)
                ? Path.Combine(parent, "raw")
                : string.Empty;
            return Directory.Exists(siblingRaw) ? siblingRaw : null;
        }

        string childRaw = Path.Combine(fullPath, "raw");
        if (Directory.Exists(childRaw))
        {
            return childRaw;
        }

        string? parentRaw = Path.Combine(Path.GetDirectoryName(fullPath) ?? fullPath, "raw");
        return Directory.Exists(parentRaw) ? parentRaw : null;
    }

    private static bool TryReadTdmsStructure(
        string filePath,
        int sourceId,
        out int channelCount,
        out double sampleRateHz)
    {
        channelCount = 0;
        sampleRateHz = 0.0;
        try
        {
            string groupName = $"source_{sourceId:D4}";
            var structure = TdmsReaderUtil.ListGroupsAndChannels(filePath);
            if (!structure.TryGetValue(groupName, out string[] channels) || channels.Length == 0)
            {
                return false;
            }

            channelCount = channels.Length;
            try
            {
                var props = TdmsReaderUtil.ReadChannelProperties(filePath, groupName, channels[0]);
                if (TryGetDouble(props, "wf_increment") is { } increment && increment > 0)
                {
                    sampleRateHz = 1.0 / increment;
                }
            }
            catch
            {
                sampleRateHz = 0.0;
            }

            return true;
        }
        catch
        {
            channelCount = 0;
            sampleRateHz = 0.0;
            return false;
        }
    }

    private static (int SourceId, int SegmentIndex) TryParseSourceSegment(string filePath)
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

    private static double? TryGetDouble(IReadOnlyDictionary<string, object> props, string key)
    {
        if (!props.TryGetValue(key, out object? value) || value is null)
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            string s when double.TryParse(s, out double parsed) => parsed,
            _ => null
        };
    }

    private static async Task<CatalogStructureDiagnostics> ReadCatalogStructureAsync(
        string catalogPath,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = catalogPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync(ct);

        var byLevel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                @"SELECT preview_level, COUNT(*)
                  FROM preview_mapping
                  GROUP BY preview_level
                  ORDER BY preview_level;";
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                string level = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                int count = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                if (!string.IsNullOrWhiteSpace(level))
                {
                    byLevel[level] = count;
                }
            }
        }

        return new CatalogStructureDiagnostics(
            RawSegmentCount: await CountAsync(connection, "SELECT COUNT(*) FROM segments WHERE stream_kind = 'raw';", ct),
            PreviewSegmentCount: await CountAsync(connection, "SELECT COUNT(*) FROM segments WHERE stream_kind = 'preview';", ct),
            PreviewMappingCount: await CountAsync(connection, "SELECT COUNT(*) FROM preview_mapping;", ct),
            SegmentSourceCount: await CountAsync(connection, "SELECT COUNT(*) FROM segment_sources;", ct),
            SegmentChannelCount: await CountAsync(connection, "SELECT COUNT(*) FROM segment_channels;", ct),
            PreviewSegmentsByLevel: byLevel);
    }

    private static async Task<int> CountAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<SessionDescriptor?> TryOpenFromCatalogAsync(
        string catalogPath,
        string sessionPath,
        CancellationToken ct)
    {
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = catalogPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            await connection.OpenAsync(ct);

            Guid sessionId = Guid.Empty;
            string taskName = Path.GetFileName(sessionPath);
            DateTimeOffset startTime = DateTimeOffset.MinValue;
            DateTimeOffset? endTime = null;
            string storageFormat = string.Empty;
            bool recovered = false;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    @"SELECT session_id, task_name, start_time_utc, end_time_utc, storage_format, recovered_state
                      FROM session_info
                      LIMIT 1;";
                await using var reader = await command.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                {
                    sessionId = TryParseGuid(reader.GetString(0)) ?? Guid.Empty;
                    taskName = reader.IsDBNull(1) ? taskName : reader.GetString(1);
                    startTime = reader.IsDBNull(2) ? DateTimeOffset.MinValue : ParseDateTimeOffset(reader.GetString(2)) ?? DateTimeOffset.MinValue;
                    endTime = reader.IsDBNull(3) ? null : ParseDateTimeOffset(reader.GetString(3));
                    storageFormat = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    recovered = !reader.IsDBNull(5) && reader.GetInt32(5) != 0;
                }
            }

            var sources = new List<SourceDescriptor>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    @"SELECT source_id, device_name, channel_count, sample_rate_hz, time_axis_kind
                      FROM sources
                      ORDER BY source_id;";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    sources.Add(new SourceDescriptor
                    {
                        SourceId = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                        DeviceName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        ChannelCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                        SampleRateHz = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3),
                        TimeAxisKind = ParseTimeAxisKind(reader.IsDBNull(4) ? null : reader.GetString(4))
                    });
                }
            }

            var previewLevels = new List<PreviewLevel>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"SELECT level_name FROM preview_levels ORDER BY level_name;";
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (!reader.IsDBNull(0)
                        && Enum.TryParse(reader.GetString(0), ignoreCase: true, out PreviewLevel level))
                    {
                        previewLevels.Add(level);
                    }
                }
            }

            return new SessionDescriptor
            {
                SessionId = sessionId,
                TaskName = taskName,
                StartTime = startTime,
                EndTime = endTime,
                StorageFormat = storageFormat,
                Recovered = recovered,
                Sources = sources,
                PreviewLevels = previewLevels
            };
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SourceDescriptor> GetSources(JsonElement root)
    {
        if (!TryGetProperty(root, "Sources", out JsonElement sourcesElement)
            || sourcesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SourceDescriptor>();
        }

        var results = new List<SourceDescriptor>();
        foreach (JsonElement element in sourcesElement.EnumerateArray())
        {
            results.Add(new SourceDescriptor
            {
                SourceId = GetInt32(element, "SourceId")
                    ?? GetInt32(element, "Id")
                    ?? 0,
                DeviceName = GetString(element, "DeviceName")
                    ?? GetString(element, "Name")
                    ?? string.Empty,
                ChannelCount = GetInt32(element, "ChannelCount") ?? 0,
                SampleRateHz = GetDouble(element, "SampleRateHz")
                    ?? GetDouble(element, "SampleRate")
                    ?? 0.0,
                TimeAxisKind = ParseTimeAxisKind(
                    GetString(element, "TimeAxisKind"))
            });
        }

        return results;
    }

    private static IReadOnlyList<PreviewLevel> GetPreviewLevels(JsonElement root)
    {
        if (!TryGetProperty(root, "PreviewLevels", out JsonElement levelsElement)
            || levelsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PreviewLevel>();
        }

        var results = new List<PreviewLevel>();
        foreach (JsonElement element in levelsElement.EnumerateArray())
        {
            string? text = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            };

            if (Enum.TryParse(text, ignoreCase: true, out PreviewLevel level))
            {
                results.Add(level);
            }
        }

        return results;
    }

    private static TimeAxisKind ParseTimeAxisKind(string? text)
    {
        if (Enum.TryParse(text, ignoreCase: true, out TimeAxisKind axisKind))
        {
            return axisKind;
        }

        return TimeAxisKind.SampleIndexMappedTime;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static Guid? GetGuid(JsonElement root, string name)
    {
        string? text = GetString(root, name);
        return TryParseGuid(text);
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null
        };
    }

    private static int? GetInt32(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
        {
            return number;
        }

        return int.TryParse(value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : null;
    }

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
        {
            return number;
        }

        return double.TryParse(value.GetRawText(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
    }

    private static DateTimeOffset? GetDateTimeOffset(JsonElement root, string name)
    {
        string? text = GetString(root, name);
        return ParseDateTimeOffset(text);
    }

    private static Guid? TryParseGuid(string? text)
    {
        return Guid.TryParse(text, out Guid value) ? value : null;
    }

    private static DateTimeOffset? ParseDateTimeOffset(string? text)
    {
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset value)
            ? value
            : null;
    }
}
