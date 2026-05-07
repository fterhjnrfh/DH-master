# 曲线性能指标记录 README

## 目标

给曲线绘制程序增加一套独立的性能记录功能，用于性能测试和报告输出。

需要记录的指标：

- `ViewCount`：当前视图数量
- `CurveCount`：当前曲线总数
- `FPS`：曲线渲染器实际渲染帧率
- `CPUPercent`：当前应用进程 CPU 占用率
- `GPUPercent`：当前应用进程 GPU 占用率

输出形式：

- 每 1 秒采样一次
- 写入 CSV 文件
- 可选：在窗口标题实时显示当前值

CSV 建议格式：

```csv
Timestamp,ViewCount,CurveCount,FPS,RenderCallsPerSecond,CPUPercent,GPUPercent
```

## 整体设计

这套功能建议拆成 3 个部分：

1. 渲染器内埋点
2. 主窗口定时采样
3. CSV 记录器

这样做的好处是：

- 不依赖外部工具
- 不改变原有绘图逻辑
- 可迁移到另一份无法合并代码的项目中

## 1. 渲染器内埋点

### 目的

记录曲线渲染器每秒实际执行了多少次绘制。

### 要求

必须在“真正发生绘制”的函数里计数，而不是在按钮、刷新请求、定时器请求处计数。

例如：

- `Render()`
- `RenderSkia()`
- `OnRenderFrame()`
- `DrawFrame()`

### 实现方式

在渲染控件中增加静态计数器：

```csharp
private static long _renderCountSinceLastSample;
private static int _attachedViewCount;
```

在真正绘制函数里累加：

```csharp
Interlocked.Increment(ref _renderCountSinceLastSample);
```

在控件挂载/卸载时统计当前活跃视图数：

```csharp
protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
{
    Interlocked.Increment(ref _attachedViewCount);
    base.OnAttachedToVisualTree(e);
}

protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
{
    Interlocked.Decrement(ref _attachedViewCount);
    base.OnDetachedFromVisualTree(e);
}
```

对外提供一个“取数并清零”的方法：

```csharp
public static RenderStats SnapshotAndResetRenderStats()
{
    long renderCount = Interlocked.Exchange(ref _renderCountSinceLastSample, 0);
    int attachedViews = Volatile.Read(ref _attachedViewCount);
    return new RenderStats(renderCount, attachedViews);
}
```

`FPS` 的建议口径：

- `总渲染调用次数 / 当前附着视图数`

也就是“平均每个视图每秒的实际渲染帧率”。

## 2. 主窗口定时采样

### 目的

每 1 秒收集一次：

- 当前视图数量
- 当前曲线数量
- 当前 FPS
- 当前 CPU/GPU

### 实现方式

在主窗口或结果显示页面加一个 1 秒定时器，例如 `DispatcherTimer`：

```csharp
private static readonly TimeSpan MetricsSampleInterval = TimeSpan.FromSeconds(1);
private DispatcherTimer? _metricsTimer;
```

启动：

```csharp
private void StartMetricsSampling()
{
    _metricsTimer?.Stop();
    _metricsTimer = new DispatcherTimer { Interval = MetricsSampleInterval };
    _metricsTimer.Tick += (_, __) => RecordPerformanceMetrics();
    _metricsTimer.Start();
}
```

采样逻辑：

```csharp
private void RecordPerformanceMetrics()
{
    var renderStats = CurveRendererControl.SnapshotAndResetRenderStats();
    int viewCount = _curvePanels.Count;
    int curveCount = _curvePanels.Sum(panel => panel.GetSelectedChannels().Length);
    int fpsDivisor = Math.Max(1, Math.Max(viewCount, renderStats.AttachedViews));
    double fps = renderStats.RenderCalls / (double)fpsDivisor;
    double renderCallsPerSecond = renderStats.RenderCalls;

    var sample = _performanceRecorder.Capture(viewCount, curveCount, fps, renderCallsPerSecond);
}
```

### 视图数怎么取

取当前实际显示的视图集合数量，例如：

```csharp
int viewCount = _curvePanels.Count;
```

### 曲线数怎么取

取当前所有视图中已显示或已选中的曲线总数，例如：

```csharp
int curveCount = _curvePanels.Sum(panel => panel.GetSelectedChannels().Length);
```

如果你的项目不是“通道选择”，也可以改成：

- 所有 subplot 中已绑定曲线对象数量
- 所有 renderer 当前激活 series 数量

### 可选：显示到窗口标题

建议现场测试时把当前值显示到标题栏，方便直接观察：

```csharp
Title = $"视图 {sample.ViewCount} | 曲线 {sample.CurveCount} | FPS {sample.FramesPerSecond:F1} | CPU {sample.CpuPercent:F1}% | GPU {sample.GpuPercent:F1}%";
```

## 3. CSV 记录器

### 目的

统一负责：

- 计算 CPU
- 读取 GPU
- 写入 CSV

### 建议单独封装成类

例如：

- `PerformanceMetricsRecorder.cs`

### CPU 计算方式

使用当前进程 CPU 时间差计算，而不是整机 CPU：

```csharp
var process = Process.GetCurrentProcess();
TimeSpan cpuNow = process.TotalProcessorTime;
double wallMs = (now - lastSampleUtc).TotalMilliseconds;
double cpuMs = (cpuNow - lastCpuTime).TotalMilliseconds;
double cpuPercent = cpuMs / (wallMs * Environment.ProcessorCount) * 100.0;
```

这表示：

- 记录的是当前应用进程 CPU 占用率
- 不是任务管理器显示的整机 CPU 总占用率

### GPU 读取方式

Windows 下推荐使用性能计数器：

```csharp
PerformanceCounterCategory("GPU Engine")
```

通过实例名中的 `pid_xxx` 过滤当前进程：

```csharp
name.Contains($"pid_{processId}_", StringComparison.OrdinalIgnoreCase)
```

计数器名使用：

```csharp
"Utilization Percentage"
```

建议缓存计数器对象，不要每秒重新创建一次，否则 `NextValue()` 可能长期接近 `0`。

### 输出目录

建议固定到测试人员容易找到的位置，例如：

```csharp
string directory = @"C:\Users\Administrator\Desktop\dhdas\dhdas\data";
```

文件名建议带时间戳：

```csharp
curve-performance-20260409-103000.csv
```

### 推荐输出字段

- `Timestamp`
- `ViewCount`
- `CurveCount`
- `FPS`
- `RenderCallsPerSecond`
- `CPUPercent`
- `GPUPercent`

## 4. 项目依赖

如果使用 Windows 性能计数器读取 GPU，需要增加依赖：

```xml
<PackageReference Include="System.Diagnostics.PerformanceCounter" Version="8.0.0" />
```

如果是 .NET Framework 老项目，有时系统自带即可，但 .NET 6/7/8 项目建议显式加包。

## 5. 迁移到另一份项目时要改哪些地方

可直接复用的部分：

- `PerformanceMetricsRecorder`
- CPU/GPU 采样逻辑
- CSV 输出逻辑
- 1 秒定时采样逻辑

需要按新项目适配的部分：

- 渲染器真实绘制函数名称
- 视图数统计方式
- 曲线数统计方式
- 日志输出目录
- 标题栏显示方式

## 6. 推荐落地顺序

建议按下面顺序实现：

1. 在渲染器里加 FPS 计数
2. 在主窗口加 1 秒采样定时器
3. 加 `PerformanceMetricsRecorder`
4. 落 CSV
5. 最后再加标题栏显示

这样便于逐步验证，不容易影响原有曲线功能。

## 7. 数据解释说明

测试报告里建议明确说明：

- `FPS`：曲线渲染器实际渲染帧率
- `CPUPercent`：当前应用进程 CPU 占用率
- `GPUPercent`：当前应用进程 GPU 占用率
- 不是整机 CPU/GPU 总占用率

否则容易和任务管理器里整机占用率混淆。

## 8. 最小实现清单

如果只想快速加上功能，最低需要：

- 一个渲染器计数器
- 一个每秒采样定时器
- 一个 CSV 记录类
- 一个 GPU 性能计数器依赖

## 9. 建议的最终效果

程序运行后应具备以下效果：

- 测试人员启动程序
- 按原方式调整视图和曲线
- 程序自动每秒记录一行 CSV
- 关闭程序后，测试人员直接去固定目录拿结果文件

这样最适合后续做：

- FPS 曲线图
- CPU 曲线图
- GPU 曲线图
- 不同视图数/曲线数下的性能对比报告
