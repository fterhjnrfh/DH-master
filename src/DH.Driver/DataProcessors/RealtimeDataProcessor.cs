using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Net;
using DH.Contracts.Abstractions;
using DH.Contracts.Models;

using DH.Datamanage.Realtime;

namespace DH.Driver.DataProcessors;

    public class RealtimeDataProcessor : TcpWorker.IDataProcessor
    {
        private readonly IDataBus _dataBus;
        private readonly StreamTable _streamTable;
        private readonly ConcurrentQueue<Action> _uiUpdateQueue;
        private Timer? _uiUpdateTimer;
        private readonly SynchronizationContext? _uiContext;
        private readonly List<byte> _rxBuffer = new();
        private readonly Dictionary<int, List<float>> _channelCaches = new();
        private readonly int _chunkSize = 2048;
        private readonly Regex _nameRegex = new Regex("^(AI\\d+)-(\\d{1,2}),([A-Za-zµΩ%/]+)$", RegexOptions.Compiled);
        private const uint Magic = 0x55AAAA55;
        private const uint TimeSeriesCommand = 0x7C;
        private const uint HandshakeCommand = 0x7D;
        private int _frameSequence;
        private bool _isVerified;
        private bool _isActive;
        private DateTime _lastPacketTime;
    private string? _expectedIp;
    private int _expectedPort;
    private int _deviceIdFromPort;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _channelAccessTimes = new();

    // 状态缓存，避免频繁UI更新
    private string _lastStatus = "未连接";
    private bool _lastConnectedState;

    // TCP状态变化事件
    public event EventHandler<TcpStatusEventArgs>? StatusChanged;
    public event EventHandler<bool>? VerifiedChanged;
    public event EventHandler<bool>? ActivityChanged;

    public class TcpStatusEventArgs : EventArgs
    {
        public bool IsConnected { get; }
        public string Status { get; }

        public TcpStatusEventArgs(bool isConnected, string status)
        {
            IsConnected = isConnected;
            Status = status;
        }
    }

        public RealtimeDataProcessor(IDataBus dataBus, StreamTable streamTable)
        {
        _dataBus = dataBus;
        _streamTable = streamTable;
        _uiUpdateQueue = new ConcurrentQueue<Action>();
        _uiContext = SynchronizationContext.Current;
        
        _uiUpdateTimer = new Timer(ProcessUiUpdates, null, 100, 100);
        }

        public void ProcessData(byte[] data, DateTime receivedTime)
        {
            try
            {
                AppendAndDecode(data, receivedTime);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"数据处理错误: {ex.Message}");
            }
        }

    public void SetVerifiedSource(string ip, int port)
    {
        _expectedIp = ip;
        _expectedPort = port;
        _deviceIdFromPort = MapPortToDevice(port);
        _isVerified = false;
        VerifiedChanged?.Invoke(this, false);
    }

    public void UpdateConnectionStatus(bool isConnected, string status)
    {
        // 状态去重：只有状态真正变化时才更新UI
        if (status != _lastStatus || isConnected != _lastConnectedState)
        {
            _lastStatus = status;
            _lastConnectedState = isConnected;
            
            // 将UI更新操作加入队列
            _uiUpdateQueue.Enqueue(() =>
            {
                // 这里可以更新ViewModel中的状态属性
                // 例如：MainViewModel.Instance.UpdateTcpStatus(isConnected, status);
                Console.WriteLine($"TCP状态: {status}, 连接: {isConnected}");

                 // 触发事件
                StatusChanged?.Invoke(this, new TcpStatusEventArgs(isConnected, status));
            });
        }
    }

    private void AppendAndDecode(byte[] data, DateTime receivedTime)
    {
        _rxBuffer.AddRange(data);
        int idx = 0;
        while (true)
        {
            int available = _rxBuffer.Count - idx;
            if (available < 12) break;
            uint m = BitConverter.ToUInt32(_rxBuffer.ToArray(), idx);
            if (m != Magic)
            {
                idx++;
                continue;
            }
            uint cmd = BitConverter.ToUInt32(_rxBuffer.ToArray(), idx + 4);
            uint len = BitConverter.ToUInt32(_rxBuffer.ToArray(), idx + 8);
            int frameLen = 12 + (int)len;
            if (_rxBuffer.Count - idx < frameLen) break;
            var packet = new byte[frameLen];
            Array.Copy(_rxBuffer.ToArray(), idx, packet, 0, frameLen);
            DecodePacket(packet, receivedTime, cmd);
            _rxBuffer.RemoveRange(0, idx + frameLen);
            idx = 0;
        }
        if (idx > 0 && idx < _rxBuffer.Count) _rxBuffer.RemoveRange(0, idx);
    }

    private void DecodePacket(byte[] packet, DateTime receivedTime, uint cmd)
    {
        if (cmd == HandshakeCommand)
        {
            int ofs = 12;
            uint tokenLen = BitConverter.ToUInt32(packet, ofs); ofs += 4;
            string token = System.Text.Encoding.ASCII.GetString(packet, ofs, checked((int)tokenLen));
            Console.WriteLine($"握手令牌: {token}");
            bool ok = token == "DHSENDER";
            if (ok && !_isVerified)
            {
                _isVerified = true;
                VerifiedChanged?.Invoke(this, true);
            }
            // 解析可选的通道名称列表，并提前声明通道以便接收端选择
            ofs += checked((int)tokenLen);
            if (packet.Length - ofs >= 4)
            {
                uint nameLen2 = BitConverter.ToUInt32(packet, ofs); ofs += 4;
                if (packet.Length - ofs >= nameLen2 && nameLen2 > 0)
                {
                    string names2 = System.Text.Encoding.ASCII.GetString(packet, ofs, checked((int)nameLen2));
                    var nameArr2 = names2.Split('|', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < nameArr2.Length; i++)
                    {
                        int id = i + 1;
                        var match = _nameRegex.Match(nameArr2[i]);
                        int chNo = match.Success ? int.Parse(match.Groups[2].Value) : (i + 1);
                        id = _deviceIdFromPort * 100 + chNo;
                        _streamTable.EnsureChannel(id, DH.Contracts.ChannelNaming.ChannelName(id));
                        _dataBus.EnsureChannel(id);
                        if (!string.IsNullOrEmpty(_expectedIp))
                        {
                            var endpoint = new IPEndPoint(IPAddress.Parse(_expectedIp!), _expectedPort);
                            var deviceId = $"AI{_deviceIdFromPort:D2}";
                            var chIdent = new ChannelIdentifier(endpoint, deviceId, chNo, unit: "");
                            _channelAccessTimes[chIdent.CanonicalKey] = DateTimeOffset.UtcNow;
                        }
                    }
                }
            }
            return;
        }
        if (cmd != TimeSeriesCommand) return;
        if (!_isVerified)
        {
            _isVerified = true;
            VerifiedChanged?.Invoke(this, true);
        }
        int offset = 12;
        ulong total = BitConverter.ToUInt64(packet, offset); offset += 8;
        uint pktCount = BitConverter.ToUInt32(packet, offset); offset += 4;
        uint chCount = BitConverter.ToUInt32(packet, offset); offset += 4;
        int sampleCount = checked((int)(pktCount * chCount));
        var interleaved = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            interleaved[i] = BitConverter.ToSingle(packet, offset + i * 4);
        }
        offset += sampleCount * 4;
        uint nameLen = BitConverter.ToUInt32(packet, offset); offset += 4;
        string names = System.Text.Encoding.ASCII.GetString(packet, offset, checked((int)nameLen));
        offset += checked((int)nameLen);
        ulong epochSec = BitConverter.ToUInt64(packet, offset); offset += 8;
        uint usec = BitConverter.ToUInt32(packet, offset); offset += 4;
        var ts = DateTimeOffset.FromUnixTimeSeconds((long)epochSec).AddMilliseconds(usec / 1000.0).UtcDateTime;
        var nameArr = names.Split('|');
        var chIds = new int[nameArr.Length];
        for (int i = 0; i < nameArr.Length; i++)
        {
            var match = _nameRegex.Match(nameArr[i]);
            int chNo = match.Success ? int.Parse(match.Groups[2].Value) : (i + 1);
            int id = _deviceIdFromPort * 100 + chNo;
            chIds[i] = id;
            if (!_channelCaches.ContainsKey(id)) _channelCaches[id] = new List<float>(1024);
            _streamTable.EnsureChannel(id, DH.Contracts.ChannelNaming.ChannelName(id));
            if (!string.IsNullOrEmpty(_expectedIp))
            {
                var endpoint = new IPEndPoint(IPAddress.Parse(_expectedIp!), _expectedPort);
                var deviceId = $"AI{_deviceIdFromPort:D2}";
                var chIdent = new ChannelIdentifier(endpoint, deviceId, chNo, unit: "");
                _channelAccessTimes[chIdent.CanonicalKey] = DateTimeOffset.UtcNow;
            }
        }
        for (int c = 0; c < chIds.Length; c++)
        {
            var cache = _channelCaches[chIds[c]];
            for (int p = 0; p < pktCount; p++)
            {
                int idx = p * (int)chCount + c;
                cache.Add(interleaved[idx]);
            }
            while (cache.Count >= _chunkSize)
            {
                var segment = cache.Take(_chunkSize).ToArray();
                cache.RemoveRange(0, _chunkSize);
                var frame = new SimpleFrame
                {
                    ChannelId = chIds[c],
                    FrameId = Interlocked.Increment(ref _frameSequence),
                    Timestamp = ts,
                    Samples = segment,
                    Header = new FrameHeader { SampleRate = 1000, ProducerTag = "RealtimeDataProcessor" }
                };
                _ = _streamTable.PublishAsync(frame, CancellationToken.None);
            }
        }
        _lastPacketTime = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(_expectedIp))
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(_expectedIp!), _expectedPort);
            var deviceId = $"AI{_deviceIdFromPort:D2}";
            for (int i = 0; i < nameArr.Length; i++)
            {
                var match = _nameRegex.Match(nameArr[i]);
                int chNo = match.Success ? int.Parse(match.Groups[2].Value) : (i + 1);
                var chIdent = new ChannelIdentifier(endpoint, deviceId, chNo, unit: "");
                _channelAccessTimes[chIdent.CanonicalKey] = DateTimeOffset.UtcNow;
            }
        }
        if (!_isActive)
        {
            _isActive = true;
            ActivityChanged?.Invoke(this, true);
        }
    }

    private void ProcessUiUpdates(object? state)
    {
        while (_uiUpdateQueue.TryDequeue(out var action))
        {
            if (_uiContext != null)
            {
                _uiContext.Post(_ => action(), null);
            }
            else
            {
                action();
            }
        }
        if (_isActive && (DateTime.UtcNow - _lastPacketTime).TotalMilliseconds > 500)
        {
            _isActive = false;
            ActivityChanged?.Invoke(this, false);
        }
    }

    public void Dispose()
    {
        _uiUpdateTimer?.Dispose();
        _uiUpdateTimer = null;
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetChannelAccessTimes() => _channelAccessTimes;

    private static int MapPortToDevice(int port)
    {
        int basePort = 4008;
        int dev = port - basePort + 1;
        if (dev < 1) dev = 1;
        if (dev > 64) dev = 64;
        return dev;
    }
}
