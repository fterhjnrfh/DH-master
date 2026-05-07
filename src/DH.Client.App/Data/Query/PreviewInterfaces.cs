using System.Threading;
using System.Threading.Tasks;
using System;

namespace DH.Client.App.Data.Query;

public interface IPreviewLevelBuilder
{
    ValueTask<PreviewLevelBuildResult> BuildAsync(
        PreviewLevelBuildRequest request,
        CancellationToken ct = default);
}

public interface IPreviewPyramidStore
{
    ValueTask<PreviewLevelReadResult> TryReadAsync(
        PreviewLevelReadRequest request,
        CancellationToken ct = default);

    ValueTask WriteAsync(
        PreviewLevelBuildResult snapshot,
        CancellationToken ct = default);
}

public interface IPreviewPyramidStoreProvider
{
    IPreviewPyramidStore GetOrCreate(Guid sessionId);

    bool TryRemove(Guid sessionId);
}
