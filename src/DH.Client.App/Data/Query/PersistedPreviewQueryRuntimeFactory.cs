using System;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public sealed class PersistedPreviewQueryRuntimeFactory : IPersistedPreviewQueryRuntimeFactory
{
    private readonly IDataSessionCatalog _sessionCatalog;
    private readonly ISessionArtifactLocator _artifactLocator;

    public PersistedPreviewQueryRuntimeFactory(
        IDataSessionCatalog? sessionCatalog = null,
        ISessionArtifactLocator? artifactLocator = null)
    {
        _artifactLocator = artifactLocator ?? new FileSystemSessionArtifactLocator();
        _sessionCatalog = sessionCatalog ?? new FileSystemDataSessionCatalog(_artifactLocator);
    }

    public async ValueTask<PersistedPreviewQueryRuntime> CreateAsync(
        string sessionPath,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            throw new ArgumentException("Session path is required.", nameof(sessionPath));
        }

        SessionDescriptor session = await _sessionCatalog.OpenAsync(sessionPath, ct);
        return new PersistedPreviewQueryRuntime(sessionPath, session, _artifactLocator);
    }
}
