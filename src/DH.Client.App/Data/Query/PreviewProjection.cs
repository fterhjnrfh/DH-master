using System;
using System.Collections.Generic;
using System.Linq;
using DH.Contracts.Models;

namespace DH.Client.App.Data.Query;

public readonly record struct ProjectedPreviewWindow(
    IReadOnlyList<CurvePoint> Points,
    int ActualPointCount,
    double MaxAbsY)
{
    public static ProjectedPreviewWindow Empty { get; } =
        new(Array.Empty<CurvePoint>(), 0, 1.0);
}

public static class PreviewProjection
{
    public static ProjectedPreviewWindow ProjectWindowWithLimit(
        IReadOnlyList<CurvePoint> source,
        double windowStart,
        double windowEnd,
        int maxPoints,
        bool requireEnvelope)
    {
        if (source == null || source.Count == 0)
        {
            return ProjectedPreviewWindow.Empty;
        }

        int startIndex = FindFirstPointAtOrAfter(source, windowStart);
        if (startIndex >= source.Count)
        {
            return ProjectedPreviewWindow.Empty;
        }

        int endIndex = FindFirstPointAtOrAfter(source, windowEnd);
        int count = endIndex - startIndex;
        if (count <= 0)
        {
            return ProjectedPreviewWindow.Empty;
        }

        if (maxPoints > 1 && count > maxPoints)
        {
            var reduced = requireEnvelope
                ? BuildEnvelopeWindowPoints(source, startIndex, endIndex - 1, maxPoints)
                : BuildUniformWindowPoints(source, startIndex, endIndex - 1, maxPoints);

            return new ProjectedPreviewWindow(
                reduced,
                count,
                ComputeMaxAbsY(source, startIndex, endIndex - 1));
        }

        CurvePoint[] result = new CurvePoint[count];
        double maxAbsY = 1.0;
        for (int i = 0; i < count; i++)
        {
            var point = source[startIndex + i];
            result[i] = point;
            maxAbsY = Math.Max(maxAbsY, Math.Abs(point.Y));
        }

        return new ProjectedPreviewWindow(result, count, maxAbsY);
    }

    public static int CountPointsInWindow(
        IReadOnlyList<CurvePoint> source,
        double windowStart,
        double windowEnd)
    {
        if (source == null || source.Count == 0)
        {
            return 0;
        }

        int startIndex = FindFirstPointAtOrAfter(source, windowStart);
        if (startIndex >= source.Count)
        {
            return 0;
        }

        int endIndex = FindFirstPointAtOrAfter(source, windowEnd);
        return Math.Max(0, endIndex - startIndex);
    }

    public static bool IsWindowCovered(
        IReadOnlyList<CurvePoint> source,
        double windowStart,
        double windowEnd)
    {
        if (source == null || source.Count == 0)
        {
            return false;
        }

        return source[0].X <= windowStart && source[^1].X >= windowEnd;
    }

    public static int FindFirstPointAtOrAfter(IReadOnlyList<CurvePoint> data, double target)
    {
        int low = 0;
        int high = data.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (data[mid].X < target)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private static IReadOnlyList<CurvePoint> BuildUniformWindowPoints(
        IReadOnlyList<CurvePoint> source,
        int startIndex,
        int endIndex,
        int maxPoints)
    {
        int count = endIndex - startIndex + 1;
        if (count <= maxPoints)
        {
            CurvePoint[] projected = new CurvePoint[count];
            for (int i = 0; i < count; i++)
            {
                projected[i] = source[startIndex + i];
            }

            return projected;
        }

        var reduced = new CurvePoint[maxPoints];
        double step = (count - 1) / (double)(maxPoints - 1);
        for (int i = 0; i < maxPoints; i++)
        {
            int sourceIndex = startIndex + (int)Math.Round(i * step);
            sourceIndex = Math.Clamp(sourceIndex, startIndex, endIndex);
            reduced[i] = source[sourceIndex];
        }

        return reduced;
    }

    private static IReadOnlyList<CurvePoint> BuildEnvelopeWindowPoints(
        IReadOnlyList<CurvePoint> source,
        int startIndex,
        int endIndex,
        int maxPoints)
    {
        int count = endIndex - startIndex + 1;
        if (count <= maxPoints)
        {
            CurvePoint[] projected = new CurvePoint[count];
            for (int i = 0; i < count; i++)
            {
                projected[i] = source[startIndex + i];
            }

            return projected;
        }

        int bucketCount = Math.Max(1, Math.Min(count, maxPoints / 2));
        var points = new List<CurvePoint>(Math.Min(maxPoints, bucketCount * 4));

        for (int bucketIndex = 0; bucketIndex < bucketCount; bucketIndex++)
        {
            int bucketStart = startIndex + (int)Math.Floor(bucketIndex * count / (double)bucketCount);
            int bucketEnd = startIndex + (int)Math.Floor((bucketIndex + 1) * count / (double)bucketCount) - 1;

            bucketStart = Math.Clamp(bucketStart, startIndex, endIndex);
            bucketEnd = Math.Clamp(bucketEnd, bucketStart, endIndex);

            int minIndex = bucketStart;
            int maxIndex = bucketStart;
            double minValue = source[bucketStart].Y;
            double maxValue = minValue;

            for (int pointIndex = bucketStart + 1; pointIndex <= bucketEnd; pointIndex++)
            {
                double value = source[pointIndex].Y;
                if (value < minValue)
                {
                    minValue = value;
                    minIndex = pointIndex;
                }

                if (value > maxValue)
                {
                    maxValue = value;
                    maxIndex = pointIndex;
                }
            }

            AppendEnvelopePointsInTimeOrder(points, source, bucketStart, minIndex, maxIndex, bucketEnd);
        }

        if (points.Count > maxPoints)
        {
            points.RemoveRange(maxPoints, points.Count - maxPoints);
        }

        return points;
    }

    private static void AppendEnvelopePointsInTimeOrder(
        List<CurvePoint> points,
        IReadOnlyList<CurvePoint> source,
        int bucketStart,
        int minIndex,
        int maxIndex,
        int bucketEnd)
    {
        Span<int> indices = stackalloc int[4];
        indices[0] = bucketStart;
        indices[1] = minIndex;
        indices[2] = maxIndex;
        indices[3] = bucketEnd;
        indices.Sort();

        int? lastIndex = null;
        for (int i = 0; i < indices.Length; i++)
        {
            int currentIndex = indices[i];
            if (lastIndex == currentIndex)
            {
                continue;
            }

            AppendPoint(points, source[currentIndex]);
            lastIndex = currentIndex;
        }
    }

    private static void AppendPoint(List<CurvePoint> points, CurvePoint point)
    {
        if (points.Count > 0)
        {
            var last = points[^1];
            if (Math.Abs(last.X - point.X) < 1e-12 && Math.Abs(last.Y - point.Y) < 1e-12)
            {
                return;
            }
        }

        points.Add(point);
    }

    private static double ComputeMaxAbsY(
        IReadOnlyList<CurvePoint> source,
        int startIndex,
        int endIndex)
    {
        double maxAbsY = 1.0;
        for (int i = startIndex; i <= endIndex; i++)
        {
            maxAbsY = Math.Max(maxAbsY, Math.Abs(source[i].Y));
        }

        return maxAbsY;
    }
}
