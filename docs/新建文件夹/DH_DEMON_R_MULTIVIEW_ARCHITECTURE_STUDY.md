# DH-Demon-R 多视图架构学习结论

## 目的

这份文档只聚焦一件事：

- 对照 `DH-Demon-R` 中与“多视图结果页”直接相关的架构
- 判断哪些地方已经被当前项目吸收
- 判断哪些地方仍然没有学到位，且最值得继续做

不研究下面这些已经不值得继续花时间的内容：

- 单视图性能链路
- 当前项目已经验证没问题的扫掠语义
- 老师项目里与我们数据源强耦合的拉流细节
- 纯历史曲线/归档查询逻辑

## 先说结论

`DH-Demon-R` 真正比当前项目强的地方，不是单个通道怎么画，也不是数据源本身，而是：

1. 结果页是“单宿主控件统一绘制多面板”
2. 显示层只读“显示专用数据仓库”
3. 面板状态集中管理，但每个面板状态仍然独立

当前项目已经学到了一部分“显示专用数据仓库”和“显示快照”思路，但**还没有真正学到单宿主多面板渲染这一层**。  
这也是为什么：

- `16V-16C` 已经接近或达到目标
- `64V-16C` 和 `64V-64C` 仍然明显掉下去

核心瓶颈已经不是单个 `CurvePanel` 的画线算法，而是 `64` 个独立控件同时存在时的宿主/UI/合成成本。

## 一、DH-Demon-R 里真正值得学习的多视图部分

### 1. 单宿主控件统一绘制多面板

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/controls/SkiaMultiGridControl.cs`
- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/Views/ResultDisplayView.axaml.cs`

它的核心特点：

- 结果页不是 `64` 个独立子控件
- 而是一个 `SkiaMultiGridControl` 统一绘制所有 panel
- 所有 panel 的布局、命中测试、绘制入口都集中在一个控件里完成

这带来的收益：

- 只有一个 Avalonia 自定义绘制入口
- 只有一个控件参与主要绘制调度
- 没有 `64` 个 `UserControl/Control` 同时各自 `InvalidateVisual`
- 多 panel 的背景、坐标、通道绘制可以在一个画布上下文中统一完成

这正是当前项目还没有真正学到位的部分。

### 2. 显示专用数据仓库

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Display/Realtime/ChannelRealtimeStore.cs`
- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Display/Realtime/ChannelRealtimeData.cs`
- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/Services/RealtimeDataPullService.cs`

它的核心特点：

- UI 不直接碰采集层复杂结构
- 数据先进入 `ChannelRealtimeStore`
- 每个通道对应一个 `ChannelRealtimeData`
- 结果页绘制时只读这个显示仓库

老师项目里这一层的意义不是“点数上限 500”，而是：

- 采集/拉流和 UI 绘制解耦
- UI 总是读取一份显示友好的中间结构

这一点当前项目已经学到一部分，但还不彻底。

### 3. 面板状态集中管理

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/controls/SkiaMultiGridControl.cs`

老师项目里每个 panel 的状态不是挂在 `64` 个独立控件实例上，而是集中存在一个宿主控件里，比如：

- `_panelVisibleSamples`
- `_panelYZoom`
- `_panelPxPerPoint`
- `_panelChannels`

这并不意味着“视图状态共享”，而是：

- 状态仍然是每个 panel 独立
- 只是数据结构从“64 个控件对象”变成“一个控件里的 64 组状态数组/字典”

这非常适合当前项目，因为你已经明确要求：

- 每个视图独立选通道
- 每个视图独立扫掠进度
- 每个视图独立时间窗
- 每个视图独立缩放和 Y 轴策略

这些独立性完全可以保留，不需要靠 `64` 个独立控件来实现。

## 二、DH-Demon-R 里不值得直接照搬的部分

### 1. 数据源拉取细节

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/Services/RealtimeDataPullService.cs`

这部分和当前项目的数据源不同，不能直接移植。  
老师项目是从 `SessionTableManager` 周期拉取，再写入 `ChannelRealtimeStore`。

当前项目的数据总线和预览点生成逻辑已经不同，所以这部分最多只能学习“解耦思路”，不值得逐行照搬。

### 2. 500 点上限本身

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/ViewModels/MainWindowViewModel.cs`

老师项目里初始化是：

- `ChannelRealtimeStore(500)`

这不适合当前项目目标。  
我们当前明确要求的是：

- 每条线最多约 `4000` 点
- 多于 `4000` 才降采样

所以“500 点”这个具体值不值得学，只需要学它“显示专用仓库”的结构。

### 3. 老师项目当前刷新节奏

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Client.App/Views/ResultDisplayView.axaml.cs`

老师项目的绘图定时器是：

- `66ms`

这对应的是大约 `15 FPS` 的级别，不是我们现在要追的目标。  
所以这里也不值得按具体数值照搬。

### 4. 历史归档/DecimatingArchive

关键文件：

- `/mnt/c/Users/Administrator/Desktop/DH-Demon-R/src/DH.Display/History/DecimatingArchive.cs`

这更偏历史/归档/查询，不是当前多视图实时结果页最关键的瓶颈。  
短期内没必要继续研究。

## 三、当前项目已经吸收了哪些思路

当前项目已经吸收的部分：

1. 底层显示数据缓存
   - 已有 `RealtimeDisplayCache`
2. 显式状态快照缓存
   - 已有 `RealtimeSweepSnapshotCache`
3. 每个视图独立扫掠状态
   - 已确认并修正语义
4. 每条线按目标点数做限点
   - 已按 `4000` 上限口径实现
5. 渲染热路径的部分分配优化
   - 已做 bin 缓冲复用、成品图缓存、增量扫掠绘制等

这些说明我们已经学到了“显示专用中间层”和“减少无意义重复计算”的一部分。

## 四、当前项目还没有学到位的部分

这部分才是接下来最应该投入的。

### 1. 结果页仍然是 64 个独立 CurvePanel

当前文件：

- `/mnt/c/users/administrator/desktop/dh/src/DH.Client.App/Views/MainWindow.axaml`
- `/mnt/c/users/administrator/desktop/dh/src/DH.Client.App/Views/MainWindow.axaml.cs`

当前项目还是：

- `UniformGrid + 64 个 CurvePanel`
- 主窗口每 `5ms` 遍历所有 panel 调 `Invalidate()`

这意味着：

- 64 个独立控件
- 64 套 Avalonia 绘制入口
- 64 套局部生命周期和状态同步开销

这一层老师项目是没有的，因为它已经把多 panel 统一进单控件了。

### 2. 每个视图仍然各自调用一套渲染控件

当前文件：

- `/mnt/c/users/administrator/desktop/dh/src/DH.Client.App/Views/CurvePanel.axaml.cs`
- `/mnt/c/users/administrator/desktop/dh/src/DH.Client.App/controls/SkiaMultiChannelView.cs`

当前虽然单视图内部已经压得不错，但多视图本质上还是：

- 一个 panel 对应一个 `SkiaMultiChannelView`

这使得多视图问题从“画线算法问题”转成了“多控件宿主问题”。

### 3. panel 状态还没有集中化

当前状态是：

- 状态散在 `64` 个 `CurvePanel` 实例里

这不利于：

- 多 panel 同步布局
- 同状态 panel 的共享计算
- 统一命中测试
- 单次大画布绘制

老师项目里这部分是集中在 `SkiaMultiGridControl` 里的。

## 五、为什么现在可以判断“该换层级优化了”

从最近几轮 CSV 可以看出一个稳定规律：

- 单视图已经明显改善
- `16V-16C` 基本可接受
- `64V-16C` 还能上到十几帧
- `64V-64C` 长期卡在 `4~5 FPS`

这说明：

- 单个视图内部算法不再是主瓶颈
- 主瓶颈已经迁移到 `64` 个视图同时存在时的宿主成本

继续只在 `CurvePanel` 或 `SkiaMultiChannelView` 里补丁式优化，收益会越来越小。

## 六、下一阶段最值得执行的方案

### 方案主线

在当前项目里实现一个“单宿主多面板渲染控件”，但保留每个视图独立状态。

### 必须保留的语义

- 每个视图独立通道选择
- 每个视图独立扫掠进度
- 每个视图独立窗口起点
- 每个视图独立缩放
- 每个视图独立 Y 轴策略
- 每个视图独立选中状态

### 需要改变的只是承载方式

从：

- `64` 个独立 `CurvePanel`

变成：

- `1` 个结果页宿主控件
- 内部持有 `64` 份 panel state
- 一次绘制中画完所有 panel

### 优先实现顺序

1. 先做结果页单宿主控件骨架
   - 负责布局、命中测试、面板状态表
2. 把当前 `CurvePanel` 的独立状态提取成 `PanelRenderState`
3. 让宿主控件直接消费 `RealtimeDisplayCache/RealtimeSweepSnapshotCache`
4. 再把 `SkiaMultiChannelView` 中真正可复用的画线逻辑下沉到宿主控件里

## 七、这份学习结论的最终判断

老师给的 `DH-Demon-R` 架构，我们已经学到了“显示专用仓库”的一半，  
但**还没有把最关键的“单宿主多面板渲染”真正吸收进当前项目**。

因此，下一步最合理的结论不是“继续零散补丁优化”，而是：

**正式进入单宿主多面板结果页重构阶段。**
