using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Client.App.Data.Query;

public sealed class FileSystemSessionArtifactLocator : ISessionArtifactLocator
{
    private static readonly string[] ManifestFileNames =
    {
        "manifest.json",
        "session.manifest.json"
    };

    private static readonly string[] CatalogFileNames =
    {
        "session.catalog.db"
    };

    private static readonly string[] PreviewRootNames =
    {
        "preview",
        "previews",
        "lod",
        "preview_levels"
    };

    public ValueTask<SessionArtifactPaths> DiscoverAsync(
        string sessionPath,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(sessionPath))
        {
            throw new ArgumentException("Session path is required.", nameof(sessionPath));
        }

        string normalizedPath = Path.GetFullPath(sessionPath);
        if (!Directory.Exists(normalizedPath))
        {
            throw new DirectoryNotFoundException(normalizedPath);
        }

        string? manifestPath = FindFirstFile(normalizedPath, ManifestFileNames);
        string? catalogPath = FindFirstFile(normalizedPath, CatalogFileNames);
        string? previewIndexManifestPath = FindFirstFile(
            normalizedPath,
            new[] { "preview.index.json" });

        var channelIndexPaths = Directory
            .EnumerateFiles(normalizedPath, "channel.index", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var timeIndexPaths = Directory
            .EnumerateFiles(normalizedPath, "time.index", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var previewRoots = Directory
            .EnumerateDirectories(normalizedPath, "*", SearchOption.AllDirectories)
            .Where(path => PreviewRootNames.Contains(
                Path.GetFileName(path),
                StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var previewFiles = previewRoots
            .SelectMany(path => Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return ValueTask.FromResult(new SessionArtifactPaths
        {
            SessionPath = normalizedPath,
            ManifestPath = manifestPath,
            CatalogPath = catalogPath,
            PreviewIndexManifestPath = previewIndexManifestPath,
            ChannelIndexPaths = channelIndexPaths,
            TimeIndexPaths = timeIndexPaths,
            PreviewRoots = previewRoots,
            PreviewFiles = previewFiles
        });
    }

    private static string? FindFirstFile(string rootPath, IEnumerable<string> fileNames)
    {
        foreach (string fileName in fileNames)
        {
            string directPath = Path.Combine(rootPath, fileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }
        }

        foreach (string fileName in fileNames)
        {
            string? discovered = Directory
                .EnumerateFiles(rootPath, fileName, SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(discovered))
            {
                return discovered;
            }
        }

        return null;
    }
}
