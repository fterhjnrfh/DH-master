using System;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public interface ICurveQueryService
{
    ValueTask<CurveWindowSnapshot> QueryAsync(
        PreviewReadRequest request,
        CancellationToken ct = default);
}

public interface ICurveFrameProvider
{
    CurveFrameVersion GetLatestVersion(Guid sessionId);

    ValueTask<CurveWindowSnapshot> GetLatestAsync(
        PreviewReadRequest request,
        CancellationToken ct = default);
}

public interface IRawSegmentReader
{
    ValueTask<RawSegmentReadResult> ReadAsync(
        RawReadRequest request,
        CancellationToken ct = default);
}

public interface IDataSessionCatalog
{
    ValueTask<SessionDescriptor> OpenAsync(
        string sessionPath,
        CancellationToken ct = default);
}

public interface IRealtimeCurveQueryRuntimeFactory
{
    RealtimeCurveQueryRuntime Create(
        DataBus dataBus,
        RealtimeDisplayCache? displayCache,
        Guid? sessionId = null,
        int defaultHistoryPointBudget = 8192,
        IPreviewLevelBuilder? previewLevelBuilder = null);
}

public interface IPersistedPreviewQueryRuntimeFactory
{
    ValueTask<PersistedPreviewQueryRuntime> CreateAsync(
        string sessionPath,
        CancellationToken ct = default);
}

public interface ICurveStatisticsService
{
    ValueTask<CurveStatisticsResult> QueryStatisticsAsync(
        CurveStatisticsRequest request,
        CancellationToken ct = default);
}
