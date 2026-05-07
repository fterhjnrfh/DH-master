using System;
using System.Collections.Generic;
using DH.Contracts.Models;

namespace DH.Client.App.Views
{
    internal sealed record CurvePanelCachedSnapshot(
        IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> WindowData,
        IReadOnlyList<CurvePoint> PrimaryChannelData,
        double WindowMaxAbsY,
        int MaxActualPointsPerCurve,
        int TotalActualPoints);

    internal sealed class CurvePanelSnapshotCache
    {
        private readonly object _gate = new();
        private IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> _windowData =
            new Dictionary<int, IReadOnlyList<CurvePoint>>();
        private IReadOnlyList<CurvePoint> _primaryChannelData =
            Array.Empty<CurvePoint>();
        private double _windowMaxAbsY = 1.0;
        private int _maxActualPointsPerCurve;
        private int _totalActualPoints;

        public double WindowMaxAbsY
        {
            get
            {
                lock (_gate)
                {
                    return _windowMaxAbsY;
                }
            }
        }

        public IReadOnlyList<CurvePoint> PrimaryChannelData
        {
            get
            {
                lock (_gate)
                {
                    return _primaryChannelData;
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _windowData = new Dictionary<int, IReadOnlyList<CurvePoint>>();
                _primaryChannelData = Array.Empty<CurvePoint>();
                _windowMaxAbsY = 1.0;
                _maxActualPointsPerCurve = 0;
                _totalActualPoints = 0;
            }
        }

        public void Store(
            IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> windowData,
            IReadOnlyList<CurvePoint> primaryChannelData,
            double windowMaxAbsY,
            int maxActualPointsPerCurve,
            int totalActualPoints)
        {
            lock (_gate)
            {
                _windowData = windowData;
                _primaryChannelData = primaryChannelData;
                _windowMaxAbsY = windowMaxAbsY;
                _maxActualPointsPerCurve = maxActualPointsPerCurve;
                _totalActualPoints = totalActualPoints;
            }
        }

        public CurvePanelCachedSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new CurvePanelCachedSnapshot(
                    _windowData,
                    _primaryChannelData,
                    _windowMaxAbsY,
                    _maxActualPointsPerCurve,
                    _totalActualPoints);
            }
        }
    }
}
