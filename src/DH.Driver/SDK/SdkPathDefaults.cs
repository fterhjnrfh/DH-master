using System;
using System.Collections.Generic;
using System.IO;

namespace DH.Driver.SDK;

public static class SdkPathDefaults
{
    public const string ConfigEnvironmentVariable = "DH_SDK_CONFIG";

    public static string ResolveDefaultConfigPath()
    {
        string? envPath = Environment.GetEnvironmentVariable(ConfigEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return Path.GetFullPath(envPath.Trim());
        }

        string? repoRoot = FindRepositoryRoot();
        var candidates = new List<string>
        {
            repoRoot != null ? Path.Combine(repoRoot, "config") : string.Empty,
            repoRoot != null ? Path.Combine(repoRoot, "sdk", "config") : string.Empty,
            Path.Combine(AppContext.BaseDirectory, "config"),
            Path.Combine(Environment.CurrentDirectory, "config")
        };
        candidates.AddRange(EnumerateDriveConfigCandidates());

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return repoRoot != null
            ? Path.Combine(repoRoot, "config")
            : Path.Combine(Environment.CurrentDirectory, "config");
    }

    private static IEnumerable<string> EnumerateDriveConfigCandidates()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            yield break;
        }

        foreach (DriveInfo drive in drives)
        {
            bool isReady;
            try
            {
                isReady = drive.IsReady;
            }
            catch
            {
                continue;
            }

            if (isReady)
            {
                yield return Path.Combine(drive.RootDirectory.FullName, "DHDAS", "config");
            }
        }
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
                    if (File.Exists(Path.Combine(dir.FullName, "DH.sln")))
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
