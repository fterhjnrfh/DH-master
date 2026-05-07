// DH.Driver/TcpWorker.cs
using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DH.Contracts;

namespace DH.Driver;

public class TcpWorker : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _networkStream;
    private Thread? _workerThread;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private readonly object _lockObject = new object();
    private readonly IDataProcessor _dataProcessor;
    private readonly Action<bool, string> _statusCallback; //状态更新回调

    // 直接依赖数据处理器，避免事件开销
    public interface IDataProcessor
    {
        void ProcessData(byte[] data, DateTime receivedTime);
        void UpdateConnectionStatus(bool isConnected, string status);
    }

    public TcpWorker(IDataProcessor dataProcessor, Action<bool, string> statusCallback)
    {
        _dataProcessor = dataProcessor ?? throw new ArgumentNullException(nameof(dataProcessor));

        _statusCallback = statusCallback ?? throw new ArgumentNullException(nameof(statusCallback));
    }

    public bool IsRunning
    {
        get
        {
            lock (_lockObject)
            {
                return _isRunning && _workerThread?.IsAlive == true;
            }
        }
    }

    public bool IsConnected => _tcpClient?.Connected == true;

    public void Start(string ip, int port)
    {
        lock (_lockObject)
        {
            if (_isRunning)
            {
                throw new InvalidOperationException("Worker is already running");
            }

            _cts = new CancellationTokenSource();
            _isRunning = true;

            _workerThread = new Thread(() => WorkerMain(ip, port, _cts.Token))
            {
                Name = "TcpWorkerThread",
                IsBackground = true
            };

            _workerThread.Start();
        }
    }

    public void Stop()
    {
        lock (_lockObject)
        {
            if (!_isRunning) return;

            _cts?.Cancel();
            _isRunning = false;

            // 等待工作线程结束
            _statusCallback(true, "等待工作线程结束...");
            if (_workerThread?.IsAlive == true)
            {
                _workerThread.Join(TimeSpan.FromSeconds(3));
            }

            _workerThread = null;
            _cts?.Dispose();
            _cts = null;
            _statusCallback(false, "断开TCP服务器...");
        }
    }

    private void WorkerMain(string ip, int port, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _dataProcessor.UpdateConnectionStatus(false, "正在连接TCP服务器...");
                _statusCallback(false, "正在连接TCP服务器...");

                _tcpClient = new TcpClient();
                var connectResult = _tcpClient.BeginConnect(ip, port, null, null);

                if (connectResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5)))
                {
                    _tcpClient.EndConnect(connectResult);
                    _networkStream = _tcpClient.GetStream();

                    _dataProcessor.UpdateConnectionStatus(true, $"已连接到 {ip}:{port}");
                    _statusCallback(true, $"已连接到 {ip}:{port}");

                    ReceiveDataLoop(cancellationToken);
                }
                else
                {
                    _dataProcessor.UpdateConnectionStatus(false, "连接超时");
                    _statusCallback(false, "连接超时");
                }
            }
            catch (OperationCanceledException)
            {
                _dataProcessor.UpdateConnectionStatus(false, "连接操作被取消");
                _statusCallback(false, "连接操作被取消");
            }
            catch (Exception ex)
            {
                _dataProcessor.UpdateConnectionStatus(false, $"连接失败: {ex.Message}");
                _statusCallback(false, $"连接失败: {ex.Message}");
            }
            finally
            {
                Disconnect();
            }
            if (!cancellationToken.IsCancellationRequested)
            {
                Thread.Sleep(2000);
            }
        }
    }

    private void ReceiveDataLoop(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && 
                   _tcpClient?.Connected == true && 
                   _networkStream != null)
            {
                // 同步读取数据（阻塞调用）
                if (_networkStream.DataAvailable)
                {
                    var bytesRead = _networkStream.Read(buffer, 0, buffer.Length);
                    // Console.WriteLine($"[TcpWorker] Received {bytesRead} bytes");
                    if (bytesRead > 0)
                    {
                        // 复制数据并直接处理
                        var receivedData = new byte[bytesRead];
                        Array.Copy(buffer, receivedData, bytesRead);
                        
                        // 直接调用数据处理方法
                        _dataProcessor.ProcessData(receivedData, DateTime.Now);
                    }
                }
                else
                {
                    // 没有数据时短暂休眠，避免CPU占用过高
                    Thread.Sleep(10);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常的取消操作
        }
        catch (Exception ex)
        {
            _dataProcessor.UpdateConnectionStatus(false, $"数据接收错误: {ex.Message}");
        }
    }

    public void SendData(byte[] data)
    {
        lock (_lockObject)
        {
            if (!IsConnected || _networkStream == null)
            {
                throw new InvalidOperationException("Not connected to server");
            }

            try
            {
                _networkStream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                _dataProcessor.UpdateConnectionStatus(false, $"发送数据失败: {ex.Message}");
                throw;
            }
        }
    }

    private void Disconnect()
    {
        lock (_lockObject)
        {
            try
            {
                _networkStream?.Close();
                _tcpClient?.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"断开连接时出错: {ex.Message}");
            }
            finally
            {
                _networkStream = null;
                _tcpClient = null;
                _dataProcessor.UpdateConnectionStatus(false, "连接已断开");
            }
        }
    }

    public void Dispose()
    {
        Stop();
        Disconnect();
    }
}