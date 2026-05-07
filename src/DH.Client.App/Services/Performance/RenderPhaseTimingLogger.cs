using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Services.Performance;

public static class RenderPhaseTimingLogger
{
    private const bool EnablePerViewRenderDetailLogs = false;
    private static readonly string LogPath;
    private static readonly string StorageLogPath;
    private static readonly ConcurrentDictionary<string, DateTime> LastWriteUtc = new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<PendingLogLine> PendingLines = new();
    private static readonly AutoResetEvent FlushSignal = new(false);
    private static readonly CancellationTokenSource FlushCts = new();
    private static readonly Task FlushTask;

    static RenderPhaseTimingLogger()
    {
        LogPath = PerformanceOutputPaths.GetPath("render-phase-timing.log");
        StorageLogPath = PerformanceOutputPaths.GetStoragePath("render-phase-timing.log");
        File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
        if (!string.Equals(LogPath, StorageLogPath, StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(StorageLogPath, string.Empty, Encoding.UTF8);
        }

        FlushTask = Task.Factory.StartNew(
            FlushLoop,
            FlushCts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    public static string CurrentLogPath => LogPath;

    public static string CurrentStorageLogPath => StorageLogPath;

    public static void LogCurvePanelRebuild(
        string panelTag,
        int activeChannelCount,
        int previewPointCount,
        bool usedSharedSnapshot,
        double totalMs,
        string phase = "unknown")
    {
        if (activeChannelCount < 16)
        {
            return;
        }

        if (!ShouldWrite($"curvepanel|{panelTag}", 500))
        {
            return;
        }

        WriteLine(
            "CurvePanel",
            $"panel={panelTag}",
            $"channels={activeChannelCount}",
            $"previewPoints={previewPointCount}",
            $"sharedSnapshot={(usedSharedSnapshot ? 1 : 0)}",
            $"phase={phase}",
            $"totalMs={totalMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogRenderSkia(
        string viewTag,
        int attachedViewCount,
        int multiCount,
        float width,
        float height,
        bool reusedFrame,
        double totalMs)
    {
        if (!EnablePerViewRenderDetailLogs)
        {
            return;
        }

        if (attachedViewCount < 16)
        {
            return;
        }

        if (!ShouldWrite($"renderskia|{viewTag}", 500))
        {
            return;
        }

        WriteLine(
            GetLogPathForView(viewTag),
            "RenderSkia",
            $"view={viewTag}",
            $"attachedViews={attachedViewCount}",
            $"channels={multiCount}",
            $"width={width.ToString("F1", CultureInfo.InvariantCulture)}",
            $"height={height.ToString("F1", CultureInfo.InvariantCulture)}",
            $"reusedFrame={(reusedFrame ? 1 : 0)}",
            $"totalMs={totalMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogSingleViewRenderState(
        string viewTag,
        int multiCount,
        int singleCount,
        bool reusedFrame,
        bool hasReusableFrame,
        bool bgInvalidated,
        bool dragging,
        bool animatingZoomX,
        bool animatingZoomY,
        bool hasDragTrace,
        bool autoFitX,
        bool autoFitY,
        float zoomX,
        float zoomY,
        bool dataRefMatches,
        double timeWindowStartSeconds)
    {
        if (!ShouldWrite($"singleview-render|{viewTag}", 250))
        {
            return;
        }

        WriteLine(
            GetLogPathForView(viewTag),
            "SingleViewRenderState",
            $"view={viewTag}",
            $"multiCount={multiCount}",
            $"singleCount={singleCount}",
            $"reusedFrame={(reusedFrame ? 1 : 0)}",
            $"hasReusableFrame={(hasReusableFrame ? 1 : 0)}",
            $"bgInvalidated={(bgInvalidated ? 1 : 0)}",
            $"dragging={(dragging ? 1 : 0)}",
            $"animZoomX={(animatingZoomX ? 1 : 0)}",
            $"animZoomY={(animatingZoomY ? 1 : 0)}",
            $"dragTrace={(hasDragTrace ? 1 : 0)}",
            $"autoFitX={(autoFitX ? 1 : 0)}",
            $"autoFitY={(autoFitY ? 1 : 0)}",
            $"zoomX={zoomX.ToString("F3", CultureInfo.InvariantCulture)}",
            $"zoomY={zoomY.ToString("F3", CultureInfo.InvariantCulture)}",
            $"dataRefMatches={(dataRefMatches ? 1 : 0)}",
            $"timeWindowStart={timeWindowStartSeconds.ToString("F6", CultureInfo.InvariantCulture)}");
    }

    public static void LogRenderMultiPhases(
        string viewTag,
        int attachedViewCount,
        int channelCount,
        int maxCount,
        double orderedMs,
        double viewportMs,
        double xRangeMs,
        double yRangeMs,
        double layerPrepMs,
        double drawMs,
        double totalMs)
    {
        if (!EnablePerViewRenderDetailLogs)
        {
            return;
        }

        if (attachedViewCount < 16)
        {
            return;
        }

        if (!ShouldWrite($"rendermulti|{viewTag}", 500))
        {
            return;
        }

        WriteLine(
            GetLogPathForView(viewTag),
            "RenderMulti",
            $"view={viewTag}",
            $"attachedViews={attachedViewCount}",
            $"channels={channelCount}",
            $"maxCount={maxCount}",
            $"orderedMs={orderedMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"viewportMs={viewportMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"xRangeMs={xRangeMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"yRangeMs={yRangeMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"layerPrepMs={layerPrepMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"drawMs={drawMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalMs={totalMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogCurveDataProviderState(
        string viewTag,
        int attachedViewCount,
        int channelCount,
        bool cacheDirty,
        bool queryPreviewEnabled,
        bool hasQueryService,
        int cachedChannelCount,
        int maxActualPointsPerCurve,
        int totalActualPoints)
    {
        if (!ShouldWrite($"curve-provider|{viewTag}", 1000))
        {
            return;
        }

        WriteLine(
            "CurveDataProviderState",
            $"view={viewTag}",
            $"attachedViews={attachedViewCount}",
            $"channels={channelCount}",
            $"cacheDirty={(cacheDirty ? 1 : 0)}",
            $"queryPreview={(queryPreviewEnabled ? 1 : 0)}",
            $"hasQueryService={(hasQueryService ? 1 : 0)}",
            $"cachedChannels={cachedChannelCount}",
            $"maxActualPointsPerCurve={maxActualPointsPerCurve}",
            $"totalActualPoints={totalActualPoints}");
    }

    public static void LogFrameReuseSummary(
        int attachedViewCount,
        long evalCount,
        long reuseHitCount,
        long noReusableFrameCount,
        long bgInvalidatedCount,
        long draggingCount,
        long animatingZoomCount,
        long animatingZoomYCount,
        long jumpingCount,
        long dragTraceCount,
        long sizeChangedCount,
        long zoomXChangedCount,
        long zoomYChangedCount,
        long autoFitXChangedCount,
        long autoFitYChangedCount,
        long useTimeAxisChangedCount,
        long timeWindowChangedCount,
        long dataRefChangedCount)
    {
        if (attachedViewCount < 16 || evalCount <= 0)
        {
            return;
        }

        WriteLine(
            "FrameReuseSummary",
            $"attachedViews={attachedViewCount}",
            $"eval={evalCount}",
            $"hits={reuseHitCount}",
            $"hitRate={(100.0 * reuseHitCount / Math.Max(1, evalCount)).ToString("F2", CultureInfo.InvariantCulture)}%",
            $"noReusableFrame={noReusableFrameCount}",
            $"bgInvalidated={bgInvalidatedCount}",
            $"dragging={draggingCount}",
            $"animZoomX={animatingZoomCount}",
            $"animZoomY={animatingZoomYCount}",
            $"jumping={jumpingCount}",
            $"dragTrace={dragTraceCount}",
            $"sizeChanged={sizeChangedCount}",
            $"zoomXChanged={zoomXChangedCount}",
            $"zoomYChanged={zoomYChangedCount}",
            $"autoFitXChanged={autoFitXChangedCount}",
            $"autoFitYChanged={autoFitYChangedCount}",
            $"useTimeAxisChanged={useTimeAxisChangedCount}",
            $"timeWindowChanged={timeWindowChangedCount}",
            $"dataRefChanged={dataRefChangedCount}");
    }

    public static void LogCurveDataFlowSummary(
        int attachedViewCount,
        long relevantUpdates,
        long throttledUpdates,
        long dirtyMarks,
        long rebuilds,
        long cleanCacheSkips,
        long sharedSnapshotBuilds,
        long localBuilds,
        long queryPathBuilds,
        long legacyFallbackBuilds)
    {
        long totalBuilds = sharedSnapshotBuilds + localBuilds + queryPathBuilds + legacyFallbackBuilds;
        if (attachedViewCount < 16 && totalBuilds == 0 && relevantUpdates == 0 && dirtyMarks == 0)
        {
            return;
        }

        WriteLine(
            "CurveDataFlowSummary",
            $"attachedViews={attachedViewCount}",
            $"relevantUpdates={relevantUpdates}",
            $"throttledUpdates={throttledUpdates}",
            $"dirtyMarks={dirtyMarks}",
            $"rebuilds={rebuilds}",
            $"cleanCacheSkips={cleanCacheSkips}",
            $"sharedSnapshotBuilds={sharedSnapshotBuilds}",
            $"localBuilds={localBuilds}",
            $"queryPathBuilds={queryPathBuilds}",
            $"legacyFallbackBuilds={legacyFallbackBuilds}");
    }

    public static void LogFrameCoordinatorSummary(
        long updateCount,
        long frameAdvanceCount,
        long frameVersion,
        long dataEpoch,
        long latestDataVersion,
        long previousPresentedDataVersion,
        bool presentedNewData)
    {
        if (!ShouldWrite("frame-coordinator-summary", 1000))
        {
            return;
        }

        WriteLine(
            "FrameCoordinatorSummary",
            $"updates={updateCount}",
            $"frameAdvances={frameAdvanceCount}",
            $"frameVersion={frameVersion}",
            $"dataEpoch={dataEpoch}",
            $"latestDataVersion={latestDataVersion}",
            $"previousPresentedDataVersion={previousPresentedDataVersion}",
            $"presentedNewData={(presentedNewData ? 1 : 0)}");
    }

    public static void LogRealtimeFrameSourceSummary(
        string frameSource,
        int viewCount,
        int activeChannelCount,
        int cachedChannelCount,
        int maxActualPointsPerCurve,
        int totalActualPoints,
        int fallbackPanelCount)
    {
        if (!ShouldWrite("realtime-frame-source-summary", 1000))
        {
            return;
        }

        WriteLine(
            "RealtimeFrameSourceSummary",
            $"frameSource={frameSource}",
            $"views={viewCount}",
            $"activeChannels={activeChannelCount}",
            $"cachedChannels={cachedChannelCount}",
            $"maxActualPointsPerCurve={maxActualPointsPerCurve}",
            $"totalActualPoints={totalActualPoints}",
            $"fallbackPanels={fallbackPanelCount}");
    }

    public static void LogRealtimeFrameBuild(
        string frameSource,
        int viewCount,
        int activeChannelCount,
        int maxPointsPerChannel,
        int maxActualPointsPerCurve,
        int totalActualPoints,
        double totalMs)
    {
        if (!ShouldWrite("realtime-frame-build", 1000))
        {
            return;
        }

        WriteLine(
            "RealtimeFrameBuild",
            $"frameSource={frameSource}",
            $"views={viewCount}",
            $"activeChannels={activeChannelCount}",
            $"maxPointsPerChannel={maxPointsPerChannel}",
            $"maxActualPointsPerCurve={maxActualPointsPerCurve}",
            $"totalActualPoints={totalActualPoints}",
            $"totalMs={totalMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogCurvePanelLegacyFallback(
        string panelTag,
        string provider,
        bool requiresExternalFrame,
        bool hasExternalFrame,
        int selectedChannelCount)
    {
        if (!ShouldWrite($"curvepanel-legacy-fallback|{panelTag}|{provider}", 1000))
        {
            return;
        }

        WriteLine(
            "CurvePanelLegacyFallback",
            $"panel={panelTag}",
            $"provider={provider}",
            $"requiresExternalFrame={(requiresExternalFrame ? 1 : 0)}",
            $"hasExternalFrame={(hasExternalFrame ? 1 : 0)}",
            $"selectedChannels={selectedChannelCount}");
    }

    public static void LogSelectionFlow(
        string source,
        string target,
        int selectedCount,
        string detail)
    {
        WriteLine(
            "SelectionFlow",
            $"source={source}",
            $"target={target}",
            $"selectedCount={selectedCount}",
            $"detail={detail}");
    }

    public static void LogCurveQueryVersion(
        string sessionId,
        long version,
        long dataEpoch)
    {
        if (!ShouldWrite($"curvequery-version|{sessionId}", 250))
        {
            return;
        }

        WriteLine(
            "CurveQueryVersion",
            $"sessionId={sessionId}",
            $"version={version}",
            $"dataEpoch={dataEpoch}");
    }

    public static void LogCurveQuerySnapshot(
        string sessionId,
        string? viewId,
        int channelCount,
        string previewLevel,
        long version,
        long dataEpoch,
        long sourceVersion,
        string buildState,
        bool isPreview,
        bool isComplete,
        string timeAxisKind,
        long totalActualPoints,
        double queryLatencyMs)
    {
        string key = $"curvequery-snapshot|{sessionId}|{viewId ?? "none"}";
        if (!ShouldWrite(key, 250))
        {
            return;
        }

        WriteLine(
            "CurveQuerySnapshot",
            $"sessionId={sessionId}",
            $"viewId={viewId ?? "none"}",
            $"channelCount={channelCount}",
            $"previewLevel={previewLevel}",
            $"version={version}",
            $"dataEpoch={dataEpoch}",
            $"sourceVersion={sourceVersion}",
            $"buildState={buildState}",
            $"isPreview={(isPreview ? 1 : 0)}",
            $"isComplete={(isComplete ? 1 : 0)}",
            $"timeAxisKind={timeAxisKind}",
            $"totalActualPoints={totalActualPoints}",
            $"queryLatencyMs={queryLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogCurveQueryValidation(
        string stage,
        string sessionId,
        string detail)
    {
        WriteLine(
            "CurveQueryValidation",
            $"stage={stage}",
            $"sessionId={sessionId}",
            $"detail={detail}");
    }

    public static void LogCurveQueryShadowCompare(
        string sessionId,
        string viewId,
        int channelCount,
        int localPreviewCount,
        int queryPreviewCount,
        int localMaxActualPointsPerCurve,
        int queryMaxActualPointsPerCurve,
        long localTotalActualPoints,
        long queryTotalActualPoints,
        double localWindowStart,
        double queryWindowStart,
        string queryBuildState,
        bool queryIsComplete,
        double compareLatencyMs)
    {
        string key = $"curvequery-shadow|{sessionId}|{viewId}";
        if (!ShouldWrite(key, 1000))
        {
            return;
        }

        WriteLine(
            "CurveQueryShadowCompare",
            $"sessionId={sessionId}",
            $"viewId={viewId}",
            $"channelCount={channelCount}",
            $"localPreviewCount={localPreviewCount}",
            $"queryPreviewCount={queryPreviewCount}",
            $"localMaxActualPointsPerCurve={localMaxActualPointsPerCurve}",
            $"queryMaxActualPointsPerCurve={queryMaxActualPointsPerCurve}",
            $"localTotalActualPoints={localTotalActualPoints}",
            $"queryTotalActualPoints={queryTotalActualPoints}",
            $"localWindowStart={localWindowStart.ToString("F6", CultureInfo.InvariantCulture)}",
            $"queryWindowStart={queryWindowStart.ToString("F6", CultureInfo.InvariantCulture)}",
            $"queryBuildState={queryBuildState}",
            $"queryIsComplete={(queryIsComplete ? 1 : 0)}",
            $"compareLatencyMs={compareLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogPreviewBuild(
        string sessionId,
        string? viewId,
        string previewLevel,
        int channelCount,
        bool cacheHit,
        string buildState,
        string buildMode,
        double buildLatencyMs)
    {
        if (!ShouldWrite($"preview-build|{sessionId}|{viewId ?? "none"}|{previewLevel}", 250))
        {
            return;
        }

        WriteLine(
            "PreviewBuild",
            $"sessionId={sessionId}",
            $"viewId={viewId ?? "none"}",
            $"previewLevel={previewLevel}",
            $"channelCount={channelCount}",
            $"cacheHit={(cacheHit ? 1 : 0)}",
            $"buildState={buildState}",
            $"buildMode={buildMode}",
            $"buildLatencyMs={buildLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogPreviewQuery(
        string sessionId,
        string? viewId,
        string previewLevel,
        int channelCount,
        bool cacheHit,
        string buildState,
        bool isComplete,
        long totalActualPoints)
    {
        if (!ShouldWrite($"preview-query|{sessionId}|{viewId ?? "none"}|{previewLevel}", 250))
        {
            return;
        }

        WriteLine(
            "PreviewQuery",
            $"sessionId={sessionId}",
            $"viewId={viewId ?? "none"}",
            $"previewLevel={previewLevel}",
            $"channelCount={channelCount}",
            $"cacheHit={(cacheHit ? 1 : 0)}",
            $"buildState={buildState}",
            $"isComplete={(isComplete ? 1 : 0)}",
            $"totalActualPoints={totalActualPoints}");
    }

    public static void LogCurveQueryPathSelection(
        string sessionId,
        string viewId,
        bool usedQueryPath,
        bool legacyPathUsed,
        string reason)
    {
        string key = $"curve-query-path|{sessionId}|{viewId}";
        if (!ShouldWrite(key, 500))
        {
            return;
        }

        WriteLine(
            "CurveQueryPathSelection",
            $"sessionId={sessionId}",
            $"viewId={viewId}",
            $"usedQueryPath={(usedQueryPath ? 1 : 0)}",
            $"legacyPathUsed={(legacyPathUsed ? 1 : 0)}",
            $"reason={reason}");
    }

    public static void LogRealtimeChannelTimebase(
        int channelId,
        int frameSamples,
        int sampleRateHz,
        double frameDurationSeconds,
        long totalSamples,
        double frameStartSeconds,
        double frameEndSeconds,
        int previewPointCount)
    {
        if (!ShouldWrite($"realtime-timebase|{channelId}", 1000))
        {
            return;
        }

        WriteLine(
            "RealtimeChannelTimebase",
            $"channelId={channelId}",
            $"frameSamples={frameSamples}",
            $"sampleRateHz={sampleRateHz}",
            $"frameDurationSeconds={frameDurationSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"totalSamples={totalSamples}",
            $"frameStartSeconds={frameStartSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"frameEndSeconds={frameEndSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"previewPointCount={previewPointCount}");
    }

    public static void LogCurvePanelTimebase(
        string panelTag,
        int activeChannelCount,
        int historyCount,
        double latestSeconds,
        double windowStartSeconds,
        double windowEndSeconds,
        int maxActualPointsPerCurve,
        int totalActualPoints)
    {
        if (!ShouldWrite($"curvepanel-timebase|{panelTag}", 1000))
        {
            return;
        }

        WriteLine(
            "CurvePanelTimebase",
            $"panel={panelTag}",
            $"activeChannels={activeChannelCount}",
            $"historyCount={historyCount}",
            $"latestSeconds={latestSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowStartSeconds={windowStartSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowEndSeconds={windowEndSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"maxActualPointsPerCurve={maxActualPointsPerCurve}",
            $"totalActualPoints={totalActualPoints}");
    }

    public static void LogRealtimeChannelIngress(
        int channelId,
        int frameId,
        string producerTag,
        int frameSamples,
        int declaredSampleRateHz,
        string frameTimestampUtc,
        double wallDeltaMs,
        double frameTimestampDeltaMs,
        long totalFrames,
        long totalSamples,
        double summaryWallSeconds,
        long summaryFrames,
        long summarySamples,
        double effectiveFramesPerSecond,
        double effectiveSamplesPerSecond)
    {
        if (!ShouldWrite($"realtime-ingress|{channelId}", 1000))
        {
            return;
        }

        WriteLine(
            "RealtimeChannelIngress",
            $"channelId={channelId}",
            $"frameId={frameId}",
            $"producerTag={producerTag}",
            $"frameSamples={frameSamples}",
            $"declaredSampleRateHz={declaredSampleRateHz}",
            $"frameTimestampUtc={frameTimestampUtc}",
            $"wallDeltaMs={wallDeltaMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"frameTimestampDeltaMs={frameTimestampDeltaMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalFrames={totalFrames}",
            $"totalSamples={totalSamples}",
            $"summaryWallSeconds={summaryWallSeconds.ToString("F3", CultureInfo.InvariantCulture)}",
            $"summaryFrames={summaryFrames}",
            $"summarySamples={summarySamples}",
            $"effectiveFramesPerSecond={effectiveFramesPerSecond.ToString("F3", CultureInfo.InvariantCulture)}",
            $"effectiveSamplesPerSecond={effectiveSamplesPerSecond.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogSdkCallbackFlow(
        int messageType,
        int groupId,
        int machineId,
        int channelCount,
        int dataCountPerChannel,
        int bufferCountBytes,
        int configuredCallbackSamples,
        int configuredChunkSize,
        double sampleRateHz,
        double summaryWallSeconds,
        long callbackCount,
        long callbackSamplesPerChannel,
        double callbacksPerSecond,
        double effectiveSamplesPerSecondPerChannel)
    {
        if (!ShouldWrite($"sdk-callback|{groupId}|{machineId}", 1000))
        {
            return;
        }

        WriteLine(
            "SdkCallbackFlow",
            $"messageType={messageType}",
            $"groupId={groupId}",
            $"machineId={machineId}",
            $"channelCount={channelCount}",
            $"dataCountPerChannel={dataCountPerChannel}",
            $"bufferCountBytes={bufferCountBytes}",
            $"configuredCallbackSamples={configuredCallbackSamples}",
            $"configuredChunkSize={configuredChunkSize}",
            $"sampleRateHz={sampleRateHz.ToString("F3", CultureInfo.InvariantCulture)}",
            $"summaryWallSeconds={summaryWallSeconds.ToString("F3", CultureInfo.InvariantCulture)}",
            $"callbackCount={callbackCount}",
            $"callbackSamplesPerChannel={callbackSamplesPerChannel}",
            $"callbacksPerSecond={callbacksPerSecond.ToString("F3", CultureInfo.InvariantCulture)}",
            $"effectiveSamplesPerSecondPerChannel={effectiveSamplesPerSecondPerChannel.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void LogPersistedPreviewQuery(
        string sessionId,
        string? viewId,
        string previewLevel,
        int channelCount,
        bool indexHit,
        double queryLatencyMs,
        long totalActualPoints,
        bool isComplete)
    {
        if (!ShouldWrite($"persisted-preview-query|{sessionId}|{viewId ?? "none"}|{previewLevel}", 250))
        {
            return;
        }

        WriteLine(
            StorageLogPath,
            "PersistedPreviewQuery",
            $"sessionId={sessionId}",
            $"viewId={viewId ?? "none"}",
            $"previewLevel={previewLevel}",
            $"channelCount={channelCount}",
            $"indexHit={(indexHit ? 1 : 0)}",
            $"queryLatencyMs={queryLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalActualPoints={totalActualPoints}",
            $"isComplete={(isComplete ? 1 : 0)}");
    }

    public static void LogPersistedRawQuery(
        string sessionId,
        string? viewId,
        int channelCount,
        bool rawIndexHit,
        bool tdmsFallbackUsed,
        double queryLatencyMs,
        long totalActualPoints,
        bool isComplete)
    {
        if (!ShouldWrite($"persisted-raw-query|{sessionId}|{viewId ?? "none"}", 250))
        {
            return;
        }

        WriteLine(
            StorageLogPath,
            "PersistedRawQuery",
            $"sessionId={sessionId}",
            $"viewId={viewId ?? "none"}",
            $"channelCount={channelCount}",
            $"rawIndexHit={(rawIndexHit ? 1 : 0)}",
            $"tdmsFallbackUsed={(tdmsFallbackUsed ? 1 : 0)}",
            $"queryLatencyMs={queryLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalActualPoints={totalActualPoints}",
            $"isComplete={(isComplete ? 1 : 0)}");
    }

    public static void LogReplayBrowserQuery(
        string sessionId,
        string previewLevel,
        double windowStart,
        double windowEnd,
        int channelCount,
        double queryLatencyMs,
        long totalActualPoints,
        bool isComplete)
    {
        if (!ShouldWrite($"replay-browser-query|{sessionId}|{previewLevel}", 250))
        {
            return;
        }

        WriteLine(
            StorageLogPath,
            "ReplayBrowserQuery",
            $"sessionId={sessionId}",
            $"previewLevel={previewLevel}",
            $"windowStart={windowStart.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowEnd={windowEnd.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowSeconds={(windowEnd - windowStart).ToString("F6", CultureInfo.InvariantCulture)}",
            $"channelCount={channelCount}",
            $"queryLatencyMs={queryLatencyMs.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalActualPoints={totalActualPoints}",
            $"isComplete={(isComplete ? 1 : 0)}");
    }

    public static void LogReplayBrowserOpen(
        string sessionId,
        string artifactPath,
        bool hasPreviewIndex,
        double sampleRateHz,
        double totalDurationSeconds,
        double initialWindowStart,
        double initialWindowEnd,
        int channelCount,
        long totalActualPoints,
        string buildState)
    {
        WriteLine(
            StorageLogPath,
            "ReplayBrowserOpen",
            $"sessionId={sessionId}",
            $"artifactPath={artifactPath}",
            $"hasPreviewIndex={(hasPreviewIndex ? 1 : 0)}",
            $"sampleRateHz={sampleRateHz.ToString("F3", CultureInfo.InvariantCulture)}",
            $"totalDurationSeconds={totalDurationSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"initialWindowStart={initialWindowStart.ToString("F6", CultureInfo.InvariantCulture)}",
            $"initialWindowEnd={initialWindowEnd.ToString("F6", CultureInfo.InvariantCulture)}",
            $"initialWindowSeconds={(initialWindowEnd - initialWindowStart).ToString("F6", CultureInfo.InvariantCulture)}",
            $"channelCount={channelCount}",
            $"totalActualPoints={totalActualPoints}",
            $"buildState={buildState}");
    }

    public static void LogReplayBrowserState(
        string sessionId,
        bool hasPreviewIndex,
        double totalDurationSeconds,
        double windowStart,
        double windowEnd,
        int channelCount,
        int currentWindowPoints,
        int overviewPoints,
        long queryActualPoints)
    {
        if (!ShouldWrite($"replay-browser-state|{sessionId}", 250))
        {
            return;
        }

        WriteLine(
            StorageLogPath,
            "ReplayBrowserState",
            $"sessionId={sessionId}",
            $"hasPreviewIndex={(hasPreviewIndex ? 1 : 0)}",
            $"totalDurationSeconds={totalDurationSeconds.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowStart={windowStart.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowEnd={windowEnd.ToString("F6", CultureInfo.InvariantCulture)}",
            $"windowSeconds={(windowEnd - windowStart).ToString("F6", CultureInfo.InvariantCulture)}",
            $"channelCount={channelCount}",
            $"currentWindowPoints={currentWindowPoints}",
            $"overviewPoints={overviewPoints}",
            $"queryActualPoints={queryActualPoints}");
    }

    private static bool ShouldWrite(string key, int throttleMs)
    {
        DateTime now = DateTime.UtcNow;
        if (LastWriteUtc.TryGetValue(key, out var last) && (now - last).TotalMilliseconds < throttleMs)
        {
            return false;
        }

        LastWriteUtc[key] = now;
        return true;
    }

    private static void WriteLine(string category, params string[] fields)
    {
        WriteLine(LogPath, category, fields);
    }

    private static void WriteLine(string path, string category, params string[] fields)
    {
        string prefix = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string line = prefix + " | " + category + " | " + string.Join(" | ", fields);
        PendingLines.Enqueue(new PendingLogLine(path, line));
        FlushSignal.Set();
    }

    private static string GetLogPathForView(string viewTag)
    {
        return viewTag.StartsWith("tdms-replay", StringComparison.OrdinalIgnoreCase)
            ? StorageLogPath
            : LogPath;
    }

    private static void FlushLoop()
    {
        try
        {
            var writers = new Dictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
            while (!FlushCts.IsCancellationRequested)
            {
                FlushSignal.WaitOne(250);
                Drain(writers);
            }

            Drain(writers);
            foreach (StreamWriter writer in writers.Values)
            {
                writer.Dispose();
            }
        }
        catch
        {
            // 诊断日志不能影响主流程
        }
    }

    private static void Drain(Dictionary<string, StreamWriter> writers)
    {
        if (PendingLines.IsEmpty)
        {
            return;
        }

        List<PendingLogLine> batch = new(128);
        while (PendingLines.TryDequeue(out PendingLogLine line))
        {
            batch.Add(line);
            if (batch.Count >= 128)
            {
                break;
            }
        }

        foreach (PendingLogLine entry in batch)
        {
            if (!writers.TryGetValue(entry.Path, out StreamWriter? writer))
            {
                writer = CreateWriter(entry.Path);
                writers.Add(entry.Path, writer);
            }

            writer.WriteLine(entry.Line);
        }

        foreach (StreamWriter writer in writers.Values)
        {
            writer.Flush();
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        return new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };
    }

    private static void Shutdown()
    {
        try
        {
            FlushCts.Cancel();
            FlushSignal.Set();
            FlushTask.Wait(1000);
        }
        catch
        {
            // ignore
        }
    }

    private readonly record struct PendingLogLine(string Path, string Line);
}
