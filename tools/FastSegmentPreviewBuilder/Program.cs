using System.Diagnostics;
using DH.Client.App.Services.Storage;

string? sessionPath = null;
List<string>? levels = null;

for (int i = 0; i < args.Length; i++)
{
    if (string.Equals(args[i], "--session-path", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        sessionPath = args[++i];
        continue;
    }

    if (string.Equals(args[i], "--levels", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        levels = args[++i]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        continue;
    }

    if (string.Equals(args[i], "--help", StringComparison.OrdinalIgnoreCase)
        || string.Equals(args[i], "-h", StringComparison.OrdinalIgnoreCase))
    {
        PrintUsage();
        return 0;
    }
}

if (string.IsNullOrWhiteSpace(sessionPath))
{
    PrintUsage();
    return 2;
}

sessionPath = Path.GetFullPath(sessionPath);
Console.WriteLine($"SessionPath={sessionPath}");
Console.WriteLine($"Levels={(levels is { Count: > 0 } ? string.Join(",", levels) : "L1,L2,L3,L4")}");
Console.WriteLine($"StartedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}");

var stopwatch = Stopwatch.StartNew();
var builder = new FastSegmentPreviewSidecarBuilder();
long lastBytes = 0;
DateTime lastProgressUtc = DateTime.UtcNow;

try
{
    FastSegmentPreviewBuildResult result = builder.Build(
        sessionPath,
        levels,
        progress =>
        {
            DateTime now = DateTime.UtcNow;
            double deltaSeconds = Math.Max(0.001, (now - lastProgressUtc).TotalSeconds);
            long byteDelta = progress.PayloadBytesProcessed - lastBytes;
            lastBytes = progress.PayloadBytesProcessed;
            lastProgressUtc = now;
            double percent = progress.TotalFiles > 0
                ? progress.FilesProcessed * 100.0 / progress.TotalFiles
                : 0.0;
            Console.WriteLine(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | files={progress.FilesProcessed:N0}/{progress.TotalFiles:N0} {percent:F2}% | payload={progress.PayloadBytesProcessed / 1024d / 1024d / 1024d:F2} GiB | rate={byteDelta / 1024d / 1024d / deltaSeconds:F1} MiB/s | elapsed={stopwatch.Elapsed:hh\\:mm\\:ss}");
        });

    stopwatch.Stop();
    Console.WriteLine("status=passed");
    Console.WriteLine($"Elapsed={result.Elapsed:hh\\:mm\\:ss}");
    Console.WriteLine($"ArtifactRootPath={result.ArtifactRootPath}");
    Console.WriteLine($"PreviewIndexPath={result.PreviewIndexPath}");
    Console.WriteLine($"FastSegmentFiles={result.FastSegmentFileCount}");
    Console.WriteLine($"TdmsSegmentFiles={result.TdmsSegmentFileCount}");
    Console.WriteLine($"Channels={result.ChannelCount}");
    Console.WriteLine($"Levels={string.Join(",", result.Levels)}");
    Console.WriteLine($"PayloadGiB={result.PayloadBytesProcessed / 1024d / 1024d / 1024d:F3}");
    return 0;
}
catch (Exception ex)
{
    stopwatch.Stop();
    Console.Error.WriteLine("status=failed");
    Console.Error.WriteLine(ex);
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  FastSegmentPreviewBuilder --session-path <session-folder-or-artifacts-folder> [--levels L2,L3,L4]");
}
