using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DH.Client.App.Services.Storage;

internal sealed class BackgroundTdmsSegmentCompressionJob
{
    public string RawFilePath { get; init; } = string.Empty;

    public string CompressedFilePath { get; init; } = string.Empty;

    public int SourceId { get; init; }

    public int SegmentIndex { get; init; }

    public long StartSample { get; init; }

    public double SampleRateHz { get; init; }

    public int[] ChannelIds { get; init; } = Array.Empty<int>();

    public int SamplesPerChannel { get; init; }

    public long PayloadBytes { get; init; }

    public bool IsPartial { get; init; }
}

internal sealed class BackgroundTdmsSegmentCompressionResult
{
    public TdmsSourceSegmentWriteResult WriteResult { get; init; } = new();

    public long StartSample { get; init; }

    public double SampleRateHz { get; init; }

    public int[] ChannelIds { get; init; } = Array.Empty<int>();

    public TimeSpan ReadElapsed { get; init; }

    public TimeSpan TotalElapsed { get; init; }
}

internal sealed class BackgroundTdmsSegmentCompressor
{
    private readonly ManualTdmsSourceSegmentFileWriter _writer = new();

    public BackgroundTdmsSegmentCompressionResult Compress(
        BackgroundTdmsSegmentCompressionJob job,
        StorageCompressionSettings compressionSettings)
    {
        if (string.IsNullOrWhiteSpace(job.RawFilePath))
        {
            throw new ArgumentException("Raw TDMS file path is required.", nameof(job));
        }

        if (!File.Exists(job.RawFilePath))
        {
            throw new FileNotFoundException("Raw TDMS segment file was not found.", job.RawFilePath);
        }

        StorageCompressionSettings settings = compressionSettings.Clone();
        settings.Normalize();
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("Background TDMS compression requires enabled compression settings.");
        }

        int[] channelIds = job.ChannelIds.Distinct().OrderBy(static id => id).ToArray();
        if (channelIds.Length == 0)
        {
            throw new InvalidDataException("Background TDMS compression job has no channels.");
        }

        long rawDataOffset = ReadRawDataOffset(job.RawFilePath);
        var totalStopwatch = Stopwatch.StartNew();
        long readTicks = 0;

        using var rawStream = new FileStream(
            job.RawFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            4 * 1024 * 1024,
            FileOptions.SequentialScan);

        float[] ReadChannelSamples(int channelId)
        {
            int channelOffset = Array.IndexOf(channelIds, channelId);
            if (channelOffset < 0)
            {
                return Array.Empty<float>();
            }

            var readStopwatch = Stopwatch.StartNew();
            long byteOffset = checked(rawDataOffset + (long)channelOffset * job.SamplesPerChannel * sizeof(float));
            int byteCount = checked(job.SamplesPerChannel * sizeof(float));
            float[] samples = new float[job.SamplesPerChannel];
            Span<byte> sampleBytes = MemoryMarshal.AsBytes(samples.AsSpan());
            rawStream.Position = byteOffset;
            ReadExactly(rawStream, sampleBytes[..byteCount]);
            readStopwatch.Stop();
            readTicks += readStopwatch.Elapsed.Ticks;
            return samples;
        }

        var request = new TdmsSourceSegmentWriteRequest
        {
            FilePath = job.CompressedFilePath,
            SourceId = job.SourceId,
            SegmentIndex = job.SegmentIndex,
            SampleRateHz = job.SampleRateHz,
            SamplesPerChannel = job.SamplesPerChannel,
            ChannelIds = channelIds,
            GetSamples = ReadChannelSamples,
            Overwrite = true,
            CompressionSettings = settings
        };

        TdmsSourceSegmentWriteResult result = _writer.Write(request);
        totalStopwatch.Stop();

        return new BackgroundTdmsSegmentCompressionResult
        {
            WriteResult = result,
            StartSample = job.StartSample,
            SampleRateHz = job.SampleRateHz,
            ChannelIds = channelIds,
            ReadElapsed = TimeSpan.FromTicks(readTicks),
            TotalElapsed = totalStopwatch.Elapsed
        };
    }

    private static long ReadRawDataOffset(string filePath)
    {
        Span<byte> header = stackalloc byte[28];
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
        ReadExactly(stream, header);
        if (header[0] != (byte)'T'
            || header[1] != (byte)'D'
            || header[2] != (byte)'S'
            || header[3] != (byte)'m')
        {
            throw new InvalidDataException($"File is not a TDMS segment: {filePath}");
        }

        ulong rawDataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(20, 8));
        return checked(28L + (long)rawDataOffset);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int read = stream.Read(buffer);
            if (read <= 0)
            {
                throw new EndOfStreamException("Unexpected end of TDMS segment while reading channel payload.");
            }

            buffer = buffer[read..];
        }
    }
}
