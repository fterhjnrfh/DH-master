using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Linq;
using Avalonia;
using SkiaSharp;

namespace DH.Client.App.Services.Performance;

public static class RenderBackendDiagnosticLogger
{
    private const string PowerShellPath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private static readonly object Sync = new();
    private static readonly string LogPath;
    private static bool _startupLogged;
    private static bool _backendLogged;

    static RenderBackendDiagnosticLogger()
    {
        LogPath = PerformanceOutputPaths.GetPath("render-backend-diagnostic.log");
        File.WriteAllText(LogPath, string.Empty, Encoding.UTF8);
    }

    public static string CurrentLogPath => LogPath;

    public static void LogStartupEnvironment()
    {
        lock (Sync)
        {
            if (_startupLogged)
            {
                return;
            }

            _startupLogged = true;
            WriteLine("=== Startup Environment ===");
            WriteLine($"TimestampLocal={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            WriteLine($"ProcessName={Process.GetCurrentProcess().ProcessName}");
            WriteLine($"ProcessId={Environment.ProcessId}");
            WriteLine($"MachineName={Environment.MachineName}");
            WriteLine($"UserName={Environment.UserName}");
            WriteLine($"OSDescription={RuntimeInformation.OSDescription}");
            WriteLine($"OSArchitecture={RuntimeInformation.OSArchitecture}");
            WriteLine($"ProcessArchitecture={RuntimeInformation.ProcessArchitecture}");
            WriteLine($"FrameworkDescription={RuntimeInformation.FrameworkDescription}");
            WriteLine($"Is64BitProcess={Environment.Is64BitProcess}");
            WriteLine($"ProcessorCount={Environment.ProcessorCount}");
            WriteLine($"AvaloniaAssemblyVersion={typeof(AppBuilder).Assembly.GetName().Version}");
            WriteLine($"SkiaSharpAssemblyVersion={typeof(SKSurface).Assembly.GetName().Version}");
            WriteLine($"BaseDirectory={AppContext.BaseDirectory}");
            WriteLine($"CurrentDirectory={Environment.CurrentDirectory}");

            string? gpuInventory = QueryVideoControllers();
            if (!string.IsNullOrWhiteSpace(gpuInventory))
            {
                WriteLine("=== Video Controllers ===");
                WriteLine(gpuInventory.TrimEnd());
            }
        }
    }

    public static void LogFirstRenderBackend(bool gpu, string backend, int width, int height)
    {
        lock (Sync)
        {
            if (_backendLogged)
            {
                return;
            }

            _backendLogged = true;
            WriteLine("=== First Render Backend ===");
            WriteLine($"TimestampLocal={DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            WriteLine($"Mode={(gpu ? "GPU" : "CPU")}");
            WriteLine($"Backend={backend}");
            WriteLine($"Width={width}");
            WriteLine($"Height={height}");

            string? gpuEngineInstances = QueryGpuEngineInstances(Environment.ProcessId);
            if (!string.IsNullOrWhiteSpace(gpuEngineInstances))
            {
                WriteLine("=== GPU Engine Instances For Process ===");
                WriteLine(gpuEngineInstances.TrimEnd());
            }
        }
    }

    private static string? QueryGpuEngineInstances(int processId)
    {
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            List<string> instanceNames = category
                .GetInstanceNames()
                .Where(name => name.Contains($"pid_{processId}_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (instanceNames.Count == 0)
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (string instanceName in instanceNames)
            {
                builder.AppendLine(instanceName);
            }

            return builder.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryVideoControllers()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = PowerShellPath,
                    Arguments = "-NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,AdapterCompatibility,DriverVersion,VideoProcessor,AdapterRAM | Format-List\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(4000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteLine(string line)
    {
        File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
    }
}
