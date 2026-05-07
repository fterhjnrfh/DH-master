using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using NewAvalonia.FlowEditor;

namespace NewAvalonia.FlowEditor
{
    public partial class FlowEditorControl : UserControl
    {
        private FlowGraph _flowGraph = new FlowGraph();
        private Point _dragStartPoint;
        private Control? _draggedElement;
        private FlowNode? _selectedNode;
        private bool _isConnecting = false; // 连接模式
        private FlowNode? _connectionSourceNode; // 用于连接的源节点
        
        // UI元素引用
        private Canvas? _flowCanvas;
        private StackPanel? _propertyPanel;

        public FlowEditorControl()
        {
            InitializeComponent();
            SetupEventHandlers();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            
            // 获取UI元素引用
            _flowCanvas = this.FindControl<Canvas>("FlowCanvas");
            _propertyPanel = this.FindControl<StackPanel>("PropertyPanel");
            
            // 设置节点模板拖拽事件
            SetupNodeTemplateEvents();
            
            // 设置其他按钮事件
            SetupControlButtonEvents();
        }

        private void SetupNodeTemplateEvents()
        {
            // 为每个节点模板添加拖拽事件
            SetupDraggableNodeTemplate("StartNodeTemplate", NodeType.Start);
            SetupDraggableNodeTemplate("GaussianNodeTemplate", NodeType.Gaussian);
            SetupDraggableNodeTemplate("MedianNodeTemplate", NodeType.Median);
            SetupDraggableNodeTemplate("MovingAverageNodeTemplate", NodeType.MovingAverage);
            SetupDraggableNodeTemplate("SignalSmoothNodeTemplate", NodeType.SignalSmooth);
            SetupDraggableNodeTemplate("EndNodeTemplate", NodeType.End);
        }

        private void SetupDraggableNodeTemplate(string templateName, NodeType nodeType)
        {
            var template = this.FindControl<Border>(templateName);
            if (template != null)
            {
                var defaultColor = (template.Background as ISolidColorBrush)?.Color ?? Colors.Transparent;
                // 设置拖拽开始事件
                template.PointerPressed += (s, e) =>
                {
                    // 获取画布上的位置
                    var position = e.GetPosition(_flowCanvas);
                    // 创建节点并添加到画布
                    AddNodeToCanvas(nodeType, position);
                };
                
                // 设置视觉反馈
                template.PointerEntered += (s, e) => 
                {
                    template.Background = new SolidColorBrush(LightenColor(defaultColor, 0.12));
                };
                
                template.PointerExited += (s, e) => 
                {
                    template.Background = new SolidColorBrush(defaultColor);
                };
            }
        }

        private static Color LightenColor(Color color, double amount)
        {
            byte Adjust(byte component) => (byte)Math.Clamp(component + 255 * amount, 0, 255);
            return new Color(color.A,
                Adjust(color.R),
                Adjust(color.G),
                Adjust(color.B));
        }

        private void SetupEventHandlers()
        {
            if (_flowCanvas != null)
            {
                _flowCanvas.PointerPressed += OnCanvasPointerPressed;
            }
        }

        private void SetupControlButtonEvents()
        {
            // 导出按钮事件
            var exportButton = this.FindControl<Button>("ExportButton");
            if (exportButton != null)
                exportButton.Click += ExportButton_Click;

            // 清空按钮事件
            var clearButton = this.FindControl<Button>("ClearButton");
            if (clearButton != null)
                clearButton.Click += ClearButton_Click;
        }

        private void ClearButton_Click(object? sender, RoutedEventArgs e)
        {
            // 清空画布上的所有节点和连接
            _flowGraph.Nodes.Clear();
            _flowGraph.Connections.Clear();
            
            if (_flowCanvas != null)
            {
                _flowCanvas.Children.Clear();
            }
            
            if (_propertyPanel != null)
            {
                _propertyPanel.Children.Clear();
            }
            
            _selectedNode = null;
            _connectionSourceNode = null;
            _isConnecting = false;
        }

        private void AddNodeToCanvas(NodeType nodeType, Point position)
        {
            var node = new FlowNode(nodeType)
            {
                Position = position
            };
            
            _flowGraph.Nodes.Add(node);
            
            // 创建可视化节点
            var nodeControl = CreateNodeControl(node);
            nodeControl.Name = node.Id; // 设置名称以便后续查找
            
            if (_flowCanvas != null)
            {
                _flowCanvas.Children.Add(nodeControl);
                Canvas.SetLeft(nodeControl, position.X);
                Canvas.SetTop(nodeControl, position.Y);
                
                // 添加选择和拖拽事件
                nodeControl.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(_flowCanvas).Properties.IsLeftButtonPressed)
                    {
                        // 左键点击：选择节点或开始拖拽
                        SelectNode(node);
                        _draggedElement = nodeControl;
                        _dragStartPoint = e.GetPosition(_flowCanvas);
                        e.Handled = true;
                    }
                    else if (e.GetCurrentPoint(_flowCanvas).Properties.IsRightButtonPressed)
                    {
                        // 右键点击：进入连接模式
                        EnterConnectionMode(node);
                        e.Handled = true;
                    }
                };
                
                nodeControl.PointerMoved += (s, e) =>
                {
                    if (_draggedElement != null && e.GetCurrentPoint(_flowCanvas).Properties.IsLeftButtonPressed)
                    {
                        var currentPoint = e.GetPosition(_flowCanvas);
                        var offsetX = currentPoint.X - _dragStartPoint.X;
                        var offsetY = currentPoint.Y - _dragStartPoint.Y;
                        
                        var newLeft = Canvas.GetLeft(_draggedElement) + offsetX;
                        var newTop = Canvas.GetTop(_draggedElement) + offsetY;
                        
                        Canvas.SetLeft(_draggedElement, newLeft);
                        Canvas.SetTop(_draggedElement, newTop);
                        
                        // 更新节点位置
                        var nodeId = _draggedElement.Name;
                        var graphNode = _flowGraph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                        if (graphNode != null)
                        {
                            graphNode.Position = new Point(newLeft, newTop);
                        }
                        
                        // 更新与此节点相关的所有连接线
                        if (!string.IsNullOrEmpty(nodeId))
                        {
                            UpdateConnectedLines(nodeId);
                        }
                        
                        _dragStartPoint = currentPoint;
                    }
                };
                
                nodeControl.PointerReleased += (s, e) =>
                {
                    _draggedElement = null;
                };
            }
        }

        private void EnterConnectionMode(FlowNode node)
        {
            _isConnecting = true;
            _connectionSourceNode = node;
            
            // 高亮显示源节点
            HighlightNode(node, Colors.Yellow);
            
            System.Console.WriteLine($"进入连接模式，源节点: {node.Name}");
            System.Console.WriteLine("请点击目标节点以建立连接");
        }

        private void HighlightNode(FlowNode node, Color highlightColor)
        {
            if (_flowCanvas == null) return;
            
            var nodeControl = _flowCanvas.Children.OfType<Border>()
                .FirstOrDefault(c => c.Name == node.Id);
                
            if (nodeControl != null)
            {
                nodeControl.BorderBrush = new SolidColorBrush(highlightColor);
                nodeControl.BorderThickness = new Thickness(3);
            }
        }

        private Border CreateNodeControl(FlowNode node)
        {
            var border = new Border
            {
                Width = 100,
                Height = 50,
                Background = node.Type == NodeType.Start ? Brushes.LightGreen :
                           node.Type == NodeType.End ? Brushes.LightCoral :
                           Brushes.LightBlue,
                BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Child = new TextBlock
                {
                    Text = node.Name,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            };
            
            return border;
        }

        private void SelectNode(FlowNode node)
        {
            // 如果处于连接模式，则创建连接
            if (_isConnecting && _connectionSourceNode != null && _connectionSourceNode != node)
            {
                CreateConnection(_connectionSourceNode, node);
                return;
            }
            
            _selectedNode = node;
            
            // 高亮选中的节点（简单方式：改变边框）
            if (_flowCanvas != null)
            {
                foreach (Control child in _flowCanvas.Children)
                {
                    if (child is Border border)
                    {
                        border.BorderThickness = border.Name == node.Id ? 
                            new Thickness(3) : new Thickness(1);
                    }
                }
            }
            
            // 更新属性面板
            UpdatePropertyPanel(node);
        }

        private void CreateConnection(FlowNode fromNode, FlowNode toNode)
        {
            // 检查是否已经存在连接
            var existingConnection = _flowGraph.Connections.FirstOrDefault(c => 
                c.FromNodeId == fromNode.Id && c.ToNodeId == toNode.Id);
                
            if (existingConnection == null)
            {
                var connection = new FlowConnection(fromNode.Id, toNode.Id);
                _flowGraph.Connections.Add(connection);
                
                // 在画布上绘制连接线
                DrawConnectionLine(fromNode, toNode);
                
                System.Console.WriteLine($"已创建连接: {fromNode.Name} -> {toNode.Name}");
                
                // 退出连接模式
                _isConnecting = false;
                _connectionSourceNode = null;
                
                // 恢复源节点样式
                RestoreNodeStyle(fromNode);
            }
        }

        private void UpdateConnectedLines(string? nodeId)
        {
            if (_flowCanvas == null || string.IsNullOrEmpty(nodeId)) return;
            
            // 查找与此节点相关的所有连接
            var connectedNodes = _flowGraph.Connections
                .Where(c => c.FromNodeId == nodeId || c.ToNodeId == nodeId)
                .SelectMany(c => new[] { c.FromNodeId, c.ToNodeId })
                .Distinct()
                .Where(id => id != nodeId)
                .ToList();
                
            // 更新所有相关连接线
            foreach (var connectedNodeId in connectedNodes)
            {
                var fromNode = _flowGraph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                var toNode = _flowGraph.Nodes.FirstOrDefault(n => n.Id == connectedNodeId);
                
                if (fromNode != null && toNode != null)
                {
                    // 更新从当前节点到连接节点的连接线
                    UpdateSingleConnectionLine(fromNode, toNode);
                }
                
                // 反向连接也要更新
                var reverseFromNode = _flowGraph.Nodes.FirstOrDefault(n => n.Id == connectedNodeId);
                var reverseToNode = _flowGraph.Nodes.FirstOrDefault(n => n.Id == nodeId);
                
                if (reverseFromNode != null && reverseToNode != null)
                {
                    // 更新从连接节点到当前节点的连接线
                    UpdateSingleConnectionLine(reverseFromNode, reverseToNode);
                }
            }
        }
        
        private void UpdateSingleConnectionLine(FlowNode fromNode, FlowNode toNode)
        {
            if (_flowCanvas == null) return;
            
            // 查找连接线
            string lineName = $"conn_{fromNode.Id}_{toNode.Id}";
            var line = _flowCanvas.Children.OfType<Line>()
                .FirstOrDefault(l => l.Name == lineName);
                
            if (line != null)
            {
                // 更新连接线端点
                var fromControl = _flowCanvas.Children.OfType<Border>()
                    .FirstOrDefault(c => c.Name == fromNode.Id);
                var toControl = _flowCanvas.Children.OfType<Border>()
                    .FirstOrDefault(c => c.Name == toNode.Id);
                    
                if (fromControl != null && toControl != null)
                {
                    line.StartPoint = GetNodeCenter(fromControl);
                    line.EndPoint = GetNodeCenter(toControl);
                }
            }
        }

        private void DrawConnectionLine(FlowNode fromNode, FlowNode toNode)
        {
            if (_flowCanvas == null) return;
            
            // 创建连接线的唯一标识符
            string lineName = $"conn_{fromNode.Id}_{toNode.Id}";
            
            // 检查是否已经存在这条连接线
            var existingLine = _flowCanvas.Children.OfType<Line>()
                .FirstOrDefault(l => l.Name == lineName);
                
            if (existingLine == null)
            {
                // 创建新的连接线
                var line = new Line
                {
                    Name = lineName,
                    Stroke = new SolidColorBrush(Colors.Black),
                    StrokeThickness = 2,
                    ZIndex = -1 // 确保连接线在节点下方
                };
                
                _flowCanvas.Children.Add(line);
                existingLine = line;
            }
            
            // 更新连接线的位置
            var fromControl = _flowCanvas.Children.OfType<Border>()
                .FirstOrDefault(c => c.Name == fromNode.Id);
            var toControl = _flowCanvas.Children.OfType<Border>()
                .FirstOrDefault(c => c.Name == toNode.Id);
            
            if (fromControl != null && toControl != null)
            {
                existingLine.StartPoint = GetNodeCenter(fromControl);
                existingLine.EndPoint = GetNodeCenter(toControl);
            }
        }

        private void UpdatePropertyPanel(FlowNode? node)
        {
            _propertyPanel?.Children.Clear();
            
            if (node == null || _propertyPanel == null) return;
            
            // 添加节点类型
            _propertyPanel.Children.Add(new TextBlock 
            { 
                Text = $"节点类型: {node.Name}", 
                FontWeight = Avalonia.Media.FontWeight.Bold,
                Foreground = Brushes.White,
                Margin = new Thickness(0, 0, 0, 6)
            });
            
            // 添加参数编辑控件
            foreach (var param in node.Parameters)
            {
                var label = new TextBlock 
                { 
                    Text = param.Key,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 4, 0, 0)
                };
                var input = CreateParameterInput(param.Key, param.Value);
                
                _propertyPanel.Children.Add(label);
                _propertyPanel.Children.Add(input);
                
                // 参数值改变事件
                if (input is TextBox textBox)
                {
                    textBox.TextChanged += (s, e) =>
                    {
                        if (double.TryParse(textBox.Text, out double value))
                        {
                            node.Parameters[param.Key] = value;
                        }
                    };
                }
                else if (input is NumericUpDown numericUpDown)
                {
                    numericUpDown.ValueChanged += (s, e) =>
                    {
                        var newValue = e.NewValue ?? node.Parameters[param.Key];
                        node.Parameters[param.Key] = newValue;
                    };
                }
            }

            // 删除节点按钮
            var deleteButton = new Button
            {
                Content = "删除节点",
                Background = new SolidColorBrush(Color.Parse("#FF5555")),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.Parse("#CC4444")),
                Margin = new Thickness(0, 10, 0, 0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Padding = new Thickness(12, 6)
            };
            deleteButton.Click += (s, e) => DeleteNode(node);
            _propertyPanel.Children.Add(deleteButton);
        }

        private void DeleteNode(FlowNode node)
        {
            // 删除数据模型
            _flowGraph.Nodes.RemoveAll(n => n.Id == node.Id);
            _flowGraph.Connections.RemoveAll(c => c.FromNodeId == node.Id || c.ToNodeId == node.Id);

            // 删除画布中的节点和相关连接线
            if (_flowCanvas != null)
            {
                var toRemove = _flowCanvas.Children.OfType<Control>()
                    .Where(c => c.Name == node.Id || (c is Line line && !string.IsNullOrEmpty(line.Name) && line.Name.Contains(node.Id)))
                    .ToList();

                foreach (var ctrl in toRemove)
                {
                    _flowCanvas.Children.Remove(ctrl);
                }
            }

            _selectedNode = null;
            _propertyPanel?.Children.Clear();
        }

        private Control CreateParameterInput(string paramName, object paramValue)
        {
            var inputBackground = new SolidColorBrush(Color.Parse("#3A3A3A"));
            var borderBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));

            if (paramValue is int intValue)
            {
                var numericUpDown = new NumericUpDown
                {
                    Value = intValue,
                    Minimum = 1,
                    Maximum = 19,
                    Width = 100,
                    Foreground = Brushes.White,
                    Background = inputBackground,
                    BorderBrush = borderBrush
                };
                return numericUpDown;
            }
            else if (paramValue is double doubleValue)
            {
                var textBox = new TextBox
                {
                    Text = doubleValue.ToString(),
                    Width = 100,
                    Watermark = "数值",
                    Foreground = Brushes.White,
                    Background = inputBackground,
                    BorderBrush = borderBrush
                };
                return textBox;
            }
            
            return new TextBox 
            { 
                Text = paramValue.ToString(), 
                Width = 100,
                Foreground = Brushes.White,
                Background = inputBackground,
                BorderBrush = borderBrush
            };
        }

        private static Point GetNodeCenter(Border control)
        {
            double width = double.IsNaN(control.Width) ? control.Bounds.Width : control.Width;
            double height = double.IsNaN(control.Height) ? control.Bounds.Height : control.Height;
            double left = Canvas.GetLeft(control);
            double top = Canvas.GetTop(control);

            return new Point(left + width / 2, top + height / 2);
        }

        private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // 取消选择当前节点
            _selectedNode = null;
            if (_propertyPanel != null)
                _propertyPanel.Children.Clear();
                
            // 移除所有节点的高亮
            if (_flowCanvas != null)
            {
                foreach (Control child in _flowCanvas.Children)
                {
                    if (child is Border border)
                    {
                        border.BorderThickness = new Thickness(1);
                    }
                }
            }
            
            // 如果点击的是画布空白区域，退出连接模式
            if (_isConnecting)
            {
                _isConnecting = false;
                _connectionSourceNode = null;
                System.Console.WriteLine("退出连接模式");
            }
        }

        private void ExportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 导出流程图逻辑
            ExportFlowGraph();
        }

        private void RunButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // 运行流程图逻辑
            RunFlowGraph();
        }

        private async void ExportFlowGraph()
        {
            try
            {
                System.Console.WriteLine("开始导出流程图...");
                
                // 使用当前工作目录作为项目根目录
                var projectRoot = Environment.CurrentDirectory;
                
                // 获取导出路径 - 改回当前工作目录下的download文件夹
                var exportPath = System.IO.Path.Combine(projectRoot, "download");
                
                // 确保导出目录存在
                if (!System.IO.Directory.Exists(exportPath))
                {
                    System.IO.Directory.CreateDirectory(exportPath);
                    System.Console.WriteLine($"已创建导出目录: {exportPath}");
                }
                
                System.Console.WriteLine($"导出路径: {exportPath}");
                
                // 获取流程图执行顺序
                var executionOrder = _flowGraph.GetExecutionOrder();
                
                // 如果流程图为空，提示用户
                if (executionOrder.Count == 0)
                {
                    System.Console.WriteLine("流程图为空，请先创建节点和连接");
                    // 创建说明文件
                    var readmePath = System.IO.Path.Combine(exportPath, "README.txt");
                    System.IO.File.WriteAllText(readmePath, 
                        "导出失败：流程图为空。\n\n" + 
                        "要导出算法，请执行以下步骤：\n" + 
                        "1. 在流程图中添加开始节点\n" + 
                        "2. 添加至少一个算法节点（如高斯滤波、中值滤波等）\n" + 
                        "3. 添加结束节点\n" + 
                        "4. 使用连接线连接这些节点\n" + 
                        "5. 然后点击导出按钮");
                    await ShowMessageDialog("导出失败", "流程图为空，请先创建节点和连接");
                    return;
                }
                
                // 获取所有算法节点（排除开始和结束节点）
                var algorithmNodes = executionOrder.Where(n => 
                    n.Type != NodeType.Start && n.Type != NodeType.End).ToList();
                
                if (algorithmNodes.Count == 0)
                {
                    System.Console.WriteLine("流程图中没有算法节点，请添加算法节点后再导出");
                    // 创建说明文件
                    var readmePath = System.IO.Path.Combine(exportPath, "README.txt");
                    System.IO.File.WriteAllText(readmePath, 
                        "导出失败：流程图中没有算法节点。\n\n" + 
                        "要导出算法，请执行以下步骤：\n" + 
                        "1. 在流程图中添加至少一个算法节点（如高斯滤波、中值滤波等）\n" + 
                        "2. 使用连接线连接开始节点 -> 算法节点 -> 结束节点\n" + 
                        "3. 然后点击导出按钮");
                    await ShowMessageDialog("导出失败", "流程图中没有算法节点，请添加算法节点后再导出");
                    return;
                }
                
                // 确定导出方式：
                // - 如果只有一个算法节点，使用SpecificAlgorithmDllGenerator以保持向后兼容性
                // - 如果有多个算法节点，使用FlowGraphExporter生成包含所有算法的流程图DLL
                string dllName = GetCombinedAlgorithmName(algorithmNodes);
                
                System.Console.WriteLine($"正在生成项目文件: {dllName}");
                try
                {
                    if (algorithmNodes.Count == 1)
                    {
                        // 单个算法：使用SpecificAlgorithmDllGenerator保持向后兼容性
                        var firstAlgorithmNode = algorithmNodes[0];
                        System.Console.WriteLine($"正在生成特定算法项目文件: {dllName} (原中文名: {firstAlgorithmNode.Name})");
                        SpecificAlgorithmDllGenerator.SaveSpecificAlgorithmDll(
                            firstAlgorithmNode.Type, 
                            firstAlgorithmNode.Parameters, 
                            exportPath, 
                            dllName);
                    }
                    else
                    {
                        // 多个算法：使用FlowGraphExporter生成完整流程图
                        System.Console.WriteLine($"正在生成流程图项目文件: {dllName} (包含 {algorithmNodes.Count} 个算法)");
                        FlowGraphExporter.SaveAsCppProject(_flowGraph, exportPath, dllName);
                    }
                    
                    System.Console.WriteLine($"C++源代码已成功生成到: {exportPath}");
                }
                catch (Exception genEx)
                {
                    System.Console.WriteLine($"生成C++源代码时发生错误: {genEx.Message}");
                    System.Console.WriteLine($"堆栈跟踪: {genEx.StackTrace}");
                    await ShowMessageDialog("导出错误", 
                        $"生成C++源代码时发生错误: {genEx.Message}\n\n" +
                        "可能的原因:\n" +
                        "1. 参数值超出有效范围\n" +
                        "2. 磁盘空间不足\n" +
                        "3. 权限问题\n" +
                        "4. 路径包含特殊字符");
                    return;
                }
                
                // 构建完整的文件路径 - 现在使用流程图导出器生成的文件
                var cppSourcePath = System.IO.Path.Combine(exportPath, $"{dllName}.cpp");
                var outputDllPath = System.IO.Path.Combine(exportPath, $"{dllName}.dll");
                
                // 首先检测编译器是否可用
                bool hasCompiler = await CheckCompilerAvailability();
                if (!hasCompiler)
                {
                    System.Console.WriteLine("未检测到C++编译器，仅生成C++源代码文件");
                    await ShowMessageDialog("导出信息", 
                        $"C++源代码已成功生成!\n路径: {exportPath}\n\n" +
                        "未检测到C++编译器，无法生成DLL文件。\n" +
                        "要生成DLL文件，请安装Visual Studio或MinGW-w64后重试。");
                }
                else
                {
                    // 尝试编译为DLL
                    System.Console.WriteLine("正在编译C++代码为动态库...");
                    bool compileSuccess = await CppCompiler.CompileToDllAuto(cppSourcePath, outputDllPath);
                    
                    if (compileSuccess)
                    {
                        System.Console.WriteLine($"动态库已成功生成: {outputDllPath}");
                        System.Console.WriteLine("导出的DLL可以在其他项目中直接使用");
                        System.Console.WriteLine("");
                        System.Console.WriteLine("DLL使用说明:");
                        System.Console.WriteLine($"- 函数名称: {dllName.ToLower()}_process");
                        System.Console.WriteLine("- 函数签名: int process(double* input, int length, double* output)");
                        System.Console.WriteLine("- 参数说明:");
                        System.Console.WriteLine("  * input: 输入信号数组指针");
                        System.Console.WriteLine("  * length: 信号长度");
                        System.Console.WriteLine("  * output: 输出信号数组指针");
                        System.Console.WriteLine("- 返回值: 0表示成功，非0表示失败");
                        System.Console.WriteLine("");
                        System.Console.WriteLine("示例使用代码 (C++):");
                        System.Console.WriteLine("#include <iostream>");
                        System.Console.WriteLine($"#include \"{dllName}.h\"");
                        System.Console.WriteLine("int main() {");
                        System.Console.WriteLine("    double input[100] = { /* 填充数据 */ };");
                        System.Console.WriteLine("    double output[100];");
                        System.Console.WriteLine($"    int result = {dllName.ToLower()}_process(input, 100, output);");
                        System.Console.WriteLine("    if (result == 0) {");
                        System.Console.WriteLine("        std::cout << \"处理成功\" << std::endl;");
                        System.Console.WriteLine("    }");
                        System.Console.WriteLine("    return 0;");
                        System.Console.WriteLine("}");
                        
                        // 根据流程图中算法的数量显示不同的提示信息
                        var algorithmNodesForMessage = executionOrder.Where(n => 
                            n.Type != NodeType.Start && n.Type != NodeType.End).ToList();
                        string algorithmCountInfo = algorithmNodesForMessage.Count > 1 ? 
                            $"包含 {algorithmNodesForMessage.Count} 个算法的流程图" : 
                            "单个算法";
                            
                        await ShowMessageDialog("导出成功", 
                            $"DLL和C++源代码已成功生成!\n{algorithmCountInfo}已导出\nDLL路径: {outputDllPath}\n源码路径: {exportPath}");
                    }
                    else
                    {
                        System.Console.WriteLine("编译失败，但C++源代码已生成，您可以在以下目录中手动编译：");
                        System.Console.WriteLine(exportPath);
                        System.Console.WriteLine("");
                        System.Console.WriteLine("手动编译方法:");
                        System.Console.WriteLine("1. 安装Visual Studio或MinGW-w64");
                        System.Console.WriteLine("2. 使用CMake或直接编译生成DLL");
                        System.Console.WriteLine($"3. 编译命令示例: g++ -shared -fPIC {dllName}.cpp -o {dllName}.dll");
                        
                        await ShowMessageDialog("导出信息", 
                            "C++源码已生成，但DLL编译失败。\n请检查是否已安装C++编译器。\n源码路径: " + exportPath);
                    }
                }
                
                // 显示生成的文件列表
                System.Console.WriteLine("");
                System.Console.WriteLine("生成的文件:");
                if (System.IO.Directory.Exists(exportPath))
                {
                    foreach (var file in System.IO.Directory.GetFiles(exportPath))
                    {
                        System.Console.WriteLine($"  {System.IO.Path.GetFileName(file)}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"导出失败: {ex.Message}");
                await ShowMessageDialog("导出失败", $"导出过程中出现异常: {ex.Message}");
            }
        }

        private string GenerateCppCode(List<FlowNode> executionOrder)
        {
            var code = new System.Text.StringBuilder();
            
            // 头文件
            code.AppendLine("#include <vector>");
            code.AppendLine("#include <cmath>");
            code.AppendLine("#include <algorithm>");
            code.AppendLine();
            code.AppendLine("extern \"C\" {");
            code.AppendLine("    __declspec(dllexport) int process_signal(double* input, int length, double* output) {");
            code.AppendLine("        // 复制输入到输出作为初始值");
            code.AppendLine("        for (int i = 0; i < length; i++) {");
            code.AppendLine("            output[i] = input[i];");
            code.AppendLine("        }");
            code.AppendLine();
            
            // 按顺序生成每个节点的处理代码
            foreach (var node in executionOrder.Where(n => n.Type != NodeType.Start && n.Type != NodeType.End))
            {
                switch (node.Type)
                {
                    case NodeType.Gaussian:
                        double sigma = node.Parameters.ContainsKey("Sigma") ? 
                                      Convert.ToDouble(node.Parameters["Sigma"]) : 1.0;
                        int kernelSize = node.Parameters.ContainsKey("KernelSize") ? 
                                       Convert.ToInt32(node.Parameters["KernelSize"]) : 5;
                        
                        code.AppendLine($"        // 高斯滤波 - Sigma: {sigma}, KernelSize: {kernelSize}");
                        // 这里应该生成实际的高斯滤波代码
                        break;
                        
                    case NodeType.Median:
                        int windowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                       Convert.ToInt32(node.Parameters["WindowSize"]) : 3;
                        
                        code.AppendLine($"        // 中值滤波 - WindowSize: {windowSize}");
                        // 这里应该生成实际的中值滤波代码
                        break;
                        
                    case NodeType.MovingAverage:
                        int maWindowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                         Convert.ToInt32(node.Parameters["WindowSize"]) : 5;
                        
                        code.AppendLine($"        // 移动平均滤波 - WindowSize: {maWindowSize}");
                        // 这里应该生成实际的移动平均滤波代码
                        break;
                        
                    case NodeType.SignalSmooth:
                        int smoothWindowSize = node.Parameters.ContainsKey("WindowSize") ? 
                                             Convert.ToInt32(node.Parameters["WindowSize"]) : 5;
                        double smoothness = node.Parameters.ContainsKey("Smoothness") ? 
                                          Convert.ToDouble(node.Parameters["Smoothness"]) : 0.5;
                        
                        code.AppendLine($"        // 信号平滑 - WindowSize: {smoothWindowSize}, Smoothness: {smoothness}");
                        // 这里应该生成实际的信号平滑代码
                        break;
                }
            }
            
            code.AppendLine();
            code.AppendLine("        return 0; // 成功");
            code.AppendLine("    }");
            code.AppendLine("}");
            
            return code.ToString();
        }

        private void RestoreNodeStyle(FlowNode node)
        {
            if (_flowCanvas == null) return;
            
            var nodeControl = _flowCanvas.Children.OfType<Border>()
                .FirstOrDefault(c => c.Name == node.Id);
                
            if (nodeControl != null)
            {
                nodeControl.BorderBrush = Brushes.Black;
                nodeControl.BorderThickness = new Thickness(1);
            }
        }
        
        // 根据节点类型获取英文算法名称
        private string GetAlgorithmEnglishName(NodeType nodeType)
        {
            return nodeType switch
            {
                NodeType.Gaussian => "GaussianFilter",
                NodeType.Median => "MedianFilter", 
                NodeType.MovingAverage => "MovingAverageFilter",
                NodeType.SignalSmooth => "SignalSmooth",
                _ => "Algorithm"
            };
        }
        
        // 根据算法节点列表生成组合名称
        private string GetCombinedAlgorithmName(List<FlowNode> algorithmNodes)
        {
            if (algorithmNodes == null || algorithmNodes.Count == 0)
                return "EmptyGraph";
            
            if (algorithmNodes.Count == 1)
            {
                // 如果只有一个算法节点，直接使用该算法的名称
                return GetAlgorithmEnglishName(algorithmNodes[0].Type);
            }
            else
            {
                // 如果有多个算法节点，使用所有算法名称的组合
                var algorithmNames = algorithmNodes.Select(node => GetAlgorithmEnglishName(node.Type)).ToList();
                // 使用下划线连接所有算法名称，并限制总长度以避免文件名过长
                string combinedName = string.Join("_", algorithmNames);
                
                // 如果名称过长，进行截断处理
                if (combinedName.Length > 50)
                {
                    // 取每个算法名称的前几个字符
                    var shortNames = algorithmNodes.Select(node => 
                    {
                        var fullName = GetAlgorithmEnglishName(node.Type);
                        return fullName.Length > 8 ? fullName.Substring(0, 8) : fullName;
                    });
                    combinedName = string.Join("_", shortNames);
                }
                
                // 确保文件名不包含无效字符
                foreach (char c in System.IO.Path.GetInvalidFileNameChars())
                {
                    combinedName = combinedName.Replace(c, '_');
                }
                
                return combinedName;
            }
        }
        
        /// <summary>
        /// 显示消息对话框
        /// </summary>
        /// <param name="title">对话框标题</param>
        /// <param name="message">消息内容</param>
        private async Task ShowMessageDialog(string title, string message)
        {
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow != null)
            {
                var okButton = new Button
                {
                    Content = "确定",
                    Width = 80,
                    Height = 30,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var dialogWindow = new Window
                {
                    Title = title,
                    Width = 350,
                    Height = 150,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Spacing = 5,
                        Margin = new Thickness(10),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = message,
                                TextWrapping = TextWrapping.Wrap
                            },
                            okButton
                        }
                    }
                };

                okButton.Click += (_, _) => dialogWindow.Close();
                await dialogWindow.ShowDialog(parentWindow);
            }
            else
            {
                System.Console.WriteLine($"{title}: {message}");
            }
        }
        
        /// <summary>
        /// 检测编译器可用性
        /// </summary>
        /// <returns>是否有可用的编译器</returns>
        private async Task<bool> CheckCompilerAvailability()
        {
            // 检查MSVC编译器
            bool hasMsvc = await CppCompiler.IsCompilerAvailable("cl");
            
            // 检查GCC编译器
            bool hasGcc = await CppCompiler.IsCompilerAvailable("g++");
            
            System.Console.WriteLine($"编译器检测结果 - MSVC: {(hasMsvc ? "可用" : "不可用")}, GCC: {(hasGcc ? "可用" : "不可用")}");
            
            return hasMsvc || hasGcc;
        }
        
        /// <summary>
        /// 显示带选择的消息对话框
        /// </summary>
        /// <param name="title">对话框标题</param>
        /// <param name="message">消息内容</param>
        /// <param name="primaryButtonText">主按钮文本</param>
        /// <param name="secondaryButtonText">次按钮文本</param>
        /// <returns>是否选择了主按钮</returns>
        private async Task<bool> ShowMessageDialogWithChoice(string title, string message, string primaryButtonText, string secondaryButtonText)
        {
            var parentWindow = TopLevel.GetTopLevel(this) as Window;
            if (parentWindow != null)
            {
                bool? result = null;

                var primaryButton = new Button
                {
                    Content = primaryButtonText,
                    Width = 80,
                    Height = 30
                };

                var secondaryButton = new Button
                {
                    Content = secondaryButtonText,
                    Width = 80,
                    Height = 30
                };

                var msgBox = new Window
                {
                    Title = title,
                    Width = 400,
                    Height = 200,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Content = new StackPanel
                    {
                        Spacing = 10,
                        Margin = new Thickness(10),
                        Children =
                        {
                            new TextBlock
                            {
                                Text = message,
                                TextWrapping = TextWrapping.Wrap
                            },
                            new StackPanel
                            {
                                Orientation = Avalonia.Layout.Orientation.Horizontal,
                                Spacing = 10,
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                                Children =
                                {
                                    primaryButton,
                                    secondaryButton
                                }
                            }
                        }
                    }
                };

                primaryButton.Click += (_, _) =>
                {
                    result = true;
                    msgBox.Close();
                };

                secondaryButton.Click += (_, _) =>
                {
                    result = false;
                    msgBox.Close();
                };

                msgBox.Closing += (_, _) =>
                {
                    if (!result.HasValue)
                    {
                        result = false;
                    }
                };

                await msgBox.ShowDialog(parentWindow);
                return result ?? false;
            }
            else
            {
                System.Console.WriteLine($"{title}: {message}");
                return true;
            }
        }
        
        private void RunFlowGraph()
        {
            // 运行流程图
            var executionOrder = _flowGraph.GetExecutionOrder();
            System.Console.WriteLine($"运行流程图，共 {executionOrder.Count} 个节点");
            
            // 显示执行顺序
            System.Console.WriteLine("执行顺序:");
            foreach (var node in executionOrder)
            {
                System.Console.WriteLine($"  -> {node.Name}");
            }
            
            // 模拟执行过程
            System.Console.WriteLine("\n开始执行算法流程...");
            for (int i = 0; i < executionOrder.Count; i++)
            {
                var node = executionOrder[i];
                System.Console.WriteLine($"执行第 {i+1} 步: {node.Name}");
                
                // 模拟算法执行时间
                System.Threading.Thread.Sleep(500);
                
                // 显示节点参数
                if (node.Parameters.Count > 0)
                {
                    System.Console.WriteLine($"  参数设置:");
                    foreach (var param in node.Parameters)
                    {
                        System.Console.WriteLine($"    {param.Key}: {param.Value}");
                    }
                }
            }
            
            System.Console.WriteLine("算法流程执行完成！");
            
            // 在UI上显示执行结果
            if (_flowCanvas != null)
            {
                // 高亮显示所有节点表示执行完成
                foreach (var node in executionOrder)
                {
                    var nodeControl = _flowCanvas.Children.OfType<Border>()
                        .FirstOrDefault(c => c.Name == node.Id);
                    if (nodeControl != null)
                    {
                        nodeControl.Background = new SolidColorBrush(Colors.LightGreen);
                    }
                }
                
                // 2秒后恢复原始样式
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(2000);
                    foreach (var node in executionOrder)
                    {
                        var nodeControl = _flowCanvas.Children.OfType<Border>()
                            .FirstOrDefault(c => c.Name == node.Id);
                        if (nodeControl != null)
                        {
                            nodeControl.Background = node.Type == NodeType.Start ? Brushes.LightGreen :
                                                     node.Type == NodeType.End ? Brushes.LightCoral :
                                                     Brushes.LightBlue;
                        }
                    }
                }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }
    }
}
