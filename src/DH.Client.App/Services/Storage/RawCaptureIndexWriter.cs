using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DH.Contracts;

namespace DH.Client.App.Services.Storage;

internal sealed class RawCaptureIndexWriter : IDisposable
{
    private const int RawIndexEntrySize = 40;

    private readonly string _rootPath;
    private readonly string _captureFilePath;
    private readonly double _sampleRateHz;
    private readonly Dictionary<int, ChannelIndexWriter> _writers = new();
    private bool _completed;

    public RawCaptureIndexWriter(
        string artifactRootPath,
        string captureFilePath,
        double sampleRateHz,
        IReadOnlyCollection<int> channelIds)
    {
        _rootPath = Path.Combine(artifactRootPath, "raw_index");
        _captureFilePath = Path.GetFullPath(captureFilePath);
        _sampleRateHz = sampleRateHz;
        Directory.CreateDirectory(_rootPath);

        foreach (int channelId in channelIds.Distinct().OrderBy(id => id))
        {
            string relativePath = $"CH{channelId:D4}.raw.index.bin";
            string fullPath = Path.Combine(_rootPath, relativePath);
            _writers[channelId] = new ChannelIndexWriter(channelId, fullPath, relativePath);
        }
    }

    public void Append(
        int channelId,
        long payloadOffset,
        int channelCount,
        int channelOffset,
        int sampleCount)
    {
        if (_completed || sampleCount <= 0 || !_writers.TryGetValue(channelId, out var writer))
        {
            return;
        }

        writer.Append(payloadOffset, channelCount, channelOffset, sampleCount);
    }

    public string Complete()
    {
        if (_completed)
        {
            return Path.Combine(_rootPath, "raw.index.json");
        }

        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }

        string manifestPath = Path.Combine(_rootPath, "raw.index.json");
        var manifest = new RawIndexManifest
        {
            CaptureFilePath = _captureFilePath,
            SampleRateHz = _sampleRateHz,
            GeneratedAtUtc = DateTime.UtcNow,
            Channels = _writers.Values
                .Select(writer => writer.ToManifestEntry())
                .OrderBy(entry => entry.ChannelId)
                .ToArray()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        _completed = true;
        return manifestPath;
    }

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
    }

    private sealed class ChannelIndexWriter : IDisposable
    {
        private readonly BinaryWriter _writer;
        private long _nextSampleIndex;
        private long _entryCount;
        private long _startSampleIndex = -1;
        private long _endSampleIndex = -1;

        public ChannelIndexWriter(int channelId, string fullPath, string relativePath)
        {
            ChannelId = channelId;
            FullPath = fullPath;
            RelativePath = relativePath;
            _writer = new BinaryWriter(File.Open(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read));
        }

        public int ChannelId { get; }

        public string FullPath { get; }

        public string RelativePath { get; }

        public void Append(
            long payloadOffset,
            int channelCount,
            int channelOffset,
            int sampleCount)
        {
            long startSampleIndex = _nextSampleIndex;
            long endSampleIndex = startSampleIndex + sampleCount - 1;

            _writer.Write(startSampleIndex);
            _writer.Write(endSampleIndex);
            _writer.Write(payloadOffset);
            _writer.Write(sampleCount * channelCount);
            _writer.Write(channelCount);
            _writer.Write(channelOffset);
            _writer.Write(sampleCount);

            _entryCount++;
            if (_startSampleIndex < 0)
            {
                _startSampleIndex = startSampleIndex;
            }

            _endSampleIndex = endSampleIndex;
            _nextSampleIndex += sampleCount;
        }

        public RawIndexChannelFileEntry ToManifestEntry()
        {
            return new RawIndexChannelFileEntry
            {
                ChannelId = ChannelId,
                RelativeFilePath = RelativePath,
                EntryCount = _entryCount,
                StartSampleIndex = _startSampleIndex,
                EndSampleIndex = _endSampleIndex
            };
        }

        public void Dispose()
        {
            _writer.Dispose();
        }
    }
}
