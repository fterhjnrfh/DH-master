using DH.Client.App.Data;
using DH.Client.App.Data.Query;
using DH.Client.App.Services.Performance;
using DH.Contracts.Models;

string? outputDirectory = null;
for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--output-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        outputDirectory = args[i + 1];
        i++;
    }
}

if (!string.IsNullOrWhiteSpace(outputDirectory))
{
    Environment.SetEnvironmentVariable("DH_PERF_OUTPUT_DIR", outputDirectory);
}

static IReadOnlyList<CurvePoint> BuildWave(int count, double startX, double step, double amplitude, double phase)
{
    var data = new CurvePoint[count];
    for (int i = 0; i < count; i++)
    {
        double x = startX + i * step;
        double y = amplitude * Math.Sin((x + phase) * 2.0 * Math.PI);
        data[i] = new CurvePoint(x, y);
    }
    return data;
}

static async Task<bool> ProbeStoreHitAsync(
    IPreviewPyramidStore store,
    RealtimeCurveQueryRuntime runtime,
    Guid sessionId,
    string viewId,
    IReadOnlyList<int> channels,
    double windowStart,
    double windowEnd,
    PreviewLevel level,
    int maxPointsPerChannel)
{
    var read = await store.TryReadAsync(new PreviewLevelReadRequest
    {
        SessionId = sessionId,
        ViewId = viewId,
        ChannelIds = channels,
        WindowStart = windowStart,
        WindowEnd = windowEnd,
        PreviewLevel = level,
        MaxPointsPerChannel = maxPointsPerChannel,
        SourceVersion = runtime.GetLatestPreviewSourceVersionForDiagnostics(),
        DataEpoch = runtime.GetLatestVersion(sessionId).DataEpoch
    });

    return read.CacheHit;
}

static async Task<int> RunQueryAsync(
    RealtimeCurveQueryRuntime runtime,
    PreviewReadRequest request,
    string label)
{
    var requestValidation = CurveQueryValidator.ValidateRequest(request);
    if (!requestValidation.IsValid)
    {
        Console.WriteLine($"{label}:REQUEST_INVALID");
        foreach (var error in requestValidation.Errors)
        {
            Console.WriteLine(error);
        }
        return 2;
    }

    var snapshot = await runtime.QueryAsync(request);
    var snapshotValidation = CurveQueryValidator.ValidateSnapshot(snapshot);
    if (!snapshotValidation.IsValid)
    {
        Console.WriteLine($"{label}:SNAPSHOT_INVALID");
        foreach (var error in snapshotValidation.Errors)
        {
            Console.WriteLine(error);
        }
        return 3;
    }

    Console.WriteLine($"{label}:PreviewLevel={snapshot.PreviewLevel}");
    Console.WriteLine($"{label}:Version={snapshot.Version}");
    Console.WriteLine($"{label}:DataEpoch={snapshot.DataEpoch}");
    Console.WriteLine($"{label}:SourceVersion={snapshot.SourceVersion}");
    Console.WriteLine($"{label}:BuildState={snapshot.BuildState}");
    Console.WriteLine($"{label}:IsPreview={snapshot.IsPreview}");
    Console.WriteLine($"{label}:IsComplete={snapshot.IsComplete}");
    Console.WriteLine($"{label}:ChannelCount={snapshot.ChannelIds.Count}");
    Console.WriteLine($"{label}:TotalActualPoints={snapshot.TotalActualPoints}");

    foreach (var kvp in snapshot.ChannelData.OrderBy(k => k.Key))
    {
        Console.WriteLine($"{label}:Channel {kvp.Key}: points={kvp.Value.Count}");
    }

    return 0;
}

var sessionId = Guid.NewGuid();
var bus = new DataBus();
var cache = new RealtimeDisplayCache(bus);
var builder = new RealtimePreviewLevelBuilder(bus, cache, defaultHistoryPointBudget: 8192);
var storeProvider = new InMemoryPreviewPyramidStoreProvider();
var runtimeFactory = new RealtimeCurveQueryRuntimeFactory(storeProvider);
using var runtime = runtimeFactory.Create(
    dataBus: bus,
    displayCache: cache,
    sessionId: sessionId,
    defaultHistoryPointBudget: 8192,
    previewLevelBuilder: builder);
var store = storeProvider.GetOrCreate(sessionId);

for (int channelId = 1; channelId <= 4; channelId++)
{
    var wave = BuildWave(
        count: 6000,
        startX: 0.0,
        step: 0.001,
        amplitude: 1.0 + channelId * 0.1,
        phase: channelId * 0.03);
    bus.PublishData(channelId, wave);
}

var baseChannels = new[] { 1, 2, 3, 4 };

var l0Request = new PreviewReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l0",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L0,
    MaxPointsPerChannel = 4000,
    RequireEnvelopeSemantics = true,
    AllowDegradedResult = true,
    RequireCompleteWindow = false
};

var l1Request = new PreviewReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l1",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L1,
    MaxPointsPerChannel = 4000,
    RequireEnvelopeSemantics = true,
    AllowDegradedResult = true,
    RequireCompleteWindow = false
};

var l2Request = new PreviewReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l2",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L2,
    MaxPointsPerChannel = 2000,
    RequireEnvelopeSemantics = true,
    AllowDegradedResult = true,
    RequireCompleteWindow = false
};

var l1ReadBefore = await store.TryReadAsync(new PreviewLevelReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l1",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L1,
    MaxPointsPerChannel = 4000,
    SourceVersion = runtime.GetLatestPreviewSourceVersionForDiagnostics(),
    DataEpoch = runtime.GetLatestVersion(sessionId).DataEpoch
});

Console.WriteLine($"PreviewStore:L1:BeforeCacheHit={l1ReadBefore.CacheHit}");
Console.WriteLine($"PerformanceRunDirectory={PerformanceOutputPaths.CurrentRunDirectory}");
Console.WriteLine($"RenderPhaseLogPath={RenderPhaseTimingLogger.CurrentLogPath}");

int l0Code = await RunQueryAsync(runtime, l0Request, "L0");
if (l0Code != 0)
{
    return l0Code;
}

int l1Code = await RunQueryAsync(runtime, l1Request, "L1.First");
if (l1Code != 0)
{
    return l1Code;
}

var latestVersionAfterL1 = runtime.GetLatestVersion(sessionId);
var l1ReadAfter = await store.TryReadAsync(new PreviewLevelReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l1",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L1,
    MaxPointsPerChannel = 4000,
    SourceVersion = runtime.GetLatestPreviewSourceVersionForDiagnostics(),
    DataEpoch = latestVersionAfterL1.DataEpoch
});
Console.WriteLine($"PreviewStore:L1:AfterCacheHit={l1ReadAfter.CacheHit}");

int l1RepeatCode = await RunQueryAsync(runtime, l1Request, "L1.Second");
if (l1RepeatCode != 0)
{
    return l1RepeatCode;
}

int l2Code = await RunQueryAsync(runtime, l2Request, "L2.First");
if (l2Code != 0)
{
    return l2Code;
}

var latestVersionAfterL2 = runtime.GetLatestVersion(sessionId);
var l2ReadAfter = await store.TryReadAsync(new PreviewLevelReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l2",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L2,
    MaxPointsPerChannel = 2000,
    SourceVersion = runtime.GetLatestPreviewSourceVersionForDiagnostics(),
    DataEpoch = latestVersionAfterL2.DataEpoch
});
Console.WriteLine($"PreviewStore:L2:AfterCacheHit={l2ReadAfter.CacheHit}");
Console.WriteLine($"PreviewSourceVersion:BeforeQuickPublish={runtime.GetLatestPreviewSourceVersionForDiagnostics()}");

var quickTail = BuildWave(
    count: 32,
    startX: 6.001,
    step: 0.001,
    amplitude: 1.5,
    phase: 0.2);
bus.PublishData(1, quickTail);
Console.WriteLine($"PreviewSourceVersion:AfterQuickPublish={runtime.GetLatestPreviewSourceVersionForDiagnostics()}");

bool l1QuickHitBeforeQuery = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:L1:BeforeQuickPublishQueryCacheHit={l1QuickHitBeforeQuery}");

int l1QuickCode = await RunQueryAsync(runtime, l1Request, "L1.AfterQuickPublish");
if (l1QuickCode != 0)
{
    return l1QuickCode;
}

bool l1QuickHit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:L1:AfterQuickPublishCacheHit={l1QuickHit}");

await Task.Delay(30);
Console.WriteLine($"PreviewSourceVersion:BeforeDelayedPublish={runtime.GetLatestPreviewSourceVersionForDiagnostics()}");

var delayedTail = BuildWave(
    count: 32,
    startX: 6.033,
    step: 0.001,
    amplitude: 1.6,
    phase: 0.4);
bus.PublishData(1, delayedTail);
Console.WriteLine($"PreviewSourceVersion:AfterDelayedPublish={runtime.GetLatestPreviewSourceVersionForDiagnostics()}");

bool l1DelayedHitBeforeQuery = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:L1:BeforeDelayedPublishQueryCacheHit={l1DelayedHitBeforeQuery}");

int l1DelayedCode = await RunQueryAsync(runtime, l1Request, "L1.AfterDelayedPublish");
if (l1DelayedCode != 0)
{
    return l1DelayedCode;
}

bool l1DelayedHit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:L1:AfterDelayedPublishCacheHit={l1DelayedHit}");

var session1WindowBRequest = new PreviewReadRequest
{
    SessionId = sessionId,
    ViewId = "smoke-l1-window-b",
    ChannelIds = baseChannels,
    WindowStart = 2.0,
    WindowEnd = 4.5,
    PreviewLevel = PreviewLevel.L1,
    MaxPointsPerChannel = 4000,
    RequireEnvelopeSemantics = true,
    AllowDegradedResult = true,
    RequireCompleteWindow = false
};

bool session1WindowBBeforeHit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1-window-b",
    baseChannels,
    2.0,
    4.5,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session1:WindowB:BeforeCacheHit={session1WindowBBeforeHit}");

int session1WindowBCode = await RunQueryAsync(runtime, session1WindowBRequest, "Session1.WindowB.First");
if (session1WindowBCode != 0)
{
    return session1WindowBCode;
}

bool session1WindowBAfterHit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1-window-b",
    baseChannels,
    2.0,
    4.5,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session1:WindowB:AfterCacheHit={session1WindowBAfterHit}");

bool session1WindowAAfterWindowBHit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session1:WindowA:AfterWindowBStillHit={session1WindowAAfterWindowBHit}");

var session2Id = Guid.NewGuid();
var bus2 = new DataBus();
var cache2 = new RealtimeDisplayCache(bus2);
var builder2 = new RealtimePreviewLevelBuilder(bus2, cache2, defaultHistoryPointBudget: 8192);
using var runtime2 = runtimeFactory.Create(
    dataBus: bus2,
    displayCache: cache2,
    sessionId: session2Id,
    defaultHistoryPointBudget: 8192,
    previewLevelBuilder: builder2);
var store2 = storeProvider.GetOrCreate(session2Id);

for (int channelId = 1; channelId <= 4; channelId++)
{
    var wave = BuildWave(
        count: 6000,
        startX: 0.0,
        step: 0.001,
        amplitude: 0.9 + channelId * 0.2,
        phase: channelId * 0.05);
    bus2.PublishData(channelId, wave);
}

var session2Request = new PreviewReadRequest
{
    SessionId = session2Id,
    ViewId = "session2-l1",
    ChannelIds = baseChannels,
    WindowStart = 1.0,
    WindowEnd = 5.0,
    PreviewLevel = PreviewLevel.L1,
    MaxPointsPerChannel = 4000,
    RequireEnvelopeSemantics = true,
    AllowDegradedResult = true,
    RequireCompleteWindow = false
};

bool session2BeforeHit = await ProbeStoreHitAsync(
    store2,
    runtime2,
    session2Id,
    "session2-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session2:BeforeCacheHit={session2BeforeHit}");

int session2Code = await RunQueryAsync(runtime2, session2Request, "Session2.L1.First");
if (session2Code != 0)
{
    return session2Code;
}

bool session2AfterHit = await ProbeStoreHitAsync(
    store2,
    runtime2,
    session2Id,
    "session2-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session2:AfterCacheHit={session2AfterHit}");

bool session1AfterSession2Hit = await ProbeStoreHitAsync(
    store,
    runtime,
    sessionId,
    "smoke-l1",
    baseChannels,
    1.0,
    5.0,
    PreviewLevel.L1,
    4000);
Console.WriteLine($"PreviewStore:Session1:AfterSession2StillHit={session1AfterSession2Hit}");

return 0;
