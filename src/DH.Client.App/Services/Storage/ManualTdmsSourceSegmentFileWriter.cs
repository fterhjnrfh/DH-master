using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DH.Client.App.Services.Storage;

public sealed class ManualTdmsSourceSegmentFileWriter
{
    private const uint TocMetaData = 1u << 1;
    private const uint TocNewObjectList = 1u << 2;
    private const uint TocRawData = 1u << 3;
    private const uint TocMask = TocMetaData | TocNewObjectList | TocRawData;
    private const uint TdmsVersion = 4713;
    private const uint NoRawData = 0xFFFFFFFF;
    private const uint RawDataIndexLength = 20;
    private const uint TdsTypeInt32 = 3;
    private const uint TdsTypeUnsignedByte = 5;
    private const uint TdsTypeSingleFloat = 9;
    private const uint TdsTypeDoubleFloat = 10;
    private const uint TdsTypeString = 0x20;

    public TdmsSourceSegmentWriteResult Write(TdmsSourceSegmentWriteRequest request)
    {
        ValidateRequest(request);

        string filePath = Path.GetFullPath(request.FilePath);
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (request.Overwrite)
        {
            TryDelete(filePath);
            TryDelete(filePath + "_index");
        }

        int[] channelIds = request.ChannelIds.Distinct().OrderBy(static id => id).ToArray();
        StorageCompressionSettings compressionSettings = request.CompressionSettings?.Clone() ?? new StorageCompressionSettings();
        compressionSettings.Normalize();
        bool compressionEnabled = compressionSettings.Enabled
            && (compressionSettings.Algorithm != CompressionType.None || compressionSettings.Preprocess != PreprocessType.None);
        ChannelPayload[] payloads = compressionEnabled
            ? BuildCompressedChannelPayloads(request, channelIds, compressionSettings)
            : Array.Empty<ChannelPayload>();
        byte[] metadata = BuildMetadata(request, channelIds, payloads, compressionEnabled);
        long payloadBytes = checked((long)channelIds.Length * request.SamplesPerChannel * sizeof(float));
        long codecPayloadBytes = compressionEnabled
            ? payloads.Sum(static payload => (long)payload.Payload.Length)
            : payloadBytes;
        ulong rawDataOffset = (ulong)metadata.Length;
        ulong nextSegmentOffset = checked((ulong)metadata.Length + (ulong)codecPayloadBytes);

        var totalStopwatch = Stopwatch.StartNew();
        var writeStopwatch = Stopwatch.StartNew();
        using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4 * 1024 * 1024, FileOptions.SequentialScan))
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("TDSm"));
            writer.Write(TocMask);
            writer.Write(TdmsVersion);
            writer.Write(nextSegmentOffset);
            writer.Write(rawDataOffset);
            writer.Write(metadata);

            if (compressionEnabled)
            {
                foreach (ChannelPayload payload in payloads)
                {
                    stream.Write(payload.Payload, 0, payload.Payload.Length);
                }
            }
            else
            {
                foreach (int channelId in channelIds)
                {
                    float[] samples = request.GetSamples(channelId);
                    if (samples.Length < request.SamplesPerChannel)
                    {
                        throw new InvalidDataException($"Channel {channelId} only has {samples.Length} samples, expected at least {request.SamplesPerChannel}.");
                    }

                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(samples.AsSpan(0, request.SamplesPerChannel));
                    stream.Write(bytes);
                }
            }
        }

        writeStopwatch.Stop();
        totalStopwatch.Stop();

        return new TdmsSourceSegmentWriteResult
        {
            FilePath = filePath,
            SourceId = request.SourceId,
            SegmentIndex = request.SegmentIndex,
            ChannelCount = channelIds.Length,
            SamplesPerChannel = request.SamplesPerChannel,
            PayloadBytes = payloadBytes,
            CodecPayloadBytes = codecPayloadBytes,
            CompressionEnabled = compressionEnabled,
            CompressionType = compressionEnabled ? compressionSettings.Algorithm : CompressionType.None,
            PreprocessType = compressionEnabled ? compressionSettings.Preprocess : PreprocessType.None,
            ChannelPayloadBytes = payloads.Select(static payload => payload.Payload.Length).ToArray(),
            FileBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0,
            AppendElapsed = writeStopwatch.Elapsed,
            SaveElapsed = TimeSpan.Zero,
            CloseElapsed = TimeSpan.Zero,
            TotalElapsed = totalStopwatch.Elapsed
        };
    }

    private static ChannelPayload[] BuildCompressedChannelPayloads(
        TdmsSourceSegmentWriteRequest request,
        int[] channelIds,
        StorageCompressionSettings compressionSettings)
    {
        var payloads = new List<ChannelPayload>(channelIds.Length);
        foreach (int channelId in channelIds)
        {
            float[] samples = request.GetSamples(channelId);
            if (samples.Length < request.SamplesPerChannel)
            {
                throw new InvalidDataException($"Channel {channelId} only has {samples.Length} samples, expected at least {request.SamplesPerChannel}.");
            }

            FloatSampleCompressionResult encoded = FloatSampleCompressionCodec.Encode(
                samples.AsSpan(0, request.SamplesPerChannel),
                compressionSettings);
            payloads.Add(new ChannelPayload(channelId, encoded.Payload, encoded.Algorithm, encoded.Preprocess));
        }

        return payloads.ToArray();
    }

    private static byte[] BuildMetadata(
        TdmsSourceSegmentWriteRequest request,
        int[] channelIds,
        IReadOnlyList<ChannelPayload> payloads,
        bool compressionEnabled)
    {
        using var memory = new MemoryStream(64 * 1024);
        using var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true);

        writer.Write((uint)(channelIds.Length + 2));
        WriteObjectWithoutRawData(writer, "/");
        WriteObjectWithoutRawData(writer, $"/'source_{request.SourceId:D4}'");

        foreach (int channelId in channelIds)
        {
            ChannelPayload? payload = compressionEnabled
                ? payloads.First(item => item.ChannelId == channelId)
                : null;
            WriteString(writer, $"/'source_{request.SourceId:D4}'/'AI{channelId:D4}'");
            writer.Write(RawDataIndexLength);
            writer.Write(compressionEnabled ? TdsTypeUnsignedByte : TdsTypeSingleFloat);
            writer.Write(1u);
            writer.Write((ulong)(compressionEnabled ? payload!.Payload.Length : request.SamplesPerChannel));
            writer.Write((uint)(compressionEnabled ? 9 : 3));
            WriteStringProperty(writer, "unit_string", "V");
            WriteDoubleProperty(writer, "wf_increment", 1.0 / request.SampleRateHz);
            WriteDoubleProperty(writer, "wf_start_offset", 0.0);
            if (compressionEnabled)
            {
                WriteStringProperty(writer, "dh_storage_format", "tdms-source-segment-compressed-v1");
                WriteStringProperty(writer, "dh_compression_algorithm", payload!.Algorithm.ToString());
                WriteStringProperty(writer, "dh_preprocess", payload.Preprocess.ToString());
                WriteIntProperty(writer, "dh_original_sample_count", request.SamplesPerChannel);
                WriteIntProperty(writer, "dh_original_byte_count", checked(request.SamplesPerChannel * sizeof(float)));
                WriteIntProperty(writer, "dh_payload_byte_count", payload.Payload.Length);
            }
        }

        writer.Flush();
        return memory.ToArray();
    }

    private static void WriteObjectWithoutRawData(BinaryWriter writer, string path)
    {
        WriteString(writer, path);
        writer.Write(NoRawData);
        writer.Write(0u);
    }

    private static void WriteStringProperty(BinaryWriter writer, string name, string value)
    {
        WriteString(writer, name);
        writer.Write(TdsTypeString);
        WriteString(writer, value);
    }

    private static void WriteDoubleProperty(BinaryWriter writer, string name, double value)
    {
        WriteString(writer, name);
        writer.Write(TdsTypeDoubleFloat);
        writer.Write(value);
    }

    private static void WriteIntProperty(BinaryWriter writer, string name, int value)
    {
        WriteString(writer, name);
        writer.Write(TdsTypeInt32);
        writer.Write(value);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void ValidateRequest(TdmsSourceSegmentWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            throw new ArgumentException("TDMS file path is required.", nameof(request));
        }

        if (request.SampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Sample rate must be positive.");
        }

        if (request.SamplesPerChannel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Samples per channel must be positive.");
        }

        if (request.ChannelIds.Length == 0)
        {
            throw new ArgumentException("At least one channel is required.", nameof(request));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record ChannelPayload(
        int ChannelId,
        byte[] Payload,
        CompressionType Algorithm,
        PreprocessType Preprocess);
}
