using ScottPlot;

namespace DH.Display.Realtime;

/// UI 端提供 Plot 引用和一个跨线程调度方法
public interface IDHPlotHost
{
    Plot Plot { get; }
    void InvokeOnUi(Action action);
    void Refresh();
}
