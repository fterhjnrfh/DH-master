using Avalonia;
using Avalonia.ReactiveUI;
using Avalonia.Media;
using System.Text;
using System;
using DH.Client.App.Services.Performance;

namespace DH.Client.App;

public static class Program
{
    public static void Main(string[] args)
    {
        // 显式启用 TLS 1.2 以兼容 Win7
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

        // 统一控制台编码为 UTF-8，确保日志与提示在 Linux 终端不乱码
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;
        RenderBackendDiagnosticLogger.LogStartupEnvironment();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new FontManagerOptions {
            // 使用系统常见默认字体以避免 GlyphTypeface 创建失败
            DefaultFamilyName = "Segoe UI",
            FontFallbacks = new[] {
                new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
                new FontFallback { FontFamily = new FontFamily("Source Han Sans SC") },
                new FontFallback { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                new FontFallback { FontFamily = new FontFamily("DejaVu Sans") },
                new FontFallback { FontFamily = new FontFamily("Symbola") },
                new FontFallback { FontFamily = new FontFamily("Segoe UI Emoji") },
                new FontFallback { FontFamily = new FontFamily("Noto Color Emoji") }
            }
        })
        .WithInterFont()
        .UseReactiveUI()
        .LogToTrace();
}
