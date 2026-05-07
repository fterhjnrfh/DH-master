// DH.Driver/TcpDriverManager.cs
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Linq;
using DH.Driver.DataProcessors;
using DH.Contracts.Abstractions;
using DH.Datamanage.Realtime;

namespace DH.Driver;

public class TcpDriverManager : INotifyPropertyChanged, IDisposable
{
    private readonly TcpWorker _tcpWorker;
    private readonly RealtimeDataProcessor _dataProcessor;

    public TcpDriverManager(IDataBus dataBus, StreamTable streamTable, Action<bool, string> statusCallback)
    {
        _dataProcessor = new RealtimeDataProcessor(dataBus, streamTable);
        _tcpWorker = new TcpWorker(_dataProcessor, statusCallback);
        _dataProcessor.VerifiedChanged += (s, v) => VerifiedChanged?.Invoke(v);
        _dataProcessor.ActivityChanged += (s, a) => ActivityChanged?.Invoke(a);
    }

    public bool IsConnected => _tcpWorker.IsConnected;
    public bool IsRunning => _tcpWorker.IsRunning;
    public event Action<bool>? VerifiedChanged;
    public event Action<bool>? ActivityChanged;

    private string _connectionStatus = "未连接";
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetField(ref _connectionStatus, value);
    }

    public void Connect(string ip, int port)
    {
        if (!IsRunning)
        {
            _dataProcessor.SetVerifiedSource(ip, port);
            _tcpWorker.Start(ip, port);
        }
    }

    public void Disconnect()
    {
        if (IsRunning)
        {
            _tcpWorker.Stop();
        }
    }

    public void Send(byte[] data)
    {
        if (IsConnected)
        {
            _tcpWorker.SendData(data);
        }
    }

    public void SendTimeSeriesPacket(ulong total, float[][] channels, string[] channelNames, DateTime timestampUtc)
    {
        if (channels == null || channels.Length == 0) throw new ArgumentException("channels");
        int chCount = channels.Length;
        int pktCount = channels[0].Length;
        if (channels.Any(ch => ch.Length != pktCount)) throw new ArgumentException("pktCount mismatch");
        if (channelNames == null || channelNames.Length != chCount) throw new ArgumentException("channelNames");

        var payload = new List<byte>(12 + 8 + 4 + 4 + pktCount * chCount * 4 + 4 + 256 + 8 + 4);
        void WLE(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); payload.AddRange(b); }

        WLE(BitConverter.GetBytes(total));
        WLE(BitConverter.GetBytes((uint)pktCount));
        WLE(BitConverter.GetBytes((uint)chCount));

        for (int p = 0; p < pktCount; p++)
        {
            for (int c = 0; c < chCount; c++)
            {
                WLE(BitConverter.GetBytes(channels[c][p]));
            }
        }

        var namesStr = string.Join("|", channelNames);
        var nameBytes = Encoding.ASCII.GetBytes(namesStr);
        WLE(BitConverter.GetBytes((uint)nameBytes.Length));
        payload.AddRange(nameBytes);

        var dto = new DateTimeOffset(timestampUtc.Kind == DateTimeKind.Utc ? timestampUtc : timestampUtc.ToUniversalTime());
        ulong epochSec = (ulong)dto.ToUnixTimeSeconds();
        long ticksInSec = timestampUtc.Ticks % TimeSpan.TicksPerSecond;
        uint usec = (uint)(ticksInSec / 10);
        WLE(BitConverter.GetBytes(epochSec));
        WLE(BitConverter.GetBytes(usec));

        uint magic = 0x55AAAA55;
        uint cmd = 0x7C;
        uint len = (uint)payload.Count;
        var packet = new List<byte>(12 + payload.Count);
        void WH(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); packet.AddRange(b); }
        WH(BitConverter.GetBytes(magic));
        WH(BitConverter.GetBytes(cmd));
        WH(BitConverter.GetBytes(len));
        packet.AddRange(payload);

        Send(packet.ToArray());
    }

    // 如果需要，可以添加状态更新的方法供DataProcessor调用
    public void UpdateStatus(bool isConnected, string status)
    {
        ConnectionStatus = status;
        OnPropertyChanged(nameof(IsConnected));
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetChannelAccessTimes()
    {
        return _dataProcessor.GetChannelAccessTimes();
    }

    #region INotifyPropertyChanged
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
    #endregion

    public void Dispose()
    {
        _tcpWorker?.Dispose();
        _dataProcessor?.Dispose();
    }
}