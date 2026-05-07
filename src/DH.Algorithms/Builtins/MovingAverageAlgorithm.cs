using DH.Contracts;
using DH.Contracts.Models;
using DH.Contracts.Abstractions;

namespace DH.Algorithms.Builtins;

public class MovingAverageAlgorithm : IAlgorithm
{
    public string Name => "MovingAverage";
    public int Window { get; }
    public MovingAverageAlgorithm(int window = 16) => Window = Math.Max(1, window);

    public AlgorithmResult Execute(IReadOnlyList<IDataFrame> frames, CancellationToken ct = default)
    {
        if (frames.Count == 0) return new AlgorithmResult(Name, -1, DateTime.UtcNow, Array.Empty<float>());
        var last = frames[^1];
        var data = last.Samples.Span;
        var outBuf = new float[data.Length];
        double sum = 0;
        var q = new Queue<float>(Window);
        for (int i = 0; i < data.Length; i++)
        {
            sum += data[i];
            q.Enqueue(data[i]);
            if (q.Count > Window) sum -= q.Dequeue();
            outBuf[i] = (float)(sum / q.Count);
        }
        return new AlgorithmResult(Name, last.ChannelId, last.Timestamp, outBuf);
    }
}