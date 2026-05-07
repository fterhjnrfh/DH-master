using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DH.Client.App.Views;

public partial class SenderPanel : UserControl
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;

    public SenderPanel()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConnect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var ip = IpBox.Text ?? "127.0.0.1";
            var port = int.TryParse(PortBox.Text, out var p) ? p : 4008;
            _client = new TcpClient();
            _client.Connect(IPAddress.Parse(ip), port);
            _stream = _client.GetStream();
            StatusText.Text = $"已连接 {ip}:{port}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnDisconnect(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            _stream?.Close();
            _client?.Close();
            _stream = null;
            _client = null;
            StatusText.Text = "已断开";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnSendOnce(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_stream == null) return;
        int chCount = (int)ChCountBox.Value;
        int pktCount = (int)PktCountBox.Value;
        var names = (NamesBox.Text ?? "").Split('|');
        if (names.Length != chCount) return;
        var channels = new float[chCount][];
        for (int c = 0; c < chCount; c++)
        {
            channels[c] = Enumerable.Range(0, pktCount)
                .Select(i => (float)Math.Sin(2 * Math.PI * i / pktCount + c))
                .ToArray();
        }
        var packet = BuildPacket((ulong)pktCount, channels, names, DateTime.UtcNow);
        _stream.Write(packet, 0, packet.Length);
    }

    private void OnStartStream(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_stream == null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        new Thread(() =>
        {
            int chCount = (int)ChCountBox.Value;
            int pktCount = (int)PktCountBox.Value;
            var names = (NamesBox.Text ?? "").Split('|');
            if (names.Length != chCount) return;
            ulong total = 0;
            var rnd = new Random();
            while (!ct.IsCancellationRequested)
            {
                var channels = new float[chCount][];
                for (int c = 0; c < chCount; c++)
                {
                    channels[c] = Enumerable.Range(0, pktCount)
                        .Select(i => (float)(Math.Sin(2 * Math.PI * i / pktCount + c) + 0.05 * (rnd.NextDouble() - 0.5)))
                        .ToArray();
                }
                var packet = BuildPacket(total, channels, names, DateTime.UtcNow);
                _stream!.Write(packet, 0, packet.Length);
                total += (ulong)pktCount;
                Thread.Sleep(50);
            }
        }) { IsBackground = true }.Start();
        StatusText.Text = "连续发送中";
    }

    private void OnStopStream(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "已停止";
    }

    private static byte[] BuildPacket(ulong total, float[][] channels, string[] channelNames, DateTime timestampUtc)
    {
        int chCount = channels.Length;
        int pktCount = channels[0].Length;
        var payload = new System.Collections.Generic.List<byte>();
        void WLE(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); payload.AddRange(b); }
        WLE(BitConverter.GetBytes(total));
        WLE(BitConverter.GetBytes((uint)pktCount));
        WLE(BitConverter.GetBytes((uint)chCount));
        for (int p = 0; p < pktCount; p++) for (int c = 0; c < chCount; c++) WLE(BitConverter.GetBytes(channels[c][p]));
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
        var packet = new System.Collections.Generic.List<byte>(12 + payload.Count);
        void WH(byte[] b) { if (!BitConverter.IsLittleEndian) Array.Reverse(b); packet.AddRange(b); }
        WH(BitConverter.GetBytes(magic));
        WH(BitConverter.GetBytes(cmd));
        WH(BitConverter.GetBytes(len));
        packet.AddRange(payload);
        return packet.ToArray();
    }
}
