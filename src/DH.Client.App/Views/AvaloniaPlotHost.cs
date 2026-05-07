// src/DH.Client.App/Views/AvaloniaPlotHost.cs
using System;
using Avalonia.Controls;
using Avalonia.Threading;
using DH.Display.Realtime;
using ScottPlot;
using ScottPlot.Avalonia;

namespace DH.Client.App.Views;

public sealed class AvaloniaPlotHost : IDHPlotHost
{
    private readonly AvaPlot _avaPlot;

    public AvaloniaPlotHost(AvaPlot avaPlot)
    {
        _avaPlot = avaPlot;
    }

    public Plot Plot => _avaPlot.Plot;

    public void InvokeOnUi(Action action)
        => Dispatcher.UIThread.Post(action);

    public void Refresh()
        => _avaPlot.Refresh();
}
