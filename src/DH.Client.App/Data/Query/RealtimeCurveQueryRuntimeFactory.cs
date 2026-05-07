using System;

namespace DH.Client.App.Data.Query;

public sealed class RealtimeCurveQueryRuntimeFactory : IRealtimeCurveQueryRuntimeFactory
{
    private readonly IPreviewPyramidStoreProvider _previewPyramidStoreProvider;

    public RealtimeCurveQueryRuntimeFactory(
        IPreviewPyramidStoreProvider? previewPyramidStoreProvider = null)
    {
        _previewPyramidStoreProvider = previewPyramidStoreProvider ?? new InMemoryPreviewPyramidStoreProvider();
    }

    public RealtimeCurveQueryRuntime Create(
        DataBus dataBus,
        RealtimeDisplayCache? displayCache,
        Guid? sessionId = null,
        int defaultHistoryPointBudget = 8192,
        IPreviewLevelBuilder? previewLevelBuilder = null)
    {
        return new RealtimeCurveQueryRuntime(
            dataBus: dataBus,
            displayCache: displayCache,
            previewLevelBuilder: previewLevelBuilder,
            sessionId: sessionId,
            defaultHistoryPointBudget: defaultHistoryPointBudget,
            previewPyramidStoreProvider: _previewPyramidStoreProvider);
    }
}
