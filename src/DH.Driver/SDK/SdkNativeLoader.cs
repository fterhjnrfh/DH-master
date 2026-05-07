using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DH.Driver.SDK;

public static class SdkNativeLoader
{
    public const string DllName = "Hardware_Standard_C_Interface.dll";
    public const string DllDirectoryEnvironmentVariable = "DH_SDK_DLL_DIR";
    public const string SdkRootEnvironmentVariable = "DH_SDK_ROOT";

    private static readonly object Sync = new();
    private static bool _loaded;
    private static string? _loadedPath;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    public static string? LoadedPath => _loadedPath;

    public static void EnsureLoaded(string? configPath = null)
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            var attempts = new List<string>();
            foreach (string directory in EnumerateCandidateDirectories(configPath))
            {
                string dllPath = Path.Combine(directory, DllName);
                attempts.Add(dllPath);
                if (!File.Exists(dllPath))
                {
                    continue;
                }

                AddSearchDirectory(directory);
                try
                {
                    NativeLibrary.Load(dllPath);
                    _loaded = true;
                    _loadedPath = dllPath;
                    Console.WriteLine($"[SDK] Native DLL loaded: {dllPath}");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SDK] Native DLL load failed: {dllPath}; {ex.Message}");
                }
            }

            string message = "Unable to find or load " + DllName + ". Tried: " + string.Join("; ", attempts);
            throw new DllNotFoundException(message);
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(string? configPath)
    {
        foreach (string? path in new[]
        {
            Environment.GetEnvironmentVariable(DllDirectoryEnvironmentVariable),
            Environment.GetEnvironmentVariable(SdkRootEnvironmentVariable),
            configPath,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            FindRepositoryRoot()
        })
        {
            foreach (string candidate in ExpandCandidate(path))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> ExpandCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            yield break;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().TrimEnd('\\', '/'));
        }
        catch
        {
            yield break;
        }

        if (File.Exists(fullPath))
        {
            fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new DirectoryInfo(fullPath);
        while (current != null)
        {
            if (seen.Add(current.FullName))
            {
                yield return current.FullName;
            }

            string binRelease = Path.Combine(current.FullName, "bin", "Release", "net6.0-windows7.0");
            if (Directory.Exists(binRelease) && seen.Add(binRelease))
            {
                yield return binRelease;
            }

            string binDebug = Path.Combine(current.FullName, "bin", "Debug", "net6.0-windows7.0");
            if (Directory.Exists(binDebug) && seen.Add(binDebug))
            {
                yield return binDebug;
            }

            current = current.Parent;
        }
    }

    private static void AddSearchDirectory(string directory)
    {
        try { SetDllDirectory(directory); } catch { }
        try { AddDllDirectory(directory); } catch { }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!path.Split(Path.PathSeparator).Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + path);
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
