using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Driver.SDK;

namespace DH.Client.App.Services.Storage;

internal static class SdkRawCaptureFormat
{
    public const string FileSuffix = ".sdkraw.bin";
    public const ulong FileMagic = 0x3157415248444448UL; // "DHDHRAW1"
    public const uint BlockMagic = 0x314B4244U; // "DBK1"
    public const int FormatVersion = 1;

    public static bool IsRawCaptureFile(string path)
        => path.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase);

    public static string GetManifestPath(string capturePath)
        => Path.ChangeExtension(capturePath, ".json");

    public static string GetCaptureStem(string capturePath)
    {
        string fileName = Path.GetFileName(capturePath);
        if (fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return fileName[..^FileSuffix.Length];
        }

        return Path.GetFileNameWithoutExtension(capturePath);
    }

    public static bool TryLoadManifest(string capturePath, out SdkRawCaptureManifest? manifest)
    {
        manifest = null;

        try
        {
            string manifestPath = GetManifestPath(capturePath);
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var json = File.ReadAllText(manifestPath);
            manifest = JsonSerializer.Deserialize<SdkRawCaptureManifest>(json);
            return manifest != null;
        }
        catch
        {
            manifest = null;
            return false;
        }
    }
}

internal sealed class SdkRawCaptureManifest
{
    public int Version { get; set; } = SdkRawCaptureFormat.FormatVersion;

    public string SessionName { get; set; } = "";

    public string CaptureFileName { get; set; } = "";

    public DateTime StartedAtUtc { get; set; }

    public DateTime StoppedAtUtc { get; set; }

    public double SampleRateHz { get; set; }

    public int ExpectedChannelCount { get; set; }

    public int ObservedChannelCount { get; set; }

    public long BlockCount { get; set; }

    public long TotalSamples { get; set; }

    public long RawPayloadBytes { get; set; }

    public long CaptureFileBytes { get; set; }

    public double WriteSeconds { get; set; }

    public long EnqueuedBlockCount { get; set; }

    public long WrittenBlockCount { get; set; }

    public long RejectedBlockCount { get; set; }

    public long WriteFaultCount { get; set; }

    public long PeakPendingBlockCount { get; set; }

    public long PeakPendingPayloadBytes { get; set; }

    public long PendingBlockLimit { get; set; }

    public long PendingPayloadByteLimit { get; set; }

    public bool ProtectionTriggered { get; set; }

    public string ProtectionReason { get; set; } = "";

    public string LastError { get; set; } = "";

    public bool DataIntegrityPassed { get; set; } = true;

    public string IntegritySummary { get; set; } = "";

    public int ObservedDeviceCount { get; set; }

    public int DeviceIntegrityIssueCount { get; set; }

    public long MissingBlockCount { get; set; }

    public long NonMonotonicBlockCount { get; set; }

    public long TotalDataGapSampleCount { get; set; }

    public long TotalDataRegressionCount { get; set; }

    public bool DeviceSampleCountsBalanced { get; set; } = true;

    public long MinDeviceSamplesPerChannel { get; set; }

    public long MaxDeviceSamplesPerChannel { get; set; }

    public double WallClockDurationSeconds { get; set; }

    public double MinSampleDerivedDurationSeconds { get; set; }

    public double MaxSampleDerivedDurationSeconds { get; set; }

    public double MinEffectiveSampleRateHz { get; set; }

    public double MaxEffectiveSampleRateHz { get; set; }

    public bool SampleRateConsistencyPassed { get; set; } = true;

    public string SampleRateConsistencySummary { get; set; } = "";

    public List<SdkRawCaptureDeviceIntegrity> DeviceIntegrity { get; set; } = new();

    public Dictionary<string, long> ChannelSampleCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<TdmsSegmentManifestEntry> TdmsSegments { get; set; } = new();

    public List<TdmsSegmentManifestEntry> CompressedTdmsSegments { get; set; } = new();

    public long CompressedCaptureFileBytes { get; set; }

    public long CompressedPayloadBytes { get; set; }

    public long CompressionFaultCount { get; set; }
}

internal sealed class TdmsSegmentManifestEntry
{
    public string Path { get; set; } = "";

    public int SourceId { get; set; }

    public int SegmentIndex { get; set; }

    public long StartSample { get; set; }

    public int SamplesPerChannel { get; set; }

    public long EndSampleExclusive => StartSample + SamplesPerChannel;

    public double SampleRateHz { get; set; }

    public int[] ChannelIds { get; set; } = Array.Empty<int>();

    public bool CompressionEnabled { get; set; }

    public string CompressionType { get; set; } = "None";

    public string PreprocessType { get; set; } = "None";

    public long CompressionOriginalPayloadBytes { get; set; }

    public long CompressionPayloadBytes { get; set; }

    public int[] ChannelPayloadBytes { get; set; } = Array.Empty<int>();
}

internal sealed class SdkRawCaptureSampleRateAnalysis
{
    public double WallClockDurationSeconds { get; init; }

    public double MinSampleDerivedDurationSeconds { get; init; }

    public double MaxSampleDerivedDurationSeconds { get; init; }

    public double MinEffectiveSampleRateHz { get; init; }

    public double MaxEffectiveSampleRateHz { get; init; }

    public bool HasData { get; init; }

    public bool IsConsistent { get; init; } = true;

    public string Summary { get; init; } = "";
}

internal sealed class SdkRawCaptureDeviceIntegrity
{
    public int DeviceId { get; set; }

    public int MachineId { get; set; }

    public int ChannelCount { get; set; }

    public int SamplesPerBlockPerChannel { get; set; }

    public long BlockCount { get; set; }

    public long SamplesPerChannel { get; set; }

    public int FirstBlockIndex { get; set; }

    public int LastBlockIndex { get; set; }

    public long FirstTotalDataCount { get; set; }

    public long LastTotalDataCount { get; set; }

    public long MissingBlockCount { get; set; }

    public long NonMonotonicBlockCount { get; set; }

    public long TotalDataGapSampleCount { get; set; }

    public long TotalDataRegressionCount { get; set; }

    public bool BlockIndexContinuityEnabled { get; set; } = true;

    public bool ChannelLayoutChanged { get; set; }

    public bool BlockSizeChanged { get; set; }

    public bool HasIssues { get; set; }

    public List<string> IssueExamples { get; set; } = new();
}

internal sealed class SdkRawCaptureDeviceIntegrityState
{
    public int DeviceId { get; init; }

    public int MachineId { get; set; }

    public int ChannelCount { get; set; }

    public int SamplesPerBlockPerChannel { get; set; }

    public long BlockCount { get; set; }

    public long SamplesPerChannel { get; set; }

    public int FirstBlockIndex { get; set; }

    public int LastBlockIndex { get; set; }

    public long FirstTotalDataCount { get; set; }

    public long LastTotalDataCount { get; set; }

    public long MissingBlockCount { get; set; }

    public long NonMonotonicBlockCount { get; set; }

    public long TotalDataGapSampleCount { get; set; }

    public long TotalDataRegressionCount { get; set; }

    public bool BlockIndexContinuityEnabled { get; set; } = true;

    public bool ChannelLayoutChanged { get; set; }

    public bool BlockSizeChanged { get; set; }

    public List<string> IssueExamples { get; } = new();

    public bool HasIssues =>
        MissingBlockCount > 0
        || NonMonotonicBlockCount > 0
        || TotalDataGapSampleCount > 0
        || TotalDataRegressionCount > 0
        || ChannelLayoutChanged
        || BlockSizeChanged;
}

internal sealed class SdkRawCaptureWriterStatistics
{
    public string CapturePath { get; init; } = "";

    public DateTime StartedAtUtc { get; init; }

    public double ConfiguredSampleRateHz { get; init; }

    public long EnqueuedBlockCount { get; init; }

    public long WrittenBlockCount { get; init; }

    public long RejectedBlockCount { get; init; }

    public long WriteFaultCount { get; init; }

    public long PendingBlockCount { get; init; }

    public long PendingPayloadBytes { get; init; }

    public long PeakPendingBlockCount { get; init; }

    public long PeakPendingPayloadBytes { get; init; }

    public long PendingBlockLimit { get; init; }

    public long PendingPayloadByteLimit { get; init; }

    public bool ProtectionTriggered { get; init; }

    public string ProtectionReason { get; init; } = "";

    public long WrittenPayloadBytes { get; init; }

    public double WriteSeconds { get; init; }

    public string LastError { get; init; } = "";

    public bool HasTimingAnalysis { get; init; }

    public bool TimingConsistent { get; init; } = true;

    public double WallClockDurationSeconds { get; init; }

    public double MinEffectiveSampleRateHz { get; init; }

    public double MaxEffectiveSampleRateHz { get; init; }

    public string TimingSummary { get; init; } = "";
}

internal sealed class SdkRawCaptureResult
{
    public IReadOnlyList<string> WrittenFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, long> SampleCounts { get; init; } = new Dictionary<string, long>();

    public SdkRawCaptureWriterStatistics Statistics { get; init; } = new();

    public SdkRawCaptureManifest Manifest { get; init; } = new();
}

internal sealed class SdkRawCaptureLiveTimingState
{
    public int DeviceId { get; init; }

    public long BlockCount { get; set; }

    public long SamplesPerChannel { get; set; }

    public long FirstTotalDataCount { get; set; }

    public long LastTotalDataCount { get; set; }

    public int LastDataCountPerChannel { get; set; }

    public DateTime FirstReceivedAtUtc { get; set; }

    public DateTime LastReceivedAtUtc { get; set; }
}

internal sealed class SdkRawCaptureWriter : IDisposable
{
    private const int FlushBlockStride = 128;
    private const long MaxPendingBlockLimit = 128;
    private const long MaxPendingPayloadByteLimit = 512L * 1024 * 1024;

    private readonly Channel<SdkRawBlock> _queue = Channel.CreateUnbounded<SdkRawBlock>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentDictionary<int, long> _channelSampleCounts = new();
    private readonly ConcurrentDictionary<int, long> _channelBatchCounts = new();
    private readonly Dictionary<int, SdkRawCaptureDeviceIntegrityState> _deviceIntegrityStates = new();
    private readonly object _liveTimingLock = new();
    private readonly Dictionary<int, SdkRawCaptureLiveTimingState> _liveTimingStates = new();

    private FileStream? _stream;
    private BinaryWriter? _writer;
    private Task? _writerTask;
    private string? _capturePath;
    private string? _manifestPath;
    private string _sessionName = "session";
    private double _sampleRateHz;
    private int _expectedChannelCount;
    private DateTime _startedAtUtc;
    private DateTime _stoppedAtUtc;
    private bool _started;
    private long _blockCount;
    private long _totalSamples;
    private long _rawPayloadBytes;
    private double _writeSeconds;
    private long _enqueuedBlockCount;
    private long _writtenBlockCount;
    private long _rejectedBlockCount;
    private long _writeFaultCount;
    private long _pendingBlockCount;
    private long _pendingPayloadBytes;
    private long _peakPendingBlockCount;
    private long _peakPendingPayloadBytes;
    private int _protectionTriggered;
    private string _protectionReason = "";
    private Exception? _writerFault;

    public string? CapturePath => _capturePath;

    public bool ProtectionTriggered => Volatile.Read(ref _protectionTriggered) != 0;

    public void Start(string basePath, string sessionName, double sampleRateHz, IReadOnlyCollection<int> expectedChannelIds)
    {
        if (_started)
        {
            throw new InvalidOperationException("SDK raw capture writer is already started.");
        }

        ResetState();

        string sessionFolder = StorageSessionNaming.CreateUniqueSessionFolder(basePath, sessionName, out string safeName);
        Directory.CreateDirectory(sessionFolder);

        _sessionName = safeName;
        _sampleRateHz = sampleRateHz;
        _expectedChannelCount = expectedChannelIds?.Count ?? 0;
        _startedAtUtc = DateTime.UtcNow;
        _capturePath = Path.Combine(sessionFolder, $"{safeName}{SdkRawCaptureFormat.FileSuffix}");
        _manifestPath = SdkRawCaptureFormat.GetManifestPath(_capturePath);
        _stream = new FileStream(
            _capturePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 1024 * 1024,
            options: FileOptions.SequentialScan);
        _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
        WriteFileHeader();

        _writerTask = Task.Run(ProcessQueueAsync);
        _started = true;
    }

    public bool TryEnqueue(SdkRawBlock rawBlock)
    {
        if (!_started || _writerFault != null || ProtectionTriggered)
        {
            Interlocked.Increment(ref _rejectedBlockCount);
            rawBlock.ReleasePayload();
            return false;
        }

        bool written = _queue.Writer.TryWrite(rawBlock);
        if (!written)
        {
            Interlocked.Increment(ref _rejectedBlockCount);
            rawBlock.ReleasePayload();
            return false;
        }

        Interlocked.Increment(ref _enqueuedBlockCount);
        long pendingBlockCount = Interlocked.Increment(ref _pendingBlockCount);
        long pendingPayloadBytes = Interlocked.Add(ref _pendingPayloadBytes, rawBlock.PayloadBytes);
        TrackLiveTiming(rawBlock);
        UpdatePeak(ref _peakPendingBlockCount, pendingBlockCount);
        UpdatePeak(ref _peakPendingPayloadBytes, pendingPayloadBytes);

        if (pendingBlockCount > MaxPendingBlockLimit || pendingPayloadBytes > MaxPendingPayloadByteLimit)
        {
            string reason = $"Pending raw capture queue exceeded hard limit ({pendingBlockCount:N0}/{MaxPendingBlockLimit:N0} blocks, {pendingPayloadBytes:N0}/{MaxPendingPayloadByteLimit:N0} bytes).";
            TriggerProtection(reason);
        }

        return true;
    }

    public SdkRawCaptureWriterStatistics GetStatistics()
    {
        var liveTimingAnalysis = AnalyzeLiveTimingStatistics(DateTime.UtcNow);
        return new SdkRawCaptureWriterStatistics
        {
            CapturePath = _capturePath ?? "",
            StartedAtUtc = _startedAtUtc,
            ConfiguredSampleRateHz = _sampleRateHz,
            EnqueuedBlockCount = Interlocked.Read(ref _enqueuedBlockCount),
            WrittenBlockCount = Interlocked.Read(ref _writtenBlockCount),
            RejectedBlockCount = Interlocked.Read(ref _rejectedBlockCount),
            WriteFaultCount = Interlocked.Read(ref _writeFaultCount),
            PendingBlockCount = Interlocked.Read(ref _pendingBlockCount),
            PendingPayloadBytes = Interlocked.Read(ref _pendingPayloadBytes),
            PeakPendingBlockCount = Interlocked.Read(ref _peakPendingBlockCount),
            PeakPendingPayloadBytes = Interlocked.Read(ref _peakPendingPayloadBytes),
            PendingBlockLimit = MaxPendingBlockLimit,
            PendingPayloadByteLimit = MaxPendingPayloadByteLimit,
            ProtectionTriggered = ProtectionTriggered,
            ProtectionReason = _protectionReason,
            WrittenPayloadBytes = Interlocked.Read(ref _rawPayloadBytes),
            WriteSeconds = _writeSeconds,
            LastError = _writerFault?.Message ?? _protectionReason,
            HasTimingAnalysis = liveTimingAnalysis.HasData,
            TimingConsistent = liveTimingAnalysis.IsConsistent,
            WallClockDurationSeconds = liveTimingAnalysis.WallClockDurationSeconds,
            MinEffectiveSampleRateHz = liveTimingAnalysis.MinEffectiveSampleRateHz,
            MaxEffectiveSampleRateHz = liveTimingAnalysis.MaxEffectiveSampleRateHz,
            TimingSummary = liveTimingAnalysis.Summary
        };
    }

    public SdkRawCaptureResult Complete()
    {
        if (!_started)
        {
            return new SdkRawCaptureResult();
        }

        _queue.Writer.TryComplete();

        try
        {
            _writerTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _writerFault ??= ex;
            Interlocked.Increment(ref _writeFaultCount);
        }
        finally
        {
            _writerTask = null;
            _started = false;
            _stoppedAtUtc = DateTime.UtcNow;
        }

        try
        {
            _writer?.Flush();
            _stream?.Flush(flushToDisk: false);
        }
        catch
        {
        }

        _writer?.Dispose();
        _writer = null;
        _stream?.Dispose();
        _stream = null;

        string capturePath = _capturePath ?? string.Empty;
        var manifest = BuildManifest();
        PersistManifest(manifest);
        var sampleCounts = manifest.ChannelSampleCounts
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        return new SdkRawCaptureResult
        {
            WrittenFiles = string.IsNullOrEmpty(capturePath)
                ? Array.Empty<string>()
                : new[] { capturePath }.Where(File.Exists).ToArray(),
            SampleCounts = sampleCounts,
            Statistics = GetStatistics(),
            Manifest = manifest
        };
    }

    public void Dispose()
    {
        if (_started)
        {
            Complete();
        }
        else
        {
            _writer?.Dispose();
            _stream?.Dispose();
            _writer = null;
            _stream = null;
        }
    }

    private void ResetState()
    {
        _channelSampleCounts.Clear();
        _channelBatchCounts.Clear();
        _deviceIntegrityStates.Clear();
        lock (_liveTimingLock)
        {
            _liveTimingStates.Clear();
        }
        _capturePath = null;
        _manifestPath = null;
        _stoppedAtUtc = default;
        _blockCount = 0;
        _totalSamples = 0;
        _rawPayloadBytes = 0;
        _writeSeconds = 0d;
        _enqueuedBlockCount = 0;
        _writtenBlockCount = 0;
        _rejectedBlockCount = 0;
        _writeFaultCount = 0;
        _pendingBlockCount = 0;
        _pendingPayloadBytes = 0;
        _peakPendingBlockCount = 0;
        _peakPendingPayloadBytes = 0;
        _protectionTriggered = 0;
        _protectionReason = "";
        _writerFault = null;
    }

    private async Task ProcessQueueAsync()
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var rawBlock))
                {
                    Interlocked.Decrement(ref _pendingBlockCount);
                    Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);

                    try
                    {
                        WriteBlock(rawBlock);
                    }
                    catch (Exception ex)
                    {
                        _writerFault ??= ex;
                        Interlocked.Increment(ref _writeFaultCount);
                        Console.WriteLine($"[SdkRawCapture] 写入块失败: {ex.Message}");
                        DrainPendingBlocks(reader);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _writerFault ??= ex;
            Interlocked.Increment(ref _writeFaultCount);
            Console.WriteLine($"[SdkRawCapture] 后台写线程异常: {ex.Message}");
        }
    }

    private void DrainPendingBlocks(ChannelReader<SdkRawBlock> reader)
    {
        while (reader.TryRead(out var rawBlock))
        {
            Interlocked.Decrement(ref _pendingBlockCount);
            Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);
            rawBlock.ReleasePayload();
        }
    }

    private void WriteFileHeader()
    {
        if (_writer == null)
        {
            throw new InvalidOperationException("Raw capture writer is not initialized.");
        }

        _writer.Write(SdkRawCaptureFormat.FileMagic);
        _writer.Write(SdkRawCaptureFormat.FormatVersion);
        _writer.Write(_startedAtUtc.Ticks);
        _writer.Write(_sampleRateHz);
        _writer.Write(_expectedChannelCount);
        _writer.Write(0);
    }

    private void WriteBlock(SdkRawBlock rawBlock)
    {
        if (_writer == null)
        {
            throw new InvalidOperationException("Raw capture writer is not initialized.");
        }

        var payload = MemoryMarshal.AsBytes(rawBlock.PayloadSpan);
        var writeStopwatch = Stopwatch.StartNew();

        try
        {
            _writer.Write(SdkRawCaptureFormat.BlockMagic);
            _writer.Write(SdkRawCaptureFormat.FormatVersion);
            _writer.Write(rawBlock.SampleTime);
            _writer.Write(rawBlock.MessageType);
            _writer.Write(rawBlock.GroupId);
            _writer.Write(rawBlock.MachineId);
            _writer.Write(rawBlock.TotalDataCount);
            _writer.Write(rawBlock.DataCountPerChannel);
            _writer.Write(rawBlock.BufferCountBytes);
            _writer.Write(rawBlock.BlockIndex);
            _writer.Write(rawBlock.ChannelCount);
            _writer.Write(rawBlock.SampleRateHz);
            _writer.Write(rawBlock.ReceivedAtUtc.Ticks);
            _writer.Write(rawBlock.PayloadFloatCount);
            _writer.Write(rawBlock.PayloadBytes);
            _writer.Write(payload);

            writeStopwatch.Stop();
            _writeSeconds += writeStopwatch.Elapsed.TotalSeconds;
            _blockCount++;
            _totalSamples += (long)rawBlock.ChannelCount * rawBlock.DataCountPerChannel;
            _rawPayloadBytes += rawBlock.PayloadBytes;
            Interlocked.Increment(ref _writtenBlockCount);
            int deviceId = ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);
            TrackDeviceIntegrity(rawBlock, deviceId);
            for (int ch = 0; ch < rawBlock.ChannelCount; ch++)
            {
                int channelId = deviceId * 100 + (ch + 1);
                _channelSampleCounts.AddOrUpdate(channelId, rawBlock.DataCountPerChannel, (_, current) => current + rawBlock.DataCountPerChannel);
                _channelBatchCounts.AddOrUpdate(channelId, 1, static (_, current) => current + 1);
            }

            if ((_blockCount % FlushBlockStride) == 0)
            {
                try
                {
                    _writer.Flush();
                    _stream?.Flush(flushToDisk: false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            rawBlock.ReleasePayload();
        }
    }

    internal static int ResolveChannelDeviceId(int groupId, int machineId)
    {
        return SdkDeviceIdResolver.ResolveDeviceId(
            groupId: groupId,
            machineId: machineId);
    }

    private void TrackLiveTiming(SdkRawBlock rawBlock)
    {
        int deviceId = ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);

        lock (_liveTimingLock)
        {
            if (!_liveTimingStates.TryGetValue(deviceId, out var state))
            {
                state = new SdkRawCaptureLiveTimingState
                {
                    DeviceId = deviceId,
                    FirstTotalDataCount = rawBlock.TotalDataCount,
                    LastTotalDataCount = rawBlock.TotalDataCount,
                    LastDataCountPerChannel = rawBlock.DataCountPerChannel,
                    FirstReceivedAtUtc = rawBlock.ReceivedAtUtc,
                    LastReceivedAtUtc = rawBlock.ReceivedAtUtc
                };
                _liveTimingStates[deviceId] = state;
            }
            else
            {
                if (rawBlock.ReceivedAtUtc <= state.FirstReceivedAtUtc)
                {
                    state.FirstReceivedAtUtc = rawBlock.ReceivedAtUtc;
                    state.FirstTotalDataCount = rawBlock.TotalDataCount;
                }

                if (rawBlock.ReceivedAtUtc >= state.LastReceivedAtUtc)
                {
                    state.LastReceivedAtUtc = rawBlock.ReceivedAtUtc;
                    state.LastTotalDataCount = rawBlock.TotalDataCount;
                    state.LastDataCountPerChannel = rawBlock.DataCountPerChannel;
                }
            }

            state.BlockCount++;
            state.SamplesPerChannel += rawBlock.DataCountPerChannel;
        }
    }

    private SdkRawCaptureSampleRateAnalysis AnalyzeLiveTimingStatistics(DateTime observedAtUtc)
    {
        List<SdkRawCaptureDeviceIntegrity> liveDevices;
        lock (_liveTimingLock)
        {
            liveDevices = _liveTimingStates.Values
                .Select(state =>
                {
                    long totalDataDerivedSamples = 0;
                    if (state.LastTotalDataCount >= state.FirstTotalDataCount && state.LastDataCountPerChannel > 0)
                    {
                        totalDataDerivedSamples = (state.LastTotalDataCount - state.FirstTotalDataCount) + state.LastDataCountPerChannel;
                    }

                    long samplesPerChannel = Math.Max(state.SamplesPerChannel, totalDataDerivedSamples);
                    return new SdkRawCaptureDeviceIntegrity
                    {
                        DeviceId = state.DeviceId,
                        SamplesPerChannel = samplesPerChannel
                    };
                })
                .Where(device => device.SamplesPerChannel > 0)
                .ToList();
        }

        return AnalyzeSampleRateConsistency(
            _sampleRateHz,
            _startedAtUtc,
            observedAtUtc,
            liveDevices,
            sampleCounts: Array.Empty<KeyValuePair<string, long>>().ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase));
    }

    private void TrackDeviceIntegrity(SdkRawBlock rawBlock, int deviceId)
    {
        if (!_deviceIntegrityStates.TryGetValue(deviceId, out var state))
        {
            state = new SdkRawCaptureDeviceIntegrityState
            {
                DeviceId = deviceId,
                MachineId = rawBlock.MachineId,
                ChannelCount = rawBlock.ChannelCount,
                SamplesPerBlockPerChannel = rawBlock.DataCountPerChannel,
                FirstBlockIndex = rawBlock.BlockIndex,
                LastBlockIndex = rawBlock.BlockIndex,
                FirstTotalDataCount = rawBlock.TotalDataCount,
                LastTotalDataCount = rawBlock.TotalDataCount
            };
            _deviceIntegrityStates[deviceId] = state;
        }
        else
        {
            if (state.ChannelCount != rawBlock.ChannelCount)
            {
                state.ChannelLayoutChanged = true;
                AddIssueExample(
                    state,
                    $"channel-count changed: expected {state.ChannelCount}, observed {rawBlock.ChannelCount} at block {rawBlock.BlockIndex}");
                state.ChannelCount = rawBlock.ChannelCount;
            }

            if (state.SamplesPerBlockPerChannel != rawBlock.DataCountPerChannel)
            {
                state.BlockSizeChanged = true;
                AddIssueExample(
                    state,
                    $"samples-per-block changed: expected {state.SamplesPerBlockPerChannel}, observed {rawBlock.DataCountPerChannel} at block {rawBlock.BlockIndex}");
                state.SamplesPerBlockPerChannel = rawBlock.DataCountPerChannel;
            }

            long expectedNextTotalDataCount = state.LastTotalDataCount + rawBlock.DataCountPerChannel;
            bool totalDataAdvanced = rawBlock.TotalDataCount > state.LastTotalDataCount;
            if (state.BlockIndexContinuityEnabled)
            {
                if (rawBlock.BlockIndex == state.LastBlockIndex && totalDataAdvanced)
                {
                    // Some SDK modes keep nBlockIndex constant (observed as all-zero)
                    // while TotalDataCount advances correctly. In that case the field
                    // is not usable for continuity checks and should be ignored.
                    state.BlockIndexContinuityEnabled = false;
                }
                else
                {
                    int expectedNextBlockIndex = state.LastBlockIndex + 1;
                    if (rawBlock.BlockIndex > expectedNextBlockIndex)
                    {
                        long missingBlocks = rawBlock.BlockIndex - expectedNextBlockIndex;
                        state.MissingBlockCount += missingBlocks;
                        AddIssueExample(
                            state,
                            $"block-index gap: last {state.LastBlockIndex}, current {rawBlock.BlockIndex}, missing {missingBlocks}");
                    }
                    else if (rawBlock.BlockIndex <= state.LastBlockIndex)
                    {
                        state.NonMonotonicBlockCount++;
                        AddIssueExample(
                            state,
                            $"block-index non-monotonic: last {state.LastBlockIndex}, current {rawBlock.BlockIndex}");
                    }
                }
            }

            if (rawBlock.TotalDataCount > expectedNextTotalDataCount)
            {
                long gapSamples = rawBlock.TotalDataCount - expectedNextTotalDataCount;
                state.TotalDataGapSampleCount += gapSamples;
                AddIssueExample(
                    state,
                    $"total-data gap: last {state.LastTotalDataCount}, current {rawBlock.TotalDataCount}, missing {gapSamples} samples");
            }
            else if (rawBlock.TotalDataCount <= state.LastTotalDataCount)
            {
                state.TotalDataRegressionCount++;
                AddIssueExample(
                    state,
                    $"total-data non-monotonic: last {state.LastTotalDataCount}, current {rawBlock.TotalDataCount}");
            }

            if (rawBlock.BlockIndex > state.LastBlockIndex)
            {
                state.LastBlockIndex = rawBlock.BlockIndex;
            }

            if (rawBlock.TotalDataCount > state.LastTotalDataCount)
            {
                state.LastTotalDataCount = rawBlock.TotalDataCount;
            }
        }

        state.MachineId = rawBlock.MachineId;
        state.BlockCount++;
        state.SamplesPerChannel += rawBlock.DataCountPerChannel;
    }

    private static void AddIssueExample(SdkRawCaptureDeviceIntegrityState state, string message)
    {
        if (state.IssueExamples.Count >= 4)
        {
            return;
        }

        state.IssueExamples.Add(message);
    }

    private SdkRawCaptureManifest BuildManifest()
    {
        string capturePath = _capturePath ?? string.Empty;
        long captureBytes = !string.IsNullOrEmpty(capturePath) && File.Exists(capturePath)
            ? new FileInfo(capturePath).Length
            : 0L;

        var sampleCounts = _channelSampleCounts
            .OrderBy(kvp => kvp.Key)
            .ToDictionary(
                kvp => DH.Contracts.ChannelNaming.ChannelName(kvp.Key),
                kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);

        var deviceIntegrity = _deviceIntegrityStates
            .OrderBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var state = kvp.Value;
                return new SdkRawCaptureDeviceIntegrity
                {
                    DeviceId = state.DeviceId,
                    MachineId = state.MachineId,
                    ChannelCount = state.ChannelCount,
                    SamplesPerBlockPerChannel = state.SamplesPerBlockPerChannel,
                    BlockCount = state.BlockCount,
                    SamplesPerChannel = state.SamplesPerChannel,
                    FirstBlockIndex = state.FirstBlockIndex,
                    LastBlockIndex = state.LastBlockIndex,
                    FirstTotalDataCount = state.FirstTotalDataCount,
                    LastTotalDataCount = state.LastTotalDataCount,
                    MissingBlockCount = state.MissingBlockCount,
                    NonMonotonicBlockCount = state.NonMonotonicBlockCount,
                    TotalDataGapSampleCount = state.TotalDataGapSampleCount,
                    TotalDataRegressionCount = state.TotalDataRegressionCount,
                    BlockIndexContinuityEnabled = state.BlockIndexContinuityEnabled,
                    ChannelLayoutChanged = state.ChannelLayoutChanged,
                    BlockSizeChanged = state.BlockSizeChanged,
                    HasIssues = state.HasIssues,
                    IssueExamples = state.IssueExamples.ToList()
                };
            })
            .ToList();

        long minDeviceSamplesPerChannel = deviceIntegrity.Count > 0
            ? deviceIntegrity.Min(d => d.SamplesPerChannel)
            : 0L;
        long maxDeviceSamplesPerChannel = deviceIntegrity.Count > 0
            ? deviceIntegrity.Max(d => d.SamplesPerChannel)
            : 0L;
        bool deviceSampleCountsBalanced = deviceIntegrity.Count <= 1 || minDeviceSamplesPerChannel == maxDeviceSamplesPerChannel;
        bool hasBoundaryTailSkewOnly = HasBoundaryTailSkewOnly(
            deviceIntegrity,
            deviceSampleCountsBalanced,
            minDeviceSamplesPerChannel,
            maxDeviceSamplesPerChannel);
        int deviceIntegrityIssueCount = deviceIntegrity.Count(d => d.HasIssues);
        long missingBlockCount = deviceIntegrity.Sum(d => d.MissingBlockCount);
        long nonMonotonicBlockCount = deviceIntegrity.Sum(d => d.NonMonotonicBlockCount);
        long totalDataGapSampleCount = deviceIntegrity.Sum(d => d.TotalDataGapSampleCount);
        long totalDataRegressionCount = deviceIntegrity.Sum(d => d.TotalDataRegressionCount);
        bool dataIntegrityPassed =
            deviceIntegrityIssueCount == 0
            && (deviceSampleCountsBalanced || hasBoundaryTailSkewOnly)
            && missingBlockCount == 0
            && nonMonotonicBlockCount == 0
            && totalDataGapSampleCount == 0
            && totalDataRegressionCount == 0;

        string integritySummary = BuildIntegritySummary(
            deviceIntegrity,
            deviceSampleCountsBalanced,
            minDeviceSamplesPerChannel,
            maxDeviceSamplesPerChannel);
        var sampleRateAnalysis = AnalyzeSampleRateConsistency(
            _sampleRateHz,
            _startedAtUtc,
            _stoppedAtUtc,
            deviceIntegrity,
            sampleCounts);

        return new SdkRawCaptureManifest
        {
            Version = SdkRawCaptureFormat.FormatVersion,
            SessionName = _sessionName,
            CaptureFileName = Path.GetFileName(capturePath),
            StartedAtUtc = _startedAtUtc,
            StoppedAtUtc = _stoppedAtUtc,
            SampleRateHz = _sampleRateHz,
            ExpectedChannelCount = _expectedChannelCount,
            ObservedChannelCount = _channelSampleCounts.Count,
            BlockCount = _blockCount,
            TotalSamples = _totalSamples,
            RawPayloadBytes = _rawPayloadBytes,
            CaptureFileBytes = captureBytes,
            WriteSeconds = _writeSeconds,
            EnqueuedBlockCount = Interlocked.Read(ref _enqueuedBlockCount),
            WrittenBlockCount = Interlocked.Read(ref _writtenBlockCount),
            RejectedBlockCount = Interlocked.Read(ref _rejectedBlockCount),
            WriteFaultCount = Interlocked.Read(ref _writeFaultCount),
            PeakPendingBlockCount = Interlocked.Read(ref _peakPendingBlockCount),
            PeakPendingPayloadBytes = Interlocked.Read(ref _peakPendingPayloadBytes),
            PendingBlockLimit = MaxPendingBlockLimit,
            PendingPayloadByteLimit = MaxPendingPayloadByteLimit,
            ProtectionTriggered = ProtectionTriggered,
            ProtectionReason = _protectionReason,
            LastError = _writerFault?.Message ?? _protectionReason,
            DataIntegrityPassed = dataIntegrityPassed,
            IntegritySummary = integritySummary,
            ObservedDeviceCount = deviceIntegrity.Count,
            DeviceIntegrityIssueCount = deviceIntegrityIssueCount,
            MissingBlockCount = missingBlockCount,
            NonMonotonicBlockCount = nonMonotonicBlockCount,
            TotalDataGapSampleCount = totalDataGapSampleCount,
            TotalDataRegressionCount = totalDataRegressionCount,
            DeviceSampleCountsBalanced = deviceSampleCountsBalanced,
            MinDeviceSamplesPerChannel = minDeviceSamplesPerChannel,
            MaxDeviceSamplesPerChannel = maxDeviceSamplesPerChannel,
            WallClockDurationSeconds = sampleRateAnalysis.WallClockDurationSeconds,
            MinSampleDerivedDurationSeconds = sampleRateAnalysis.MinSampleDerivedDurationSeconds,
            MaxSampleDerivedDurationSeconds = sampleRateAnalysis.MaxSampleDerivedDurationSeconds,
            MinEffectiveSampleRateHz = sampleRateAnalysis.MinEffectiveSampleRateHz,
            MaxEffectiveSampleRateHz = sampleRateAnalysis.MaxEffectiveSampleRateHz,
            SampleRateConsistencyPassed = sampleRateAnalysis.IsConsistent,
            SampleRateConsistencySummary = sampleRateAnalysis.Summary,
            DeviceIntegrity = deviceIntegrity,
            ChannelSampleCounts = sampleCounts
        };
    }

    private static string BuildIntegritySummary(
        IReadOnlyList<SdkRawCaptureDeviceIntegrity> deviceIntegrity,
        bool deviceSampleCountsBalanced,
        long minDeviceSamplesPerChannel,
        long maxDeviceSamplesPerChannel)
    {
        if (deviceIntegrity.Count == 0)
        {
            return "No device integrity data recorded.";
        }

        var issues = new List<string>();
        long missingBlocks = deviceIntegrity.Sum(d => d.MissingBlockCount);
        long nonMonotonicBlocks = deviceIntegrity.Sum(d => d.NonMonotonicBlockCount);
        long totalDataGapSamples = deviceIntegrity.Sum(d => d.TotalDataGapSampleCount);
        long totalDataRegressions = deviceIntegrity.Sum(d => d.TotalDataRegressionCount);

        if (missingBlocks > 0)
        {
            issues.Add($"missing block indexes {missingBlocks:N0}");
        }

        if (nonMonotonicBlocks > 0)
        {
            issues.Add($"non-monotonic block indexes {nonMonotonicBlocks:N0}");
        }

        if (totalDataGapSamples > 0)
        {
            issues.Add($"missing samples by total-data count {totalDataGapSamples:N0}");
        }

        if (totalDataRegressions > 0)
        {
            issues.Add($"non-monotonic total-data count {totalDataRegressions:N0}");
        }

        if (!deviceSampleCountsBalanced)
        {
            var minDevice = deviceIntegrity
                .OrderBy(d => d.SamplesPerChannel)
                .ThenBy(d => d.DeviceId)
                .First();
            var maxDevice = deviceIntegrity
                .OrderByDescending(d => d.SamplesPerChannel)
                .ThenBy(d => d.DeviceId)
                .First();

            if (HasBoundaryTailSkewOnly(deviceIntegrity, deviceSampleCountsBalanced, minDeviceSamplesPerChannel, maxDeviceSamplesPerChannel))
            {
                issues.Add(
                    $"device tail-block boundary skew AI{minDevice.DeviceId:00}={minDeviceSamplesPerChannel:N0} to AI{maxDevice.DeviceId:00}={maxDeviceSamplesPerChannel:N0}");
            }
            else
            {
                issues.Add(
                    $"device samples/channel range AI{minDevice.DeviceId:00}={minDeviceSamplesPerChannel:N0} to AI{maxDevice.DeviceId:00}={maxDeviceSamplesPerChannel:N0}");
            }
        }

        return issues.Count == 0
            ? "No device continuity issues detected."
            : string.Join("; ", issues);
    }

    private static SdkRawCaptureSampleRateAnalysis AnalyzeSampleRateConsistency(
        double sampleRateHz,
        DateTime startedAtUtc,
        DateTime stoppedAtUtc,
        IReadOnlyList<SdkRawCaptureDeviceIntegrity> deviceIntegrity,
        IReadOnlyDictionary<string, long> sampleCounts)
    {
        double wallClockDurationSeconds = Math.Max(0d, (stoppedAtUtc - startedAtUtc).TotalSeconds);
        long minSamplesPerChannel = 0;
        long maxSamplesPerChannel = 0;

        if (deviceIntegrity.Count > 0)
        {
            minSamplesPerChannel = deviceIntegrity.Min(device => device.SamplesPerChannel);
            maxSamplesPerChannel = deviceIntegrity.Max(device => device.SamplesPerChannel);
        }
        else if (sampleCounts.Count > 0)
        {
            minSamplesPerChannel = sampleCounts.Values.Min();
            maxSamplesPerChannel = sampleCounts.Values.Max();
        }

        if (sampleRateHz <= 0d || wallClockDurationSeconds <= 0d || maxSamplesPerChannel <= 0)
        {
            return new SdkRawCaptureSampleRateAnalysis
            {
                WallClockDurationSeconds = wallClockDurationSeconds,
                Summary = "Insufficient timing data."
            };
        }

        double minSampleDerivedDurationSeconds = minSamplesPerChannel / sampleRateHz;
        double maxSampleDerivedDurationSeconds = maxSamplesPerChannel / sampleRateHz;
        double minEffectiveSampleRateHz = minSamplesPerChannel / wallClockDurationSeconds;
        double maxEffectiveSampleRateHz = maxSamplesPerChannel / wallClockDurationSeconds;
        double minRateRatio = minEffectiveSampleRateHz / sampleRateHz;
        double maxRateRatio = maxEffectiveSampleRateHz / sampleRateHz;

        const double toleranceRatio = 0.15d;
        bool isConsistent =
            wallClockDurationSeconds < 1.0d
            || (minRateRatio >= 1.0d - toleranceRatio && maxRateRatio <= 1.0d + toleranceRatio);

        return new SdkRawCaptureSampleRateAnalysis
        {
            WallClockDurationSeconds = wallClockDurationSeconds,
            MinSampleDerivedDurationSeconds = minSampleDerivedDurationSeconds,
            MaxSampleDerivedDurationSeconds = maxSampleDerivedDurationSeconds,
            MinEffectiveSampleRateHz = minEffectiveSampleRateHz,
            MaxEffectiveSampleRateHz = maxEffectiveSampleRateHz,
            HasData = true,
            IsConsistent = isConsistent,
            Summary = BuildSampleRateConsistencySummary(
                sampleRateHz,
                wallClockDurationSeconds,
                minSampleDerivedDurationSeconds,
                maxSampleDerivedDurationSeconds,
                minEffectiveSampleRateHz,
                maxEffectiveSampleRateHz)
        };
    }

    private static string BuildSampleRateConsistencySummary(
        double sampleRateHz,
        double wallClockDurationSeconds,
        double minSampleDerivedDurationSeconds,
        double maxSampleDerivedDurationSeconds,
        double minEffectiveSampleRateHz,
        double maxEffectiveSampleRateHz)
    {
        string effectiveRateText = FormatDoubleRange(minEffectiveSampleRateHz, maxEffectiveSampleRateHz, "N0");
        string durationText = FormatDoubleRange(minSampleDerivedDurationSeconds, maxSampleDerivedDurationSeconds, "N2");
        double minRatioPercent = sampleRateHz > 0d ? (minEffectiveSampleRateHz / sampleRateHz) * 100d : 0d;
        double maxRatioPercent = sampleRateHz > 0d ? (maxEffectiveSampleRateHz / sampleRateHz) * 100d : 0d;
        string ratioText = FormatDoubleRange(minRatioPercent, maxRatioPercent, "N1");
        return $"文件头采样率={sampleRateHz:N0} Hz，反推采样率={effectiveRateText} Hz，墙钟时长={wallClockDurationSeconds:N2}s，样本换算时长={durationText}s，比例={ratioText}%";
    }

    private static string FormatDoubleRange(double minValue, double maxValue, string format)
    {
        return Math.Abs(minValue - maxValue) < 0.000001d
            ? minValue.ToString(format)
            : $"{minValue.ToString(format)} ~ {maxValue.ToString(format)}";
    }

    private static bool HasBoundaryTailSkewOnly(
        IReadOnlyList<SdkRawCaptureDeviceIntegrity> deviceIntegrity,
        bool deviceSampleCountsBalanced,
        long minDeviceSamplesPerChannel,
        long maxDeviceSamplesPerChannel)
    {
        if (deviceIntegrity.Count <= 1 || deviceSampleCountsBalanced)
        {
            return false;
        }

        if (deviceIntegrity.Any(d =>
                d.HasIssues
                || d.SamplesPerBlockPerChannel <= 0
                || d.SamplesPerChannel <= 0))
        {
            return false;
        }

        var blockSizes = deviceIntegrity
            .Select(d => d.SamplesPerBlockPerChannel)
            .Distinct()
            .ToList();
        if (blockSizes.Count != 1)
        {
            return false;
        }

        long spread = maxDeviceSamplesPerChannel - minDeviceSamplesPerChannel;
        int commonBlockSize = blockSizes[0];
        if (spread <= 0
            || commonBlockSize <= 0
            || spread > commonBlockSize
            || (spread % commonBlockSize) != 0)
        {
            return false;
        }

        return deviceIntegrity.All(device =>
        {
            long tailSpread = device.SamplesPerChannel - minDeviceSamplesPerChannel;
            return tailSpread >= 0
                && tailSpread <= commonBlockSize
                && (tailSpread % commonBlockSize) == 0;
        });
    }

    private void TriggerProtection(string reason)
    {
        if (Interlocked.CompareExchange(ref _protectionTriggered, 1, 0) != 0)
        {
            return;
        }

        _protectionReason = reason;
        Console.WriteLine($"[SdkRawCapture] Protection triggered: {reason}");
    }

    private void PersistManifest(SdkRawCaptureManifest manifest)
    {
        if (string.IsNullOrEmpty(_manifestPath))
        {
            return;
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_manifestPath, json);
    }

    private static void UpdatePeak(ref long target, long candidate)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }

}
