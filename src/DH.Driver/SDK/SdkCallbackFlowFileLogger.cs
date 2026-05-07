using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DH.Driver.SDK;

internal static class SdkCallbackFlowFileLogger
{
    private const string OverrideEnvironmentVariable = "DH_PERF_OUTPUT_DIR";
    private static readonly object SyncRoot = new();
    private static readonly string BaseDirectory =
        Path.Combine(Environment.CurrentDirectory, "data", "performance");
    private static readonly string LogPath = InitializeLogPath();

    private static string InitializeLogPath()
    {
        string? runDirectory = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            Directory.CreateDirectory(BaseDirectory);
            runDirectory = Path.Combine(
                BaseDirectory,
                "realtime",
                DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable(OverrideEnvironmentVariable, runDirectory);
        }

        Directory.CreateDirectory(runDirectory);
        string path = Path.Combine(runDirectory, "sdk-callback-flow.log");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty, Encoding.UTF8);
        }

        return path;
    }

    public static void WriteLine(params string[] fields)
    {
        string line =
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
            " | SdkCallbackFlow | " +
            string.Join(" | ", fields);

        lock (SyncRoot)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }
}
