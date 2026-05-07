using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

namespace DH.Client.App.Services.Performance;

public static class AmplitudeOverflowDiagnosticLogger
{
    private static readonly object Sync = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastWriteUtc = new(StringComparer.Ordinal);
    private static readonly string LogPath;

    static AmplitudeOverflowDiagnosticLogger()
    {
        LogPath = PerformanceOutputPaths.GetPath("curve-amplitude-overflow.log");
    }

    public static string CurrentLogPath => LogPath;

    public static void Log(
        string viewTag,
        int channelId,
        double pointX,
        double pointY,
        float projectedY,
        float plotTop,
        float plotBottom,
        double windowMaxAbsY,
        double lockedMaxAbsY,
        float scaleY,
        float centerY,
        int visiblePointCount,
        int totalChannelCount,
        float width,
        float height)
    {
        string throttleKey = $"{viewTag}|{channelId}";
        DateTime now = DateTime.UtcNow;
        if (LastWriteUtc.TryGetValue(throttleKey, out var last) && (now - last).TotalSeconds < 2)
        {
            return;
        }

        LastWriteUtc[throttleKey] = now;

        string line = string.Join(" | ",
            now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            $"view={viewTag}",
            $"channel={channelId}",
            $"pointX={pointX.ToString("F6", CultureInfo.InvariantCulture)}",
            $"pointY={pointY.ToString("F6", CultureInfo.InvariantCulture)}",
            $"projectedY={projectedY.ToString("F3", CultureInfo.InvariantCulture)}",
            $"plotTop={plotTop.ToString("F3", CultureInfo.InvariantCulture)}",
            $"plotBottom={plotBottom.ToString("F3", CultureInfo.InvariantCulture)}",
            $"windowMaxAbsY={windowMaxAbsY.ToString("F6", CultureInfo.InvariantCulture)}",
            $"lockedMaxAbsY={lockedMaxAbsY.ToString("F6", CultureInfo.InvariantCulture)}",
            $"scaleY={scaleY.ToString("F6", CultureInfo.InvariantCulture)}",
            $"centerY={centerY.ToString("F3", CultureInfo.InvariantCulture)}",
            $"visiblePointCount={visiblePointCount}",
            $"totalChannelCount={totalChannelCount}",
            $"width={width.ToString("F1", CultureInfo.InvariantCulture)}",
            $"height={height.ToString("F1", CultureInfo.InvariantCulture)}");

        lock (Sync)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
