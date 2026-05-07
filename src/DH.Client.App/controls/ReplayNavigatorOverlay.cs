using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DH.Client.App.Controls;

public sealed class ReplayNavigatorOverlay : Control
{
    public static readonly StyledProperty<double> TotalDurationSecondsProperty =
        AvaloniaProperty.Register<ReplayNavigatorOverlay, double>(nameof(TotalDurationSeconds));

    public static readonly StyledProperty<double> WindowStartSecondsProperty =
        AvaloniaProperty.Register<ReplayNavigatorOverlay, double>(nameof(WindowStartSeconds));

    public static readonly StyledProperty<double> WindowEndSecondsProperty =
        AvaloniaProperty.Register<ReplayNavigatorOverlay, double>(nameof(WindowEndSeconds));

    public double TotalDurationSeconds
    {
        get => GetValue(TotalDurationSecondsProperty);
        set => SetValue(TotalDurationSecondsProperty, value);
    }

    public double WindowStartSeconds
    {
        get => GetValue(WindowStartSecondsProperty);
        set => SetValue(WindowStartSecondsProperty, value);
    }

    public double WindowEndSeconds
    {
        get => GetValue(WindowEndSecondsProperty);
        set => SetValue(WindowEndSecondsProperty, value);
    }

    static ReplayNavigatorOverlay()
    {
        AffectsRender<ReplayNavigatorOverlay>(
            TotalDurationSecondsProperty,
            WindowStartSecondsProperty,
            WindowEndSecondsProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 1 || height <= 1 || TotalDurationSeconds <= 0)
        {
            return;
        }

        double start = Math.Clamp(WindowStartSeconds / TotalDurationSeconds, 0.0, 1.0);
        double end = Math.Clamp(WindowEndSeconds / TotalDurationSeconds, start, 1.0);
        double x = start * width;
        double rectWidth = Math.Max(2.0, (end - start) * width);

        var outsideBrush = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0));
        if (x > 0)
        {
            context.FillRectangle(outsideBrush, new Rect(0, 0, x, height));
        }

        double right = Math.Min(width, x + rectWidth);
        if (right < width)
        {
            context.FillRectangle(outsideBrush, new Rect(right, 0, width - right, height));
        }

        var windowBrush = new SolidColorBrush(Color.FromArgb(34, 33, 150, 243));
        var borderPen = new Pen(new SolidColorBrush(Color.FromRgb(33, 150, 243)), 2.0);
        context.FillRectangle(windowBrush, new Rect(x, 0, rectWidth, height));
        context.DrawRectangle(borderPen, new Rect(x, 1, rectWidth, Math.Max(1.0, height - 2)));
    }
}
