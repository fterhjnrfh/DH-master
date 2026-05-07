using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DH.Client.App.Services.Storage;

internal sealed class PreviewSidecarWriter : IDisposable
{
    private readonly string _rootPath;
    private readonly string _sessionName;
    private readonly double _sampleRateHz;
    private readonly IReadOnlyList<PreviewLevelSpec> _levels;
    private readonly Dictionary<(string LevelName, int ChannelId), ChannelLevelWriter> _writers = new();
    private readonly List<PreviewFileIndexEntry> _indexEntries = new();
    private bool _completed;

    public PreviewSidecarWriter(
        string basePath,
        string sessionName,
        double sampleRateHz,
        IReadOnlyCollection<int> channelIds,
        IReadOnlyList<PreviewLevelSpec> levels)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            throw new ArgumentException("Base path is required.", nameof(basePath));
        }

        if (string.IsNullOrWhiteSpace(sessionName))
        {
            throw new ArgumentException("Session name is required.", nameof(sessionName));
        }

        if (levels == null || levels.Count == 0)
        {
            throw new ArgumentException("At least one preview level is required.", nameof(levels));
        }

        _rootPath = Path.Combine(basePath, "preview_levels");
        _sessionName = sessionName;
        _sampleRateHz = sampleRateHz;
        _levels = levels;

        Directory.CreateDirectory(_rootPath);

        foreach (var level in _levels)
        {
            Directory.CreateDirectory(Path.Combine(_rootPath, level.LevelName));
        }

        foreach (int channelId in channelIds.Distinct().OrderBy(id => id))
        {
            foreach (var level in _levels)
            {
                string relativePath = Path.Combine(level.LevelName, $"CH{channelId:D4}.preview.bin");
                string filePath = Path.Combine(_rootPath, relativePath);
                var writer = new ChannelLevelWriter(level, channelId, filePath, relativePath);
                _writers[(level.LevelName, channelId)] = writer;
                _indexEntries.Add(writer.IndexEntry);
            }
        }
    }

    public static IReadOnlyList<PreviewLevelSpec> CreateDefaultLevels()
    {
        return new[]
        {
            new PreviewLevelSpec("L1", 512),
            new PreviewLevelSpec("L2", 32_768),
            new PreviewLevelSpec("L3", 2_097_152),
            new PreviewLevelSpec("L4", 134_217_728)
        };
    }

    public static IReadOnlyList<PreviewLevelSpec> CreateRealtimeCaptureLevels()
    {
        return new[]
        {
            new PreviewLevelSpec("L1", 512)
        };
    }

    public static IReadOnlyList<PreviewLevelSpec> CreateCaptureCoarseLevels()
    {
        return new[]
        {
            new PreviewLevelSpec("L2", 32_768),
            new PreviewLevelSpec("L3", 2_097_152),
            new PreviewLevelSpec("L4", 134_217_728)
        };
    }

    public void Write(int channelId, ReadOnlySpan<double> samples)
    {
        if (_completed || samples.IsEmpty)
        {
            return;
        }

        foreach (var level in _levels)
        {
            if (_writers.TryGetValue((level.LevelName, channelId), out var writer))
            {
                writer.Append(samples);
            }
        }
    }

    public void Write(int channelId, ReadOnlySpan<float> samples)
    {
        if (_completed || samples.IsEmpty)
        {
            return;
        }

        foreach (var level in _levels)
        {
            if (_writers.TryGetValue((level.LevelName, channelId), out var writer))
            {
                writer.Append(samples);
            }
        }
    }

    public PreviewSidecarArtifacts Complete()
    {
        if (_completed)
        {
            return BuildArtifacts();
        }

        foreach (var writer in _writers.Values)
        {
            writer.FlushPending();
            writer.Dispose();
        }

        string indexManifestPath = Path.Combine(_rootPath, "preview.index.json");
        var manifest = new PreviewIndexManifest
        {
            SessionName = _sessionName,
            SampleRateHz = _sampleRateHz,
            GeneratedAtUtc = DateTime.UtcNow,
            Files = _indexEntries
                .OrderBy(entry => entry.LevelName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.ChannelId)
                .ToArray()
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true
        };
        File.WriteAllText(indexManifestPath, JsonSerializer.Serialize(manifest, options));
        _completed = true;
        return BuildArtifacts();
    }

    private PreviewSidecarArtifacts BuildArtifacts()
    {
        return new PreviewSidecarArtifacts
        {
            RootPath = _rootPath,
            IndexManifestPath = Path.Combine(_rootPath, "preview.index.json"),
            DataFilePaths = _writers.Values
                .Select(writer => writer.FilePath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };
    }

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
    }

    private sealed class ChannelLevelWriter : IDisposable
    {
        private readonly PreviewLevelSpec _level;
        private readonly BinaryWriter _writer;
        private long _nextSampleIndex;
        private int _bucketSampleCount;
        private long _bucketStartSampleIndex;
        private double _minValue;
        private double _maxValue;
        private int _minOffset;
        private int _maxOffset;
        private double _sum;
        private double _sumSquares;
        private bool _hasBucket;

        public ChannelLevelWriter(
            PreviewLevelSpec level,
            int channelId,
            string filePath,
            string relativeFilePath)
        {
            _level = level;
            FilePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            _writer = new BinaryWriter(File.Open(filePath, FileMode.Create, FileAccess.Write, FileShare.Read));
            IndexEntry = new PreviewFileIndexEntry
            {
                LevelName = level.LevelName,
                ChannelId = channelId,
                BucketSampleSpan = level.BucketSampleSpan,
                RelativeFilePath = relativeFilePath,
                StartSampleIndex = -1,
                EndSampleIndex = -1
            };
        }

        public string FilePath { get; }

        public PreviewFileIndexEntry IndexEntry { get; }

        public void Append(ReadOnlySpan<double> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                double value = samples[i];
                AppendValue(value);
            }
        }

        public void Append(ReadOnlySpan<float> samples)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                AppendValue(samples[i]);
            }
        }

        private void AppendValue(double value)
        {
                long sampleIndex = _nextSampleIndex++;
                int offsetInBucket = _bucketSampleCount;

                if (!_hasBucket)
                {
                    _bucketStartSampleIndex = sampleIndex;
                    _bucketSampleCount = 0;
                    _minValue = value;
                    _maxValue = value;
                    _minOffset = 0;
                    _maxOffset = 0;
                    _sum = 0.0;
                    _sumSquares = 0.0;
                    _hasBucket = true;
                }

                if (value < _minValue)
                {
                    _minValue = value;
                    _minOffset = offsetInBucket;
                }

                if (value > _maxValue)
                {
                    _maxValue = value;
                    _maxOffset = offsetInBucket;
                }

                _sum += value;
                _sumSquares += value * value;
                _bucketSampleCount++;

                if (_bucketSampleCount >= _level.BucketSampleSpan)
                {
                    FlushPending();
                }
        }

        public void FlushPending()
        {
            if (!_hasBucket || _bucketSampleCount <= 0)
            {
                return;
            }

            long endSampleIndex = _bucketStartSampleIndex + _bucketSampleCount - 1;
            _writer.Write(_bucketStartSampleIndex);
            _writer.Write(endSampleIndex);
            _writer.Write(_bucketSampleCount);
            _writer.Write(_minValue);
            _writer.Write(_maxValue);
            _writer.Write(_minOffset);
            _writer.Write(_maxOffset);
            _writer.Write(_sum);
            _writer.Write(_sumSquares);

            IndexEntry.BucketCount++;
            if (IndexEntry.StartSampleIndex < 0)
            {
                IndexEntry.StartSampleIndex = _bucketStartSampleIndex;
            }

            IndexEntry.EndSampleIndex = endSampleIndex;
            _hasBucket = false;
            _bucketSampleCount = 0;
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }
}
