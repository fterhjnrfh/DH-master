using System;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts.Models;

namespace DH.Client.App.Data
{
    /// <summary>
    /// 基于参数规格生成正弦波数据的生产者，按照真实通道数据管道发布帧。
    /// - 数据格式：默认 float32（SimpleFrame.Samples）
    /// - 精度：内部使用 double 计算、外部发布为 float 确保 >= 1e-3 精度
    /// </summary>
    public class SineWaveDataProducer
    {
        private readonly DataBus _dataBus;
        private readonly int _channelCount;
        private readonly int _pointsPerChannel;
        private readonly int _sampleRate;
        private readonly double _minFrequency;
        private readonly double _maxFrequency;
        private readonly double _minAmplitude;
        private readonly double _maxAmplitude;
        private readonly double _phaseOffset;
        private readonly string _dataFormat;
        private CancellationTokenSource? _cts;
        private Task? _producerTask;
        private bool _isRunning;
        private int _frameSequence;

        // 每个通道的固定参数（确保稳定频率/振幅/相位）
        private double[] _channelFreqs;
        private double[] _channelAmps;
        private double[] _channelPhases;

        public SineWaveDataProducer(
            DataBus dataBus,
            int channelCount = 64,
            int pointsPerChannel = 1000,
            int sampleRate = 1000,
            double minFrequency = 0.5,
            double maxFrequency = 5.0,
            double minAmplitude = 50.0,
            double maxAmplitude = 100.0,
            double phaseOffset = 0.0,
            string dataFormat = "float32")
        {
            _dataBus = dataBus ?? throw new ArgumentNullException(nameof(dataBus));
            _channelCount = channelCount;
            _pointsPerChannel = pointsPerChannel;
            _sampleRate = sampleRate;
            _minFrequency = minFrequency;
            _maxFrequency = maxFrequency;
            _minAmplitude = minAmplitude;
            _maxAmplitude = maxAmplitude;
            _phaseOffset = phaseOffset;
            _dataFormat = dataFormat.ToLowerInvariant();

            _channelFreqs = new double[_channelCount + 1];
            _channelAmps = new double[_channelCount + 1];
            _channelPhases = new double[_channelCount + 1];

            var rand = new Random(12345);
            for (int ch = 1; ch <= _channelCount; ch++)
            {
                // 在范围内均匀分布 + 少量随机抖动
                double t = (double)(ch - 1) / Math.Max(1, _channelCount - 1);
                _channelFreqs[ch] = _minFrequency + t * (_maxFrequency - _minFrequency) + (rand.NextDouble() - 0.5) * 0.2;
                _channelAmps[ch] = _minAmplitude + t * (_maxAmplitude - _minAmplitude) + (rand.NextDouble() - 0.5) * 5.0;
                _channelPhases[ch] = _phaseOffset + rand.NextDouble() * 2.0 * Math.PI;
            }
        }

        public void Start()
        {
            if (_isRunning) return;
            _cts = new CancellationTokenSource();
            _producerTask = Task.Run(ProducerLoop, _cts.Token);
            _isRunning = true;
        }

        public void Stop()
        {
            if (!_isRunning) return;
            try { _cts?.Cancel(); _producerTask?.Wait(1000); } catch { }
            _isRunning = false;
        }

        private async Task ProducerLoop()
        {
            var token = _cts!.Token;
            var rand = new Random();
            // 以 50ms 更新节奏推进时间，确保 UI 流畅
            int updateIntervalMs = 50;
            double globalTime = 0.0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    for (int ch = 1; ch <= _channelCount; ch++)
                    {
                        double freq = _channelFreqs[ch];
                        double amp = _channelAmps[ch];
                        double phase = _channelPhases[ch];

                        // 先使用 double 计算，最后根据数据格式转换为目标精度
                        var samplesF = new float[_pointsPerChannel];
                        for (int i = 0; i < _pointsPerChannel; i++)
                        {
                            double t = (double)i / _sampleRate + globalTime;
                            double value = amp * Math.Sin(2.0 * Math.PI * freq * t + phase);
                            // 轻微噪声（可调），增强真实感但不影响精度要求
                            value += (rand.NextDouble() - 0.5) * amp * 0.02;
                            samplesF[i] = (float)value; // float32 输出
                        }

                        var frame = new SimpleFrame
                        {
                            ChannelId = ch,
                            FrameId = Interlocked.Increment(ref _frameSequence),
                            Timestamp = DateTime.UtcNow,
                            Samples = samplesF,
                            Header = new FrameHeader { SampleRate = _sampleRate, ProducerTag = "SineWaveDataProducer" }
                        };

                        await _dataBus.PublishFrameAsync(frame, token);
                    }

                    globalTime += updateIntervalMs / 1000.0;
                    await Task.Delay(updateIntervalMs, token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"SineWaveDataProducer异常: {ex.Message}");
            }
        }
    }
}
