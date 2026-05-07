using System;
using System.Collections.Generic;
using DH.Client.App.Controls;
using DH.Client.App.Services.Performance;
using DH.Contracts.Models;

namespace DH.Client.App.Views
{
    internal sealed class CurvePanelFrameProviderShim
    {
        private readonly CurvePanelSnapshotCache _snapshotCache;
        private readonly Func<bool> _requiresExternalFrameSnapshot;
        private readonly Func<bool> _useExternalFrameSnapshot;
        private readonly Func<int> _selectedChannelCount;
        private readonly Func<string> _diagnosticTag;
        private readonly Action _onMissingExternalFrame;

        public CurvePanelFrameProviderShim(
            CurvePanelSnapshotCache snapshotCache,
            Func<bool> requiresExternalFrameSnapshot,
            Func<bool> useExternalFrameSnapshot,
            Func<int> selectedChannelCount,
            Func<string> diagnosticTag,
            Action onMissingExternalFrame)
        {
            _snapshotCache = snapshotCache;
            _requiresExternalFrameSnapshot = requiresExternalFrameSnapshot;
            _useExternalFrameSnapshot = useExternalFrameSnapshot;
            _selectedChannelCount = selectedChannelCount;
            _diagnosticTag = diagnosticTag;
            _onMissingExternalFrame = onMissingExternalFrame;
        }

        public double GetWindowMaxAbsY()
        {
            return _snapshotCache.WindowMaxAbsY;
        }

        public IReadOnlyList<CurvePoint> GetPrimaryChannelData()
        {
            if (_requiresExternalFrameSnapshot() && !_useExternalFrameSnapshot())
            {
                LogLegacyFallbackBlocked("primary-provider");
                return Array.Empty<CurvePoint>();
            }

            if (!_useExternalFrameSnapshot())
            {
                LogLegacyFallbackBlocked("primary-provider");
                _onMissingExternalFrame();
            }

            return _snapshotCache.PrimaryChannelData;
        }

        public IReadOnlyDictionary<int, IReadOnlyList<CurvePoint>> GetAllChannelData()
        {
            if (_requiresExternalFrameSnapshot() && !_useExternalFrameSnapshot())
            {
                LogLegacyFallbackBlocked("multi-provider");
                return new Dictionary<int, IReadOnlyList<CurvePoint>>();
            }

            if (!_useExternalFrameSnapshot())
            {
                LogLegacyFallbackBlocked("multi-provider");
                _onMissingExternalFrame();
            }

            var snapshot = _snapshotCache.Snapshot();
            RenderPhaseTimingLogger.LogCurveDataProviderState(
                _diagnosticTag(),
                SkiaMultiChannelView.CurrentAttachedViewCount,
                _selectedChannelCount(),
                cacheDirty: false,
                queryPreviewEnabled: false,
                hasQueryService: false,
                snapshot.WindowData.Count,
                snapshot.MaxActualPointsPerCurve,
                snapshot.TotalActualPoints);
            return snapshot.WindowData;
        }

        private void LogLegacyFallbackBlocked(string provider)
        {
            RenderPhaseTimingLogger.LogCurvePanelLegacyFallback(
                _diagnosticTag(),
                provider,
                _requiresExternalFrameSnapshot(),
                _useExternalFrameSnapshot(),
                _selectedChannelCount());
        }
    }
}
