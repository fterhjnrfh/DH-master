using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace DH.Client.App.Services.Performance;

public sealed class PerformanceMetricsRecorder : IDisposable
{
    private const string GpuCategoryName = "GPU Engine";
    private const string GpuCounterName = "Utilization Percentage";
    private readonly Process _process;
    private readonly string _csvPath;
    private readonly StreamWriter _writer;
    private readonly object _sync = new();
    private readonly Dictionary<string, PerformanceCounter> _gpuCounters = new(StringComparer.OrdinalIgnoreCase);
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc;
    private bool _headerWritten;

    public PerformanceMetricsRecorder()
    {
        _process = Process.GetCurrentProcess();
        _lastCpuTime = _process.TotalProcessorTime;
        _lastCpuSampleUtc = DateTime.UtcNow;

        _csvPath = PerformanceOutputPaths.GetPath("curve-performance.csv");
        _writer = new StreamWriter(_csvPath, append: false);
    }

    public string CsvPath => _csvPath;

    public PerformanceMetricsSample Capture(PerformanceMetricsCaptureContext context)
    {
        DateTime now = DateTime.UtcNow;
        TimeSpan cpuNow = _process.TotalProcessorTime;
        double wallMs = Math.Max(1.0, (now - _lastCpuSampleUtc).TotalMilliseconds);
        double cpuMs = Math.Max(0.0, (cpuNow - _lastCpuTime).TotalMilliseconds);
        double cpuPercent = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
        double gpuPercent = ReadProcessGpuPercent(_process.Id);
        double workingSetMb = _process.WorkingSet64 / (1024.0 * 1024.0);

        int effectiveViewCount = Math.Max(1, Math.Max(context.ViewCount, context.AttachedViewCount));
        double targetRenderCallsPerSecond = effectiveViewCount * context.TargetFramesPerSecond;
        double renderCapacityPercent = targetRenderCallsPerSecond > 0.0
            ? context.RenderCallsPerSecond / targetRenderCallsPerSecond * 100.0
            : 0.0;
        double fpsGap = context.FramesPerSecond - context.TargetFramesPerSecond;
        double pointResolutionPercent = context.TargetPointsPerCurve > 0
            ? context.MaxEstimatedPointsPerCurve / (double)context.TargetPointsPerCurve * 100.0
            : 0.0;
        double actualPointCoveragePercent = context.TargetPointsPerCurve > 0
            ? context.MaxActualPointsPerCurve / (double)context.TargetPointsPerCurve * 100.0
            : 0.0;

        var sample = new PerformanceMetricsSample(
            TimestampLocal: DateTime.Now,
            ScenarioLabel: $"{context.ViewCount}V-{context.MaxCurvesPerView}C",
            FrameSource: context.FrameSource,
            ViewCount: context.ViewCount,
            AttachedViewCount: context.AttachedViewCount,
            CurveCount: context.CurveCount,
            AverageCurvesPerView: context.AverageCurvesPerView,
            MaxCurvesPerView: context.MaxCurvesPerView,
            FramesPerSecond: context.FramesPerSecond,
            TargetFramesPerSecond: context.TargetFramesPerSecond,
            FpsGap: fpsGap,
            MeetsPerViewFpsTarget: context.FramesPerSecond >= context.TargetFramesPerSecond,
            RenderCallsPerSecond: context.RenderCallsPerSecond,
            TargetRenderCallsPerSecond: targetRenderCallsPerSecond,
            RenderCapacityPercent: renderCapacityPercent,
            AverageEstimatedPointsPerCurve: context.AverageEstimatedPointsPerCurve,
            MaxEstimatedPointsPerCurve: context.MaxEstimatedPointsPerCurve,
            AverageActualPointsPerCurve: context.AverageActualPointsPerCurve,
            MaxActualPointsPerCurve: context.MaxActualPointsPerCurve,
            TargetPointsPerCurve: context.TargetPointsPerCurve,
            PointResolutionPercent: pointResolutionPercent,
            ActualPointCoveragePercent: actualPointCoveragePercent,
            TotalEstimatedPoints: context.TotalEstimatedPoints,
            TotalActualPoints: context.TotalActualPoints,
            CpuPercent: cpuPercent,
            GpuPercent: gpuPercent,
            WorkingSetMb: workingSetMb);

        _lastCpuSampleUtc = now;
        _lastCpuTime = cpuNow;
        WriteSample(sample);
        return sample;
    }

    private void WriteSample(PerformanceMetricsSample sample)
    {
        lock (_sync)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",",
                    "Timestamp",
                    "Scenario",
                    "FrameSource",
                    "ViewCount",
                    "AttachedViewCount",
                    "CurveCount",
                    "AverageCurvesPerView",
                    "MaxCurvesPerView",
                    "FPS",
                    "TargetFPS",
                    "FpsGap",
                    "MeetsPerViewFpsTarget",
                    "RenderCallsPerSecond",
                    "TargetRenderCallsPerSecond",
                    "RenderCapacityPercent",
                    "AverageEstimatedPointsPerCurve",
                    "MaxEstimatedPointsPerCurve",
                    "AverageActualPointsPerCurve",
                    "MaxActualPointsPerCurve",
                    "TargetPointsPerCurve",
                    "PointResolutionPercent",
                    "ActualPointCoveragePercent",
                    "TotalEstimatedPoints",
                    "TotalActualPoints",
                    "CPUPercent",
                    "GPUPercent",
                    "WorkingSetMB"));
                _headerWritten = true;
            }

            _writer.WriteLine(string.Join(",",
                sample.TimestampLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                sample.ScenarioLabel,
                sample.FrameSource,
                sample.ViewCount.ToString(CultureInfo.InvariantCulture),
                sample.AttachedViewCount.ToString(CultureInfo.InvariantCulture),
                sample.CurveCount.ToString(CultureInfo.InvariantCulture),
                sample.AverageCurvesPerView.ToString("F2", CultureInfo.InvariantCulture),
                sample.MaxCurvesPerView.ToString(CultureInfo.InvariantCulture),
                sample.FramesPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                sample.TargetFramesPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                sample.FpsGap.ToString("F2", CultureInfo.InvariantCulture),
                sample.MeetsPerViewFpsTarget ? "1" : "0",
                sample.RenderCallsPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                sample.TargetRenderCallsPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                sample.RenderCapacityPercent.ToString("F2", CultureInfo.InvariantCulture),
                sample.AverageEstimatedPointsPerCurve.ToString("F2", CultureInfo.InvariantCulture),
                sample.MaxEstimatedPointsPerCurve.ToString(CultureInfo.InvariantCulture),
                sample.AverageActualPointsPerCurve.ToString("F2", CultureInfo.InvariantCulture),
                sample.MaxActualPointsPerCurve.ToString(CultureInfo.InvariantCulture),
                sample.TargetPointsPerCurve.ToString(CultureInfo.InvariantCulture),
                sample.PointResolutionPercent.ToString("F2", CultureInfo.InvariantCulture),
                sample.ActualPointCoveragePercent.ToString("F2", CultureInfo.InvariantCulture),
                sample.TotalEstimatedPoints.ToString(CultureInfo.InvariantCulture),
                sample.TotalActualPoints.ToString(CultureInfo.InvariantCulture),
                sample.CpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                sample.GpuPercent.ToString("F2", CultureInfo.InvariantCulture),
                sample.WorkingSetMb.ToString("F2", CultureInfo.InvariantCulture)));
            _writer.Flush();
        }
    }

    private double ReadProcessGpuPercent(int processId)
    {
        try
        {
            var category = new PerformanceCounterCategory(GpuCategoryName);
            HashSet<string> instanceNames = category
                .GetInstanceNames()
                .Where(name => name.Contains($"pid_{processId}_", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string staleInstanceName in _gpuCounters.Keys.Except(instanceNames).ToList())
            {
                _gpuCounters[staleInstanceName].Dispose();
                _gpuCounters.Remove(staleInstanceName);
            }

            foreach (string instanceName in instanceNames)
            {
                if (_gpuCounters.ContainsKey(instanceName))
                {
                    continue;
                }

                var counter = new PerformanceCounter(GpuCategoryName, GpuCounterName, instanceName, readOnly: true);
                _ = counter.NextValue();
                _gpuCounters[instanceName] = counter;
            }

            double total = 0.0;
            foreach (PerformanceCounter counter in _gpuCounters.Values)
            {
                total += counter.NextValue();
            }

            return Math.Clamp(total, 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (PerformanceCounter counter in _gpuCounters.Values)
            {
                counter.Dispose();
            }

            _gpuCounters.Clear();
            _writer.Dispose();
        }
    }
}

public readonly record struct PerformanceMetricsCaptureContext(
    string FrameSource,
    int ViewCount,
    int AttachedViewCount,
    int CurveCount,
    double AverageCurvesPerView,
    int MaxCurvesPerView,
    double FramesPerSecond,
    double TargetFramesPerSecond,
    double RenderCallsPerSecond,
    double AverageEstimatedPointsPerCurve,
    int MaxEstimatedPointsPerCurve,
    double AverageActualPointsPerCurve,
    int MaxActualPointsPerCurve,
    int TargetPointsPerCurve,
    int TotalEstimatedPoints,
    int TotalActualPoints);

public readonly record struct PerformanceMetricsSample(
    DateTime TimestampLocal,
    string ScenarioLabel,
    string FrameSource,
    int ViewCount,
    int AttachedViewCount,
    int CurveCount,
    double AverageCurvesPerView,
    int MaxCurvesPerView,
    double FramesPerSecond,
    double TargetFramesPerSecond,
    double FpsGap,
    bool MeetsPerViewFpsTarget,
    double RenderCallsPerSecond,
    double TargetRenderCallsPerSecond,
    double RenderCapacityPercent,
    double AverageEstimatedPointsPerCurve,
    int MaxEstimatedPointsPerCurve,
    double AverageActualPointsPerCurve,
    int MaxActualPointsPerCurve,
    int TargetPointsPerCurve,
    double PointResolutionPercent,
    double ActualPointCoveragePercent,
    int TotalEstimatedPoints,
    int TotalActualPoints,
    double CpuPercent,
    double GpuPercent,
    double WorkingSetMb);
