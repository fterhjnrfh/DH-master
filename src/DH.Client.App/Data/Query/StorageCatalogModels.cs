using System;
using System.Collections.Generic;

namespace DH.Client.App.Data.Query;

public sealed record SessionArtifactPaths
{
    public string SessionPath { get; init; } = string.Empty;
    public string? ManifestPath { get; init; }
    public string? CatalogPath { get; init; }
    public string? PreviewIndexManifestPath { get; init; }
    public IReadOnlyList<string> ChannelIndexPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TimeIndexPaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewRoots { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PreviewFiles { get; init; } = Array.Empty<string>();
}
