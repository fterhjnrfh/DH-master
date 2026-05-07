# 拖拽/组态设计器 (Drag & Drop Configuration Designer)

[![.NET](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/Avalonia%20UI-11.x-purple.svg)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

一个基于 Avalonia UI 的企业级可视化拖拽设计器，支持控件连接和功能组合的组态系统。本项目采用现代化的 MVVM 架构，提供了完整的可扩展框架用于构建复杂的工业组态应用。

## 🚀 快速开始

### 方式一：使用运行脚本（推荐，自动激活C++编译器环境）

由于项目需要编译 C++ 算法，建议使用提供的批处理脚本运行：

**简单版本**（快速启动）：
```bash
# 双击运行或在命令行执行
run_with_env.bat
```

**详细版本**（包含环境检测信息）：
```bash
# 双击运行或在命令行执行
run_with_detailed_env.bat
```

这些脚本会自动：
- 检测并激活 Visual Studio C++ 编译器环境（MSVC）
- 设置必要的环境变量
- 运行应用程序

> **注意**：如果脚本无法找到 Visual Studio，请手动编辑脚本中的路径，指向您的 Visual Studio 安装目录下的 `vcvars64.bat` 文件。

### 方式二：手动运行（需要先激活C++环境）

```bash
# 1. 首先激活 Visual Studio C++ 编译器环境
# 在命令行中执行（根据您的 VS 版本调整路径）：
call "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvars64.bat"

# 2. 然后运行项目
dotnet restore
dotnet build
dotnet run
```

**首次运行体验**：

1. 从控件库拖拽 2 个 `TextBox` 和 1 个 `DisplayControl` 到设计画布。
2. 通过点击控件中心的连接点，将这三个控件相互连接起来，形成一个控件组。
3. 选中该控件组中的任意一个控件，在右侧的属性面板中点击“激活工作逻辑”按钮。
4. 在弹出的对话框中选择“正弦波绘制器”，并点击确认。
5. 点击主窗口右下角的“运行”按钮，查看实时生成的正弦波效果。

> 如需完整的操作指引，可在应用顶部的“帮助”页签随时查看详细步骤与常见问题。

## 项目概述

本项目是一个功能完整的拖拽设计器，用户可以：

- 通过拖拽方式设计界面
- 建立控件间的连接关系
- 手动选择并激活控件组的“工作逻辑”（功能组合）
- 在运行窗口中测试已激活功能的界面

## 核心功能

### 1. 拖拽设计器

- **控件库**：提供多种控件（按钮、标签、文本框、复选框、组合框、显示屏等）
- **设计画布**：支持控件的拖拽放置和位置调整
- **属性面板**：实时编辑选中控件的属性，并提供激活工作逻辑的入口
- **可折叠面板**：控件库和属性面板支持折叠/展开

#### 模拟信号源控件

**SimulatedSignalSourceControl** 提供专业的信号模拟功能：
- **心电图式滚动**：信号从右侧流入，模拟实时监测效果
- **粗糙信号生成**：模拟未经处理的原始实验数据
- **多层噪声系统**：
  - 主要噪声：基础随机波动
  - 次要噪声：中等强度干扰
  - 高频抖动：模拟电子设备噪声
- **基线漂移**：模拟长期测量中的基线变化
- **偶发干扰**：2% 概率的尖峰噪声，模拟电磁干扰
- **实时参数调节**：峰位置、峰高度、峰宽度、噪声级别、基线漂移

### 2. 控件连接系统

- **连接点**：每个控件中心都有可交互的连接点
- **连接状态**：
  - 蓝色：默认状态（未连接）
  - 红色：连接状态（准备连接）
  - 绿色：已连接状态（与其他控件有连接）
- **连接操作**：
  - 点击连接点进入连接状态
  - 再次点击同一个点取消连接状态
  - 连接状态下点击另一个点建立连接
  - 重复连接已连接的控件可取消连接
- **可视化反馈**：
  - 深蓝色连接线显示控件间的连接关系（从控件中心连接）
  - 连接线显示在控件下方，不会遮挡操作
  - 连接的控件组会显示统一的边框
  - 支持右键点击连接线删除连接

### 3. 工作逻辑/功能组合系统

- **手动激活**：将控件连接成组后，系统会根据其构成，在属性面板提供激活入口。
- **智能筛选**：点击激活后，系统会弹出一个列表，其中只包含与当前控件组配置兼容的“工作逻辑”。
- **选择与配置**：用户从列表中手动选择一个工作逻辑（如“正弦波绘制器”）来激活。
- **自动配置**：激活后，系统会根据所选逻辑的预设，自动配置组内控件的属性（如将输入框重命名为“波幅”和“频率”，并填入默认值）。
- **可视化**：激活的控件组会显示带有功能名称的特殊边框，以表明其当前的工作逻辑。

### 4. 运行时系统

- **预览窗口**：将设计转换为可交互的运行界面。
- **功能执行**：已激活的工作逻辑（如“正弦波绘制器”）在运行时会真正执行其功能。
- **实时参数**：在运行窗口中修改输入框数值，可实时更新正弦波的显示。
- **控件提示**：鼠标悬停显示控件名称。

### 5. 帮助页签

**作用**：集中展示设计器与流程图编辑器的完整操作路径，便于新人快速熟悉两套完全不同的工作流。

#### 设计器操作指南

1. **控件库**：
    - 基础控件包含按钮、标签、文本框、复选框、组合框、显示屏/显示屏2。
    - 高级控件包含 OxyPlot 正弦波、SkiaSharp 正弦波、模拟信号源 (CPU)、GPU 模拟信号 (GL)、模拟 TCP 方波（CPU/GPU）、实测 TCP 波形等。
2. **拖拽与布局**：点击控件后即会在当前视口生成实例，使用鼠标拖动即可微调位置。选择控件后，可在属性面板修改名称、大小、位置、文本内容和前景色并点击“应用”保存；“删除”按钮用于移除控件。
3. **连线与控件组**：每个控件中心存在连接点。点击一次进入连接模式，点第二个控件即可连线；连线成功后连接点变绿，再次点击或右键连接线即可解除。成组控件会显示统一的边框标记。
4. **激活工作逻辑**：选择任意已连成组的控件，在属性面板点击“⚡ 激活工作逻辑”。弹出的列表仅展示当前连线结构可用的功能（如正弦波绘制器、GPU 波形预览等），选定后系统会自动配置控件名称与默认参数并在画布标记功能标签。“❌ 取消工作逻辑”可将该组恢复为未配置状态。
5. **运行预览**：点击画布右下角“运行”按钮，进入实时预览窗口。窗口中修改输入参数可即时刷新波形或业务逻辑反馈；未激活的控件组在此阶段不会执行逻辑。
6. **实用技巧**：控件库/属性面板可拖动或折叠；在重新布局前先取消工作逻辑可避免遗留配置。

#### 流程图编辑器操作指南

1. **节点库**：内置开始、结束、以及高斯滤波、中值滤波、移动平均、信号平滑等处理节点，每个节点附带默认参数（如窗口大小、Sigma、Smoothness）。
2. **放置与移动**：点击任意节点模板，即可在画布当前点击位置生成节点；拖动节点可重新排列执行顺序。选中节点后，右侧属性面板会列出全部参数，支持文本输入或 NumericUpDown 调整，并提供“删除节点”按钮快速移除。
3. **连线规则**：右键节点进入连接模式（边框高亮），再右键目标节点即可创建从 From→To 的连线。所有连接线都绑定在节点中心，移动节点后会自动刷新；重复连线不会生成副本，需要移除节点或清空画布来重建。
4. **导出与清空**：
    - “导出为 DLL/SO” 会根据 FlowGraph 执行顺序生成对应的 C++/CUDA 代码，默认输出到 `download/` 文件夹。
    - “清空画布” 会删除所有节点、连接线与参数设置，适合重新建模或演示前恢复初始状态。
5. **建模建议**：流程至少包含一个开始和结束节点；可串联多个滤波节点构建自定义管线。导出前请确认每个节点参数是否匹配真实算法需求。

#### 常见问题

- **脚本或导出失败**：先执行 `run_with_env.bat` 以激活 Visual Studio C++ 环境，再返回应用操作。
- **GPU 控件空白**：确认本机 GPU/驱动可用，必要时切换到 CPU/Skia 版本控件。
- **算法模块未出现**：点击顶部“算法管理”查看 DLL 是否被发现，并确认实现了 `IAlgorithm` 接口。
- **.xtj 加密失败**：确认文件未被占用，并查看控制台中 XtjCrypto 输出的异常信息。

## 技术架构

### 框架和库

- **UI 框架**：Avalonia UI (.NET 9.0)
- **图表库**：OxyPlot.Avalonia
- **架构模式**：MVVM (Model-View-ViewModel)

### 项目结构

```
NewAvalonia/
├── Models/                     # 数据模型
│   ├── ControlInfo.cs         # 控件信息模型
│   ├── ConnectionPoint.cs     # 连接点模型
│   ├── ControlConnection.cs   # 控件连接模型
│   └── ControlGroup.cs        # 控件组模型
├── ViewModels/                # 视图模型
│   ├── MainWindowViewModel.cs
│   └── DisplayControlViewModel.cs      # 显示屏控件视图模型
├── Views/                     # 视图组件
│   ├── MainWindow.axaml       # 主窗口（设计画布）
│   ├── PreviewWindow.axaml    # 预览窗口（运行界面）
│   ├── ConnectionPointView.axaml      # 连接点视图
│   ├── ConnectionLineView.axaml       # 连接线视图
│   ├── ControlGroupBorderView.axaml   # 控件组边框视图
│   ├── DisplayControl.axaml           # 显示屏控件
├── Services/                  # 服务层
│   ├── IConnectionManager.cs          # 连接管理接口
│   ├── ConnectionManager.cs           # 连接管理服务
│   ├── IFunctionCombinationService.cs # 功能组合接口
│   ├── FunctionCombinationService.cs  # 功能组合服务
│   ├── ConnectionOperationManager.cs  # 连接操作管理器
│   ├── ConnectionLineManager.cs       # 连接线管理器
│   └── RuntimeFunctionManager.cs      # 运行时功能管理器
└── Commands/                  # 命令实现
    └── RelayCommand.cs
```

### 核心组件说明

#### 数据模型层

- **ControlInfo**：控件的基本信息（类型、名称、位置、大小、内容等）
- **ConnectionPoint**：连接点信息（位置、状态、类型）
- **ControlConnection**：控件间的连接关系
- **ControlGroup**：连接的控件组，包含功能类型信息

#### 服务层

- **ConnectionManager**：管理控件间的连接关系，处理连接的创建、删除和验证
- **FunctionCombinationService**：检测和管理功能组合（如正弦波生成器）
- **ConnectionOperationManager**：处理设计画布上的连接操作和视觉反馈
- **RuntimeFunctionManager**：管理运行时的功能组合激活和参数绑定

#### 视图层

- **MainWindow**：主设计界面，包含控件库、设计画布和属性面板
- **PreviewWindow**：运行预览窗口，显示可交互的界面
- **ConnectionPointView**：可交互的连接点组件
- **ConnectionLineView**：连接线的可视化组件
- **ControlGroupBorderView**：控件组边框组件

## 使用指南

### 基本操作

#### 1. 创建控件

1. 从左侧控件库选择控件类型
2. 控件会自动添加到设计画布中心
3. 使用属性面板调整控件属性

#### 2. 控件操作

- **选择控件**：点击控件显示蓝色选择边框
- **移动控件**：拖拽控件到新位置
- **编辑属性**：在右侧属性面板修改控件属性
- **应用更改**：点击"应用"按钮保存属性修改

#### 3. 建立连接

1. 点击第一个控件的蓝色连接点（变为红色）
2. 点击第二个控件的蓝色连接点
3. 两个控件的连接点都变为绿色，显示连接成功
4. 连接的控件会显示统一的灰色边框

#### 4. 管理连接

- **取消连接状态**：再次点击红色连接点
- **删除连接**：重复连接已连接的控件
- **多重连接**：一个控件可以与多个控件连接

### 正弦波生成器组合

#### 创建与激活步骤

1. 从控件库添加 2 个 `TextBox` 控件和 1 个 `DisplayControl` 控件。
2. 将这三个控件通过中心的连接点相互连接，形成一个控件组。
3. 选中该组内任意一个控件，右侧的属性面板会出现“激活工作逻辑”按钮，点击它。
4. 在弹出的对话框中，系统会列出所有兼容此控件组合的工作逻辑（例如“正弦波绘制器”、“方波绘制器”）。
5. 选择“正弦波绘制器”并点击“确认”。
6. 激活成功后，你会看到：
   - 控件组的边框标签变为“正弦波绘制器”。
   - 两个 `TextBox` 的`Name`和`Content`属性被自动更新为“波幅”、“频率”及其默认值。

#### 运行测试

1. 点击右下角"运行"按钮
2. 在预览窗口中：
   - 显示屏控件显示动态正弦波
   - 修改输入框数值实时更新波形
   - 鼠标悬停显示控件名称

### 模拟信号源功能

#### 信号特性

**SimulatedSignalSourceControl** 专为实验数据模拟设计：

- **心电图式滚动显示**：
  - 固定X轴范围（0-200mm）
  - 信号从右侧连续流入
  - 模拟实时监测设备效果

- **粗糙信号生成**：
  - 模拟未经滤波处理的原始数据
  - 多层随机噪声叠加
  - 基于位置的确定性随机种子

- **噪声模拟系统**：
  - **主要噪声**：基础随机波动，强度可调
  - **次要噪声**：中等强度干扰信号
  - **高频抖动**：模拟电子设备固有噪声
  - **偶发尖峰**：2%概率的突发干扰，模拟电磁干扰或设备故障

- **基线漂移**：
  - 正弦波形式的长期基线变化
  - 模拟温度漂移或设备老化效应
  - 可调节漂移幅度

#### 参数控制

- **峰位置**：控制主要信号峰在周期中的位置
- **峰高度**：调节信号幅度（0-5倍）
- **峰宽度**：控制信号峰的宽度（1-50）
- **噪声级别**：调节整体噪声强度（0-1）
- **基线漂移**：控制基线变化幅度（0-2）

#### 应用场景

- 图像处理算法测试的信号源
- 滤波算法验证
- 信号分析软件开发
- 实验数据模拟和教学演示

> 📘 **GPU/CPU 版本如何选择？**
>
> - 需要 OpenGL/GPU 加速时选用控件库中“🔷 GPU模拟信号 (GL)”（`GLSimulatedSignalControl`）或“🧪 模拟TCP方波 (GPU)”（`TcpSquareWaveGlControl`），也可打开 GPU 演示控件验证性能。
> - 在无 GPU、WSL 或远程桌面环境无法创建 OpenGL 上下文时，改用 CPU 渲染的 `SimulatedSignalSourceControl` 或 “🧪 skiasharp模拟信号控件新”（`TcpSquareWaveSkiaControl`）。
> - 新控件 `TcpSquareWaveSkiaControl` 默认模拟 TCP 50 ms 方波，窗口满 2 秒后自动滚动，适合快速验证信号逻辑。

## 控件类型

### 基础控件

- **Button**：按钮控件
- **Label**：标签控件
- **TextBox**：文本输入框
- **CheckBox**：复选框
- **ComboBox**：下拉组合框

### 专用控件

- **DisplayControl**：显示屏控件，支持 OxyPlot 图表显示
- **OxyPlot 正弦波**：增强版 OxyPlot 正弦波控件，支持更多参数和交互
- **SimulatedSignalSourceControl（🔬 模拟信号源 (CPU)）**：CPU 渲染的模拟信号源控件，生成粗糙的实验信号数据
  - 支持心电图式滚动显示
  - 可调节峰位置、峰高度、峰宽度
  - 内置多层噪声模拟（主要噪声、次要噪声、高频抖动）
  - 支持基线漂移和偶发尖峰干扰
  - 实时参数调节和动画效果
- **GLSimulatedSignalControl（🔷 GPU模拟信号 (GL)）**：基于 OpenGL + Skia 的 GPU 加速控件。需要在具备真实 GPU/驱动的 Windows 环境下运行，否则会出现黑屏或空白。
- **GPUAcceleratedWaveformControl**：演示 GPUAcceleratedWaveformCanvas 的示例控件，可一次性加载多条波形验证 GPU 性能。
- **TcpSquareWaveSkiaControl（🧪 模拟TCP方波 (CPU)）**：CPU 渲染的 TCP 方波模拟控件，按 50ms 发包节奏和 2s 时间窗滚动，适合在无 OpenGL 的环境（如 WSL、远程桌面）中替代 GPU 控件。
- **TcpSquareWaveGlControl（🧪 模拟TCP方波 (GPU)）**：与 `GLSimulatedSignalControl` 类似的 GPU 版本，沿用 TCP 50ms 方波逻辑，依赖 GPUAcceleratedWaveformCanvas。
- **TcpRealtimeWaveformControl（🌐 实测TCP波形）**：新的实时采集控件，复用了 `DH2` 项目中的 TCP 包解码路径（`StreamDecoder` + `TimeSeriesParser`），直接连接指定 IP/端口并解析 0x7C 数据流。控件内置频道选择器与连接状态提示，可将真实采集软件输出的包体即时绘制成波形。
- **TcpRealtimeWaveformControl 细节**：
  - 绘制方式：Skia GPU，4 秒固定窗口从左到右绘制，窗口满后翻页继续绘制，时间轴不会累计偏差。
  - 纵轴缩放：首个窗口动态自适应并锁定 min/max，后续窗口沿用同一比例，避免跳变。
  - 标注信息：顶部叠加英文信息条（Latest/Min/Max，单位 mV）；首窗未锁定前覆盖半透明提示 “Calibrating...”。
  - 数据保真：不做降采样或限幅，按包内样本直接绘制（默认 1 ms/样本，如协议提供采样间隔可替换）。

> ⚠️ “SkiaSharp” 命名既用于 CPU 版（`SimulatedSignalSourceSKControl`、`TcpSquareWaveSkiaControl`），也用于 GPU 版（`GLSimulatedSignalControl`）。需要 GPU 加速时请确认控件名称前是否带有 “GL”/“GPU”。

### GPU/Skia 控件使用注意事项

1. **运行环境**：`GLSimulatedSignalControl` / `GPUAcceleratedWaveformControl` 使用 Avalonia.OpenGL，必须在 Windows 原生环境或具备可用 GPU 驱动的机器上运行。WSL、无显卡云主机或远程桌面软件可能导致控件内容空白。
2. **替代方案**：若只需验证波形逻辑或在 CI/无 GPU 环境下预览，请改用 `SimulatedSignalSourceSKControl` 或 `TcpSquareWaveSkiaControl` 等 CPU 渲染版本。
3. **调试建议**：GPU 控件若不显示，优先检查显卡驱动、Avalonia OpenGL 输出以及是否通过 `dotnet run` 在本机执行。确认 GPU 正常后再切换回 GPU 控件。
4. **性能差异**：CPU 版控件的刷新频率依赖 `DispatcherTimer`，在高 DPI/多控件情况下 CPU 占用较高；GPU 版控件在批量波形（>50 条）时更平滑，但需要额外的硬件与驱动支持。

## 🛠️ 开发指南

### 代码架构原则

本项目遵循以下架构原则：

- **MVVM 模式**：严格的视图-视图模型-模型分离
- **依赖注入**：服务层通过接口解耦
- **事件驱动**：组件间通过事件通信，避免紧耦合
- **单一职责**：每个类专注于单一功能
- **开闭原则**：对扩展开放，对修改封闭

### 🔧 扩展新的功能组合

功能组合是本系统的核心特性，允许多个控件协同工作实现复杂功能。

#### 步骤 1：定义功能类型

```csharp
// Models/ControlGroup.cs
public enum FunctionCombinationType
{
    None,
    SinWaveGenerator,
    DataProcessor,        // 新增：数据处理器
    ChartDisplay,         // 新增：图表显示器
    ControlPanel          // 新增：控制面板
}
```

### 🔐 算法文件与加密流程

项目支持两套算法扩展方式：

1. **脚本/JSON 算法（`.xtj`）**：
   - 放置在任意位置，通过控件属性中“算法文件”路径加载。
    - 如果需要分发给最终用户，可在顶部 Tab 页 “算法加密” 中点击“选择并加密 .xtj 文件”将 `.xtj` 明文加密为 `.xtjs`，内部使用 `Services/XtjCrypto`（AES-GCM）绑定到当前程序集。
   - 程序加载 `.xtjs` 时会自动解密，解密失败会在控制台输出原因。

2. **动态库算法（`.dll`）**：
   - 参考 `AlgorithmModule` 项目实现 `IAlgorithm` 接口，编译后放入 `AlgorithmModule/bin` 或自定义文件夹。
   - 在 UI 顶部“算法管理”按钮可查看可用 DLL 算法列表，运行时 `AlgorithmManager` 会自动发现。

### 🔩 FlowEditor 与 C++/CUDA 组件

- “流程图编辑器”页签使用 `FlowEditorControl`，支持拖拽节点并导出流程图。生成的流程可通过 `FlowGraphExporter` 与 `SpecificAlgorithmDllGenerator` 转成 C++ 代码。
- `FlowEditor/CppCompiler.cs` 会调用系统 MSVC 工具链，请确保执行 `run_with_env.bat` 或在 Visual Studio 开发者命令行内运行。
- GPU 验证相关文档见根目录 `GPU_ACCELERATION_VERIFICATION_GUIDE.md`，C++ 算法示例在 `download/` 和 `CppAlgorithms/`，方便在本地编译 DLL 并回注到算法模块。

#### 步骤 2：实现检测逻辑

```csharp
// Services/FunctionCombinationService.cs
public async Task<FunctionCombinationType> DetectFunctionTypeAsync(ControlGroup group, List<ControlInfo> controls)
{
    var controlTypes = controls.Select(c => c.Type).ToList();

    // 数据处理器：2个TextBox + 1个Button + 1个Label
    if (controlTypes.Count(t => t == "TextBox") == 2 &&
        controlTypes.Count(t => t == "Button") == 1 &&
        controlTypes.Count(t => t == "Label") == 1)
    {
        return FunctionCombinationType.DataProcessor;
    }

    // 现有的正弦波检测逻辑...
    return FunctionCombinationType.None;
}
```

#### 步骤 3：实现激活逻辑

```csharp
// Services/FunctionCombinationService.cs
public async Task ActivateFunctionAsync(ControlGroup group, List<ControlInfo> controls)
{
    switch (group.FunctionType)
    {
        case FunctionCombinationType.DataProcessor:
            await ActivateDataProcessorAsync(controls);
            break;
        // 其他功能类型...
    }
}

private async Task ActivateDataProcessorAsync(List<ControlInfo> controls)
{
    var textBoxes = controls.Where(c => c.Type == "TextBox").ToList();
    var button = controls.FirstOrDefault(c => c.Type == "Button");
    var label = controls.FirstOrDefault(c => c.Type == "Label");

    // 设置控件属性
    if (textBoxes.Count >= 2)
    {
        textBoxes[0].Name = "输入A";
        textBoxes[0].Content = "0";
        textBoxes[1].Name = "输入B";
        textBoxes[1].Content = "0";
    }

    if (button != null)
    {
        button.Name = "计算";
        button.Content = "执行计算";
    }

    if (label != null)
    {
        label.Name = "结果";
        label.Content = "等待计算...";
    }
}
```

#### 步骤 4：运行时绑定

```csharp
// Services/RuntimeFunctionManager.cs
public class DataProcessorBinding : IFunctionBinding
{
    private TextBox inputA, inputB;
    private Button calculateButton;
    private Label resultLabel;

    public async Task InitializeAsync(List<Control> controls)
    {
        inputA = controls.OfType<TextBox>().FirstOrDefault();
        inputB = controls.OfType<TextBox>().Skip(1).FirstOrDefault();
        calculateButton = controls.OfType<Button>().FirstOrDefault();
        resultLabel = controls.OfType<Label>().FirstOrDefault();

        if (calculateButton != null)
        {
            calculateButton.Click += OnCalculateClick;
        }
    }

    private void OnCalculateClick(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(inputA?.Text, out var a) &&
            double.TryParse(inputB?.Text, out var b))
        {
            var result = a + b; // 示例计算
            if (resultLabel != null)
                resultLabel.Content = $"结果: {result}";
        }
    }

    public void Cleanup()
    {
        if (calculateButton != null)
            calculateButton.Click -= OnCalculateClick;
    }
}
```

### 🎨 添加新控件类型

#### 步骤 1：创建控件视图

```csharp
// Views/CustomSlider.axaml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="NewAvalonia.Views.CustomSlider">
    <Border Background="LightGray" CornerRadius="5">
        <Slider Name="MainSlider"
                Minimum="0" Maximum="100"
                Value="{Binding Value}"
                Width="200" Height="30"/>
    </Border>
</UserControl>
```

#### 步骤 2：创建视图模型（如需要）

```csharp
// ViewModels/CustomSliderViewModel.cs
public class CustomSliderViewModel : ViewModelBase
{
    private double _value = 50;

    public double Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public event EventHandler<double> ValueChanged;

    partial void OnValueChanged(double value)
    {
        ValueChanged?.Invoke(this, value);
    }
}
```

#### 步骤 3：注册到控件库

```csharp
// MainWindow.axaml.cs - 在InitializeComponent()后添加
private void InitializeToolbox()
{
    // 现有控件...

    var btnAddSlider = new Button { Content = "滑块", Margin = new Thickness(5) };
    btnAddSlider.Click += (s, e) => ToolboxItem_Click("CustomSlider");
    toolboxPanel.Children.Add(btnAddSlider);
}
```

#### 步骤 4：添加创建逻辑

```csharp
// MainWindow.axaml.cs
private void ToolboxItem_Click(string controlType)
{
    switch (controlType)
    {
        case "CustomSlider":
            var sliderInfo = new ControlInfo
            {
                Id = Guid.NewGuid().ToString(),
                Type = "CustomSlider",
                Name = $"滑块{GetNextControlNumber("CustomSlider")}",
                X = 100, Y = 100,
                Width = GetDefaultWidth("CustomSlider"),
                Height = GetDefaultHeight("CustomSlider"),
                Content = GetDefaultContent("CustomSlider")
            };
            AddControlToCanvas(sliderInfo);
            break;
        // 其他控件类型...
    }
}

private int GetDefaultWidth(string controlType) => controlType switch
{
    "CustomSlider" => 200,
    _ => 100
};

private int GetDefaultHeight(string controlType) => controlType switch
{
    "CustomSlider" => 30,
    _ => 30
};

private string GetDefaultContent(string controlType) => controlType switch
{
    "CustomSlider" => "50",
    _ => ""
};
```

#### 步骤 5：运行时控件创建

```csharp
// MainWindow.axaml.cs
private Control CreateInteractiveControl(ControlInfo controlInfo)
{
    return controlInfo.Type switch
    {
        "CustomSlider" => new CustomSlider
        {
            DataContext = new CustomSliderViewModel { Value = double.Parse(controlInfo.Content ?? "50") }
        },
        // 其他控件类型...
        _ => new TextBlock { Text = "未知控件" }
    };
}
```

### 🔍 调试和故障排除

#### 常见问题诊断

**问题 1：连接点不显示**

```csharp
// 检查点：ConnectionPointView是否正确添加
// 位置：MainWindow.axaml.cs - AddControlToCanvas方法
private void AddControlToCanvas(ControlInfo controlInfo)
{
    // 确保这行代码存在
    var connectionPoint = new ConnectionPointView(controlInfo.Id);
    Canvas.SetLeft(connectionPoint, controlInfo.X + controlInfo.Width / 2 - 10);
    Canvas.SetTop(connectionPoint, controlInfo.Y + controlInfo.Height / 2 - 10);
    Canvas.SetZIndex(connectionPoint, 1000); // 确保在最上层
    designCanvas.Children.Add(connectionPoint);
}
```

**问题 2：功能组合不被识别**

```csharp
// 调试代码：在FunctionCombinationService.DetectFunctionTypeAsync中添加
public async Task<FunctionCombinationType> DetectFunctionTypeAsync(ControlGroup group, List<ControlInfo> controls)
{
    Console.WriteLine($"检测功能组合 - 控件数量: {controls.Count}");
    foreach (var control in controls)
    {
        Console.WriteLine($"  控件类型: {control.Type}, 名称: {control.Name}");
    }

    // 现有检测逻辑...
}
```

**问题 3：运行时绑定失败**

```csharp
// 调试代码：在RuntimeFunctionManager中添加详细日志
public async Task BindFunctionAsync(ControlGroup group, Canvas previewCanvas)
{
    Console.WriteLine($"开始绑定功能: {group.FunctionType}");

    var runtimeControls = new List<Control>();
    foreach (var controlId in group.ControlIds)
    {
        var control = FindRuntimeControl(previewCanvas, controlId);
        if (control != null)
        {
            runtimeControls.Add(control);
            Console.WriteLine($"找到运行时控件: {control.GetType().Name}");
        }
        else
        {
            Console.WriteLine($"未找到控件ID: {controlId}");
        }
    }
}
```

#### 性能优化建议

1. **连接线渲染优化**

```csharp
// 使用对象池避免频繁创建销毁
public class ConnectionLinePool
{
    private readonly Queue<ConnectionLineView> _pool = new();

    public ConnectionLineView Rent()
    {
        return _pool.Count > 0 ? _pool.Dequeue() : new ConnectionLineView();
    }

    public void Return(ConnectionLineView line)
    {
        line.Reset(); // 重置状态
        _pool.Enqueue(line);
    }
}
```

2. **事件处理优化**

```csharp
// 使用弱事件模式避免内存泄漏
public class WeakEventManager<T> where T : EventArgs
{
    private readonly List<WeakReference> _handlers = new();

    public void AddHandler(EventHandler<T> handler)
    {
        _handlers.Add(new WeakReference(handler));
    }

    public void RaiseEvent(object sender, T args)
    {
        var toRemove = new List<WeakReference>();
        foreach (var weakRef in _handlers)
        {
            if (weakRef.Target is EventHandler<T> handler)
            {
                handler(sender, args);
            }
            else
            {
                toRemove.Add(weakRef);
            }
        }

        // 清理失效的引用
        foreach (var item in toRemove)
        {
            _handlers.Remove(item);
        }
    }
}
```

### 📋 开发工作流

#### 功能开发流程

1. **需求分析** → 确定功能范围和技术方案
2. **接口设计** → 定义服务接口和数据模型
3. **单元测试** → 编写测试用例（推荐使用 xUnit）
4. **实现开发** → 按照 TDD 原则开发功能
5. **集成测试** → 验证与现有系统的兼容性
6. **代码审查** → 确保代码质量和规范性

#### Git 工作流建议

```bash
# 功能分支开发
git checkout -b feature/new-control-type
git add .
git commit -m "feat: 添加自定义滑块控件支持"

# 合并到主分支前的检查
git checkout main
git pull origin main
git checkout feature/new-control-type
git rebase main

# 提交PR前的最终检查
dotnet build --configuration Release
dotnet test
```

#### 代码规范检查清单

- [ ] 所有公共方法都有 XML 文档注释
- [ ] 异步方法正确使用 async/await
- [ ] 资源正确释放（实现 IDisposable）
- [ ] 事件处理器正确解绑
- [ ] 异常处理覆盖关键路径
- [ ] 单元测试覆盖率 > 80%

## 已知问题和限制

### 当前限制

- 连接关系不支持持久化保存
- 仅支持一种预设功能组合（正弦波生成器）
- 控件组的功能激活在运行时可能需要调试

### 待优化项

- 添加更多功能组合类型
- 实现连接关系的保存和加载
- 优化深色模式下的视觉效果
- 添加撤销/重做功能
- 改进模拟信号源的性能优化

## 技术细节

### 连接系统架构

- **模块化设计**：每个功能都封装在独立的服务类中
- **事件驱动**：使用事件通知机制保持组件间的松耦合
- **状态管理**：清晰的连接状态管理和视觉反馈

### 资源管理和内存安全

- **IDisposable 模式**：所有长期运行的控件都实现了正确的资源清理
- **定时器管理**：DispatcherTimer 在控件销毁时自动停止和解绑
- **事件解绑**：防止内存泄漏的完整事件处理器清理
- **弱引用模式**：在适当场景使用弱引用避免循环引用

### 性能考虑

- **增量更新**：只更新发生变化的连接线和边框
- **事件解绑**：运行时功能停用时正确清理事件绑定
- **内存管理**：使用字典缓存提高查找效率
- **随机数优化**：基于位置的确定性种子，避免频繁创建Random对象

### 信号处理优化

- **高效渲染**：OxyPlot 图表的增量更新机制
- **数据点管理**：合理的采样率和数据点数量控制
- **动画流畅性**：50ms 定时器间隔保证流畅的动画效果
- **内存回收**：及时清理过期的数据点和图形对象

## 开发环境

- **.NET 9.0**
- **Avalonia UI 11.x**
- **OxyPlot.Avalonia**
- **Windows 平台**

## 构建和运行

```bash
# 克隆项目
git clone [repository-url]

# 进入项目目录
cd NewAvalonia

# 还原依赖
dotnet restore

# 构建项目
dotnet build

# 运行项目
dotnet run
```

## 🧪 测试策略

### 单元测试

```csharp
// Tests/Services/ConnectionManagerTests.cs
[Fact]
public async Task CreateConnection_ValidControls_ShouldSucceed()
{
    // Arrange
    var manager = new ConnectionManager();
    var control1 = new ControlInfo { Id = "1", Type = "TextBox" };
    var control2 = new ControlInfo { Id = "2", Type = "Button" };

    // Act
    var result = await manager.CreateConnectionAsync(control1.Id, control2.Id);

    // Assert
    Assert.True(result);
    Assert.Contains(manager.GetConnections(), c =>
        c.SourceControlId == "1" && c.TargetControlId == "2");
}
```

### 集成测试

```csharp
// Tests/Integration/FunctionCombinationTests.cs
[Fact]
public async Task SinWaveGenerator_CompleteWorkflow_ShouldWork()
{
    // 测试完整的正弦波生成器工作流
    // 1. 创建控件
    // 2. 建立连接
    // 3. 检测功能组合
    // 4. 激活运行时功能
    // 5. 验证参数绑定
}
```

### UI 测试建议

- 使用 Avalonia.Headless 进行无头 UI 测试
- 测试拖拽操作的边界情况
- 验证连接点的交互逻辑
- 确保属性面板的数据绑定正确

## 📚 API 文档

### 核心接口

#### IConnectionManager

```csharp
public interface IConnectionManager
{
    Task<bool> CreateConnectionAsync(string sourceId, string targetId);
    Task<bool> RemoveConnectionAsync(string sourceId, string targetId);
    List<ControlConnection> GetConnections();
    List<ControlConnection> GetConnectionsForControl(string controlId);
    event EventHandler<ConnectionEventArgs> ConnectionCreated;
    event EventHandler<ConnectionEventArgs> ConnectionRemoved;
}
```

#### IFunctionCombinationService

```csharp
public interface IFunctionCombinationService
{
    Task<List<ControlGroup>> DetectGroupsAsync(List<ControlConnection> connections, List<ControlInfo> controls);
    Task<FunctionCombinationType> DetectFunctionTypeAsync(ControlGroup group, List<ControlInfo> controls);
    Task ActivateFunctionAsync(ControlGroup group, List<ControlInfo> controls);
    event EventHandler<FunctionActivatedEventArgs> FunctionActivated;
}
```

### 数据模型

#### ControlInfo

```csharp
public class ControlInfo
{
    public string Id { get; set; }           // 唯一标识符
    public string Type { get; set; }         // 控件类型
    public string Name { get; set; }         // 显示名称
    public int X { get; set; }               // X坐标
    public int Y { get; set; }               // Y坐标
    public int Width { get; set; }           // 宽度
    public int Height { get; set; }          // 高度
    public string Content { get; set; }      // 内容
    public string BackgroundColor { get; set; } // 背景色
    public string ForegroundColor { get; set; } // 前景色
}
```

## 🚀 部署指南

### 开发环境部署

```bash
# 开发模式运行
dotnet run --configuration Debug

# 启用热重载
dotnet watch run
```

### 生产环境部署

```bash
# 发布单文件应用
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# 发布框架依赖应用
dotnet publish -c Release -r win-x64 --self-contained false
```

### Docker 部署（可选）

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["NewAvalonia.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet build -c Release -o /app/build

FROM build AS publish
RUN dotnet publish -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "NewAvalonia.dll"]
```

## 🔮 路线图

### 短期目标 (1-2 个月)

- [ ] 实现数据持久化（JSON 格式保存/加载）
- [ ] 添加撤销/重做功能
- [ ] 支持控件复制/粘贴
- [ ] 实现网格对齐和吸附功能

### 中期目标 (3-6 个月)

- [ ] 插件系统架构
- [ ] 更多预设功能组合
- [ ] 主题系统和深色模式优化
- [ ] 性能监控和优化

### 长期目标 (6 个月+)

- [ ] 云端协作功能
- [ ] 实时数据源集成
- [ ] 移动端支持
- [ ] 企业级权限管理

## 🤝 贡献指南

### 提交规范

使用[Conventional Commits](https://www.conventionalcommits.org/)规范：

```
feat: 添加新功能
fix: 修复bug
docs: 文档更新
style: 代码格式调整
refactor: 代码重构
test: 测试相关
chore: 构建过程或辅助工具的变动
```

### Pull Request 流程

1. Fork 项目到个人仓库
2. 创建功能分支：`git checkout -b feature/amazing-feature`
3. 提交更改：`git commit -m 'feat: add amazing feature'`
4. 推送分支：`git push origin feature/amazing-feature`
5. 创建 Pull Request

### 代码审查清单

- [ ] 代码符合项目规范
- [ ] 包含适当的单元测试
- [ ] 文档已更新
- [ ] 无明显性能问题
- [ ] 向后兼容性检查

## 📞 支持与联系

### 技术支持

- **Issues**: [GitHub Issues](https://github.com/your-repo/issues)
- **讨论**: [GitHub Discussions](https://github.com/your-repo/discussions)
- **Wiki**: [项目 Wiki](https://github.com/your-repo/wiki)

### 开发团队

- **架构师**: [姓名] - 负责整体架构设计
- **前端开发**: [姓名] - 负责 UI/UX 实现
- **后端开发**: [姓名] - 负责服务层开发

### 许可证

本项目采用 [MIT License](LICENSE) 开源协议。

---

## 📖 附录

### 常用命令速查

```bash
# 项目管理
dotnet new avalonia -n ProjectName    # 创建新的Avalonia项目
dotnet add package PackageName        # 添加NuGet包
dotnet remove package PackageName     # 移除NuGet包

# 开发调试
dotnet build --verbosity detailed     # 详细构建信息
dotnet test --logger console         # 运行测试并显示详细输出
dotnet format                         # 代码格式化

# 性能分析
dotnet-trace collect -p [PID]         # 性能追踪
dotnet-counters monitor -p [PID]      # 性能计数器监控
```

### 相关资源

- [Avalonia UI 官方文档](https://docs.avaloniaui.net/)
- [OxyPlot 文档](https://oxyplot.readthedocs.io/)
- [.NET 9.0 新特性](https://docs.microsoft.com/en-us/dotnet/core/whats-new/dotnet-9)
- [MVVM 模式最佳实践](https://docs.microsoft.com/en-us/xamarin/xamarin-forms/enterprise-application-patterns/mvvm)

---

_🎯 本项目致力于为工业组态软件开发提供现代化、可扩展的基础框架。欢迎贡献代码和提出改进建议！_
