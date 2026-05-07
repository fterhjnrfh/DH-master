using System;
using System.Collections.Generic;
using System.Linq;
using NewAvalonia.Algorithms;
using NewAvalonia.Models;

namespace NewAvalonia.Services
{
    /// <summary>
    /// 提供实时波形算法处理能力的轻量服务。
    /// </summary>
    public sealed class RealtimeWaveformProcessor
    {
        private const int MaxHistorySamples = 8192;

        private readonly Dictionary<string, Func<AlgorithmModuleBase>> _algorithmFactories;
        private readonly List<float> _history = new();
        private readonly object _gate = new();

        private AlgorithmModuleBase? _currentAlgorithm;
        private string _currentAlgorithmName = string.Empty;
        private Dictionary<string, object> _currentParameters = new();

        public RealtimeWaveformProcessor()
        {
            _algorithmFactories = new Dictionary<string, Func<AlgorithmModuleBase>>(StringComparer.OrdinalIgnoreCase)
            {
                ["移动平均滤波器"] = () => new MovingAverageFilter(),
                ["高斯滤波"] = () => new GaussianFilter(),
                ["中值滤波"] = () => new MedianFilter(),
                ["信号平滑"] = () => new SignalSmoother()
            };
        }

        public IReadOnlyList<string> GetAvailableAlgorithms()
        {
            return _algorithmFactories.Keys.OrderBy(n => n).ToList();
        }

        public void SetAlgorithm(string algorithmName)
        {
            lock (_gate)
            {
                if (string.IsNullOrWhiteSpace(algorithmName) || !_algorithmFactories.TryGetValue(algorithmName, out var factory))
                {
                    _currentAlgorithm = null;
                    _currentAlgorithmName = string.Empty;
                    _currentParameters = new Dictionary<string, object>();
                    _history.Clear();
                    return;
                }

                _currentAlgorithm = factory();
                _currentAlgorithmName = algorithmName;
                _currentParameters = new Dictionary<string, object>(_currentAlgorithm.GetParameters());
                _currentAlgorithm.SetParameters(new Dictionary<string, object>(_currentParameters));
                _history.Clear();
            }
        }

        public Dictionary<string, object> GetAlgorithmParameters()
        {
            lock (_gate)
            {
                return new Dictionary<string, object>(_currentParameters);
            }
        }

        public void SetAlgorithmParameters(Dictionary<string, object> parameters)
        {
            if (parameters == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_currentAlgorithm is null)
                {
                    return;
                }

                foreach (var kvp in parameters)
                {
                    _currentParameters[kvp.Key] = kvp.Value;
                }

                _currentAlgorithm.SetParameters(new Dictionary<string, object>(_currentParameters));
            }
        }

        public List<float> ProcessRealtimeData(IReadOnlyList<float> samples)
        {
            if (samples == null || samples.Count == 0)
            {
                return new List<float>();
            }

            lock (_gate)
            {
                if (_currentAlgorithm is null)
                {
                    return new List<float>(samples);
                }

                _history.AddRange(samples);
                if (_history.Count > MaxHistorySamples)
                {
                    int excess = _history.Count - MaxHistorySamples;
                    _history.RemoveRange(0, excess);
                }

                var buffer = _history.Select(v => (double)v).ToArray();
                var processed = _currentAlgorithm.Process(buffer);

                int take = Math.Min(samples.Count, processed.Length);
                var result = new List<float>(take);
                int start = processed.Length - take;
                if (start < 0) start = 0;
                for (int i = start; i < processed.Length; i++)
                {
                    result.Add((float)processed[i]);
                }

                return result;
            }
        }

        public void ClearBuffer()
        {
            lock (_gate)
            {
                _history.Clear();
            }
        }
    }
}
