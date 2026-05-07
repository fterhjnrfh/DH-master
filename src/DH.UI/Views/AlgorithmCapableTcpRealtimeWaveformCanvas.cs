using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using NewAvalonia.Services;
using SkiaSharp;

namespace NewAvalonia.Views
{
	public class AlgorithmCapableTcpRealtimeWaveformCanvas : Control
	{
		private const double WindowDurationMs = 4000d;

		private readonly object _gate = new();
		private readonly List<double> _times = new();
		private readonly List<float> _values = new();
		private readonly List<float> _processedValues = new();
		private readonly RealtimeWaveformProcessor _realtimeProcessor = new();

		private double _windowStartMs;
		private double _currentTimeMs;
		private double _lastValue;
		private double? _fixedMin;
		private double? _fixedMax;
		private bool _boundsLocked;
		private bool _autoCalibrating = true;
		private bool _applyAlgorithm;
		private string _selectedAlgorithm = string.Empty;

		public event Action<double, double>? WindowRangeChanged;

		public void AppendSamples(IReadOnlyList<float> samples, double sampleIntervalMs)
		{
			if (samples == null || samples.Count == 0)
			{
				return;
			}

			if (sampleIntervalMs <= 0)
			{
				sampleIntervalMs = 1;
			}

			List<float> processedSamples;
			if (_applyAlgorithm && !string.IsNullOrWhiteSpace(_selectedAlgorithm))
			{
				_realtimeProcessor.SetAlgorithm(_selectedAlgorithm);
				processedSamples = _realtimeProcessor.ProcessRealtimeData(samples);

				if (processedSamples.Count > samples.Count)
				{
					int start = processedSamples.Count - samples.Count;
					processedSamples = processedSamples.GetRange(start, samples.Count);
				}
				else if (processedSamples.Count < samples.Count)
				{
					float pad = processedSamples.Count > 0 ? processedSamples[^1] : 0f;
					while (processedSamples.Count < samples.Count)
					{
						processedSamples.Add(pad);
					}
				}
			}
			else
			{
				processedSamples = new List<float>(samples);
			}

			lock (_gate)
			{
				double t = _currentTimeMs;

				if (_times.Count == 0)
				{
					StartNewWindow(t, processedSamples[0]);
				}

				for (int i = 0; i < processedSamples.Count; i++)
				{
					double elapsed = t - _windowStartMs;
					if (elapsed >= WindowDurationMs)
					{
						if (!_boundsLocked && _processedValues.Count > 0)
						{
							_fixedMin = _processedValues.Min();
							_fixedMax = _processedValues.Max();
							_boundsLocked = true;
							_autoCalibrating = false;
						}

						double nextWindowStart = _windowStartMs + WindowDurationMs;
						StartNewWindow(nextWindowStart, (float)_lastValue);
						elapsed = t - _windowStartMs;
					}

					_times.Add(t);
					_values.Add(i < samples.Count ? samples[i] : (_values.Count > 0 ? _values[^1] : 0f));
					_processedValues.Add(processedSamples[i]);

					_lastValue = processedSamples[i];
					t += sampleIntervalMs;
				}

				_currentTimeMs = t;
			}

			Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Background);
		}

		public void Reset()
		{
			lock (_gate)
			{
				_times.Clear();
				_values.Clear();
				_processedValues.Clear();
				_windowStartMs = 0;
				_currentTimeMs = 0;
				_lastValue = 0;
			}

			_fixedMin = null;
			_fixedMax = null;
			_boundsLocked = false;
			_autoCalibrating = true;
			_realtimeProcessor.ClearBuffer();
			WindowRangeChanged?.Invoke(0, WindowDurationMs);
			InvalidateVisual();
		}

		private void StartNewWindow(double startTimeMs, float seedValue)
		{
			_windowStartMs = startTimeMs;
			_times.Clear();
			_values.Clear();
			_processedValues.Clear();
			_times.Add(startTimeMs);
			_values.Add(seedValue);
			_processedValues.Add(seedValue);
			WindowRangeChanged?.Invoke(_windowStartMs, _windowStartMs + WindowDurationMs);
		}

		public void SetAlgorithm(string algorithmName, bool enabled = true)
		{
			_selectedAlgorithm = algorithmName;
			_applyAlgorithm = enabled && !string.IsNullOrWhiteSpace(algorithmName);

			if (_applyAlgorithm)
			{
				_realtimeProcessor.SetAlgorithm(algorithmName);
			}
			else
			{
				_realtimeProcessor.ClearBuffer();
			}

			InvalidateVisual();
		}

		public void SetAlgorithmParameters(Dictionary<string, object> parameters)
		{
			if (_applyAlgorithm)
			{
				_realtimeProcessor.SetAlgorithmParameters(parameters);
			}
		}

		public Dictionary<string, object> GetAlgorithmParameters()
		{
			return _applyAlgorithm ? _realtimeProcessor.GetAlgorithmParameters() : new Dictionary<string, object>();
		}

		public void ReapplyCurrentAlgorithm()
		{
			if (!_applyAlgorithm || string.IsNullOrWhiteSpace(_selectedAlgorithm))
			{
				lock (_gate)
				{
					_processedValues.Clear();
					_processedValues.AddRange(_values);
				}
				InvalidateVisual();
				return;
			}

			List<float> snapshot;
			lock (_gate)
			{
				snapshot = new List<float>(_values);
			}

			if (snapshot.Count == 0)
			{
				return;
			}

			_realtimeProcessor.ClearBuffer();

			var processed = new List<float>(snapshot.Count);
			const int batchSize = 256;
			for (int i = 0; i < snapshot.Count; i += batchSize)
			{
				int length = Math.Min(batchSize, snapshot.Count - i);
				var batch = snapshot.GetRange(i, length);
				var result = _realtimeProcessor.ProcessRealtimeData(batch);
				processed.AddRange(result);
			}

			lock (_gate)
			{
				_processedValues.Clear();
				if (processed.Count >= _values.Count)
				{
					_processedValues.AddRange(processed.GetRange(processed.Count - _values.Count, _values.Count));
				}
				else
				{
					_processedValues.AddRange(processed);
					float pad = _processedValues.Count > 0 ? _processedValues[^1] : 0f;
					while (_processedValues.Count < _values.Count)
					{
						_processedValues.Add(pad);
					}
				}
			}

			InvalidateVisual();
		}

		private (double[] times, float[] originalValues, float[] processedValues) Snapshot()
		{
			lock (_gate)
			{
				return (_times.ToArray(), _values.ToArray(), _processedValues.ToArray());
			}
		}

		public override void Render(DrawingContext context)
		{
			base.Render(context);
			var bounds = new Rect(Bounds.Size);
			if (bounds.Width <= 0 || bounds.Height <= 0)
			{
				return;
			}

			var (times, originalValues, processedValues) = Snapshot();
			context.Custom(new WaveformDrawOp(
				bounds,
				_windowStartMs,
				WindowDurationMs,
				times,
				originalValues,
				processedValues,
				_lastValue,
				_fixedMin,
				_fixedMax,
				FixBoundsOnce,
				_autoCalibrating));
		}

		private void FixBoundsOnce(double min, double max)
		{
			lock (_gate)
			{
				if (_fixedMin is null || _fixedMax is null)
				{
					_fixedMin = min;
					_fixedMax = max;
				}
			}
		}

		private sealed class WaveformDrawOp : ICustomDrawOperation
		{
			private readonly Rect _bounds;
			private readonly double _windowStart;
			private readonly double _windowDuration;
			private readonly double[] _times;
			private readonly float[] _originalValues;
			private readonly float[] _processedValues;
			private readonly double _last;
			private readonly double? _fixedMin;
			private readonly double? _fixedMax;
			private readonly Action<double, double>? _fixBounds;
			private readonly bool _autoCalibrating;

			public WaveformDrawOp(
				Rect bounds,
				double windowStart,
				double windowDuration,
				double[] times,
				float[] originalValues,
				float[] processedValues,
				double last,
				double? fixedMin,
				double? fixedMax,
				Action<double, double>? fixBounds,
				bool autoCalibrating)
			{
				_bounds = bounds;
				_windowStart = windowStart;
				_windowDuration = windowDuration;
				_times = times;
				_originalValues = originalValues;
				_processedValues = processedValues;
				_last = last;
				_fixedMin = fixedMin;
				_fixedMax = fixedMax;
				_fixBounds = fixBounds;
				_autoCalibrating = autoCalibrating;
			}

			public void Render(ImmediateDrawingContext context)
			{
				var leaseFeature = context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
				using var lease = leaseFeature?.Lease();
				var canvas = lease?.SkCanvas;
				if (canvas is null)
				{
					return;
				}

				var rect = new SKRect((float)_bounds.X, (float)_bounds.Y, (float)(_bounds.X + _bounds.Width), (float)(_bounds.Y + _bounds.Height));
				canvas.Save();
				canvas.ClipRect(rect);

				DrawBackground(canvas, rect);
				DrawGrid(canvas, rect);
				var (min, max) = ResolveRange();
				DrawWave(canvas, rect, _originalValues, new SKColor(0x5A, 0x9B, 0xF8, 180), min, max);
				DrawWave(canvas, rect, _processedValues, new SKColor(0xFF, 0xC1, 0x07, 230), min, max);
				DrawOverlay(canvas, rect, min, max);

				canvas.Restore();

				(float min, float max) ResolveRange()
				{
					float min = float.MaxValue;
					float max = float.MinValue;

					void Consider(float[] values)
					{
						if (values.Length == 0)
						{
							return;
						}

						float localMin = values.Min();
						float localMax = values.Max();
						if (localMin < min) min = localMin;
						if (localMax > max) max = localMax;
					}

					Consider(_originalValues);
					Consider(_processedValues);

					if (_fixedMin is not null && _fixedMax is not null)
					{
						min = (float)Math.Min(min, _fixedMin.Value);
						max = (float)Math.Max(max, _fixedMax.Value);
					}

					if (!float.IsFinite(min) || !float.IsFinite(max))
					{
						min = -1f;
						max = 1f;
					}

					if (Math.Abs(max - min) < 1e-6f)
					{
						max = min + 1e-3f;
					}

					if (_fixedMin is null || _fixedMax is null)
					{
						_fixBounds?.Invoke(min, max);
					}

					return (min, max);
				}
			}

			private static void DrawBackground(SKCanvas canvas, SKRect rect)
			{
				using var paint = new SKPaint
				{
					Shader = SKShader.CreateLinearGradient(
						new SKPoint(rect.Left, rect.Top),
						new SKPoint(rect.Left, rect.Bottom),
						new[] { new SKColor(0x10, 0x16, 0x22), new SKColor(0x05, 0x0A, 0x12) },
						null,
						SKShaderTileMode.Clamp)
				};
				canvas.DrawRect(rect, paint);
			}

			private static void DrawGrid(SKCanvas canvas, SKRect rect)
			{
				using var paint = new SKPaint
				{
					Color = new SKColor(0x1F, 0x2E, 0x45),
					StrokeWidth = 1,
					Style = SKPaintStyle.Stroke,
					PathEffect = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0)
				};

				for (int i = 0; i <= 12; i++)
				{
					float x = rect.Left + (float)(rect.Width * i / 12d);
					canvas.DrawLine(x, rect.Top, x, rect.Bottom, paint);
				}

				for (int i = 0; i <= 6; i++)
				{
					float y = rect.Top + (float)(rect.Height * i / 6d);
					canvas.DrawLine(rect.Left, y, rect.Right, y, paint);
				}
			}

			private void DrawWave(SKCanvas canvas, SKRect rect, float[] values, SKColor color, float min, float max)
			{
				if (_times.Length < 2 || values.Length == 0)
				{
					return;
				}

				using var paint = new SKPaint
				{
					Color = color,
					StrokeWidth = 2.5f,
					Style = SKPaintStyle.Stroke,
					StrokeJoin = SKStrokeJoin.Round,
					IsAntialias = true
				};

				using var path = new SKPath();
				bool started = false;
				for (int i = 0; i < _times.Length && i < values.Length; i++)
				{
					double ratio = (_times[i] - _windowStart) / _windowDuration;
					if (ratio < 0)
					{
						continue;
					}

					if (ratio > 1 && started)
					{
						break;
					}

					float x = rect.Left + (float)(ratio * rect.Width);
					float y = ValueToY(values[i], min, max, rect);

					if (!started)
					{
						path.MoveTo(x, y);
						started = true;
					}
					else
					{
						path.LineTo(x, y);
					}
				}

				if (started)
				{
					canvas.DrawPath(path, paint);
				}
			}

			private void DrawOverlay(SKCanvas canvas, SKRect rect, float min, float max)
			{
				using var textPaint = new SKPaint
				{
					Color = SKColors.White,
					IsAntialias = true,
					TextSize = 12
				};

				string info = $"Processed: {_last:F3}   Range: {min:F3} ~ {max:F3}";
				canvas.DrawText(info, rect.Left + 12, rect.Top + 18, textPaint);

				string window = $"Window: {_windowStart:0}ms - {_windowStart + _windowDuration:0}ms";
				canvas.DrawText(window, rect.Left + 12, rect.Top + 36, textPaint);

				if (_autoCalibrating)
				{
					using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 180) };
					var overlay = new SKRect(rect.Left + 6, rect.Top + 6, rect.Right - 6, rect.Bottom - 6);
					canvas.DrawRect(overlay, bg);

					using var alertPaint = new SKPaint
					{
						Color = SKColors.White,
						IsAntialias = true,
						TextSize = 20,
						FakeBoldText = true
					};
					const string message = "Calibrating bounds...";
					var bounds = new SKRect();
					alertPaint.MeasureText(message, ref bounds);
					float textX = overlay.Left + (overlay.Width - bounds.Width) / 2;
					float textY = overlay.Top + (overlay.Height + bounds.Height) / 2 - bounds.Bottom;
					canvas.DrawText(message, textX, textY, alertPaint);
				}
			}

			private static float ValueToY(float value, float min, float max, SKRect rect)
			{
				float amplitude = Math.Max(1e-6f, max - min);
				float normalized = (value - min) / amplitude;
				normalized = Math.Clamp(normalized, 0f, 1f);
				return rect.Bottom - normalized * rect.Height;
			}

			public Rect Bounds => _bounds;
			public bool HitTest(Point p) => _bounds.Contains(p);
			public void Dispose() { }
			public bool Equals(ICustomDrawOperation? other) => false;
		}
	}
}

