namespace DH.Contracts.Models;

public readonly record struct CurvePoint(double X, double Y);

public record AlgorithmResult(string AlgorithmName, int ChannelId, DateTime Timestamp, float[] Data);

public class FrameHeader
{
    public int SampleRate { get; init; }
    public long? StartSampleIndex { get; init; }
    public double? SampleIntervalSeconds { get; init; }
    public string ProducerTag { get; init; } = string.Empty;
}

// Concrete frame for demo
public class SimpleFrame : DH.Contracts.Abstractions.IDataFrame
{
    public int FrameId { get; init; }
    public DateTime Timestamp { get; init; }
    public int ChannelId { get; init; }
    public ReadOnlyMemory<float> Samples { get; init; }
    public FrameHeader Header { get; init; } = new FrameHeader { SampleRate = 1000, ProducerTag = "unknown" };
}

public class ChannelInfo { public int ChannelId { get; init; } public string Name { get; init; } = string.Empty; public bool Online { get; set; } }

// 与 DH2 对齐：通道全局标识符，用于会话索引与 UI 统计
public readonly record struct ChannelIdentifier
{
    public System.Net.IPEndPoint Endpoint { get; init; }
    public string DeviceId { get; init; }
    public int ChannelNumber { get; init; }
    public string Unit { get; init; }

    public ChannelIdentifier(System.Net.IPEndPoint endpoint, string deviceId, int channelNumber, string unit)
    {
        Endpoint = endpoint;
        DeviceId = deviceId ?? throw new System.ArgumentNullException(nameof(deviceId));
        ChannelNumber = channelNumber;
        Unit = unit ?? string.Empty;
    }

    public string CanonicalKey => $"{Endpoint}/{DeviceId}/CH{ChannelNumber:D2}";
    public string CanonicalSeriesKey => $"{DeviceId}_CH{ChannelNumber:D2}";
}

public static class ChannelIdentifierExtensions
{
    // 解析 CanonicalKey: "{Endpoint}/{DeviceId}/CH{NN}"
    public static (string DeviceId, int ChannelNumber)? ParseCanonicalKey(string key)
    {
        try
        {
            var parts = key.Split('/');
            if (parts.Length < 3) return null;
            var deviceId = parts[^2];
            var ch = parts[^1];
            if (!ch.StartsWith("CH")) return null;
            if (!int.TryParse(ch.Substring(2), out var num)) return null;
            return (deviceId, num);
        }
        catch { return null; }
    }
}
