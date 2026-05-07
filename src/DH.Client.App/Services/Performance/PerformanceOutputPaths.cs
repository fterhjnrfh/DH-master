using System;
using System.IO;
using DH.Client.App.Services;

namespace DH.Client.App.Services.Performance;

public static class PerformanceOutputPaths
{
    private const string OverrideEnvironmentVariable = "DH_PERF_OUTPUT_DIR";
    private const string RealtimeCategory = "realtime";
    private const string StorageCategory = "storage";
    private static readonly string BaseDirectory =
        AppDataPaths.ResolveUnderDataRoot("performance");
    private static readonly string RealtimeRunDirectory;
    private static readonly string StorageRunDirectory;

    static PerformanceOutputPaths()
    {
        string? overrideDirectory = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            RealtimeRunDirectory = overrideDirectory;
            StorageRunDirectory = overrideDirectory;
        }
        else
        {
            Directory.CreateDirectory(BaseDirectory);
            string runName = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            RealtimeRunDirectory = Path.Combine(BaseDirectory, RealtimeCategory, runName);
            StorageRunDirectory = Path.Combine(BaseDirectory, StorageCategory, runName);
        }

        Directory.CreateDirectory(RealtimeRunDirectory);
        Directory.CreateDirectory(StorageRunDirectory);
        Environment.SetEnvironmentVariable(OverrideEnvironmentVariable, RealtimeRunDirectory);
    }

    public static string CurrentRunDirectory => RealtimeRunDirectory;

    public static string CurrentStorageRunDirectory => StorageRunDirectory;

    public static string GetPath(string fileName)
    {
        return Path.Combine(RealtimeRunDirectory, fileName);
    }

    public static string GetStoragePath(string fileName)
    {
        return Path.Combine(StorageRunDirectory, fileName);
    }
}
