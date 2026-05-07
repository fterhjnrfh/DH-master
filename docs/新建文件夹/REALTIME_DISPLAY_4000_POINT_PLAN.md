# 实时结果页扫掠绘制优化方案

## 1. 已确认的显示语义

- 结果页不是连续滚动，也不是示波器拖尾。
- 每个视图都必须保持“扫掠式”显示语义：
  - 清空画布
  - 从左到右匀速绘制
  - 到达右侧后清空
  - 再从左侧重新开始
- 保持“整窗跳变”效果，不改成连续滑窗。
- 每个视图内，每条曲线最终送进 renderer 的可见点数控制在约 `4000` 以内即可。
- 少于 `4000` 不补点，多于 `4000` 才降采样。

## 2. 必须隔离的视图状态

- 每个视图的通道选择必须独立。
- 每个视图的扫掠进度必须独立。
- 每个视图的时间窗口状态必须独立。
- 每个视图的 `X/Y` 缩放必须独立。
- 每个视图的 `Y` 轴策略必须独立。
- 每个视图自己的显示快照和绘制缓存必须独立。

这意味着后续不能再做“多个视图共享同一份窗口结果”这一类会串状态的优化。

## 3. 可以共享的基础层

- `DataBus` 中每个通道的底层预览数据。
- 每个通道最近一段点数据的只读快照。
- 不带视图状态的基础索引能力。

共享只能停留在“通道原始预览数据”这一层，不能跨过视图边界去共享扫掠窗口、缩放结果或最终显示结果。

## 4. 当前代码链路

当前结果页链路如下：

1. `DataBus.PublishFrameAsync()` 接收原始帧。
2. `DataBus.ConvertFrameToCurvePoints()` 先把一帧原始样本压成预览点。
3. `MainWindow` 的固定定时器触发所有 `CurvePanel.Invalidate()`。
4. 每个 `CurvePanel` 在重绘前独立准备本视图窗口数据。
5. `SkiaMultiChannelView.RenderMulti()` 再做分箱、坐标投影和绘制。

当前关键入口：

- 数据预览压缩入口：
  [DataBus.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Data/DataBus.cs#L132)
- 固定刷新入口：
  [MainWindow.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/MainWindow.axaml.cs#L216)
- 每视图窗口数据准备入口：
  [CurvePanel.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/CurvePanel.axaml.cs#L527)
- 每视图缓存检查入口：
  [CurvePanel.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/CurvePanel.axaml.cs#L570)
- 渲染热路径入口：
  [SkiaMultiChannelView.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/controls/SkiaMultiChannelView.cs#L564)

## 5. 优化目标

- 维持固定刷新率设计，目标按 `5ms` 节奏设计。
- 不改变扫掠语义，只降低每帧的数据准备成本和绘制成本。
- 让每个视图都独立完成自己的扫掠状态推进。
- 让每条曲线在进入 renderer 前就已经被压到“足够画”的点数范围。
- 尽量减少渲染线程里的重复切片、重复分配和重复遍历。

## 6. 不允许再碰的方向

- 不改成连续滚动时间窗。
- 不改成多视图共享同一份扫掠窗口结果。
- 不改成单画布统一驱动所有视图扫掠状态。
- 不把多个视图的 `Y` 轴、自适应范围、时间窗绑定到一起。

## 7. 正确的目标架构

目标链路应调整为：

1. 原始帧进入 `DataBus`。
2. `DataBus` 只负责保留每通道最近一段基础预览点。
3. 每个 `CurvePanel` 独立维护自己的“视图显示状态”：
   - 当前扫掠周期
   - 当前窗口起点
   - 当前扫掠进度
   - 当前缩放状态
   - 当前 `Y` 轴状态
4. 每个 `CurvePanel` 按自己的状态，从共享通道快照中提取所需数据。
5. 每个 `CurvePanel` 对每条曲线独立降采样到约 `4000` 点以内。
6. `SkiaMultiChannelView` 只消费该视图已经准备好的轻量数据并完成绘制。

一句话概括：

共享底层通道数据，隔离每视图显示状态。

## 8. 第一阶段实施内容

### 阶段 1A：恢复并固化正确扫掠语义

- 明确 `CurvePanel` 中“扫掠周期、窗口起点、当前绘制位置”的状态字段。
- 检查并修正 `UpdateSampleRate()`、`BuildSweepChannelData()`、`EnsureCachedSweepData()` 是否仍按扫掠式语义取数。
- 明确“清空后重新从零开始”的切换点，不允许出现连续滑动、拖尾或蛇形残留。

### 阶段 1B：把每视图降采样前移

- 在 `CurvePanel` 内新增“每视图独立显示快照”层。
- 该快照按“当前视图选中的通道 + 当前扫掠周期 + 当前缩放条件”生成。
- 每条曲线只在快照构建时做一次点数控制，目标约 `4000` 点以内。
- 无新数据、无状态变化时复用快照，不在每次 `Render` 时重建。

### 阶段 1C：瘦身 `SkiaMultiChannelView`

- `RenderMulti()` 不再负责决定“这次该画哪些数据”。
- `RenderMulti()` 只负责：
  - 读取已经准备好的每通道点序列
  - 做像素投影
  - 画线
- 优先减少以下开销：
  - 每帧临时数组分配
  - 每帧重复分箱
  - 每帧重复扫描整批数据求范围

## 9. 第二阶段实施内容

- 视情况优化 `DataBus.ConvertFrameToCurvePoints()` 的基础预览策略，让它更适合扫掠显示。
- 进一步拆分“通道基础预览数据”和“视图显示快照”之间的职责。
- 为每个视图增加更稳定的局部缓存失效条件，只在真正需要时重建视图快照。

## 10. 第一阶段最小改动入口

后续第一批代码改动只从下面几个点进入：

- 固定刷新节奏：
  [MainWindow.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/MainWindow.axaml.cs#L216)
- 数据预览生成：
  [DataBus.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Data/DataBus.cs#L132)
- 视图数据准备：
  [CurvePanel.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/CurvePanel.axaml.cs#L527)
- 视图缓存控制：
  [CurvePanel.axaml.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/Views/CurvePanel.axaml.cs#L570)
- 渲染热路径：
  [SkiaMultiChannelView.cs](/mnt/c/Users/Administrator/Desktop/DH/src/DH.Client.App/controls/SkiaMultiChannelView.cs#L564)

## 11. 执行原则

- 先修语义，再做性能。
- 先保证每视图独立，再谈共享。
- 先减少每帧做的事，再提高刷新频率收益。
- 不再做任何会改变扫掠显示语义的“架构优化”。
