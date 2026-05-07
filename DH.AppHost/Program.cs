using System;
using System.Diagnostics;
using System.IO;

namespace DH.AppHost;

public static class Program
{
    public static int Main(string[] args)
    {
        var clientExe = Path.Combine(AppContext.BaseDirectory, "DH.Client.App.exe");
        if (!File.Exists(clientExe))
        {
            Console.Error.WriteLine($"未找到客户端可执行文件: {clientExe}");
            return 1;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = clientExe,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(clientExe)!,
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            Console.Error.WriteLine("无法启动 DH.Client.App.exe");
            return 1;
        }

        process.WaitForExit();
        return process.ExitCode;
    }
}
