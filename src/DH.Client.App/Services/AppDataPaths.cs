using System;
using System.IO;

namespace DH.Client.App.Services;

public static class AppDataPaths
{
    private const string DataRootEnvironmentVariable = "DH_DATA_ROOT";
    private const string SolutionFileName = "DH.sln";

    public static string RepositoryRoot => FindRepositoryRoot() ?? Environment.CurrentDirectory;

    public static string DataRoot => ResolveDataRoot();

    public static string ResolveUnderDataRoot(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return DataRoot;
        }

        return Path.GetFullPath(Path.Combine(DataRoot, relativePath));
    }

    public static string ResolveStoragePath(string? path)
    {
        try
        {
            string normalizedPath = path?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return DataRoot;
            }

            string slashNormalized = normalizedPath.Replace('/', '\\');
            if (slashNormalized.Equals("data", StringComparison.OrdinalIgnoreCase)
                || slashNormalized.Equals(".\\data", StringComparison.OrdinalIgnoreCase))
            {
                return DataRoot;
            }

            if (IsWindowsAbsolutePath(normalizedPath))
            {
                return slashNormalized;
            }

            if (Path.IsPathRooted(normalizedPath))
            {
                return Path.GetFullPath(normalizedPath);
            }

            return Path.GetFullPath(Path.Combine(RepositoryRoot, normalizedPath));
        }
        catch
        {
            return string.IsNullOrWhiteSpace(path) ? DataRoot : Path.GetFullPath(path);
        }
    }

    private static bool IsWindowsAbsolutePath(string path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && (path[2] == '\\' || path[2] == '/');
    }

    private static string ResolveDataRoot()
    {
        string? overrideRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            return Path.GetFullPath(overrideRoot.Trim());
        }

        return Path.Combine(RepositoryRoot, "data");
    }

    private static string? FindRepositoryRoot()
    {
        string[] starts =
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        };

        foreach (string start in starts)
        {
            try
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    if (File.Exists(Path.Combine(dir.FullName, SolutionFileName)))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
            }
        }

        return null;
    }
}
