using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DH.Client.App.Services.Performance;
using DH.Contracts;
using DH.Driver.SDK;

namespace DH.Client.App.Services.Storage;

internal sealed class SdkTdmsCaptureWriter : IDisposable
{
    private const long MaxPendingBlockLimit = 320;
    private const long MaxPendingPayloadByteLimit = 2L * 1024 * 1024 * 1024;
    private const long MaxSourcePendingBlockLimit = 64;
    private const long MaxSourcePendingPayloadByteLimit = 384L * 1024 * 1024;
    // Keep source segments small enough that 256ch/1MHz capture does not allocate
    // multi-GB waves of LOH buffers at each segment boundary.
    private const long StreamAppendChunkPayloadByteLimit = 48L * 1024 * 1024;
    private const double StreamAppendChunkSeconds = 0.5d;
    private const int TdmsSegmentWriterCount = 2;
    private const int BackgroundCompressionWorkerCount = 1;
    private const long InlineBackgroundCompressionPayloadBytePerSecondLimit = 256L * 1024 * 1024;
    private const double QueueStatusLogSeconds = 10d;
    private const long MaxPendingSegmentLimit = 200;
    private const long MaxPendingSegmentPayloadByteLimit = 64L * 1024 * 1024 * 1024;
    private const bool EnableHotPathWriteHash = false;
    private const bool EnableCapturePreviewSidecar = false;

    private readonly ConcurrentDictionary<int, SourceTdmsWriter> _sourceWriters = new();
    private Channel<PendingTdmsSegment> _segmentWriteQueue = CreateSegmentWriteQueue();
    private Channel<BackgroundTdmsSegmentCompressionJob> _backgroundCompressionQueue = CreateBackgroundCompressionQueue();
    private readonly List<Task> _segmentWriterTasks = new();
    private readonly List<Task> _backgroundCompressionTasks = new();
    private readonly ConcurrentDictionary<int, long> _channelSampleCounts = new();
    private readonly List<string> _writtenFiles = new();
    private readonly List<string> _compressedFiles = new();
    private readonly List<string> _previewFiles = new();
    private readonly List<TdmsSegmentManifestEntry> _tdmsSegments = new();
    private readonly List<TdmsSegmentManifestEntry> _compressedTdmsSegments = new();
    private readonly ConcurrentDictionary<int, TdmsSourceRawTimingState> _sourceRawTiming = new();
    private readonly HashSet<int> _expectedChannelIds = new();
    private readonly object _diagnosticLogLock = new();
    private readonly object _metricsLock = new();
    private readonly object _resultLock = new();
    private readonly object _previewWriterLock = new();

    private string _sessionName = "session";
    private string? _sessionFolder;
    private string? _rawFolder;
    private string? _compressedFolder;
    private string? _artifactRootPath;
    private string? _diagnosticLogPath;
    private string? _performanceDiagnosticLogPath;
    private PreviewSidecarWriter? _previewWriter;
    private double _sampleRateHz;
    private DateTime _startedAtUtc;
    private DateTime _stoppedAtUtc;
    private bool _started;
    private long _enqueuedBlockCount;
    private long _writtenBlockCount;
    private long _rejectedBlockCount;
    private long _writeFaultCount;
    private long _pendingBlockCount;
    private long _pendingPayloadBytes;
    private long _peakPendingBlockCount;
    private long _peakPendingPayloadBytes;
    private long _totalSamples;
    private long _tdmsSegmentPayloadBytes;
    private long _tdmsSegmentFileBytes;
    private long _tdmsSegmentWriteTicks;
    private long _tdmsSegmentAppendTicks;
    private long _tdmsSegmentSaveTicks;
    private long _tdmsSegmentCloseTicks;
    private long _previewWriteTicks;
    private long _previewSegmentCount;
    private long _previewPayloadBytes;
    private long _backgroundCompressionSegmentCount;
    private long _backgroundCompressionFaultCount;
    private long _backgroundCompressionPayloadBytes;
    private long _backgroundCompressionFileBytes;
    private long _backgroundCompressionTicks;
    private long _backgroundCompressionReadTicks;
    private long _pendingBackgroundCompressionCount;
    private long _pendingBackgroundCompressionPayloadBytes;
    private long _peakPendingBackgroundCompressionCount;
    private long _peakPendingBackgroundCompressionPayloadBytes;
    private long _tdmsFullSegmentCount;
    private long _tdmsPartialSegmentCount;
    private long _pendingSegmentCount;
    private long _pendingSegmentPayloadBytes;
    private long _peakPendingSegmentCount;
    private long _peakPendingSegmentPayloadBytes;
    private long _peakSourceBlockTicks;
    private long _peakSourceBlockDeinterleaveTicks;
    private long _lastQueueStatusTicks;
    private double _writeSeconds;
    private int _backgroundCompressionDrainEnabled;
    private bool _backgroundCompressionDuringCapture;
    private int _protectionTriggered;
    private string _protectionReason = "";
    private Exception? _writerFault;
    private Exception? _backgroundCompressionFault;
    private StorageCompressionSettings _requestedCompressionSettings = new();
    private StorageCompressionSettings _backgroundCompressionSettings = new();
    private StorageCompressionSettings _compressionSettings = new();

    public bool ProtectionTriggered => Volatile.Read(ref _protectionTriggered) != 0;

    private bool ShouldRunBackgroundCompression
        => _backgroundCompressionSettings.Enabled
           && (_backgroundCompressionSettings.Algorithm != CompressionType.None
               || _backgroundCompressionSettings.Preprocess != PreprocessType.None);

    private static double EstimateCapturePayloadBytesPerSecond(int channelCount, double sampleRateHz)
        => Math.Max(0, channelCount) * Math.Max(0d, sampleRateHz) * sizeof(float);

    public void Start(
        string basePath,
        string sessionName,
        double sampleRateHz,
        IReadOnlyCollection<int> expectedChannelIds,
        StorageCompressionSettings? compressionSettings = null)
    {
        if (_started)
        {
            throw new InvalidOperationException("SDK TDMS capture writer is already started.");
        }

        ResetState();

        _sampleRateHz = sampleRateHz;
        _requestedCompressionSettings = compressionSettings?.Clone() ?? new StorageCompressionSettings();
        _requestedCompressionSettings.Normalize();
        _backgroundCompressionSettings = _requestedCompressionSettings.Clone();
        _backgroundCompressionSettings.Normalize();
        _compressionSettings = CreateCaptureHotPathCompressionSettings(_requestedCompressionSettings);
        _compressionSettings.Normalize();
        _backgroundCompressionDuringCapture = ShouldRunBackgroundCompression
            && EstimateCapturePayloadBytesPerSecond(expectedChannelIds.Count, sampleRateHz)
                <= InlineBackgroundCompressionPayloadBytePerSecondLimit;
        Volatile.Write(ref _backgroundCompressionDrainEnabled, _backgroundCompressionDuringCapture ? 1 : 0);
        _startedAtUtc = DateTime.UtcNow;
        _sessionFolder = StorageSessionNaming.CreateUniqueSessionFolder(basePath, sessionName, out string safeSessionName);
        _sessionName = safeSessionName;
        _rawFolder = Path.Combine(_sessionFolder, "raw");
        _compressedFolder = Path.Combine(_sessionFolder, "compressed");
        _artifactRootPath = Path.Combine(_sessionFolder, $"{_sessionName}.artifacts");
        Directory.CreateDirectory(_rawFolder);
        if (ShouldRunBackgroundCompression)
        {
            Directory.CreateDirectory(_compressedFolder);
        }

        Directory.CreateDirectory(_artifactRootPath);
        _diagnosticLogPath = Path.Combine(_sessionFolder, "tdms-capture-writer.log");
        _performanceDiagnosticLogPath = PerformanceOutputPaths.GetStoragePath("tdms-capture-writer.log");
        StorageCompressionSettings.WriteSnapshot(
            Path.Combine(_artifactRootPath, StorageCompressionSettings.DefaultFileName),
            _compressionSettings);

        _expectedChannelIds.Clear();
        foreach (int channelId in expectedChannelIds.Distinct().OrderBy(static id => id))
        {
            _expectedChannelIds.Add(channelId);
        }

        foreach (var group in _expectedChannelIds.GroupBy(ChannelNaming.GetDeviceId))
        {
            CreateSourceWriter(group.Key, group.ToArray());
        }

        for (int i = 0; i < TdmsSegmentWriterCount; i++)
        {
            int workerId = i + 1;
            _segmentWriterTasks.Add(Task.Run(() => ProcessSegmentQueueAsync(workerId)));
        }

        if (ShouldRunBackgroundCompression)
        {
            for (int i = 0; i < BackgroundCompressionWorkerCount; i++)
            {
                int workerId = i + 1;
                _backgroundCompressionTasks.Add(Task.Run(() => ProcessBackgroundCompressionQueueAsync(workerId)));
            }

            StorageCompressionSettings.WriteSnapshot(
                Path.Combine(_artifactRootPath, "storage.background-compression.json"),
                _backgroundCompressionSettings);
            LogDiagnostic(
                $"background-compression-started workers={BackgroundCompressionWorkerCount:N0} requested={_backgroundCompressionSettings.Describe()} duringCapture={_backgroundCompressionDuringCapture} estimatedPayloadBytesPerSecond={EstimateCapturePayloadBytesPerSecond(expectedChannelIds.Count, sampleRateHz):N0} inlineLimitBytesPerSecond={InlineBackgroundCompressionPayloadBytePerSecondLimit:N0} folder={_compressedFolder}");
        }

        if (EnableCapturePreviewSidecar)
        {
            _previewWriter = new PreviewSidecarWriter(
                _artifactRootPath,
                _sessionName,
                _sampleRateHz,
                _expectedChannelIds,
                PreviewSidecarWriter.CreateCaptureCoarseLevels());
            LogDiagnostic($"preview-sidecar-start levels=L2,L3,L4 root={Path.Combine(_artifactRootPath, "preview_levels")}");
        }
        else
        {
            _previewWriter = null;
            LogDiagnostic("preview-sidecar-disabled mode=offline-build");
        }

        _started = true;
        WriteDiagnosticHeader(basePath, expectedChannelIds.Count);
        if (_requestedCompressionSettings.Enabled && !_compressionSettings.Enabled)
        {
            LogDiagnostic(
                $"compression-hot-path-bypassed requested={_requestedCompressionSettings.Describe()} effective={_compressionSettings.Describe()} backgroundRealtime={ShouldRunBackgroundCompression} backgroundDuringCapture={_backgroundCompressionDuringCapture} reason=preserve-256ch-1MHz-capture-throughput");
        }

        LogDiagnostic($"writer-started sourceWriters={_sourceWriters.Count:N0} tdmsSegmentWriters={TdmsSegmentWriterCount:N0} previewLevels={(EnableCapturePreviewSidecar ? "L2,L3,L4" : "disabled/offline-build")}");
    }

    private static StorageCompressionSettings CreateCaptureHotPathCompressionSettings(
        StorageCompressionSettings? requestedSettings)
    {
        StorageCompressionSettings settings = requestedSettings?.Clone() ?? new StorageCompressionSettings();
        settings.Normalize();
        if (!settings.Enabled)
        {
            return settings;
        }

        return new StorageCompressionSettings
        {
            Enabled = false,
            Algorithm = CompressionType.None,
            Preprocess = PreprocessType.None,
            Options = settings.Options.Clone()
        };
    }

    public bool TryEnqueue(SdkRawBlock rawBlock)
    {
        if (!_started || _writerFault != null || ProtectionTriggered)
        {
            Interlocked.Increment(ref _rejectedBlockCount);
            LogDiagnostic(
                $"enqueue-rejected started={_started} writerFault={_writerFault?.Message ?? ""} protection={ProtectionTriggered} blockDevice={SdkRawCaptureWriter.ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId)} payloadBytes={rawBlock.PayloadBytes:N0}");
            rawBlock.ReleasePayload();
            return false;
        }

        int sourceId = SdkRawCaptureWriter.ResolveChannelDeviceId(rawBlock.GroupId, rawBlock.MachineId);
        ObserveSourceRawTiming(sourceId, rawBlock);
        SourceTdmsWriter? sourceWriter = GetSourceWriterForBlock(sourceId, rawBlock.ChannelCount);
        if (sourceWriter == null)
        {
            rawBlock.ReleasePayload();
            return true;
        }

        if (!sourceWriter.TryEnqueue(rawBlock))
        {
            Interlocked.Increment(ref _rejectedBlockCount);
            rawBlock.ReleasePayload();
            return false;
        }

        long enqueued = Interlocked.Increment(ref _enqueuedBlockCount);
        long pendingBlockCount = Interlocked.Increment(ref _pendingBlockCount);
        long pendingPayloadBytes = Interlocked.Add(ref _pendingPayloadBytes, rawBlock.PayloadBytes);
        UpdatePeak(ref _peakPendingBlockCount, pendingBlockCount);
        UpdatePeak(ref _peakPendingPayloadBytes, pendingPayloadBytes);

        if (enqueued <= 20 || enqueued % 50 == 0)
        {
            LogDiagnostic(
                $"enqueue block={enqueued:N0} source={sourceId} sdkBlock={rawBlock.BlockIndex} samplesPerChannel={rawBlock.DataCountPerChannel:N0} channelCount={rawBlock.ChannelCount:N0} payloadBytes={rawBlock.PayloadBytes:N0} pendingBlocks={pendingBlockCount:N0} pendingBytes={pendingPayloadBytes:N0} sourcePendingBlocks={sourceWriter.PendingBlocks:N0} sourcePendingBytes={sourceWriter.PendingPayloadBytes:N0}");
        }

        if (pendingBlockCount > MaxPendingBlockLimit || pendingPayloadBytes > MaxPendingPayloadByteLimit)
        {
            TriggerProtection(
                $"Pending TDMS capture queues exceeded hard limit ({pendingBlockCount:N0}/{MaxPendingBlockLimit:N0} blocks, {pendingPayloadBytes:N0}/{MaxPendingPayloadByteLimit:N0} bytes). {BuildThroughputSummary()}");
        }
        else if (sourceWriter.PendingBlocks > MaxSourcePendingBlockLimit || sourceWriter.PendingPayloadBytes > MaxSourcePendingPayloadByteLimit)
        {
            TriggerProtection(
                $"Pending TDMS source queue exceeded hard limit (source={sourceId}, {sourceWriter.PendingBlocks:N0}/{MaxSourcePendingBlockLimit:N0} blocks, {sourceWriter.PendingPayloadBytes:N0}/{MaxSourcePendingPayloadByteLimit:N0} bytes). {BuildThroughputSummary()}");
        }
        else if (Interlocked.Read(ref _pendingSegmentCount) > MaxPendingSegmentLimit
            || Interlocked.Read(ref _pendingSegmentPayloadBytes) > MaxPendingSegmentPayloadByteLimit)
        {
            TriggerProtection(
                $"Pending TDMS segment writer queue exceeded hard limit ({Interlocked.Read(ref _pendingSegmentCount):N0}/{MaxPendingSegmentLimit:N0} segments, {Interlocked.Read(ref _pendingSegmentPayloadBytes):N0}/{MaxPendingSegmentPayloadByteLimit:N0} bytes). {BuildThroughputSummary()}");
        }

        return true;
    }

    public SdkRawCaptureWriterStatistics GetStatistics()
    {
        return new SdkRawCaptureWriterStatistics
        {
            CapturePath = _sessionFolder ?? "",
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
            WrittenPayloadBytes = Interlocked.Read(ref _totalSamples) * sizeof(float),
            WriteSeconds = GetWriteSeconds(),
            LastError = _writerFault?.Message ?? _protectionReason
        };
    }

    public (long MinSamplesPerChannel, long MaxSamplesPerChannel, int SourceCount) GetCurrentSourceSampleRange()
    {
        IReadOnlyList<TdmsSourceSampleCount> sourceCounts = BuildCurrentSourceSampleCounts();
        if (sourceCounts.Count == 0)
        {
            return (0L, 0L, 0);
        }

        return (
            sourceCounts.Min(static source => source.SamplesPerChannel),
            sourceCounts.Max(static source => source.SamplesPerChannel),
            sourceCounts.Count);
    }

    public bool WaitForMinimumSourceSamples(long targetSamplesPerChannel, TimeSpan maxWait)
    {
        if (targetSamplesPerChannel <= 0)
        {
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < maxWait && _started && _writerFault == null && !ProtectionTriggered)
        {
            var range = GetCurrentSourceSampleRange();
            if (range.SourceCount > 0 && range.MinSamplesPerChannel >= targetSamplesPerChannel)
            {
                LogDiagnostic(
                    $"stop-alignment-wait-reached targetSamples={targetSamplesPerChannel:N0} minSamples={range.MinSamplesPerChannel:N0} maxSamples={range.MaxSamplesPerChannel:N0} sourceCount={range.SourceCount:N0} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
                return true;
            }

            Thread.Sleep(25);
        }

        var finalRange = GetCurrentSourceSampleRange();
        LogDiagnostic(
            $"stop-alignment-wait-finished targetSamples={targetSamplesPerChannel:N0} reached={finalRange.MinSamplesPerChannel >= targetSamplesPerChannel} minSamples={finalRange.MinSamplesPerChannel:N0} maxSamples={finalRange.MaxSamplesPerChannel:N0} sourceCount={finalRange.SourceCount:N0} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3}");
        return finalRange.MinSamplesPerChannel >= targetSamplesPerChannel;
    }

    public SdkRawCaptureResult Complete()
    {
        if (!_started)
        {
            return new SdkRawCaptureResult();
        }

        LogDiagnostic($"complete-begin {BuildThroughputSummary()} pendingBlocks={Interlocked.Read(ref _pendingBlockCount):N0} pendingBytes={Interlocked.Read(ref _pendingPayloadBytes):N0}");

        TimeSpan sourceWriterDrainElapsed = TimeSpan.Zero;
        TimeSpan segmentWriterDrainElapsed = TimeSpan.Zero;
        TimeSpan backgroundCompressionDrainElapsed = TimeSpan.Zero;
        var waitStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var writer in _sourceWriters.Values.OrderBy(static writer => writer.SourceId))
            {
                try
                {
                    writer.Complete();
                }
                catch (Exception ex)
                {
                    _writerFault ??= ex;
                    Interlocked.Increment(ref _writeFaultCount);
                    LogDiagnostic($"complete-source-writer-fault source={writer.SourceId} error={ex}");
                }
            }

            waitStopwatch.Stop();
            sourceWriterDrainElapsed = waitStopwatch.Elapsed;
            LogDiagnostic($"complete-source-writers-finished elapsedMs={waitStopwatch.Elapsed.TotalMilliseconds:F3} {BuildThroughputSummary()}");
        }
        catch (Exception ex)
        {
            _writerFault ??= ex;
            Interlocked.Increment(ref _writeFaultCount);
            LogDiagnostic($"complete-source-writers-fault error={ex}");
        }
        finally
        {
            waitStopwatch.Restart();
            try
            {
                _segmentWriteQueue.Writer.TryComplete();
                try
                {
                    Task.WaitAll(_segmentWriterTasks.ToArray());
                }
                catch (Exception ex)
                {
                    _writerFault ??= ex;
                    Interlocked.Increment(ref _writeFaultCount);
                    LogDiagnostic($"complete-segment-writers-fault error={ex}");
                }

                waitStopwatch.Stop();
                segmentWriterDrainElapsed = waitStopwatch.Elapsed;
                LogDiagnostic($"complete-segment-writers-finished elapsedMs={waitStopwatch.Elapsed.TotalMilliseconds:F3} {BuildThroughputSummary()} pendingSegments={Interlocked.Read(ref _pendingSegmentCount):N0} pendingSegmentBytes={Interlocked.Read(ref _pendingSegmentPayloadBytes):N0}");
                if (_stoppedAtUtc == default)
                {
                    _stoppedAtUtc = DateTime.UtcNow;
                }

                waitStopwatch.Restart();
                try
                {
                    Volatile.Write(ref _backgroundCompressionDrainEnabled, 1);
                    _backgroundCompressionQueue.Writer.TryComplete();
                    try
                    {
                        Task.WaitAll(_backgroundCompressionTasks.ToArray());
                    }
                    catch (Exception ex)
                    {
                        _backgroundCompressionFault ??= ex;
                        Interlocked.Increment(ref _backgroundCompressionFaultCount);
                        LogDiagnostic($"complete-background-compression-fault error={ex}");
                    }

                    waitStopwatch.Stop();
                    backgroundCompressionDrainElapsed = waitStopwatch.Elapsed;
                    LogDiagnostic(
                        $"complete-background-compression-finished elapsedMs={waitStopwatch.Elapsed.TotalMilliseconds:F3} compressedSegments={Interlocked.Read(ref _backgroundCompressionSegmentCount):N0} compressionFaults={Interlocked.Read(ref _backgroundCompressionFaultCount):N0} pendingCompression={Interlocked.Read(ref _pendingBackgroundCompressionCount):N0} pendingCompressionBytes={Interlocked.Read(ref _pendingBackgroundCompressionPayloadBytes):N0} compressedFiles={GetCompressedFiles().Count:N0}");
                }
                finally
                {
                    waitStopwatch.Restart();
                }

                IReadOnlyList<string> completedPreviewFiles = CompletePreviewWriter();
                lock (_resultLock)
                {
                    _previewFiles.Clear();
                    _previewFiles.AddRange(completedPreviewFiles);
                }
            }
            finally
            {
                _previewWriter = null;
            }
            if (_stoppedAtUtc == default)
            {
                _stoppedAtUtc = DateTime.UtcNow;
            }

            LogCaptureSummary(sourceWriterDrainElapsed, segmentWriterDrainElapsed, backgroundCompressionDrainElapsed);
            _started = false;
        }

        IReadOnlyList<string> writtenFiles = GetWrittenFiles();
        IReadOnlyList<string> compressedFiles = GetCompressedFiles();
        IReadOnlyList<string> previewFiles = GetPreviewFiles();
        IReadOnlyDictionary<string, long> sampleCounts = BuildSampleCounts();

        try
        {
            WriteSessionManifest(writtenFiles, previewFiles, compressedFiles);
        }
        catch (Exception ex)
        {
            LogDiagnostic($"complete-session-manifest-failed error={ex}");
        }

        var manifest = BuildManifest(writtenFiles);
        LogDiagnostic($"complete-finished integrity={manifest.DataIntegrityPassed} protection={manifest.ProtectionTriggered} rejected={manifest.RejectedBlockCount:N0} faults={manifest.WriteFaultCount:N0} captureBytes={manifest.CaptureFileBytes:N0} files={writtenFiles.Count:N0} compressedFiles={compressedFiles.Count:N0}");
        return new SdkRawCaptureResult
        {
            WrittenFiles = writtenFiles,
            SampleCounts = sampleCounts,
            Statistics = GetStatistics(),
            Manifest = manifest
        };
    }

    private IReadOnlyList<string> CompletePreviewWriter()
    {
        PreviewSidecarWriter? previewWriter = _previewWriter;
        if (previewWriter is null)
        {
            return Array.Empty<string>();
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            PreviewSidecarArtifacts artifacts = previewWriter.Complete();
            string[] previewFiles = artifacts.DataFilePaths
                .Concat(new[] { artifacts.IndexManifestPath })
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            stopwatch.Stop();
            LogDiagnostic(
                $"preview-sidecar-complete files={previewFiles.Length:N0} elapsedMs={stopwatch.Elapsed.TotalMilliseconds:F3} previewSegments={Interlocked.Read(ref _previewSegmentCount):N0} previewPayloadBytes={Interlocked.Read(ref _previewPayloadBytes):N0} avgPreviewMs={GetPreviewAverageMilliseconds():F3} root={artifacts.RootPath}");
            return previewFiles;
        }
        catch (Exception ex)
        {
            _writerFault ??= ex;
            Interlocked.Increment(ref _writeFaultCount);
            LogDiagnostic($"preview-sidecar-complete-failed error={ex}");
            TriggerProtection($"Preview sidecar failed: {ex.Message}");
            return Array.Empty<string>();
        }
        finally
        {
            previewWriter.Dispose();
            _previewWriter = null;
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            Complete();
        }

        foreach (var writer in _sourceWriters.Values)
        {
            writer.Dispose();
        }

        _segmentWriteQueue.Writer.TryComplete();
        _backgroundCompressionQueue.Writer.TryComplete();
        _previewWriter?.Dispose();
        _previewWriter = null;
        try
        {
            Task.WaitAll(_segmentWriterTasks.ToArray(), TimeSpan.FromSeconds(5));
            Task.WaitAll(_backgroundCompressionTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        _sourceWriters.Clear();
        _segmentWriterTasks.Clear();
        _backgroundCompressionTasks.Clear();
    }

    private SourceTdmsWriter? GetSourceWriterForBlock(int sourceId, int channelCount)
    {
        if (_sourceWriters.TryGetValue(sourceId, out var writer))
        {
            return writer;
        }

        if (_expectedChannelIds.Count > 0)
        {
            return null;
        }

        int[] allChannels = Enumerable
            .Range(1, Math.Max(channelCount, 0))
            .Select(channelNumber => ChannelNaming.MakeChannelId(sourceId, channelNumber))
            .ToArray();
        return CreateSourceWriter(sourceId, allChannels);
    }

    private SourceTdmsWriter CreateSourceWriter(int sourceId, IReadOnlyCollection<int> channelIds)
    {
        if (_rawFolder == null)
        {
            throw new InvalidOperationException("TDMS raw folder is not initialized.");
        }

        return _sourceWriters.GetOrAdd(
            sourceId,
            id =>
            {
                var writer = new SourceTdmsWriter(
                    this,
                    id,
                    _rawFolder,
                    _sampleRateHz,
                    channelIds);
                writer.Start();
                LogDiagnostic($"source-writer-started source={id} channels={channelIds.Count:N0}");
                return writer;
            });
    }

    private static Channel<PendingTdmsSegment> CreateSegmentWriteQueue()
        => Channel.CreateUnbounded<PendingTdmsSegment>(
            new UnboundedChannelOptions
            {
                SingleReader = TdmsSegmentWriterCount == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    private static Channel<BackgroundTdmsSegmentCompressionJob> CreateBackgroundCompressionQueue()
        => Channel.CreateUnbounded<BackgroundTdmsSegmentCompressionJob>(
            new UnboundedChannelOptions
            {
                SingleReader = BackgroundCompressionWorkerCount == 1,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

    private bool TryEnqueueSegment(PendingTdmsSegment segment)
    {
        if (ProtectionTriggered || _writerFault != null)
        {
            segment.Dispose();
            return false;
        }

        if (!_segmentWriteQueue.Writer.TryWrite(segment))
        {
            segment.Dispose();
            return false;
        }

        long pendingSegments = Interlocked.Increment(ref _pendingSegmentCount);
        long pendingBytes = Interlocked.Add(ref _pendingSegmentPayloadBytes, segment.PayloadBytes);
        UpdatePeak(ref _peakPendingSegmentCount, pendingSegments);
        UpdatePeak(ref _peakPendingSegmentPayloadBytes, pendingBytes);

        if (pendingSegments <= 10 || pendingSegments % 10 == 0)
        {
            LogDiagnostic(
                $"tdms-segment-enqueued source={segment.SourceId} segment={segment.SegmentIndex:N0} channels={segment.ChannelIds.Length:N0} samplesPerChannel={segment.SamplesPerChannel:N0} payloadBytes={segment.PayloadBytes:N0} pendingSegments={pendingSegments:N0} pendingSegmentBytes={pendingBytes:N0}");
        }
        LogQueueStatusIfDue("enqueue");

        if (pendingSegments > MaxPendingSegmentLimit || pendingBytes > MaxPendingSegmentPayloadByteLimit)
        {
            TriggerProtection(
                $"Pending TDMS segment writer queue exceeded hard limit ({pendingSegments:N0}/{MaxPendingSegmentLimit:N0} segments, {pendingBytes:N0}/{MaxPendingSegmentPayloadByteLimit:N0} bytes). {BuildThroughputSummary()}");
        }

        return true;
    }

    private async Task ProcessSegmentQueueAsync(int workerId)
    {
        var reader = _segmentWriteQueue.Reader;
        var writer = new ManualTdmsSourceSegmentFileWriter();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var segment))
                {
                    Interlocked.Decrement(ref _pendingSegmentCount);
                    Interlocked.Add(ref _pendingSegmentPayloadBytes, -segment.PayloadBytes);
                    try
                    {
                        WriteSegment(workerId, writer, segment);
                    }
                    catch (Exception ex)
                    {
                        _writerFault ??= ex;
                        Interlocked.Increment(ref _writeFaultCount);
                        LogDiagnostic($"tdms-segment-writer-failed worker={workerId:N0} source={segment.SourceId} segment={segment.SegmentIndex:N0} error={ex}");
                        TriggerProtection($"TDMS segment writer failed: {ex.Message}");
                        segment.Dispose();
                        DrainSegmentQueue(reader);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _writerFault ??= ex;
            Interlocked.Increment(ref _writeFaultCount);
            LogDiagnostic($"tdms-segment-writer-loop-failed worker={workerId:N0} error={ex}");
            TriggerProtection($"TDMS segment writer loop failed: {ex.Message}");
        }
    }

    private void WriteSegment(int workerId, ManualTdmsSourceSegmentFileWriter writer, PendingTdmsSegment segment)
    {
        try
        {
            var request = new TdmsSourceSegmentWriteRequest
            {
                FilePath = segment.FilePath,
                SourceId = segment.SourceId,
                SegmentIndex = segment.SegmentIndex,
                SampleRateHz = _sampleRateHz,
                SamplesPerChannel = segment.SamplesPerChannel,
                ChannelIds = segment.ChannelIds,
                GetSamples = segment.GetSamples,
                Overwrite = true,
                CompressionSettings = _compressionSettings
            };

            TdmsSourceSegmentWriteResult result = writer.Write(request);
            WriteSegmentPreview(segment);
            if (segment.IsPartial)
            {
                Interlocked.Increment(ref _tdmsPartialSegmentCount);
            }
            else
            {
                Interlocked.Increment(ref _tdmsFullSegmentCount);
            }

            long count = Interlocked.Read(ref _tdmsFullSegmentCount) + Interlocked.Read(ref _tdmsPartialSegmentCount);

            Interlocked.Add(ref _tdmsSegmentPayloadBytes, result.PayloadBytes);
            Interlocked.Add(ref _tdmsSegmentFileBytes, result.FileBytes);
            Interlocked.Add(ref _tdmsSegmentWriteTicks, result.TotalElapsed.Ticks);
            Interlocked.Add(ref _tdmsSegmentAppendTicks, result.AppendElapsed.Ticks);
            Interlocked.Add(ref _tdmsSegmentSaveTicks, result.SaveElapsed.Ticks);
            Interlocked.Add(ref _tdmsSegmentCloseTicks, result.CloseElapsed.Ticks);

            lock (_resultLock)
            {
                if (File.Exists(result.FilePath))
                {
                    _writtenFiles.Add(result.FilePath);
                }

                _tdmsSegments.Add(segment.ToManifestEntry(result));
            }

            TryEnqueueBackgroundCompression(segment);

            if (count <= 20 || count % 20 == 0 || result.TotalElapsed.TotalMilliseconds >= 1000)
            {
                LogDiagnostic(
                    $"tdms-segment-written worker={workerId:N0} source={result.SourceId} segment={result.SegmentIndex:N0} channels={result.ChannelCount:N0} samplesPerChannel={result.SamplesPerChannel:N0} payloadBytes={result.PayloadBytes:N0} codecPayloadBytes={result.CodecPayloadBytes:N0} fileBytes={result.FileBytes:N0} compression={result.CompressionType}/{result.PreprocessType} totalMs={result.TotalElapsed.TotalMilliseconds:F3} appendMs={result.AppendElapsed.TotalMilliseconds:F3} saveMs={result.SaveElapsed.TotalMilliseconds:F3} closeMs={result.CloseElapsed.TotalMilliseconds:F3} previewAvgMs={GetPreviewAverageMilliseconds():F3} pendingSegments={Interlocked.Read(ref _pendingSegmentCount):N0} pendingSegmentBytes={Interlocked.Read(ref _pendingSegmentPayloadBytes):N0}");
            }
            LogQueueStatusIfDue("write");
        }
        finally
        {
            segment.Dispose();
        }
    }

    private void TryEnqueueBackgroundCompression(PendingTdmsSegment segment)
    {
        if (!ShouldRunBackgroundCompression
            || string.IsNullOrWhiteSpace(_compressedFolder)
            || !File.Exists(segment.FilePath))
        {
            return;
        }

        string compressedPath = Path.Combine(
            _compressedFolder,
            $"source_{segment.SourceId:D4}_seg{segment.SegmentIndex:D6}.tdms");
        var job = new BackgroundTdmsSegmentCompressionJob
        {
            RawFilePath = segment.FilePath,
            CompressedFilePath = compressedPath,
            SourceId = segment.SourceId,
            SegmentIndex = segment.SegmentIndex,
            StartSample = segment.StartSample,
            SampleRateHz = segment.SampleRateHz,
            ChannelIds = segment.ChannelIds,
            SamplesPerChannel = segment.SamplesPerChannel,
            PayloadBytes = segment.PayloadBytes,
            IsPartial = segment.IsPartial
        };

        if (!_backgroundCompressionQueue.Writer.TryWrite(job))
        {
            Interlocked.Increment(ref _backgroundCompressionFaultCount);
            LogDiagnostic(
                $"background-compression-enqueue-failed source={segment.SourceId} segment={segment.SegmentIndex:N0} path={compressedPath}");
            return;
        }

        long pendingCount = Interlocked.Increment(ref _pendingBackgroundCompressionCount);
        long pendingBytes = Interlocked.Add(ref _pendingBackgroundCompressionPayloadBytes, segment.PayloadBytes);
        UpdatePeak(ref _peakPendingBackgroundCompressionCount, pendingCount);
        UpdatePeak(ref _peakPendingBackgroundCompressionPayloadBytes, pendingBytes);

        if (pendingCount <= 10 || pendingCount % 20 == 0)
        {
            LogDiagnostic(
                $"background-compression-enqueued source={segment.SourceId} segment={segment.SegmentIndex:N0} payloadBytes={segment.PayloadBytes:N0} pendingCompression={pendingCount:N0} pendingCompressionBytes={pendingBytes:N0}");
        }
    }

    private async Task ProcessBackgroundCompressionQueueAsync(int workerId)
    {
        var reader = _backgroundCompressionQueue.Reader;
        var compressor = new BackgroundTdmsSegmentCompressor();
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (Volatile.Read(ref _backgroundCompressionDrainEnabled) == 0)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                }

                while (reader.TryRead(out var job))
                {
                    Interlocked.Decrement(ref _pendingBackgroundCompressionCount);
                    Interlocked.Add(ref _pendingBackgroundCompressionPayloadBytes, -job.PayloadBytes);
                    try
                    {
                        CompressSegment(workerId, compressor, job);
                    }
                    catch (Exception ex)
                    {
                        _backgroundCompressionFault ??= ex;
                        Interlocked.Increment(ref _backgroundCompressionFaultCount);
                        LogDiagnostic(
                            $"background-compression-failed worker={workerId:N0} source={job.SourceId} segment={job.SegmentIndex:N0} raw={job.RawFilePath} error={ex}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _backgroundCompressionFault ??= ex;
            Interlocked.Increment(ref _backgroundCompressionFaultCount);
            LogDiagnostic($"background-compression-loop-failed worker={workerId:N0} error={ex}");
        }
    }

    private void CompressSegment(
        int workerId,
        BackgroundTdmsSegmentCompressor compressor,
        BackgroundTdmsSegmentCompressionJob job)
    {
        BackgroundTdmsSegmentCompressionResult compressionResult = compressor.Compress(
            job,
            _backgroundCompressionSettings);
        TdmsSourceSegmentWriteResult result = compressionResult.WriteResult;
        long count = Interlocked.Increment(ref _backgroundCompressionSegmentCount);
        Interlocked.Add(ref _backgroundCompressionPayloadBytes, result.CodecPayloadBytes);
        Interlocked.Add(ref _backgroundCompressionFileBytes, result.FileBytes);
        Interlocked.Add(ref _backgroundCompressionTicks, compressionResult.TotalElapsed.Ticks);
        Interlocked.Add(ref _backgroundCompressionReadTicks, compressionResult.ReadElapsed.Ticks);

        lock (_resultLock)
        {
            if (File.Exists(result.FilePath))
            {
                _compressedFiles.Add(result.FilePath);
            }

            _compressedTdmsSegments.Add(new TdmsSegmentManifestEntry
            {
                Path = result.FilePath,
                SourceId = result.SourceId,
                SegmentIndex = result.SegmentIndex,
                StartSample = compressionResult.StartSample,
                SamplesPerChannel = result.SamplesPerChannel,
                SampleRateHz = compressionResult.SampleRateHz,
                ChannelIds = compressionResult.ChannelIds,
                CompressionEnabled = result.CompressionEnabled,
                CompressionType = result.CompressionType.ToString(),
                PreprocessType = result.PreprocessType.ToString(),
                CompressionOriginalPayloadBytes = result.PayloadBytes,
                CompressionPayloadBytes = result.CodecPayloadBytes,
                ChannelPayloadBytes = result.ChannelPayloadBytes
            });
        }

        if (count <= 20 || count % 20 == 0 || compressionResult.TotalElapsed.TotalMilliseconds >= 1000)
        {
            LogDiagnostic(
                $"background-compression-written worker={workerId:N0} source={result.SourceId} segment={result.SegmentIndex:N0} channels={result.ChannelCount:N0} samplesPerChannel={result.SamplesPerChannel:N0} rawPayloadBytes={result.PayloadBytes:N0} compressedPayloadBytes={result.CodecPayloadBytes:N0} fileBytes={result.FileBytes:N0} compression={result.CompressionType}/{result.PreprocessType} totalMs={compressionResult.TotalElapsed.TotalMilliseconds:F3} readMs={compressionResult.ReadElapsed.TotalMilliseconds:F3} writeMs={result.TotalElapsed.TotalMilliseconds:F3} pendingCompression={Interlocked.Read(ref _pendingBackgroundCompressionCount):N0} pendingCompressionBytes={Interlocked.Read(ref _pendingBackgroundCompressionPayloadBytes):N0}");
        }
    }

    private void WriteSegmentPreview(PendingTdmsSegment segment)
    {
        if (!EnableCapturePreviewSidecar)
        {
            return;
        }

        PreviewSidecarWriter? previewWriter = _previewWriter;
        if (previewWriter is null || segment.ChannelIds.Length == 0 || segment.SamplesPerChannel <= 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        lock (_previewWriterLock)
        {
            foreach (int channelId in segment.ChannelIds.OrderBy(static id => id))
            {
                float[] samples = segment.GetSamples(channelId);
                int count = Math.Min(segment.SamplesPerChannel, samples.Length);
                if (count > 0)
                {
                    previewWriter.Write(channelId, samples.AsSpan(0, count));
                }
            }
        }

        stopwatch.Stop();
        Interlocked.Add(ref _previewWriteTicks, stopwatch.Elapsed.Ticks);
        Interlocked.Increment(ref _previewSegmentCount);
        Interlocked.Add(ref _previewPayloadBytes, segment.PayloadBytes);
    }

    private void DrainSegmentQueue(ChannelReader<PendingTdmsSegment> reader)
    {
        while (reader.TryRead(out var segment))
        {
            Interlocked.Decrement(ref _pendingSegmentCount);
            Interlocked.Add(ref _pendingSegmentPayloadBytes, -segment.PayloadBytes);
            segment.Dispose();
        }
    }

    private void OnSourceBlockDequeued(SdkRawBlock rawBlock)
    {
        Interlocked.Decrement(ref _pendingBlockCount);
        Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);
    }

    private void ObserveSourceRawTiming(int sourceId, SdkRawBlock rawBlock)
    {
        TdmsSourceRawTimingState state = _sourceRawTiming.GetOrAdd(
            sourceId,
            static id => new TdmsSourceRawTimingState(id));
        state.Observe(rawBlock);
    }

    private void OnSourceBlockWritten(
        int sourceId,
        int sdkBlockIndex,
        int selectedChannelCount,
        int sourceChannelCount,
        int samplesPerChannel,
        long payloadBytes,
        TimeSpan elapsed,
        TimeSpan deinterleaveElapsed,
        TimeSpan storageTotalElapsed,
        TimeSpan storageAppendElapsed,
        TimeSpan storageHashElapsed,
        TimeSpan storageEnsureElapsed,
        TimeSpan storageMetricsElapsed,
        int segmentIndex)
    {
        if (selectedChannelCount <= 0)
        {
            return;
        }

        long blockNumber = Interlocked.Increment(ref _writtenBlockCount);
        Interlocked.Add(ref _totalSamples, (long)selectedChannelCount * samplesPerChannel);
        UpdatePeak(ref _peakSourceBlockTicks, elapsed.Ticks);
        UpdatePeak(ref _peakSourceBlockDeinterleaveTicks, deinterleaveElapsed.Ticks);
        lock (_metricsLock)
        {
            _writeSeconds += elapsed.TotalSeconds;
        }

        if (blockNumber <= 60 || blockNumber % 50 == 0 || elapsed.TotalMilliseconds >= 100)
        {
            double totalMBps = elapsed.TotalSeconds > 0
                ? payloadBytes / 1024d / 1024d / elapsed.TotalSeconds
                : 0d;
            LogDiagnostic(
                $"write-block block={blockNumber:N0} source={sourceId} segment={segmentIndex:N0} sdkBlock={sdkBlockIndex} selectedChannels={selectedChannelCount:N0}/{sourceChannelCount:N0} samplesPerChannel={samplesPerChannel:N0} payloadBytes={payloadBytes:N0} totalMs={elapsed.TotalMilliseconds:F3} deinterleaveMs={deinterleaveElapsed.TotalMilliseconds:F3} storageTotalMs={storageTotalElapsed.TotalMilliseconds:F3} ddcAppendMs={storageAppendElapsed.TotalMilliseconds:F3} hashMs={storageHashElapsed.TotalMilliseconds:F3} ensureMs={storageEnsureElapsed.TotalMilliseconds:F3} metricsMs={storageMetricsElapsed.TotalMilliseconds:F3} blockMBps={totalMBps:F1} pendingBlocks={Interlocked.Read(ref _pendingBlockCount):N0} pendingBytes={Interlocked.Read(ref _pendingPayloadBytes):N0}");
        }
        if (elapsed.TotalSeconds >= 1d)
        {
            LogDiagnostic(
                $"source-block-stall block={blockNumber:N0} source={sourceId} segment={segmentIndex:N0} totalMs={elapsed.TotalMilliseconds:F3} deinterleaveMs={deinterleaveElapsed.TotalMilliseconds:F3} pendingBlocks={Interlocked.Read(ref _pendingBlockCount):N0} pendingBytes={Interlocked.Read(ref _pendingPayloadBytes):N0} pendingSegments={Interlocked.Read(ref _pendingSegmentCount):N0} pendingSegmentBytes={Interlocked.Read(ref _pendingSegmentPayloadBytes):N0}");
        }
    }

    private void OnSourceChannelSamples(int channelId, int sampleCount)
    {
        while (true)
        {
            if (_channelSampleCounts.TryGetValue(channelId, out long current))
            {
                if (_channelSampleCounts.TryUpdate(channelId, current + sampleCount, current))
                {
                    return;
                }
            }
            else if (_channelSampleCounts.TryAdd(channelId, sampleCount))
            {
                return;
            }
        }
    }

    private void OnSourceFault(int sourceId, Exception ex)
    {
        _writerFault ??= ex;
        Interlocked.Increment(ref _writeFaultCount);
        Console.WriteLine($"[SdkTdmsCapture] Source {sourceId} 写入失败: {ex.Message}");
        LogDiagnostic($"source-writer-failed source={sourceId} error={ex}");
        TriggerProtection($"TDMS source writer failed, source={sourceId}: {ex.Message}");
    }

    private void LogCaptureSummary(
        TimeSpan sourceWriterDrainElapsed,
        TimeSpan segmentWriterDrainElapsed,
        TimeSpan backgroundCompressionDrainElapsed)
    {
        long payloadBytes = Interlocked.Read(ref _tdmsSegmentPayloadBytes);
        long fileBytes = Interlocked.Read(ref _tdmsSegmentFileBytes);
        long writeTicks = Interlocked.Read(ref _tdmsSegmentWriteTicks);
        long appendTicks = Interlocked.Read(ref _tdmsSegmentAppendTicks);
        long saveTicks = Interlocked.Read(ref _tdmsSegmentSaveTicks);
        long closeTicks = Interlocked.Read(ref _tdmsSegmentCloseTicks);
        long previewTicks = Interlocked.Read(ref _previewWriteTicks);
        long previewSegmentCount = Interlocked.Read(ref _previewSegmentCount);
        long previewPayloadBytes = Interlocked.Read(ref _previewPayloadBytes);
        long compressionTicks = Interlocked.Read(ref _backgroundCompressionTicks);
        long compressionReadTicks = Interlocked.Read(ref _backgroundCompressionReadTicks);
        long compressionSegmentCount = Interlocked.Read(ref _backgroundCompressionSegmentCount);
        long compressionPayloadBytes = Interlocked.Read(ref _backgroundCompressionPayloadBytes);
        long compressionFileBytes = Interlocked.Read(ref _backgroundCompressionFileBytes);
        long segmentCount = Interlocked.Read(ref _tdmsFullSegmentCount) + Interlocked.Read(ref _tdmsPartialSegmentCount);
        DateTime stoppedAtUtc = _stoppedAtUtc == default ? DateTime.UtcNow : _stoppedAtUtc;
        double captureSeconds = Math.Max((stoppedAtUtc - _startedAtUtc).TotalSeconds, 0.001);
        double tdmsWriteSeconds = Math.Max(TimeSpan.FromTicks(writeTicks).TotalSeconds, 0.001);
        double payloadMiB = payloadBytes / 1024d / 1024d;
        double compressionRatio = payloadBytes > 0 && compressionPayloadBytes > 0
            ? (double)compressionPayloadBytes / payloadBytes
            : 0d;

        LogDiagnostic(
            $"complete-tdms-summary files={segmentCount:N0} fullSegments={Interlocked.Read(ref _tdmsFullSegmentCount):N0} partialSegments={Interlocked.Read(ref _tdmsPartialSegmentCount):N0} payloadBytes={payloadBytes:N0} fileBytes={fileBytes:N0} captureElapsedMs={captureSeconds * 1000d:F3} sourceDrainMs={sourceWriterDrainElapsed.TotalMilliseconds:F3} segmentDrainMs={segmentWriterDrainElapsed.TotalMilliseconds:F3} tdmsWriteMsSum={TimeSpan.FromTicks(writeTicks).TotalMilliseconds:F3} appendMsSum={TimeSpan.FromTicks(appendTicks).TotalMilliseconds:F3} saveMsSum={TimeSpan.FromTicks(saveTicks).TotalMilliseconds:F3} closeMsSum={TimeSpan.FromTicks(closeTicks).TotalMilliseconds:F3} previewSegments={previewSegmentCount:N0} previewPayloadBytes={previewPayloadBytes:N0} previewWriteMsSum={TimeSpan.FromTicks(previewTicks).TotalMilliseconds:F3} avgPreviewMs={GetPreviewAverageMilliseconds():F3} backgroundCompressionSegments={compressionSegmentCount:N0} backgroundCompressionFaults={Interlocked.Read(ref _backgroundCompressionFaultCount):N0} backgroundCompressionPayloadBytes={compressionPayloadBytes:N0} backgroundCompressionFileBytes={compressionFileBytes:N0} backgroundCompressionRatio={compressionRatio:F4} backgroundCompressionDrainMs={backgroundCompressionDrainElapsed.TotalMilliseconds:F3} backgroundCompressionMsSum={TimeSpan.FromTicks(compressionTicks).TotalMilliseconds:F3} backgroundCompressionReadMsSum={TimeSpan.FromTicks(compressionReadTicks).TotalMilliseconds:F3} capturePayloadMiBps={payloadMiB / captureSeconds:F1} tdmsWriterPayloadMiBpsSum={payloadMiB / tdmsWriteSeconds:F1} peakPendingBlocks={Interlocked.Read(ref _peakPendingBlockCount):N0} peakPendingBlockBytes={Interlocked.Read(ref _peakPendingPayloadBytes):N0} peakPendingSegments={Interlocked.Read(ref _peakPendingSegmentCount):N0} peakPendingSegmentBytes={Interlocked.Read(ref _peakPendingSegmentPayloadBytes):N0} peakPendingBackgroundCompression={Interlocked.Read(ref _peakPendingBackgroundCompressionCount):N0} peakPendingBackgroundCompressionBytes={Interlocked.Read(ref _peakPendingBackgroundCompressionPayloadBytes):N0} peakSourceBlockMs={TimeSpan.FromTicks(Interlocked.Read(ref _peakSourceBlockTicks)).TotalMilliseconds:F3} peakSourceBlockDeinterleaveMs={TimeSpan.FromTicks(Interlocked.Read(ref _peakSourceBlockDeinterleaveTicks)).TotalMilliseconds:F3}");

        foreach (var timing in BuildSourceTimingDiagnostics().OrderBy(static item => item.SourceId))
        {
            LogDiagnostic(
                $"source-timing source={timing.SourceId} blocks={timing.BlockCount:N0} samplesPerChannel={timing.SamplesPerChannel:N0} firstTotalData={timing.FirstTotalDataCount:N0} lastTotalData={timing.LastTotalDataCount:N0} firstTotalDataOffsetSamples={timing.FirstTotalDataOffsetSamples:N0} firstReceiveOffsetMs={timing.FirstReceiveOffsetMs:F3}");
        }
    }

    private void LogQueueStatusIfDue(string reason)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Read(ref _lastQueueStatusTicks);
        if (previous != 0)
        {
            double elapsedSinceLastSeconds = (now - previous) / (double)Stopwatch.Frequency;
            if (elapsedSinceLastSeconds < QueueStatusLogSeconds)
            {
                return;
            }
        }

        if (Interlocked.CompareExchange(ref _lastQueueStatusTicks, now, previous) != previous)
        {
            return;
        }

        long segmentCount = Interlocked.Read(ref _tdmsFullSegmentCount) + Interlocked.Read(ref _tdmsPartialSegmentCount);
        long payloadBytes = Interlocked.Read(ref _tdmsSegmentPayloadBytes);
        long writeTicks = Interlocked.Read(ref _tdmsSegmentWriteTicks);
        double captureSeconds = Math.Max((DateTime.UtcNow - _startedAtUtc).TotalSeconds, 0.001);
        double wallMiBps = payloadBytes / 1024d / 1024d / captureSeconds;
        double writerSeconds = Math.Max(TimeSpan.FromTicks(writeTicks).TotalSeconds, 0.001);
        double writerTimeMiBps = payloadBytes / 1024d / 1024d / writerSeconds;
        double avgSegmentMs = segmentCount > 0
            ? TimeSpan.FromTicks(writeTicks).TotalMilliseconds / segmentCount
            : 0d;

        LogDiagnostic(
            $"tdms-queue-status reason={reason} tdmsSegmentWriters={TdmsSegmentWriterCount:N0} writtenSegments={segmentCount:N0} pendingSegments={Interlocked.Read(ref _pendingSegmentCount):N0} pendingSegmentBytes={Interlocked.Read(ref _pendingSegmentPayloadBytes):N0} peakPendingSegments={Interlocked.Read(ref _peakPendingSegmentCount):N0} peakPendingSegmentBytes={Interlocked.Read(ref _peakPendingSegmentPayloadBytes):N0} pendingCompression={Interlocked.Read(ref _pendingBackgroundCompressionCount):N0} pendingCompressionBytes={Interlocked.Read(ref _pendingBackgroundCompressionPayloadBytes):N0} compressedSegments={Interlocked.Read(ref _backgroundCompressionSegmentCount):N0} compressionFaults={Interlocked.Read(ref _backgroundCompressionFaultCount):N0} pendingBlocks={Interlocked.Read(ref _pendingBlockCount):N0} pendingBytes={Interlocked.Read(ref _pendingPayloadBytes):N0} enqueuedBlocks={Interlocked.Read(ref _enqueuedBlockCount):N0} writtenBlocks={Interlocked.Read(ref _writtenBlockCount):N0} rejectedBlocks={Interlocked.Read(ref _rejectedBlockCount):N0} payloadBytes={payloadBytes:N0} avgSegmentMs={avgSegmentMs:F3} avgPreviewMs={GetPreviewAverageMilliseconds():F3} wallPayloadMiBps={wallMiBps:F1} writerTimePayloadMiBps={writerTimeMiBps:F1}");
    }

    private double GetPreviewAverageMilliseconds()
    {
        long segmentCount = Interlocked.Read(ref _previewSegmentCount);
        if (segmentCount <= 0)
        {
            return 0d;
        }

        return TimeSpan.FromTicks(Interlocked.Read(ref _previewWriteTicks)).TotalMilliseconds / segmentCount;
    }

    private IReadOnlyList<string> GetWrittenFiles()
    {
        lock (_resultLock)
        {
            return _writtenFiles
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private IReadOnlyList<string> GetCompressedFiles()
    {
        lock (_resultLock)
        {
            return _compressedFiles
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private IReadOnlyList<string> GetPreviewFiles()
    {
        lock (_resultLock)
        {
            return _previewFiles
                .Where(File.Exists)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private IReadOnlyList<TdmsSegmentManifestEntry> GetCompressedTdmsSegments()
    {
        lock (_resultLock)
        {
            return _compressedTdmsSegments
                .OrderBy(segment => segment.SourceId)
                .ThenBy(segment => segment.SegmentIndex)
                .ToArray();
        }
    }

    private IReadOnlyList<TdmsSourceTimingDiagnostic> BuildSourceTimingDiagnostics()
    {
        var snapshots = _sourceRawTiming.Values
            .Select(static state => state.Snapshot())
            .Where(static item => item.BlockCount > 0)
            .OrderBy(static item => item.SourceId)
            .ToArray();
        if (snapshots.Length == 0)
        {
            return Array.Empty<TdmsSourceTimingDiagnostic>();
        }

        long minFirstTotalData = snapshots.Min(static item => item.FirstTotalDataCount);
        DateTime minFirstReceive = snapshots.Min(static item => item.FirstReceivedAtUtc);
        return snapshots
            .Select(item => new TdmsSourceTimingDiagnostic
            {
                SourceId = item.SourceId,
                BlockCount = item.BlockCount,
                SamplesPerChannel = item.SamplesPerChannel,
                FirstTotalDataCount = item.FirstTotalDataCount,
                LastTotalDataCount = item.LastTotalDataCount,
                FirstTotalDataOffsetSamples = item.FirstTotalDataCount - minFirstTotalData,
                FirstReceiveOffsetMs = (item.FirstReceivedAtUtc - minFirstReceive).TotalMilliseconds,
                FirstReceivedAtUtc = item.FirstReceivedAtUtc,
                LastReceivedAtUtc = item.LastReceivedAtUtc
            })
            .ToArray();
    }

    private void WriteSessionManifest(
        IReadOnlyCollection<string> writtenFiles,
        IReadOnlyCollection<string> previewFiles,
        IReadOnlyCollection<string> compressedFiles)
    {
        if (string.IsNullOrWhiteSpace(_artifactRootPath))
        {
            return;
        }

        PersistedPreviewSessionManifestWriter.Write(
            _artifactRootPath,
            _sessionName,
            _sampleRateHz,
            _expectedChannelIds,
            writtenFiles,
            previewFiles,
            BuildManifest(writtenFiles),
            compressedFiles,
            GetCompressedTdmsSegments());
    }

    private IReadOnlyDictionary<string, long> BuildSampleCounts()
        => _channelSampleCounts
            .OrderBy(static kvp => kvp.Key)
            .ToDictionary(
                static kvp => ChannelNaming.ChannelName(kvp.Key),
                static kvp => kvp.Value,
                StringComparer.OrdinalIgnoreCase);

    private SdkRawCaptureManifest BuildManifest(IReadOnlyCollection<string> writtenFiles)
    {
        string firstFile = writtenFiles.FirstOrDefault() ?? string.Empty;
        var sampleCounts = BuildSampleCounts();
        var tdmsSegments = GetTdmsSegments().ToList();
        var sourceSampleCounts = BuildTdmsSourceSampleCounts(tdmsSegments);
        long minDeviceSamplesPerChannel = sourceSampleCounts.Count > 0
            ? sourceSampleCounts.Min(source => source.SamplesPerChannel)
            : 0L;
        long maxDeviceSamplesPerChannel = sourceSampleCounts.Count > 0
            ? sourceSampleCounts.Max(source => source.SamplesPerChannel)
            : 0L;
        bool deviceSampleCountsBalanced = sourceSampleCounts.Count <= 1
            || minDeviceSamplesPerChannel == maxDeviceSamplesPerChannel;
        int deviceIntegrityIssueCount = deviceSampleCountsBalanced
            ? 0
            : sourceSampleCounts.Count(source => source.SamplesPerChannel != maxDeviceSamplesPerChannel);
        double minSampleDerivedDurationSeconds = _sampleRateHz > 0
            ? minDeviceSamplesPerChannel / _sampleRateHz
            : 0d;
        double maxSampleDerivedDurationSeconds = _sampleRateHz > 0
            ? maxDeviceSamplesPerChannel / _sampleRateHz
            : 0d;
        DateTime stoppedAtUtc = _stoppedAtUtc == default ? DateTime.UtcNow : _stoppedAtUtc;
        double wallClockDurationSeconds = Math.Max(0d, (stoppedAtUtc - _startedAtUtc).TotalSeconds);
        bool runtimeHealthy = !ProtectionTriggered
            && _writerFault == null
            && Interlocked.Read(ref _writeFaultCount) == 0
            && Interlocked.Read(ref _rejectedBlockCount) == 0;

        var manifest = new SdkRawCaptureManifest
        {
            SessionName = _sessionName,
            CaptureFileName = string.IsNullOrWhiteSpace(firstFile) ? string.Empty : Path.GetFileName(firstFile),
            StartedAtUtc = _startedAtUtc,
            StoppedAtUtc = stoppedAtUtc,
            SampleRateHz = _sampleRateHz,
            ExpectedChannelCount = _expectedChannelIds.Count,
            ObservedChannelCount = _channelSampleCounts.Count,
            ObservedDeviceCount = sourceSampleCounts.Count,
            BlockCount = Interlocked.Read(ref _writtenBlockCount),
            TotalSamples = Interlocked.Read(ref _totalSamples),
            RawPayloadBytes = Interlocked.Read(ref _totalSamples) * sizeof(float),
            CaptureFileBytes = writtenFiles.Where(File.Exists).Sum(static path => new FileInfo(path).Length),
            WriteSeconds = GetWriteSeconds(),
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
            DataIntegrityPassed = runtimeHealthy && deviceSampleCountsBalanced,
            IntegritySummary = BuildTdmsIntegritySummary(sourceSampleCounts, deviceSampleCountsBalanced),
            DeviceIntegrityIssueCount = deviceIntegrityIssueCount,
            DeviceSampleCountsBalanced = deviceSampleCountsBalanced,
            MinDeviceSamplesPerChannel = minDeviceSamplesPerChannel,
            MaxDeviceSamplesPerChannel = maxDeviceSamplesPerChannel,
            WallClockDurationSeconds = wallClockDurationSeconds,
            MinSampleDerivedDurationSeconds = minSampleDerivedDurationSeconds,
            MaxSampleDerivedDurationSeconds = maxSampleDerivedDurationSeconds,
            MinEffectiveSampleRateHz = wallClockDurationSeconds > 0 ? minDeviceSamplesPerChannel / wallClockDurationSeconds : 0d,
            MaxEffectiveSampleRateHz = wallClockDurationSeconds > 0 ? maxDeviceSamplesPerChannel / wallClockDurationSeconds : 0d,
            SampleRateConsistencyPassed = deviceSampleCountsBalanced,
            SampleRateConsistencySummary = BuildTdmsSampleTimingSummary(
                wallClockDurationSeconds,
                minSampleDerivedDurationSeconds,
                maxSampleDerivedDurationSeconds),
            DeviceIntegrity = sourceSampleCounts
                .Select(source => new SdkRawCaptureDeviceIntegrity
                {
                    DeviceId = source.SourceId,
                    MachineId = source.SourceId,
                    ChannelCount = source.ChannelCount,
                    BlockCount = source.SegmentCount,
                    SamplesPerChannel = source.SamplesPerChannel,
                    HasIssues = !deviceSampleCountsBalanced && source.SamplesPerChannel != maxDeviceSamplesPerChannel,
                    IssueExamples = !deviceSampleCountsBalanced && source.SamplesPerChannel != maxDeviceSamplesPerChannel
                        ? new List<string> { $"tdms source ended {maxDeviceSamplesPerChannel - source.SamplesPerChannel:N0} samples before longest source" }
                        : new List<string>()
                })
                .ToList(),
            ChannelSampleCounts = new Dictionary<string, long>(sampleCounts, StringComparer.OrdinalIgnoreCase)
        };
        manifest.TdmsSegments = tdmsSegments;
        manifest.CompressedTdmsSegments = GetCompressedTdmsSegments().ToList();
        manifest.CompressedCaptureFileBytes = GetCompressedFiles().Where(File.Exists).Sum(static path => new FileInfo(path).Length);
        manifest.CompressedPayloadBytes = Interlocked.Read(ref _backgroundCompressionPayloadBytes);
        manifest.CompressionFaultCount = Interlocked.Read(ref _backgroundCompressionFaultCount);
        manifest.SourceTimingDiagnostics = BuildSourceTimingDiagnostics().ToList();
        return manifest;
    }

    private static IReadOnlyList<TdmsSourceSampleCount> BuildTdmsSourceSampleCounts(
        IReadOnlyCollection<TdmsSegmentManifestEntry> segments)
        => segments
            .GroupBy(static segment => segment.SourceId)
            .OrderBy(static group => group.Key)
            .Select(static group =>
            {
                long samplesPerChannel = group.Max(static segment => segment.EndSampleExclusive);
                int channelCount = group
                    .SelectMany(static segment => segment.ChannelIds)
                    .Distinct()
                    .Count();
                return new TdmsSourceSampleCount(
                    group.Key,
                    group.Count(),
                    channelCount,
                    samplesPerChannel);
            })
            .ToArray();

    private IReadOnlyList<TdmsSourceSampleCount> BuildCurrentSourceSampleCounts()
    {
        KeyValuePair<int, long>[] snapshot = _channelSampleCounts.ToArray();
        if (_expectedChannelIds.Count > 0)
        {
            return _expectedChannelIds
                .GroupBy(ChannelNaming.GetDeviceId)
                .OrderBy(static group => group.Key)
                .Select(group =>
                {
                    int[] channelIds = group.ToArray();
                    long samplesPerChannel = channelIds
                        .Select(channelId => snapshot.FirstOrDefault(item => item.Key == channelId).Value)
                        .DefaultIfEmpty(0L)
                        .Min();
                    return new TdmsSourceSampleCount(
                        group.Key,
                        0,
                        channelIds.Length,
                        samplesPerChannel);
                })
                .ToArray();
        }

        return snapshot
            .GroupBy(static item => ChannelNaming.GetDeviceId(item.Key))
            .OrderBy(static group => group.Key)
            .Select(static group => new TdmsSourceSampleCount(
                group.Key,
                0,
                group.Count(),
                group.Min(static item => item.Value)))
            .ToArray();
    }

    private static string BuildTdmsIntegritySummary(
        IReadOnlyList<TdmsSourceSampleCount> sources,
        bool deviceSampleCountsBalanced)
    {
        if (sources.Count == 0)
        {
            return "No TDMS source segment data recorded.";
        }

        if (deviceSampleCountsBalanced)
        {
            return "TDMS source segment files were written with balanced source durations.";
        }

        var minSource = sources
            .OrderBy(static source => source.SamplesPerChannel)
            .ThenBy(static source => source.SourceId)
            .First();
        var maxSource = sources
            .OrderByDescending(static source => source.SamplesPerChannel)
            .ThenBy(static source => source.SourceId)
            .First();
        long spread = maxSource.SamplesPerChannel - minSource.SamplesPerChannel;
        return $"TDMS source duration mismatch AI{minSource.SourceId:00}={minSource.SamplesPerChannel:N0} to AI{maxSource.SourceId:00}={maxSource.SamplesPerChannel:N0} samples/channel, spread={spread:N0}.";
    }

    private static string BuildTdmsSampleTimingSummary(
        double wallClockDurationSeconds,
        double minSampleDerivedDurationSeconds,
        double maxSampleDerivedDurationSeconds)
        => $"wallClock={wallClockDurationSeconds:N3}s, sampleDuration={FormatDoubleRange(minSampleDerivedDurationSeconds, maxSampleDerivedDurationSeconds, "N3")}s.";

    private static string FormatDoubleRange(double minValue, double maxValue, string format)
        => Math.Abs(minValue - maxValue) < 0.000001d
            ? minValue.ToString(format)
            : $"{minValue.ToString(format)} ~ {maxValue.ToString(format)}";

    private sealed record TdmsSourceSampleCount(
        int SourceId,
        int SegmentCount,
        int ChannelCount,
        long SamplesPerChannel);

    private IReadOnlyList<TdmsSegmentManifestEntry> GetTdmsSegments()
    {
        lock (_resultLock)
        {
            return _tdmsSegments
                .Where(segment => File.Exists(segment.Path))
                .OrderBy(segment => segment.SourceId)
                .ThenBy(segment => segment.SegmentIndex)
                .ToArray();
        }
    }

    private void TriggerProtection(string reason)
    {
        if (Interlocked.Exchange(ref _protectionTriggered, 1) == 0)
        {
            _protectionReason = reason;
            Console.WriteLine($"[SdkTdmsCapture] 触发保护: {reason}");
            LogDiagnostic($"protection-triggered reason={reason} pendingBlocks={Interlocked.Read(ref _pendingBlockCount):N0} pendingBytes={Interlocked.Read(ref _pendingPayloadBytes):N0} peakBlocks={Interlocked.Read(ref _peakPendingBlockCount):N0} peakBytes={Interlocked.Read(ref _peakPendingPayloadBytes):N0}");
        }
    }

    private string BuildThroughputSummary()
    {
        double writeSeconds = GetWriteSeconds();
        long writtenPayloadBytes = Interlocked.Read(ref _totalSamples) * sizeof(float);
        double writeMBps = writeSeconds > 0
            ? writtenPayloadBytes / 1024d / 1024d / writeSeconds
            : 0d;
        return $"enqueued={Interlocked.Read(ref _enqueuedBlockCount):N0}, written={Interlocked.Read(ref _writtenBlockCount):N0}, rejected={Interlocked.Read(ref _rejectedBlockCount):N0}, writeMBps={writeMBps:F1}";
    }

    private double GetWriteSeconds()
    {
        lock (_metricsLock)
        {
            return _writeSeconds;
        }
    }

    private static void UpdatePeak(ref long target, long value)
    {
        long current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private void ResetState()
    {
        _segmentWriteQueue.Writer.TryComplete();
        _backgroundCompressionQueue.Writer.TryComplete();
        try
        {
            Task.WaitAll(_segmentWriterTasks.ToArray(), TimeSpan.FromSeconds(5));
            Task.WaitAll(_backgroundCompressionTasks.ToArray(), TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        foreach (var writer in _sourceWriters.Values)
        {
            writer.Dispose();
        }

        _sourceWriters.Clear();
        _segmentWriterTasks.Clear();
        _backgroundCompressionTasks.Clear();
        _segmentWriteQueue = CreateSegmentWriteQueue();
        _backgroundCompressionQueue = CreateBackgroundCompressionQueue();
        _channelSampleCounts.Clear();
        _writtenFiles.Clear();
        _compressedFiles.Clear();
        _previewFiles.Clear();
        _tdmsSegments.Clear();
        _compressedTdmsSegments.Clear();
        _sourceRawTiming.Clear();
        _expectedChannelIds.Clear();
        _sessionFolder = null;
        _rawFolder = null;
        _compressedFolder = null;
        _artifactRootPath = null;
        _diagnosticLogPath = null;
        _performanceDiagnosticLogPath = null;
        _stoppedAtUtc = default;
        _enqueuedBlockCount = 0;
        _writtenBlockCount = 0;
        _rejectedBlockCount = 0;
        _writeFaultCount = 0;
        _pendingBlockCount = 0;
        _pendingPayloadBytes = 0;
        _peakPendingBlockCount = 0;
        _peakPendingPayloadBytes = 0;
        _totalSamples = 0;
        _tdmsSegmentPayloadBytes = 0;
        _tdmsSegmentFileBytes = 0;
        _tdmsSegmentWriteTicks = 0;
        _tdmsSegmentAppendTicks = 0;
        _tdmsSegmentSaveTicks = 0;
        _tdmsSegmentCloseTicks = 0;
        _previewWriteTicks = 0;
        _previewSegmentCount = 0;
        _previewPayloadBytes = 0;
        _backgroundCompressionSegmentCount = 0;
        _backgroundCompressionFaultCount = 0;
        _backgroundCompressionPayloadBytes = 0;
        _backgroundCompressionFileBytes = 0;
        _backgroundCompressionTicks = 0;
        _backgroundCompressionReadTicks = 0;
        _pendingBackgroundCompressionCount = 0;
        _pendingBackgroundCompressionPayloadBytes = 0;
        _peakPendingBackgroundCompressionCount = 0;
        _peakPendingBackgroundCompressionPayloadBytes = 0;
        _tdmsFullSegmentCount = 0;
        _tdmsPartialSegmentCount = 0;
        _pendingSegmentCount = 0;
        _pendingSegmentPayloadBytes = 0;
        _peakPendingSegmentCount = 0;
        _peakPendingSegmentPayloadBytes = 0;
        _peakSourceBlockTicks = 0;
        _peakSourceBlockDeinterleaveTicks = 0;
        _lastQueueStatusTicks = 0;
        _writeSeconds = 0d;
        _backgroundCompressionDrainEnabled = 0;
        _backgroundCompressionDuringCapture = false;
        _protectionTriggered = 0;
        _protectionReason = "";
        _writerFault = null;
        _backgroundCompressionFault = null;
        _previewWriter = null;
        _requestedCompressionSettings = new StorageCompressionSettings();
        _backgroundCompressionSettings = new StorageCompressionSettings();
        _compressionSettings = new StorageCompressionSettings();
    }

    private void WriteDiagnosticHeader(
        string basePath,
        int configuredChannelCount)
    {
        LogDiagnostic(
            $"writer-created format=tdms-source-segment-async session={_sessionName} basePath={basePath} sessionFolder={_sessionFolder} rawFolder={_rawFolder} compressedFolder={_compressedFolder} artifactRoot={_artifactRootPath} sampleRateHz={_sampleRateHz:F3} configuredChannels={configuredChannelCount:N0} hotPathHash={EnableHotPathWriteHash} compression={_compressionSettings.Describe()} backgroundCompression={_backgroundCompressionSettings.Describe()} backgroundCompressionEnabled={ShouldRunBackgroundCompression} backgroundCompressionDuringCapture={_backgroundCompressionDuringCapture} pendingLimits={MaxPendingBlockLimit:N0}blocks/{MaxPendingPayloadByteLimit:N0}bytes sourcePendingLimits={MaxSourcePendingBlockLimit:N0}blocks/{MaxSourcePendingPayloadByteLimit:N0}bytes segmentPendingLimits={MaxPendingSegmentLimit:N0}segments/{MaxPendingSegmentPayloadByteLimit:N0}bytes");
        LogDiagnostic($"diagnostic-log sessionPath={_diagnosticLogPath} storagePerfPath={_performanceDiagnosticLogPath}");
    }

    private void LogDiagnostic(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} | {message}{Environment.NewLine}";
        lock (_diagnosticLogLock)
        {
            AppendDiagnosticLine(_diagnosticLogPath, line);
            if (!string.Equals(_diagnosticLogPath, _performanceDiagnosticLogPath, StringComparison.OrdinalIgnoreCase))
            {
                AppendDiagnosticLine(_performanceDiagnosticLogPath, line);
            }
        }
    }

    private static void AppendDiagnosticLine(string? path, string line)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, line);
        }
        catch
        {
        }
    }

    private sealed class SourceTdmsWriter : IDisposable
    {
        private readonly SdkTdmsCaptureWriter _owner;
        private readonly Channel<SdkRawBlock> _queue = Channel.CreateUnbounded<SdkRawBlock>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
        private readonly HashSet<int> _channelIds;
        private readonly string _rawFolder;
        private readonly double _sampleRateHz;
        private StreamChunkAccumulator? _chunk;
        private Task? _task;
        private int _chunkIndex;
        private long _nextChunkStartSample;
        private long _pendingBlocks;
        private long _pendingPayloadBytes;
        private Exception? _fault;
        private bool _completed;

        public SourceTdmsWriter(
            SdkTdmsCaptureWriter owner,
            int sourceId,
            string rawFolder,
            double sampleRateHz,
            IReadOnlyCollection<int> channelIds)
        {
            _owner = owner;
            SourceId = sourceId;
            _rawFolder = rawFolder;
            _sampleRateHz = sampleRateHz;
            _channelIds = channelIds.ToHashSet();
        }

        public int SourceId { get; }

        public long PendingBlocks => Interlocked.Read(ref _pendingBlocks);

        public long PendingPayloadBytes => Interlocked.Read(ref _pendingPayloadBytes);

        public void Start()
        {
            _task = Task.Run(ProcessQueueAsync);
        }

        public bool TryEnqueue(SdkRawBlock rawBlock)
        {
            if (_completed || _fault != null)
            {
                return false;
            }

            if (!_queue.Writer.TryWrite(rawBlock))
            {
                return false;
            }

            Interlocked.Increment(ref _pendingBlocks);
            Interlocked.Add(ref _pendingPayloadBytes, rawBlock.PayloadBytes);
            return true;
        }

        public void Complete()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _queue.Writer.TryComplete();
            try
            {
                _task?.GetAwaiter().GetResult();
            }
            finally
            {
                _task = null;
                FlushCurrentChunk();
            }
        }

        public IReadOnlyList<string> GetWrittenFiles()
            => Array.Empty<string>();

        public void Dispose()
        {
            Complete();
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
                        _owner.OnSourceBlockDequeued(rawBlock);
                        Interlocked.Decrement(ref _pendingBlocks);
                        Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);

                        try
                        {
                            WriteBlock(rawBlock);
                        }
                        catch (Exception ex)
                        {
                            _fault = ex;
                            _owner.OnSourceFault(SourceId, ex);
                            DrainPendingBlocks(reader);
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _fault = ex;
                _owner.OnSourceFault(SourceId, ex);
            }
        }

        private void WriteBlock(SdkRawBlock rawBlock)
        {
            var stopwatch = Stopwatch.StartNew();
            TimeSpan deinterleaveElapsed = TimeSpan.Zero;
            TimeSpan storageAppendElapsed = TimeSpan.Zero;
            TimeSpan storageMetricsElapsed = TimeSpan.Zero;
            int channelCount = rawBlock.ChannelCount;
            int samplesPerChannel = rawBlock.DataCountPerChannel;
            int selectedChannelCount = 0;
            long payloadBytes = 0;
            int chunkIndex = _chunkIndex;

            try
            {
                if (channelCount <= 0 || samplesPerChannel <= 0)
                {
                    return;
                }

                ReadOnlySpan<float> payload = rawBlock.PayloadSpan;
                int requiredFloatCount = checked(channelCount * samplesPerChannel);
                if (payload.Length < requiredFloatCount)
                {
                    throw new InvalidDataException($"SDK block payload is incomplete: expected {requiredFloatCount}, actual {payload.Length}.");
                }

                int consumed = 0;
                while (consumed < samplesPerChannel)
                {
                    if (_chunk == null)
                    {
                        OpenNextChunk(channelCount);
                    }

                    StreamChunkAccumulator chunk = _chunk;
                    chunkIndex = chunk.ChunkIndex;
                    int copyCount = Math.Min(samplesPerChannel - consumed, chunk.RemainingSamples);
                    if (copyCount <= 0)
                    {
                        FlushCurrentChunk();
                        continue;
                    }

                    var deinterleaveStopwatch = Stopwatch.StartNew();
                    int[] targetChannelIds = new int[chunk.ChannelIds.Length];
                    int[] sourceOffsets = new int[chunk.ChannelIds.Length];
                    float[][] targets = new float[chunk.ChannelIds.Length][];
                    int chunkSelectedChannelCount = 0;
                    foreach (int channelId in chunk.ChannelIds)
                    {
                        int channelOffset = ChannelNaming.GetChannelNumber(channelId) - 1;
                        if (channelOffset < 0 || channelOffset >= channelCount)
                        {
                            continue;
                        }

                        targetChannelIds[chunkSelectedChannelCount] = channelId;
                        sourceOffsets[chunkSelectedChannelCount] = channelOffset;
                        targets[chunkSelectedChannelCount] = chunk.GetBuffer(channelId);
                        chunkSelectedChannelCount++;
                    }

                    int targetOffset = chunk.SamplesPerChannel;
                    for (int sampleIndex = 0; sampleIndex < copyCount; sampleIndex++)
                    {
                        int sourceBase = (consumed + sampleIndex) * channelCount;
                        int targetIndex = targetOffset + sampleIndex;
                        for (int targetBufferIndex = 0; targetBufferIndex < chunkSelectedChannelCount; targetBufferIndex++)
                        {
                            targets[targetBufferIndex][targetIndex] = payload[sourceBase + sourceOffsets[targetBufferIndex]];
                        }
                    }

                    for (int targetBufferIndex = 0; targetBufferIndex < chunkSelectedChannelCount; targetBufferIndex++)
                    {
                        _owner.OnSourceChannelSamples(targetChannelIds[targetBufferIndex], copyCount);
                    }

                    deinterleaveStopwatch.Stop();
                    deinterleaveElapsed += deinterleaveStopwatch.Elapsed;

                    if (chunkSelectedChannelCount > 0)
                    {
                        selectedChannelCount = Math.Max(selectedChannelCount, chunkSelectedChannelCount);
                        long chunkPayloadBytes = (long)chunkSelectedChannelCount * copyCount * sizeof(float);
                        payloadBytes += chunkPayloadBytes;
                        chunk.Advance(copyCount, chunkPayloadBytes);
                    }

                    consumed += copyCount;
                    if (chunk.IsFull)
                    {
                        FlushCurrentChunk();
                    }
                }

                var metricsStopwatch = Stopwatch.StartNew();
                metricsStopwatch.Stop();
                storageMetricsElapsed = metricsStopwatch.Elapsed;
            }
            finally
            {
                stopwatch.Stop();
                if (selectedChannelCount > 0)
                {
                    _owner.OnSourceBlockWritten(
                        SourceId,
                        rawBlock.BlockIndex,
                        selectedChannelCount,
                        channelCount,
                        samplesPerChannel,
                        payloadBytes,
                        stopwatch.Elapsed,
                        deinterleaveElapsed,
                        storageAppendElapsed + storageMetricsElapsed,
                        storageAppendElapsed,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        storageMetricsElapsed,
                        chunkIndex);
                }

                rawBlock.ReleasePayload();
            }
        }

        private void OpenNextChunk(int sourceChannelCount)
        {
            _chunkIndex++;
            _chunk = new StreamChunkAccumulator(
                _chunkIndex,
                _nextChunkStartSample,
                GetStreamChannelIds(sourceChannelCount),
                CalculateChunkSampleLimit(sourceChannelCount));
        }

        private void FlushCurrentChunk()
        {
            StreamChunkAccumulator? chunk = _chunk;
            if (chunk == null || chunk.SamplesPerChannel <= 0)
            {
                _chunk = null;
                return;
            }

            _chunk = null;
            _nextChunkStartSample += chunk.SamplesPerChannel;
            string filePath = Path.Combine(_rawFolder, $"source_{SourceId:D4}_seg{chunk.ChunkIndex:D6}.tdms");
            var segment = new PendingTdmsSegment(
                filePath,
                SourceId,
                chunk.ChunkIndex,
                chunk.StartSample,
                _sampleRateHz,
                chunk.ChannelIds,
                chunk.SamplesPerChannel,
                chunk.PayloadBytes,
                chunk.Buffers,
                _owner._sessionName,
                chunk.IsPartial);

            if (!_owner.TryEnqueueSegment(segment))
            {
                _owner.LogDiagnostic(
                    $"tdms-segment-dropped source={SourceId} segment={chunk.ChunkIndex:N0} channels={chunk.ChannelIds.Length:N0} samplesPerChannel={chunk.SamplesPerChannel:N0} payloadBytes={chunk.PayloadBytes:N0} protection={_owner.ProtectionTriggered}");
                return;
            }

            // Successful segment preparation is intentionally not logged per segment.
            // At 256ch/1MHz and 0.5s segments that log becomes part of the hot path.
        }

        private int CalculateChunkSampleLimit(int sourceChannelCount)
        {
            int safeSourceChannelCount = Math.Max(1, sourceChannelCount);
            long payloadSampleLimit = Math.Max(1, StreamAppendChunkPayloadByteLimit / safeSourceChannelCount / sizeof(float));
            long timeSampleLimit = _sampleRateHz > 0
                ? Math.Max(1, (long)Math.Round(_sampleRateHz * StreamAppendChunkSeconds))
                : payloadSampleLimit;
            long sampleLimit = Math.Min(payloadSampleLimit, timeSampleLimit);
            return (int)Math.Min(int.MaxValue, Math.Max(1, sampleLimit));
        }

        private int[] GetStreamChannelIds(int sourceChannelCount)
        {
            if (_channelIds.Count > 0)
            {
                return _channelIds.OrderBy(static id => id).ToArray();
            }

            return Enumerable
                .Range(1, Math.Max(0, sourceChannelCount))
                .Select(channelNumber => ChannelNaming.MakeChannelId(SourceId, channelNumber))
                .ToArray();
        }

        private void DrainPendingBlocks(ChannelReader<SdkRawBlock> reader)
        {
            while (reader.TryRead(out var rawBlock))
            {
                _owner.OnSourceBlockDequeued(rawBlock);
                Interlocked.Decrement(ref _pendingBlocks);
                Interlocked.Add(ref _pendingPayloadBytes, -rawBlock.PayloadBytes);
                rawBlock.ReleasePayload();
            }
        }

        private sealed class StreamChunkAccumulator
        {
            private readonly Dictionary<int, float[]> _buffers;

            public StreamChunkAccumulator(int chunkIndex, IReadOnlyCollection<int> channelIds, int sampleLimit)
            {
                ChunkIndex = chunkIndex;
                ChannelIds = channelIds.OrderBy(static id => id).ToArray();
                SampleLimit = sampleLimit;
                _buffers = ChannelIds.ToDictionary(static id => id, _ => new float[sampleLimit]);
            }

            public StreamChunkAccumulator(int chunkIndex, long startSample, IReadOnlyCollection<int> channelIds, int sampleLimit)
                : this(chunkIndex, channelIds, sampleLimit)
            {
                StartSample = startSample;
            }

            public int ChunkIndex { get; }

            public long StartSample { get; }

            public int[] ChannelIds { get; }

            public int SampleLimit { get; }

            public int SamplesPerChannel { get; private set; }

            public long PayloadBytes { get; private set; }

            public int RemainingSamples => Math.Max(0, SampleLimit - SamplesPerChannel);

            public bool IsFull => SamplesPerChannel >= SampleLimit || PayloadBytes >= StreamAppendChunkPayloadByteLimit;

            public bool IsPartial => SamplesPerChannel < SampleLimit;

            public IReadOnlyDictionary<int, float[]> Buffers => _buffers;

            public float[] GetBuffer(int channelId) => _buffers[channelId];

            public void Advance(int sampleCount, long payloadBytes)
            {
                SamplesPerChannel += sampleCount;
                PayloadBytes += payloadBytes;
            }
        }
    }

    private sealed class PendingTdmsSegment : IDisposable
    {
        private readonly IReadOnlyDictionary<int, float[]> _buffers;

        public PendingTdmsSegment(
            string filePath,
            int sourceId,
            int segmentIndex,
            long startSample,
            double sampleRateHz,
            int[] channelIds,
            int samplesPerChannel,
            long payloadBytes,
            IReadOnlyDictionary<int, float[]> buffers,
            string sessionName,
            bool isPartial)
        {
            FilePath = filePath;
            SourceId = sourceId;
            SegmentIndex = segmentIndex;
            StartSample = startSample;
            SampleRateHz = sampleRateHz;
            ChannelIds = channelIds;
            SamplesPerChannel = samplesPerChannel;
            PayloadBytes = payloadBytes;
            SessionName = sessionName;
            _buffers = buffers;
            IsPartial = isPartial;
        }

        public string FilePath { get; }

        public int SourceId { get; }

        public int SegmentIndex { get; }

        public long StartSample { get; }

        public double SampleRateHz { get; }

        public int[] ChannelIds { get; }

        public int SamplesPerChannel { get; }

        public long PayloadBytes { get; }

        public string SessionName { get; }

        public bool IsPartial { get; }

        public float[] GetSamples(int channelId)
        {
            if (_buffers.TryGetValue(channelId, out var samples))
            {
                return samples;
            }

            return Array.Empty<float>();
        }

        public TdmsSegmentManifestEntry ToManifestEntry(TdmsSourceSegmentWriteResult result)
            => new()
            {
                Path = FilePath,
                SourceId = SourceId,
                SegmentIndex = SegmentIndex,
                StartSample = StartSample,
                SamplesPerChannel = SamplesPerChannel,
                SampleRateHz = SampleRateHz,
                ChannelIds = ChannelIds,
                CompressionEnabled = result.CompressionEnabled,
                CompressionType = result.CompressionType.ToString(),
                PreprocessType = result.PreprocessType.ToString(),
                CompressionOriginalPayloadBytes = result.PayloadBytes,
                CompressionPayloadBytes = result.CodecPayloadBytes,
                ChannelPayloadBytes = result.ChannelPayloadBytes
            };

        public void Dispose()
        {
        }
    }

    private sealed class TdmsSourceRawTimingState
    {
        private readonly object _gate = new();

        public TdmsSourceRawTimingState(int sourceId)
        {
            SourceId = sourceId;
        }

        public int SourceId { get; }

        private long _blockCount;
        private long _samplesPerChannel;
        private long _firstTotalDataCount;
        private long _lastTotalDataCount;
        private DateTime _firstReceivedAtUtc;
        private DateTime _lastReceivedAtUtc;

        public void Observe(SdkRawBlock rawBlock)
        {
            lock (_gate)
            {
                if (_blockCount == 0)
                {
                    _firstTotalDataCount = rawBlock.TotalDataCount;
                    _firstReceivedAtUtc = rawBlock.ReceivedAtUtc;
                }

                _blockCount++;
                _samplesPerChannel += rawBlock.DataCountPerChannel;
                _lastTotalDataCount = rawBlock.TotalDataCount;
                _lastReceivedAtUtc = rawBlock.ReceivedAtUtc;
            }
        }

        public TdmsSourceRawTimingSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new TdmsSourceRawTimingSnapshot(
                    SourceId,
                    _blockCount,
                    _samplesPerChannel,
                    _firstTotalDataCount,
                    _lastTotalDataCount,
                    _firstReceivedAtUtc,
                    _lastReceivedAtUtc);
            }
        }
    }

    private sealed record TdmsSourceRawTimingSnapshot(
        int SourceId,
        long BlockCount,
        long SamplesPerChannel,
        long FirstTotalDataCount,
        long LastTotalDataCount,
        DateTime FirstReceivedAtUtc,
        DateTime LastReceivedAtUtc);
}
