using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public interface ISessionArtifactLocator
{
    ValueTask<SessionArtifactPaths> DiscoverAsync(
        string sessionPath,
        CancellationToken ct = default);
}
