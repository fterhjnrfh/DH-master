using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DH.Client.App.Services.Storage;

public sealed class TdmsSourceSegmentWriteRequest
{
    public string FilePath { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public int SegmentIndex { get; init; }
    public double SampleRateHz { get; init; }
    public int SamplesPerChannel { get; init; }
    public int[] ChannelIds { get; init; } = Array.Empty<int>();
    public Func<int, float[]> GetSamples { get; init; } = _ => Array.Empty<float>();
    public bool Overwrite { get; init; } = true;
    public StorageCompressionSettings? CompressionSettings { get; init; }
}

public sealed class TdmsSourceSegmentWriteResult
{
    public string FilePath { get; init; } = string.Empty;
    public int SourceId { get; init; }
    public int SegmentIndex { get; init; }
    public int ChannelCount { get; init; }
    public int SamplesPerChannel { get; init; }
    public long PayloadBytes { get; init; }
    public long CodecPayloadBytes { get; init; }
    public bool CompressionEnabled { get; init; }
    public CompressionType CompressionType { get; init; }
    public PreprocessType PreprocessType { get; init; }
    public int[] ChannelPayloadBytes { get; init; } = Array.Empty<int>();
    public long FileBytes { get; init; }
    public TimeSpan AppendElapsed { get; init; }
    public TimeSpan SaveElapsed { get; init; }
    public TimeSpan CloseElapsed { get; init; }
    public TimeSpan TotalElapsed { get; init; }
}

public sealed record TdmsSourceSegmentStructureInfo(int GroupCount, int ChannelCount);

public sealed class TdmsSourceSegmentFileWriter
{
    public static bool IsAvailable => TdmsNative.IsAvailable;

    public static TdmsSourceSegmentStructureInfo ReadStructure(string filePath)
    {
        if (!TdmsNative.IsAvailable)
        {
            throw new InvalidOperationException("TDMS library is not available. Please place nilibddc.dll in the app directory or PATH.");
        }

        IntPtr file = IntPtr.Zero;
        int err = TdmsNative.DDC_OpenFileEx(filePath, "TDMS", 1, ref file);
        ThrowIfError(err, "DDC_OpenFileEx", filePath);
        if (file == IntPtr.Zero)
        {
            throw new IOException($"DDC_OpenFileEx returned an empty file handle for {filePath}.");
        }

        try
        {
            int groupCount = 0;
            err = TdmsNative.DDC_GetNumChannelGroups(file, ref groupCount);
            ThrowIfError(err, "DDC_GetNumChannelGroups", filePath);

            var groups = new IntPtr[groupCount];
            if (groupCount > 0)
            {
                err = TdmsNative.DDC_GetChannelGroups(file, groups, groupCount);
                ThrowIfError(err, "DDC_GetChannelGroups", filePath);
            }

            int totalChannelCount = 0;
            foreach (IntPtr group in groups)
            {
                int channelCount = 0;
                err = TdmsNative.DDC_GetNumChannels(group, ref channelCount);
                ThrowIfError(err, "DDC_GetNumChannels", filePath);
                totalChannelCount += channelCount;
            }

            return new TdmsSourceSegmentStructureInfo(groupCount, totalChannelCount);
        }
        finally
        {
            try
            {
                TdmsNative.DDC_CloseFile(file);
            }
            catch
            {
            }
        }
    }

    public TdmsSourceSegmentWriteResult Write(TdmsSourceSegmentWriteRequest request)
    {
        ValidateRequest(request);
        if (!TdmsNative.IsAvailable)
        {
            throw new InvalidOperationException("TDMS library is not available. Please place nilibddc.dll in the app directory or PATH.");
        }

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

        IntPtr file = IntPtr.Zero;
        IntPtr group = IntPtr.Zero;
        var totalStopwatch = Stopwatch.StartNew();
        var appendElapsed = TimeSpan.Zero;
        var saveElapsed = TimeSpan.Zero;
        var closeElapsed = TimeSpan.Zero;

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            int err = TdmsNative.DDC_CreateFile(filePath, "TDMS", SanitizeAscii(fileName), "", SanitizeAscii(fileName), "DH", ref file);
            ThrowIfError(err, "DDC_CreateFile", filePath);

            string groupName = $"source_{request.SourceId:D4}";
            err = TdmsNative.DDC_AddChannelGroup(file, groupName, groupName, ref group);
            ThrowIfError(err, "DDC_AddChannelGroup", filePath);

            CreateFileMetadata(file, request);

            var appendStopwatch = Stopwatch.StartNew();
            foreach (int channelId in request.ChannelIds.Distinct().OrderBy(static id => id))
            {
                float[] samples = request.GetSamples(channelId);
                if (samples.Length < request.SamplesPerChannel)
                {
                    throw new InvalidDataException($"Channel {channelId} only has {samples.Length} samples, expected at least {request.SamplesPerChannel}.");
                }

                IntPtr channel = IntPtr.Zero;
                string channelName = SanitizeAscii($"AI{channelId:D4}");
                err = TdmsNative.DDC_AddChannel(group, TdmsNative.DDCDataType.Float, channelName, $"Channel {channelId}", "V", ref channel);
                ThrowIfError(err, $"DDC_AddChannel channel={channelId}", filePath);

                CreateChannelMetadata(channel, channelId, request.SampleRateHz);

                err = TdmsNative.DDC_AppendDataValuesFloat(channel, samples, (uint)request.SamplesPerChannel);
                ThrowIfError(err, $"DDC_AppendDataValuesFloat channel={channelId}", filePath);
            }

            appendStopwatch.Stop();
            appendElapsed = appendStopwatch.Elapsed;

            var saveStopwatch = Stopwatch.StartNew();
            err = TdmsNative.DDC_SaveFile(file);
            saveStopwatch.Stop();
            saveElapsed = saveStopwatch.Elapsed;
            ThrowIfError(err, "DDC_SaveFile", filePath);

            var closeStopwatch = Stopwatch.StartNew();
            err = TdmsNative.DDC_CloseFile(file);
            closeStopwatch.Stop();
            closeElapsed = closeStopwatch.Elapsed;
            file = IntPtr.Zero;
            ThrowIfError(err, "DDC_CloseFile", filePath);

            totalStopwatch.Stop();
            return BuildResult(request, filePath, appendElapsed, saveElapsed, closeElapsed, totalStopwatch);
        }
        finally
        {
            if (file != IntPtr.Zero)
            {
                var closeStopwatch = Stopwatch.StartNew();
                try
                {
                    TdmsNative.DDC_CloseFile(file);
                }
                finally
                {
                    closeStopwatch.Stop();
                    closeElapsed = closeStopwatch.Elapsed;
                    totalStopwatch.Stop();
                }
            }
            else
            {
                totalStopwatch.Stop();
            }
        }
    }

    private static TdmsSourceSegmentWriteResult BuildResult(
        TdmsSourceSegmentWriteRequest request,
        string filePath,
        TimeSpan appendElapsed,
        TimeSpan saveElapsed,
        TimeSpan closeElapsed,
        Stopwatch totalStopwatch)
    {
        long payloadBytes = (long)request.ChannelIds.Distinct().Count() * request.SamplesPerChannel * sizeof(float);
        long fileBytes = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        return new TdmsSourceSegmentWriteResult
        {
            FilePath = filePath,
            SourceId = request.SourceId,
            SegmentIndex = request.SegmentIndex,
            ChannelCount = request.ChannelIds.Distinct().Count(),
            SamplesPerChannel = request.SamplesPerChannel,
            PayloadBytes = payloadBytes,
            FileBytes = fileBytes,
            AppendElapsed = appendElapsed,
            SaveElapsed = saveElapsed,
            CloseElapsed = closeElapsed,
            TotalElapsed = totalStopwatch.Elapsed
        };
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

    private static void CreateFileMetadata(IntPtr file, TdmsSourceSegmentWriteRequest request)
    {
        TryCreateFileProperty(file, "dh_storage_format", "tdms-source-segment-v1");
        TryCreateFileProperty(file, "dh_source_id", request.SourceId.ToString(CultureInfo.InvariantCulture));
        TryCreateFileProperty(file, "dh_segment_index", request.SegmentIndex.ToString(CultureInfo.InvariantCulture));
        TryCreateFileProperty(file, "dh_samples_per_channel", request.SamplesPerChannel.ToString(CultureInfo.InvariantCulture));
        TryCreateFileProperty(file, "dh_sample_rate_hz", request.SampleRateHz.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void CreateChannelMetadata(IntPtr channel, int channelId, double sampleRateHz)
    {
        TdmsNative.DDC_CreateChannelPropertyString(channel, "wf_xname", "Time");
        TdmsNative.DDC_CreateChannelPropertyString(channel, "wf_xunit_string", "s");
        TdmsNative.DDC_CreateChannelPropertyDouble(channel, "wf_increment", 1.0 / sampleRateHz);
        TdmsNative.DDC_CreateChannelPropertyDouble(channel, "wf_start_offset", 0.0);
        TdmsNative.DDC_CreateChannelPropertyString(channel, "dh_channel_id", channelId.ToString(CultureInfo.InvariantCulture));
    }

    private static void TryCreateFileProperty(IntPtr file, string property, string value)
    {
        try
        {
            TdmsNative.DDC_CreateFilePropertyString(file, property, value);
        }
        catch
        {
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

    private static void ThrowIfError(int err, string operation, string filePath)
    {
        if (err != 0)
        {
            throw new IOException($"{operation} failed for {filePath}: {err} {TdmsNative.DescribeError(err)}");
        }
    }

    private static string SanitizeAscii(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "session";
        }

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch >= 32 && ch <= 126 && ch != '/' && ch != '\\' && ch != ':' && ch != '*' && ch != '?' && ch != '"' && ch != '<' && ch != '>' && ch != '|')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        string safe = sb.ToString().Trim('_', ' ');
        return string.IsNullOrWhiteSpace(safe) ? "session" : safe;
    }
}
