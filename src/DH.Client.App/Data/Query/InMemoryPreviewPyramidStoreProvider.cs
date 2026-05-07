using System;
using System.Collections.Concurrent;

namespace DH.Client.App.Data.Query;

public sealed class InMemoryPreviewPyramidStoreProvider : IPreviewPyramidStoreProvider
{
    private readonly ConcurrentDictionary<Guid, IPreviewPyramidStore> _stores = new();

    public IPreviewPyramidStore GetOrCreate(Guid sessionId)
    {
        return _stores.GetOrAdd(sessionId, static _ => new InMemoryPreviewPyramidStore());
    }

    public bool TryRemove(Guid sessionId)
    {
        return _stores.TryRemove(sessionId, out _);
    }
}
