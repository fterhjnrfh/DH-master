using System;
using System.Threading;
using System.Threading.Tasks;

namespace DH.Contracts;

public interface ITcpDriver
{
    event EventHandler<TcpConnectionEventArgs> ConnectionStatusChanged;
    event EventHandler<TcpDataReceivedEventArgs> DataReceived;
    
    bool IsConnected { get; }
    string ServerIp { get; }
    int ServerPort { get; }
    
    Task ConnectAsync(string ip, int port, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task SendAsync(byte[] data, CancellationToken cancellationToken = default);
}

public class TcpConnectionEventArgs : EventArgs
{
    public bool IsConnected { get; }
    public string Message { get; }
    public string ServerIp { get; }
    public int ServerPort { get; }

    public TcpConnectionEventArgs(bool isConnected, string message, string serverIp, int serverPort)
    {
        IsConnected = isConnected;
        Message = message;
        ServerIp = serverIp;
        ServerPort = serverPort;
    }
}

public class TcpDataReceivedEventArgs : EventArgs
{
    public byte[] Data { get; }
    public DateTime ReceivedTime { get; }

    public TcpDataReceivedEventArgs(byte[] data, DateTime receivedTime)
    {
        Data = data;
        ReceivedTime = receivedTime;
    }
}