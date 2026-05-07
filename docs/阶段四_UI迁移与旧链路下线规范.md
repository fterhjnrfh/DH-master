# Phase 4：UI 迁移与旧链路下线规范

**阶段目标**：让 UI 正式切到查询层，并有计划地下线旧的直连数据访问链路  
**阶段定位**：收口实施阶段，不再允许长期双轨运行  
**文档版本**：v1.1  
**日期**：2026-04-22

---

## 当前完成度

**更新时间**：2026-05-01
**状态**：受控迁移中（先验证高速段直写与只读旁路，再接 UI 主绘制）

进入判断：

1. `Phase 2` 查询层、运行期 preview cache、session-scoped store、factory 装配点已可运行。
2. `Phase 3` 已完成 `manifest + catalog + preview/raw index + persisted query` 最小闭环。
3. `20260426-113416` Phase 3 smoke 已通过：`MetadataSource=Catalog`、`PreviewFilesValidated=640`、`RawIndexFilesValidated=160`。
4. `20260501-225658` SDK manual TDMS 直接保存已完成约 `68min / 2.2T` 人工验证，`7285` 个 source/segment `.tdms`，无保护、无拒绝、无写入故障。
5. `20260501-154751` Windows 原生写盘 probe 已达 `1765.7MiB/s`，当前 `160ch * 1MHz` 写盘压力具备明显余量；`64*64*100kHz` 仍需按同口径补测。

开始条件：

1. `Phase 3` 完成最小目录与索引读取骨架。已满足。
2. 查询层能同时消费实时源和历史 artifacts。已满足最小闭环。
3. 至少一个受控 UI 场景能通过新链独立验证。历史回放已满足；实时 UI 主链尚未迁移，必须在本阶段先做受控迁移。
4. 迁移前必须冻结性能基线与回退门槛，不能先接 UI 再补验收。

## 0. Phase 4 进入前架构守门

### 0.1 必须继续满足的需求文档约束

| 需求 | 当前架构状态 | Phase 4 守门结论 |
| --- | --- | --- |
| `1V-64C >= 80 FPS` | 旧链路有批准基线，新查询链尚未接实时 UI 主链 | 接入实时 UI 前必须跑迁移前/后对比，低于底线或回退超过 `5%` 不允许合入 |
| `64V-64C >= 60 FPS` | 旧链路有批准基线，新查询链尚未接实时 UI 主链 | 多视图迁移必须单独验收，不能用单视图结果替代 |
| `4000` 不是显示上限 | 查询模型保留 `TotalActualPoints` / `MaxActualPointsPerChannel`，UI 仍存在若干 `4000` 点显示预算 | Phase 4 必须区分真实点数、显示点数、渲染预算，禁止把显示预算写成真实点数 |
| 包络异常值保真 | 实时 preview 与持久化 preview 均采用 envelope/min/max 语义 | UI 迁移时不得改成均值抽样或普通等距抽样 |
| 64 个独立视图 | 当前结果页仍保留独立视图模型 | 迁移时只能共享查询/缓存层，不能合并视图语义 |
| UI 不阻塞采集/落盘 | 实时链仍以内存缓存为主，回放链走 artifacts | UI 查询必须异步、可取消、可跳帧，不允许在 UI 线程做文件读取或重建 preview |
| 录制与绘制同时运行 | Phase 4 守门要求 SDK 原始采集期间保持实时发布，同时把预览拆通道迁移出 callback 热路径 | raw block 入队优先，预览发布低优先级、可丢中间帧、不得阻塞 callback |
| 历史回放快速打开 | TDMS direct-save session 已具备 `manifest + TdmsSegments + L0局部seek` 秒级小窗口打开能力；preview sidecar 录后/后台生成 | 无 preview 时只能做小窗口明细，不等价于全局总览；全局缩放仍需 L1-L4 或至少 L2-L4 sidecar |
| `2s` 视窗默认优先 preview | `L1.2s` 已通过，`L0.2s` 用于精确明细/统计 | UI 缩放策略必须保留“先 preview，必要时 raw”的层级选择 |
| 物理 TDMS 文件口径 | 当前高性能原始段已切为 source-sharded 物理 `.tdms`，`data/session_20260501_225658_246` 已完成约 `68min / 2.2T` 长录制验证 | 可按“多个 source/segment `.tdms` + manifest 组成一个逻辑 session”的口径推进回放和压缩算法入口 |

### 0.2 当前代码结构风险

1. `CurvePanel` 对 `RealtimeDisplayCache` / `RealtimeSweepSnapshotCache` / `RealtimeFrameCoordinator` 的直接字段和注入入口已删除；`DataBus` 仅保留在 `AttachSelectorDataBus` 选择器数据源边界，不能再作为面板本地构帧 fallback。
2. `CurvePanel` 不再保留实时 query service 注入点，实时主绘制由 `MainWindow + CurveViewState + RealtimeSweepSnapshotCache` 统一组帧。
3. `TDMS查看` 已接入 `PersistedPreviewQueryRuntime`，但仍保留少量 `TdmsReaderUtil` 直读能力用于旧文件/通道枚举；正式路径必须继续收口到 catalog/query。
4. `MaxPointsPerChannel`、`ReplayTargetPointsPerChannel`、`MaxPreviewPointsPerChannel` 只能视为显示/查询预算，不能作为真实点数上限。
5. 任何 UI 迁移都必须写 `legacyPathUsed`、`queryLatencyMs`、`renderLatencyMs`，否则不能判断指标下降来自查询、渲染还是旧链回退。
6. `StartSdkRawCaptureSession` 不允许通过 `SetSdkRealtimePublishEnabled(false)` 关闭采集期间的实时绘制。
7. `SdkDataProcessor` 的预览拆通道和发布必须走非阻塞预览发布队列，不能回退到 callback 热路径。

### 0.3 Phase 4 接入顺序冻结

1. 先修复 SDK 录制与实时预览的并行能力：raw block 入队和写盘优先，实时预览走低优先级队列。
2. 再做“只读旁路”验证：UI 继续用旧链显示，同时并行请求新查询层并记录结果，不参与绘制。
3. 再迁移单视图实时显示：只迁移 `1V-64C`，验证 `80 FPS` 底线和相对基线回退不超过 `5%`。
4. 再迁移多视图实时显示：迁移 `64V-64C`，验证 `60 FPS` 底线和独立视图语义。
5. 历史回放保持当前新链主路径，补齐日志和旧直读路径退场。
6. 最后清理旧链路直接依赖，保留的旧类只能作为底层适配层。

当前执行状态：第 1 项已按非阻塞预览队列落代码，并通过 `20260426-132316` 人工验证；日志显示 `realtimePublishEnabled=True`、`previewDroppedBlocks=0`。TDMS 直接保存已在 `20260501-225658` 真实 SDK 长录制中跑通约 `68min / 2.2T`，停止 `segmentDrainMs=360.959`。第 2 项只读旁路曾通过选中视图后台 shadow 查询验证，日志写入 `CurveQueryShadowCompare`，不参与当前绘制结果；实时主绘制迁移后，该断开的 shadow/query 对象已从实时窗口清理，历史/TDMS 回放查询链路不受影响。`20260503-184417` 实测表明单纯启用 Skia 帧复用后 `1V-64C` / `64V-16C` 仍约 `30 FPS`；`20260503-222942` 进一步验证，直接在 `CurvePanel` 渲染热路径同步调用 `ICurveFrameProvider.GetLatestAsync(L0)` 会让 `64V-64C` 从约 `30 FPS` 退化到约 `3 FPS`，该改法已回退。`20260504-001027` 显示渲染外 tick 预准备可把 `64V-64C` 从灾难性退化恢复到约 `17-29 FPS`，但空载单视图仍约 `60 FPS`，说明当前 Avalonia/显示链路存在 60Hz 级调度上限，`64V-64C` 还叠加 64 个独立 `CurvePanel` / DrawOp 的调度成本。批量 Skia 多视图实验控件因组件行为与已验收的 8x8/7x7/6x6... `UniformGrid + CurvePanel` 交互不一致，已从主窗口删除；后续实时多视图仍以原多面板控件为准。主窗口已引入 `CurveViewState` 作为实时路径的选择、通道、设备和缩放状态来源，在线通道变更、设备切换、采样率变化和增删视图已同步刷新 view state / frame；单视图和多视图均通过主窗口统一 `RealtimeCurveFrameSnapshot` 构建入口生成当前帧，旧 `CurvePanel` 只消费外部 frame snapshot 并保留交互/兼容壳。性能 CSV 已增加 `FrameSource` 字段，`render-phase-timing.log` 已增加 `RealtimeFrameSourceSummary`，用于区分 `external-single` 与 legacy fallback。`CurvePanel` 已支持 `RequireExternalFrameSnapshot()`，新主路径下 provider 缺少 external frame 时返回空数据并记录 `CurvePanelLegacyFallback`，且外部不能再通过布尔参数关闭 external-frame 保护；性能采样已从 `CurvePanel` 旧缓存 getter 脱钩，改为使用 `CurveViewState + 最近 frame 统计`；external-frame 模式下通道/设备/采样率变更不再维护旧 sweep dirty state。主窗口新建面板时已不再注入旧 display/query/frameCoordinator 依赖，`CurvePanel` external 模式下也不订阅旧 DataBus 更新事件；旧的 `CurvePanel` display cache / sweep cache / frame coordinator / query service 注入入口已删除，避免主路径重新接回旧链路；顶部全局缩放已改为直接修改 `CurveViewState` 并通过 `SetZoomState` 下发给单视图兼容壳，外部不再从 `CurvePanel` 反读缩放状态；通道选择已增加 `CurvePanel.SelectedChannelsCommitted` 事件同步回 `CurveViewState`，外部不再从 `CurvePanel` 反读选中通道；设备过滤下发已改为 external-frame 纯视图状态，不触发旧 sweep dirty 或自动选通道；新建实时面板不再 `AttachDataHub`，只通过 `AttachSelectorDataBus` 给内部选择器提供通道列表，面板本体不再持有实时 DataBus 作为主绘制数据源；`AttachLegacyDataHub` 及旧 DataBus 本地构帧入口已删除，provider 缺少 external frame 时只返回空数据并写 fallback-blocked 日志；无调用的旧公共入口 `PrepareRealtimeFrameData` / `ClearExternalFrameSnapshot` / `ZoomIn` / `ZoomOut` / `ZoomInX` / `ZoomOutX` / `ZoomInY` / `ZoomOutY` / `ResetZoom` / `Dispose` 已删除，外部可调用面继续收窄；`CurvePanel` 内部已移除旧 `RealtimeDisplayCache` / `RealtimeSweepSnapshotCache` / `RealtimeFrameCoordinator` 字段和分支，不再提供 DataBus 本地读取 fallback；此前临时拆出的 legacy 本地构帧 helper 已清理。下一步继续压缩单视图兼容壳，并逐步把交互状态迁出 `CurvePanel`。

最新推进：未引用的旧 `CurveCanvas` / `DrawingContext` 软件绘制控件已删除，实时曲线文件内只保留当前 Skia/external-frame 兼容壳与显式 legacy fallback。

最新推进：`CurvePanel` 内部 60 FPS `DispatcherTimer` 旧刷新入口已删除，实时刷新统一归 `MainWindow` 管理，`Stop()` 仅保留为兼容清理入口。

最新推进：`CurvePanel` 的 provider 快照缓存与统计字段已集中到 `CurvePanelSnapshotCache`，控件本体不再直接维护缓存锁和缓存字段。

最新推进：Skia provider shim 已在 `CurvePanel` 构造阶段统一绑定，external-frame 主路径不再依赖旧数据入口；缺少 external frame 时只清空/保持空快照并记录 fallback-blocked 日志。

最新推进：`AttachLegacyDataHub`、旧 DataBus 本地构帧入口，以及此前拆出的 `LegacyCurvePanelFrameBuilder` / `LegacySweepWindowClock` / `LegacySweepWindowProjector` / `CurvePanelDataFlowMetrics` 已删除；`CurvePanel` 只允许通过外部 frame snapshot 获得绘制数据，DataBus 只服务通道选择器。

最新推进：`CurvePanel` 本体已不再订阅 `OnlineChannelManager.OnlineChannelsChanged`；在线通道变化只由 `ChannelSelector` 与主窗口状态流处理，避免单视图壳重复触发旧式刷新。

最新推进：`CurvePanel` 内部缩放按钮已通过 `ZoomStateCommitted` 回写 `MainWindow.CurveViewState`，顶部全局缩放、单视图面板缩放和 batch 多视图缩放现在共享同一份 zoom/auto-fit 状态。

最新推进：实时主窗口已移除不再消费的 `RealtimeFrameCoordinator` 字段、5ms tick `AdvanceFrame()` 调用和旧 bridge 清理残留，避免旧迁移日志/状态推进继续混入实时主绘制路径。

最新推进：`CurvePanelSnapshotCache` 已删除 legacy dirty / MatchesClean / LastPreviewPointCount 状态，面板快照只表示外部 frame 的最后一次写入，不再模拟旧本地缓存重建生命周期。

最新推进：`CurvePanel.SetExternalFrameSnapshot` 已去掉旧缓存生命周期参数，只接收窗口数据、Y 轴范围和真实点数统计；active/cached channel 统计统一留在 `MainWindow` 的 frame summary。

最新修正：批量 Skia 多视图实验控件已删除，`16*16` / `64*64` 等预设只走原 `UniformGrid + CurvePanel` 多面板链路，避免组件行为偏离已验收的 8x8/7x7/6x6... 网格交互。

最新推进：Skia provider compatibility shim 已抽出到 `CurvePanelFrameProviderShim`，`CurvePanel` 只负责把 Skia 委托绑定到 shim，不再直接实现 provider 取数和 fallback 日志逻辑。

### 0.4 禁止直接进入的做法

1. 禁止一次性把 `CurvePanel` 全量切换到新查询层。
2. 禁止为了帧率把单曲线真实点数压回 `4000` 以内。
3. 禁止在 UI 线程执行 TDMS/raw 文件读取。
4. 禁止把历史回放整通道读取保留为默认路径。
5. 禁止只验证 1 个视图后推断 64 个视图也满足指标。
6. 禁止以“录制时关闭实时预览”作为性能规避手段。
7. 禁止在 SDK callback 中执行高成本绘制准备、整通道转换或等待 UI/查询结果。

---

## 1. 范围

1. `CurvePanel` 改为只依赖查询层
2. `SkiaMultiChannelView` 不再假设底层来自旧缓存
3. 旧直连数据访问链路退场
4. 保留必要的兼容适配层

---

## 2. 输入与输出

### 2.1 输入

- Phase 1 冻结接口
- Phase 2 新查询链路可运行
- Phase 3 历史回放快速打开路径可运行

### 2.2 输出

- UI 统一走新查询接口
- 旧链路退居兼容层或正式下线
- 单视图、多视图、历史回放统一消费查询结果

### 2.3 模块变更清单

| 模块 | 输入 | 输出 | 结果 |
| --- | --- | --- | --- |
| `CurvePanel` | Query / Frame Provider | `CurveWindowSnapshot` | 仅消费查询结果 |
| `SkiaMultiChannelView` | 点数据与视图状态 | 绘制结果 | 不再假设旧缓存来源 |
| 历史回放视图 | Query Layer | `CurveWindowSnapshot` | 统一显示路径 |

---

## 3. 需要下线的旧依赖

后续目标是让 UI 不再直接依赖：

- `DataBus`
- `RealtimeDisplayCache`
- `TdmsReaderUtil`

它们可以保留在底层适配实现中，但不应继续直接暴露给视图层。

---

## 4. 迁移顺序

### 4.1 第一步

让单视图先切到查询层。

### 4.2 第二步

让多视图切到查询层。

### 4.3 第三步

让历史回放视图也切到查询层。

### 4.4 第四步

清理旧链路：

- 旧事件驱动 UI 刷新链
- 旧整通道读文件路径
- 旧散落在 panel 内部的数据裁剪逻辑

### 4.5 迁移顺序输入输出表

| 步骤 | 输入 | 输出 |
| --- | --- | --- |
| 单视图迁移 | Query Layer 最小实现 | 单视图只走新链路 |
| 多视图迁移 | 单视图迁移完成 | 多视图只走新链路 |
| 历史回放迁移 | Phase 3 可运行 | 回放只走新链路 |
| 旧链路清理 | 新链路稳定 | 旧链路退场 |

---

## 5. 禁止项

1. 不允许 UI 继续同时依赖新旧两套主数据链。
2. 不允许新功能继续挂在旧直连链路上。
3. 不允许历史回放保留整通道默认读法作为主路径。

---

## 6. 交付物

1. UI 层依赖关系清单
2. 旧链路下线清单
3. 兼容适配层清单
4. 回归验证清单

## 6.1 回归验证最低要求表

| 验证项 | 要求 |
| --- | --- |
| 单视图 | 显示语义不回退 |
| 多视图 | 独立视图能力不回退 |
| 历史回放 | 快速打开路径可用 |
| 包络线 | 异常值可见性不回退 |
| 查询层 | 新旧版本切换稳定 |
| 验收指标 | `1V-64C >= 80 FPS` 且 `64V-64C >= 60 FPS` |
| 点数语义 | 不允许把 `4000` 当作显示硬上限 |

---

## 7. 阶段验收标准

1. UI 所有窗口查询只走新接口
2. 旧链路不再被视图层直接调用
3. 单视图、多视图、历史回放都能走同一套查询模型
4. 关键交互语义不回退

---

## 7.1 日志要求

本阶段最低日志字段：

- `viewId`
- `sessionId`
- `previewLevel`
- `version`
- `dataEpoch`
- `sourceVersion`
- `buildState`
- `isPreview`
- `isComplete`
- `legacyPathUsed`
- `queryLatencyMs`
- `renderLatencyMs`

要求：

- 必须能从日志判断 UI 是否仍落到旧链路
- 必须能区分单视图、多视图、历史回放三个入口

## 7.1.1 日志落盘要求

沿用统一目录规则：

- `data/architecture-validation/phase4/yyyyMMdd-HHmmss/`

本阶段最少要求：

- `summary.log`
- `validation.log`
- `ui-migration.log`
- `legacy-fallback.log`

`summary.log` 必须额外记录：

- `migratedScenarios`
- `legacyFallbackCount`
- `avgQueryLatencyMs`
- `avgRenderLatencyMs`
- `result`

## 7.2 人工验证要求

本阶段至少覆盖以下人工测试场景：

1. 单视图实时显示
2. 多视图实时显示
3. 单视图历史回放
4. 多视图历史回放
5. 视图独立勾选
6. 视图独立缩放
7. 单曲线与多曲线切换

每个场景至少验证：

- 是否只走新查询接口
- 关键交互语义是否与旧实现一致
- 是否没有再落回旧的直连路径

## 7.3 量化退出门槛

本阶段通过标准：

1. UI 主路径对新接口依赖覆盖率达到 `100%`。
2. `legacyPathUsed` 在正式场景下应为 `0`。
3. 单视图、多视图、历史回放三个入口均通过人工验证清单。
4. 已迁移场景帧率相对批准基线不得回退超过 `5%`。
5. 关键交互语义不得回退：
   - 独立勾选
   - 独立缩放
   - 单曲线 / 多曲线显示一致性
   - 包络异常值可见性
6. 指标语义不得回退：
   - `1V-64C` 必须达到 `80 FPS` 底线
   - `64V-64C` 必须达到 `60 FPS` 底线
   - 不允许通过把单曲线真实点数压回 `4000` 以内达标

## 7.4 旧链路退场清单

本阶段应正式下线：

- `CurvePanel` 对 `DataBus` 的直接窗口取数主路径
- `CurvePanel` 对 `RealtimeDisplayCache` 的直接主路径
- `TdmsReaderUtil` 作为 UI 默认读取入口
- 旧事件驱动 UI 主刷新链

允许保留：

- 兼容适配层
- 调试开关

要求：

- 所有保留项必须明确标注为兼容层，不得继续扩展业务功能

---

## 8. 结论

Phase 4 的目标不是再发明新层，而是：

**让前面定义好的分层真正成为系统默认路径，并正式清理旧链路。**
