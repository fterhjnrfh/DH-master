using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts;

namespace DH.Driver.Tcpdriver;

public class TcpDriver : ITcpDriver, IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private StreamReader? _streamReader;
    private StreamWriter? _streamWriter;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private readonly object _lockObject = new object();

    public event EventHandler<TcpConnectionEventArgs>? ConnectionStatusChanged;
    public event EventHandler<TcpDataReceivedEventArgs>? DataReceived;

    public bool IsConnected => _tcpClient?.Connected == true;
    public string ServerIp { get; private set; } = string.Empty;
    public int ServerPort { get; private set; }

    public async Task ConnectAsync(string ip, int port, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            throw new InvalidOperationException("Already connected to server");
        }

        try
        {
            lock (_lockObject)
            {
                _tcpClient = new TcpClient();
                _receiveCts = new CancellationTokenSource();
            }

            ServerIp = ip;
            ServerPort = port;

            await _tcpClient.ConnectAsync(ip, port);
            _networkStream = _tcpClient.GetStream();
            // 明确指定 UTF-8（无 BOM）与 BOM 探测，避免跨平台默认编码差异
            _streamReader = new StreamReader(_networkStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _streamWriter = new StreamWriter(_networkStream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };

            // 启动数据接收任务
            _receiveTask = Task.Run(() => ReceiveDataAsync(_receiveCts.Token), _receiveCts.Token);

            OnConnectionStatusChanged(true, $"Connected to {ip}:{port}");
        }
        catch (Exception ex)
        {
            DisposeResources();
            OnConnectionStatusChanged(false, $"Connection failed: {ex.Message}");
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (!IsConnected) return;

        try
        {
            _receiveCts?.Cancel();
            
            if (_receiveTask != null)
            {
                await _receiveTask.ContinueWith(_ => { }); // 等待接收任务完成
            }

            DisposeResources();
            OnConnectionStatusChanged(false, "Disconnected from server");
        }
        catch (Exception ex)
        {
            OnConnectionStatusChanged(false, $"Disconnect error: {ex.Message}");
        }
    }

    public async Task SendAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _networkStream == null)
        {
            throw new InvalidOperationException("Not connected to server");
        }

        try
        {
            await _networkStream.WriteAsync(data, 0, data.Length, cancellationToken);
        }
        catch (Exception ex)
        {
            OnConnectionStatusChanged(false, $"Send error: {ex.Message}");
            throw;
        }
    }

    private async Task ReceiveDataAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && _networkStream != null)
            {
                var bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break; // 连接已关闭

                var receivedData = new byte[bytesRead];
                Array.Copy(buffer, receivedData, bytesRead);
                
                OnDataReceived(receivedData, DateTime.Now);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的取消操作
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                OnConnectionStatusChanged(false, $"Receive error: {ex.Message}");
            }
        }
    }

    protected virtual void OnConnectionStatusChanged(bool isConnected, string message)
    {
        ConnectionStatusChanged?.Invoke(this, new TcpConnectionEventArgs(isConnected, message, ServerIp, ServerPort));
    }

    protected virtual void OnDataReceived(byte[] data, DateTime receivedTime)
    {
        Console.WriteLine($"Received -------------");
        
        // 触发 DataReceived 事件
        DataReceived?.Invoke(this, new TcpDataReceivedEventArgs(data, receivedTime));
    }

    private void DisposeResources()
    {
        lock (_lockObject)
        {
            _streamReader?.Dispose();
            _streamWriter?.Dispose();
            _networkStream?.Dispose();
            _tcpClient?.Close();
            _receiveCts?.Dispose();

            _streamReader = null;
            _streamWriter = null;
            _networkStream = null;
            _tcpClient = null;
            _receiveCts = null;
            _receiveTask = null;
        }
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
        DisposeResources();
    }
}