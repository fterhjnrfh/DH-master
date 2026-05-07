using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using System.Collections.Generic;
using System;
using System.Threading.Tasks;
using NewAvalonia.Models;
using NewAvalonia.Views;
using NewAvalonia.ViewModels;
using System.Linq;
using Avalonia.Interactivity;
using System.Runtime.InteropServices;

namespace NewAvalonia.Views;

public partial class DesignerWorkbenchView : UserControl
{
    private Control? selectedElement = null;
    private Point elementStartPoint;
    private bool isDragging = false;
    private List<ControlInfo> controlList = new List<ControlInfo>();

    // 添加连接系统相关变量
    private NewAvalonia.Services.ConnectionOperationManager? connectionOperationManager;

    // 添加用于拖动悬浮窗口的变量
    private Point windowStartPoint;
    private bool isDraggingWindow = false;
    private Border? draggingWindow = null;

    // 添加用于折叠面板的变量
    private bool isToolboxCollapsed = false;
    private bool isPropertyCollapsed = false;
    private double toolboxExpandedHeight;
    private double propertyExpandedHeight;

    public DesignerWorkbenchView()
    {
        InitializeComponent();
        AttachEvents();
    }

    private void AttachEvents()
    {
        // 控件库按钮事件
        btnAddButton.Click += (s, e) => ToolboxItem_Click("Button");
        btnAddLabel.Click += (s, e) => ToolboxItem_Click("Label");
        btnAddTextBox.Click += (s, e) => ToolboxItem_Click("TextBox");
        btnAddCheckBox.Click += (s, e) => ToolboxItem_Click("CheckBox");
        btnAddComboBox.Click += (s, e) => ToolboxItem_Click("ComboBox");
        btnAddNewOxyPlotSinWave.Click += (s, e) => ToolboxItem_Click("NewOxyPlotSinWaveControl");
        btnAddDisplayControl.Click += (s, e) => ToolboxItem_Click("DisplayControl");
        btnAddDisplayControl2.Click += (s, e) => ToolboxItem_Click("DisplayControl2");
        btnAddSimulatedSignal.Click += (s, e) => ToolboxItem_Click("SimulatedSignalSourceControl");
        btnAddGLSimulatedSignal.Click += (s, e) => ToolboxItem_Click("GLSimulatedSignalControl");
        btnAddNewOpenGLSinWave.Click += (s, e) => ToolboxItem_Click("NewOpenGLSinWaveControl");
        btnAddSkiaTcpSignalNew.Click += (s, e) => ToolboxItem_Click("TcpSquareWaveSkiaControl");
        btnAddTcpSquareWaveGpu.Click += (s, e) => ToolboxItem_Click("TcpSquareWaveGlControl");
        btnAddTcpRealtimeWaveform.Click += (s, e) => ToolboxItem_Click("TcpRealtimeWaveformControl");
        btnEncryptXtj.Click += EncryptAlgorithms_Click;


        // 设计画布事件
        designCanvas.PointerPressed += Canvas_PointerPressed;
        designCanvas.PointerMoved += Canvas_PointerMoved;

        // 初始化工作逻辑服务
        var workingLogicService = new NewAvalonia.Services.WorkingLogicService();
        
        // 初始化连接操作管理器
        connectionOperationManager = new NewAvalonia.Services.ConnectionOperationManager(designCanvas, workingLogicService);
        connectionOperationManager.ControlsUpdated += OnControlsUpdated;

        // 应用按钮事件
        btnApply.Click += ApplyProperties_Click;
        btnDelete.Click += DeleteControl_Click;
        
        // 激活控件组按钮事件
        btnActivateGroup.Click += ActivateGroup_Click;
        btnDeactivateGroup.Click += DeactivateGroup_Click;

        // 运行按钮事件
        btnRunPreview.Click += RunPreview_Click;

        // 为悬浮窗口添加拖动事件
        toolboxHeader.PointerPressed += Window_PointerPressed;
        toolboxHeader.PointerMoved += Window_PointerMoved;
        toolboxHeader.PointerReleased += Window_PointerReleased;

        propertyHeader.PointerPressed += Window_PointerPressed;
        propertyHeader.PointerMoved += Window_PointerMoved;
        propertyHeader.PointerReleased += Window_PointerReleased;

        // 为折叠按钮添加事件
        toolboxToggle.Click += ToolboxToggle_Click;
        propertyToggle.Click += PropertyToggle_Click;

        // 图像处理器扩展属性事件
        // 注意：这些按钮在SimulatedSignalSourceControl中，需要在控件加载后绑定
        // btnBrowseAlgorithm.Click += BrowseAlgorithm_Click;
        // btnReloadAlgorithm.Click += ReloadAlgorithm_Click;

        // 保存初始高度
        toolboxExpandedHeight = toolboxBorder.Height;
        propertyExpandedHeight = propertyBorder.Height;

    }

    // 处理控件库折叠/展开
    private void ToolboxToggle_Click(object? sender, RoutedEventArgs e)
    {
        isToolboxCollapsed = !isToolboxCollapsed;

        if (isToolboxCollapsed)
        {
            // 保存当前高度并折叠
            toolboxExpandedHeight = toolboxBorder.Height;
            toolboxBorder.Height = 30; // 只显示标题栏
            toolboxToggle.Content = "+"; // 更改按钮文本为加号
            toolboxContent.IsVisible = false; // 隐藏内容
        }
        else
        {
            // 恢复高度
            toolboxBorder.Height = toolboxExpandedHeight;
            toolboxToggle.Content = "-"; // 更改按钮文本为减号
            toolboxContent.IsVisible = true; // 显示内容
        }
    }

    // 处理属性面板折叠/展开
    private void PropertyToggle_Click(object? sender, RoutedEventArgs e)
    {
        isPropertyCollapsed = !isPropertyCollapsed;

        if (isPropertyCollapsed)
        {
            // 保存当前高度并折叠
            propertyExpandedHeight = propertyBorder.Height;
            propertyBorder.Height = 30; // 只显示标题栏
            propertyToggle.Content = "+"; // 更改按钮文本为加号
            propertyContent.IsVisible = false; // 隐藏内容
        }
        else
        {
            // 恢复高度
            propertyBorder.Height = propertyExpandedHeight;
            propertyToggle.Content = "-"; // 更改按钮文本为减号
            propertyContent.IsVisible = true; // 显示内容
        }
    }

    // 处理悬浮窗口标题栏按下事件
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border header)
        {
            // 获取父级Border容器
            var parentWindow = header.Parent?.Parent as Border;
            if (parentWindow != null)
            {
                windowStartPoint = e.GetPosition(mainCanvas);
                isDraggingWindow = true;
                draggingWindow = parentWindow;
                e.Handled = true;
            }
        }
    }

    // 处理悬浮窗口拖动事件
    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (isDraggingWindow && draggingWindow != null)
        {
            Point mousePoint = e.GetPosition(mainCanvas);

            double newLeft = Canvas.GetLeft(draggingWindow) + (mousePoint.X - windowStartPoint.X);
            double newTop = Canvas.GetTop(draggingWindow) + (mousePoint.Y - windowStartPoint.Y);

            // 确保窗口不会被拖出设计画布边界
            newLeft = Math.Max(0, Math.Min(newLeft, mainCanvas.Bounds.Width - draggingWindow.Bounds.Width));
            newTop = Math.Max(0, Math.Min(newTop, mainCanvas.Bounds.Height - draggingWindow.Bounds.Height));

            Canvas.SetLeft(draggingWindow, newLeft);
            Canvas.SetTop(draggingWindow, newTop);

            windowStartPoint = mousePoint;
        }
    }

    // 处理悬浮窗口释放事件
    private void Window_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        isDraggingWindow = false;
        draggingWindow = null;
    }

    // 处理控件库中的控件点击
    private void ToolboxItem_Click(string objectType)
    {
        // 创建控件信息
        ControlInfo controlInfo = new ControlInfo
        {
            Type = objectType,
            Name = objectType + "_" + (controlList.Count + 1),
            Width = GetDefaultWidth(objectType),
            Height = GetDefaultHeight(objectType),
            Content = GetDefaultContent(objectType)
        };

        // 添加到控件列表
        controlList.Add(controlInfo);

        // 创建运行控件（无交互功能）
        Control? previewElement = CreatePreviewControl(controlInfo);
        if (previewElement != null)
        {
            // 设置控件位置为设计画布中心
            double centerX = designCanvas.Bounds.Width / 2 - controlInfo.Width / 2;
            double centerY = designCanvas.Bounds.Height / 2 - controlInfo.Height / 2;

            // 如果设计画布还没有渲染大小，使用默认值
            if (designCanvas.Bounds.Width == 0 || designCanvas.Bounds.Height == 0)
            {
                centerX = 100;
                centerY = 100;
            }

            Canvas.SetLeft(previewElement, centerX);
            Canvas.SetTop(previewElement, centerY);

            // 更新控件信息中的位置
            controlInfo.Left = centerX;
            controlInfo.Top = centerY;

            // 添加控件到设计画布
            designCanvas.Children.Add(previewElement);

            // 为控件添加鼠标事件
            previewElement.PointerPressed += Element_PointerPressed;
            previewElement.PointerMoved += Element_PointerMoved;
            previewElement.PointerReleased += Element_PointerReleased;
        }
    }

    // 获取控件默认宽度
    private double GetDefaultWidth(string type)
    {
        switch (type)
        {
            case "TextBox":
            case "CheckBox":
            case "ComboBox":
                return 100;
            case "DisplayControl":
            case "DisplayControl2":
                return 400;
            case "SimulatedSignalSourceControl":
            case "TcpSquareWaveSkiaControl":
            case "TcpSquareWaveGlControl":
            case "TcpRealtimeWaveformControl":
                return 880; // 仅用于初始定位/居中，实际运行时宽度为Auto (double.NaN)
            case "GLSimulatedSignalControl":
                return 880; // OpenGL模拟信号控件的宽度

            case "NewOxyPlotSinWaveControl":
                return 750;
            case "NewOpenGLSinWaveControl":
                return 600; // 与OxyPlot正弦波类似的宽度
            case "Button":
            case "Label":
            default:
                return 80;
        }
    }

    // 获取控件默认高度
    private double GetDefaultHeight(string type)
    {
        switch (type)
        {
            case "DisplayControl":
            case "DisplayControl2":
                return 200;
            case "SimulatedSignalSourceControl":
            case "TcpSquareWaveSkiaControl":
            case "TcpSquareWaveGlControl":
            case "TcpRealtimeWaveformControl":
                return 420; // 严格保持与Skia控件统一的占位高度
            case "GLSimulatedSignalControl":
                return 480; // OpenGL模拟信号控件的高度

            case "NewOxyPlotSinWaveControl":
                return 500;
            case "NewOpenGLSinWaveControl":
                return 400; // 与OxyPlot正弦波类似的高度
            case "CheckBox":
                return 20;
            case "Button":
            case "Label":
            case "TextBox":
            case "ComboBox":
            default:
                return 30;
        }
    }

    // 获取控件默认内容
    private string GetDefaultContent(string type)
    {
        return type switch
        {
            "Button" => "Button",
            "Label" => "Label",
            "TextBox" => "TextBox",
            "CheckBox" => "CheckBox",
            "ComboBox" => "",
            "DisplayControl" => "DisplayControl",
            "DisplayControl2" => "DisplayControl2",
            "SimulatedSignalSourceControl" => "SimulatedSignalSourceControl",
            "GLSimulatedSignalControl" => "GLSimulatedSignalControl",
            "TcpSquareWaveSkiaControl" => "TcpSquareWaveSkiaControl",
            "TcpSquareWaveGlControl" => "TcpSquareWaveGlControl",
            "TcpRealtimeWaveformControl" => "TcpRealtimeWaveformControl",
            "NewOpenGLSinWaveControl" => "🎨 SkiaSharp正弦波",
            _ => type,
        };
    }

    // 创建运行控件（无交互功能）
    private Control? CreatePreviewControl(ControlInfo controlInfo)
    {
        Control? element = null;

        // 确定文字颜色画刷
        IBrush foregroundBrush = controlInfo.ForegroundColor == "White" ? Brushes.White : Brushes.Black;

        switch (controlInfo.Type)
        {
            case "Button":
                element = new Label()
                {
                    Content = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.LightGray,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = foregroundBrush // 应用文字颜色
                };
                break;
            case "Label":
                element = new Label()
                {
                    Content = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.LightBlue,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = foregroundBrush // 应用文字颜色
                };
                break;
            case "TextBox":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.White,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1)
                };

                // 在Border中添加TextBlock来显示文本
                TextBlock textBlock = new TextBlock()
                {
                    Text = controlInfo.Content,
                    Margin = new Thickness(2),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = foregroundBrush // 应用文字颜色
                };
                ((Border)element).Child = textBlock;
                break;
            case "CheckBox":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.LightGray
                };

                // 在Border中添加CheckBox样式的运行
                StackPanel panel = new StackPanel() { Orientation = Avalonia.Layout.Orientation.Horizontal };
                Border checkBoxBorder = new Border()
                {
                    Width = 16,
                    Height = 16,
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 5, 0)
                };
                TextBlock checkBoxText = new TextBlock()
                {
                    Text = controlInfo.Content,
                    Foreground = foregroundBrush // 应用文字颜色
                };
                panel.Children.Add(checkBoxBorder);
                panel.Children.Add(checkBoxText);
                ((Border)element).Child = panel;
                break;
            case "ComboBox":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.White,
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(1)
                };

                // 在Border中添加ComboBox样式的运行
                Grid comboGrid = new Grid();
                comboGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
                comboGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(20) });

                TextBlock comboText = new TextBlock()
                {
                    Text = "ComboBox",
                    Margin = new Thickness(2),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = foregroundBrush // 应用文字颜色
                };
                Grid.SetColumn(comboText, 0);

                var arrow = new Avalonia.Controls.Shapes.Path()
                {
                    Data = Geometry.Parse("M 0 0 L 4 4 L 8 0 Z"),
                    Fill = Brushes.Black,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                };
                Grid.SetColumn(arrow, 1);

                comboGrid.Children.Add(comboText);
                comboGrid.Children.Add(arrow);
                ((Border)element).Child = comboGrid;
                break;
            case "NewOxyPlotSinWaveControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkSlateBlue,
                    BorderBrush = Brushes.LightBlue,
                    BorderThickness = new Thickness(2)
                };
                TextBlock previewTextNewOxy = new TextBlock()
                {
                    Text = "newOxyPlot 正弦波",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTextNewOxy;
                break;
            case "DisplayControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkSlateGray,
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(1)
                };
                TextBlock previewTextDisplay = new TextBlock()
                {
                    Text = "显示屏",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White
                };
                ((Border)element).Child = previewTextDisplay;
                break;
            case "DisplayControl2":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkSlateBlue, // 区别于原来的DarkSlateGray
                    BorderBrush = Brushes.LightBlue,
                    BorderThickness = new Thickness(1)
                };
                TextBlock previewTextDisplay2 = new TextBlock()
                {
                    Text = "显示屏2",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White
                };
                ((Border)element).Child = previewTextDisplay2;
                break;
            case "GLSimulatedSignalControl":
                // 设计画布仅显示占位样式，不加载真实控件
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.MediumPurple, // 使用紫色背景区别于其他控件
                    BorderBrush = Brushes.LightBlue,
                    BorderThickness = new Thickness(1)
                };
                TextBlock previewTextGL = new TextBlock()
                {
                    Text = "GPU模拟信号 (GL)",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTextGL;
                break;

            case "NewOpenGLSinWaveControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkBlue, // 使用深蓝色背景区别于其他控件
                    BorderBrush = Brushes.LightBlue,
                    BorderThickness = new Thickness(1)
                };
                TextBlock previewTextOpenGL = new TextBlock()
                {
                    Text = "🔷 SkiaSharp正弦波",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTextOpenGL;
                break;
            case "SimulatedSignalSourceControl":
                // 设计画布仅显示占位样式，不加载真实控件
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DimGray,
                    BorderBrush = Brushes.LightGray,
                    BorderThickness = new Thickness(1)
                };
                TextBlock previewTextSim = new TextBlock()
                {
                    Text = "模拟信号源 (CPU)",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTextSim;
                break;
            case "TcpSquareWaveSkiaControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkSlateGray,
                    BorderBrush = Brushes.LightSeaGreen,
                    BorderThickness = new Thickness(1.5)
                };
                TextBlock previewTcp = new TextBlock()
                {
                    Text = "TCP 方波 (CPU)",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.LightGreen,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTcp;
                break;
            case "TcpSquareWaveGlControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.DarkSlateGray,
                    BorderBrush = Brushes.DeepSkyBlue,
                    BorderThickness = new Thickness(1.5)
                };
                TextBlock previewTcpGpu = new TextBlock()
                {
                    Text = "TCP 方波 (GPU)",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.LightSkyBlue,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTcpGpu;
                break;
            case "TcpRealtimeWaveformControl":
                element = new Border()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Background = Brushes.Black,
                    BorderBrush = Brushes.CadetBlue,
                    BorderThickness = new Thickness(1.5)
                };
                TextBlock previewTcpRealtime = new TextBlock()
                {
                    Text = "TCP 实测波形",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Foreground = Brushes.CadetBlue,
                    FontWeight = FontWeight.Bold
                };
                ((Border)element).Child = previewTcpRealtime;
                break;
        }

        // 为元素添加标识，便于查找对应的控件信息
        if (element != null)
        {
            element.DataContext = controlInfo;

            // 创建包含连接点的容器
            return CreateControlWithConnectionPoint(element, controlInfo);
        }

        return element;
    }

    // 创建包含连接点的控件容器
    private Control CreateControlWithConnectionPoint(Control originalControl, ControlInfo controlInfo)
    {
        // 创建Grid容器
        var container = new Grid()
        {
            Width = controlInfo.Width,
            Height = controlInfo.Height,
            ZIndex = 5 // 确保控件在连接线上方
        };

        // 添加原始控件
        originalControl.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        originalControl.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
        container.Children.Add(originalControl);

        // 创建连接点
        var connectionPoint = new ConnectionPointView()
        {
            ControlId = controlInfo.Id,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ZIndex = 10 // 确保连接点在最上层
        };

        // 添加连接点事件处理
        connectionPoint.ConnectionPointPressed += OnConnectionPointPressed;

        container.Children.Add(connectionPoint);

        // 注册连接点到连接操作管理器
        connectionOperationManager?.RegisterConnectionPoint(controlInfo.Id, connectionPoint);

        // 将控件信息关联到容器
        container.DataContext = controlInfo;
        container.Tag = connectionPoint; // 保存连接点引用

        return container;
    }

    // 连接点事件处理方法
    private async void OnConnectionPointPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is ConnectionPointView connectionPoint && connectionOperationManager != null)
        {
            // 同时选中对应的控件
            var controlInfo = controlList.FirstOrDefault(c => c.Id == connectionPoint.ControlId);
            if (controlInfo != null)
            {
                // 在设计画布中找到对应的控件容器
                var controlContainer = designCanvas.Children.OfType<Control>()
                    .FirstOrDefault(c => c.DataContext is ControlInfo info && info.Id == connectionPoint.ControlId);
                if (controlContainer != null)
                {
                    SelectElement(controlContainer);
                }
            }
            
            await connectionOperationManager.HandleConnectionPointClick(connectionPoint.ControlId, connectionPoint, controlList);
            e.Handled = true;
        }
    }



    private async void OnControlsUpdated(List<ControlInfo> updatedControls)
    {
        // 更新设计画布上的控件显示
        foreach (Control child in designCanvas.Children)
        {
            if (child.DataContext is ControlInfo controlInfo)
            {
                var updatedControl = updatedControls.FirstOrDefault(c => c.Id == controlInfo.Id);
                if (updatedControl != null)
                {
                    // 更新控件内容显示
                    UpdatePreviewElementContent(child, updatedControl);
                }
            }
        }
        
        // 如果当前有选中的控件，刷新属性面板中的激活按钮状态
        if (selectedElement != null && selectedElement.DataContext is ControlInfo selectedControlInfo)
        {
            await CheckAndShowActivateButton(selectedControlInfo.Id);
        }
    }

    // 处理设计画布点击事件，取消选择
    private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 只有点击设计画布本身才取消选择，点击控件不触发
        if (e.Source == designCanvas)
        {
            DeselectElement();

            // 如果正在连接，取消连接
            if (connectionOperationManager?.IsConnecting == true)
            {
                connectionOperationManager.CancelConnection();
            }
        }
    }

    // 处理设计画布鼠标移动事件
    private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
    {
        // 当前不需要处理鼠标移动事件
    }

    // 处理控件点击事件，选择控件
    private void Element_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control element)
        {
            // 选择元素
            SelectElement(element);

            elementStartPoint = e.GetPosition(element);
            isDragging = true;
            e.Handled = true;
        }
    }

    // 处理控件拖拽移动
    private void Element_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (isDragging && selectedElement != null)
        {
            Control element = selectedElement;
            Point mousePoint = e.GetPosition(designCanvas);

            // 更新控件位置
            double newLeft = mousePoint.X - elementStartPoint.X;
            double newTop = mousePoint.Y - elementStartPoint.Y;

            Canvas.SetLeft(element, newLeft);
            Canvas.SetTop(element, newTop);

            // 同时更新选择边框的位置
            if (element.Tag is Border border)
            {
                Canvas.SetLeft(border, newLeft);
                Canvas.SetTop(border, newTop);
            }

            // 更新控件信息中的位置
            if (element.DataContext is ControlInfo controlInfo)
            {
                controlInfo.Left = newLeft;
                controlInfo.Top = newTop;
            }

            // 更新属性面板中的位置信息
            if (element == selectedElement)
            {
                txtLeft.Text = newLeft.ToString();
                txtTop.Text = newTop.ToString();
            }

            // 更新控件组边框位置
            if (connectionOperationManager != null)
            {
                _ = connectionOperationManager.UpdateControlPositions(controlList);
            }
        }
    }

    // 处理控件鼠标抬起事件
    private void Element_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (isDragging)
        {
            isDragging = false;
        }
    }

    // 选择元素
    private void SelectElement(Control element)
    {
        // 如果之前有选中元素，移除选择视觉效果
        DeselectElement();

        selectedElement = element;

        // 添加选择视觉效果
        Border selectionBorder = new Border()
        {
            BorderBrush = Brushes.Blue,
            BorderThickness = new Thickness(2),
            Width = element.Width,
            Height = element.Height,
            IsHitTestVisible = false // 边框不响应鼠标事件
        };

        Canvas.SetLeft(selectionBorder, Canvas.GetLeft(element));
        Canvas.SetTop(selectionBorder, Canvas.GetTop(element));

        // 将边框保存到元素的Tag属性中
        element.Tag = selectionBorder;
        designCanvas.Children.Add(selectionBorder);

        // 显示控件属性
        ShowElementProperties(element);
    }

    // 取消选择元素
    private void DeselectElement()
    {
        if (selectedElement != null)
        {
            // 移除选择视觉效果
            if (selectedElement.Tag is Border border)
            {
                designCanvas.Children.Remove(border);
            }
            selectedElement = null;
            ClearPropertyPanel();
        }
    }

    // 显示控件属性
    private async void ShowElementProperties(Control element)
    {
        if (element.DataContext is ControlInfo controlInfo)
        {
            txtName.Text = controlInfo.Name;
            txtWidth.Text = controlInfo.Width.ToString();
            txtHeight.Text = controlInfo.Height.ToString();
            txtLeft.Text = controlInfo.Left.ToString();
            txtTop.Text = controlInfo.Top.ToString();
            txtContent.Text = controlInfo.Content;

            // 设置文字颜色选择
            if (controlInfo.ForegroundColor == "White")
            {
                cmbForegroundColor.SelectedIndex = 1; // 白色
            }
            else
            {
                cmbForegroundColor.SelectedIndex = 0; // 黑色（默认）
            }
            
            // 显示或隐藏模拟信号源扩展属性面板
            ShowSimulatedSignalSourceProperties(controlInfo);
            
            // 检查控件是否属于控件组，如果是则显示激活按钮
            await CheckAndShowActivateButton(controlInfo.Id);
        }
    }

    // 显示或隐藏模拟信号源扩展属性
    private void ShowSimulatedSignalSourceProperties(ControlInfo controlInfo)
    {
        // 检查是否为模拟信号源控件
        bool isSimulatedSignalSource = controlInfo.Type == "SimulatedSignalSourceControl";
        
        // TODO: 在这里添加模拟信号源的扩展属性面板显示逻辑
        // 暂时不显示任何扩展面板，等待后续实现
    }

    // 检查控件是否属于控件组并显示/隐藏相应按钮
    private async Task CheckAndShowActivateButton(string controlId)
    {
        try
        {
            if (connectionOperationManager != null)
            {
                var groups = await connectionOperationManager.GetAllControlGroupsAsync();
                var group = groups.FirstOrDefault(g => g.ControlIds.Contains(controlId));
                if (group != null)
                {
                    // 控件属于某个控件组，根据激活状态显示相应按钮
                    if (group.FunctionType != FunctionCombinationType.None)
                    {
                        // 控件组已激活，显示取消按钮
                        btnActivateGroup.IsVisible = false;
                        btnDeactivateGroup.IsVisible = true;
                        btnDeactivateGroup.Tag = group.Id;
                        btnActivateGroup.Tag = null;
                    }
                    else
                    {
                        // 控件组未激活，显示激活按钮
                        btnActivateGroup.IsVisible = true;
                        btnDeactivateGroup.IsVisible = false;
                        btnActivateGroup.Tag = group.Id;
                        btnDeactivateGroup.Tag = null;
                    }
                }
                else
                {
                    // 控件不属于任何控件组，隐藏所有按钮
                    btnActivateGroup.IsVisible = false;
                    btnDeactivateGroup.IsVisible = false;
                    btnActivateGroup.Tag = null;
                    btnDeactivateGroup.Tag = null;
                }
            }
            else
            {
                btnActivateGroup.IsVisible = false;
                btnDeactivateGroup.IsVisible = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"检查控件组状态时发生错误: {ex.Message}");
            btnActivateGroup.IsVisible = false;
            btnDeactivateGroup.IsVisible = false;
        }
    }
    
    // 激活控件组按钮点击事件
    private async void ActivateGroup_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (btnActivateGroup.Tag is string groupId && connectionOperationManager != null)
            {
                var groups = await connectionOperationManager.GetAllControlGroupsAsync();
                var group = groups.FirstOrDefault(g => g.Id == groupId);
                if (group != null)
                {
                    // 获取控件组的控件信息
                    var controls = group.ControlIds.Select(id => controlList.FirstOrDefault(c => c.Id == id))
                                       .Where(c => c != null).Cast<ControlInfo>().ToList();
                    
                    // 获取可用的工作逻辑
                    var workingLogicService = new NewAvalonia.Services.WorkingLogicService();
                    var availableLogics = await workingLogicService.GetAvailableLogicsAsync(group, controls);
                    
                    // 显示选择对话框
                    bool confirmed;
                    WorkingLogic? selectedLogic;
                    var hostWindow = GetHostWindow();
                    (confirmed, selectedLogic) = await Views.WorkingLogicSelectionDialog.ShowDialogAsync(hostWindow, availableLogics);
                    
                    if (confirmed && selectedLogic != null)
                    {
                        // 激活选定的工作逻辑
                        await workingLogicService.SelectAndActivateLogicAsync(groupId, selectedLogic, controls);
                        
                        // 更新控件组的功能类型
                        group.FunctionType = selectedLogic.FunctionType;
                        
                        // 通知连接操作管理器更新边框显示
                        await connectionOperationManager.UpdateControlPositions(controlList);
                        
                        // 立即刷新按钮状态
                        if (selectedElement != null && selectedElement.DataContext is ControlInfo selectedControlInfo)
                        {
                            await CheckAndShowActivateButton(selectedControlInfo.Id);
                        }
                        
                        // 取消选中任何控件以增强稳定性
                        DeselectElement();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"激活控件组时发生错误: {ex.Message}");
        }
    }
    
    // 取消控件组工作逻辑按钮点击事件
    private async void DeactivateGroup_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (btnDeactivateGroup.Tag is string groupId && connectionOperationManager != null)
            {
                var groups = await connectionOperationManager.GetAllControlGroupsAsync();
                var group = groups.FirstOrDefault(g => g.Id == groupId);
                if (group != null)
                {
                    // 取消工作逻辑
                    var workingLogicService = new NewAvalonia.Services.WorkingLogicService();
                    await workingLogicService.DeactivateLogicAsync(groupId);
                    
                    // 重置控件组的功能类型
                    group.FunctionType = FunctionCombinationType.None;
                    
                    // 通知连接操作管理器更新边框显示
                    await connectionOperationManager.UpdateControlPositions(controlList);
                    
                    // 立即刷新按钮状态
                     if (selectedElement != null && selectedElement.DataContext is ControlInfo selectedControlInfo)
                     {
                         await CheckAndShowActivateButton(selectedControlInfo.Id);
                     }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"取消控件组工作逻辑时发生错误: {ex.Message}");
        }
    }

    // 清空属性面板
    private void ClearPropertyPanel()
    {
        txtName.Text = "";
        txtWidth.Text = "";
        txtHeight.Text = "";
        txtContent.Text = "";
        txtLeft.Text = "";
        txtTop.Text = "";
        cmbForegroundColor.SelectedIndex = 0; // 默认选择黑色
        btnActivateGroup.IsVisible = false; // 隐藏激活按钮
        btnDeactivateGroup.IsVisible = false; // 隐藏取消按钮
    }

    // 应用属性修改
    private void ApplyProperties_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedElement == null) return;

        Control element = selectedElement;
        if (element.DataContext is ControlInfo controlInfo)
        {
            // 更新控件信息
            controlInfo.Name = txtName.Text ?? "";

            if (double.TryParse(txtWidth.Text, out double width))
                controlInfo.Width = width;

            if (double.TryParse(txtHeight.Text, out double height))
                controlInfo.Height = height;

            if (double.TryParse(txtLeft.Text, out double left))
                controlInfo.Left = left;

            if (double.TryParse(txtTop.Text, out double top))
                controlInfo.Top = top;

            controlInfo.Content = txtContent.Text ?? "";

            // 更新文字颜色属性
            if (cmbForegroundColor.SelectedItem is ComboBoxItem selectedItem)
            {
                controlInfo.ForegroundColor = selectedItem.Tag?.ToString() ?? "Black";
            }

            // 更新运行控件
            element.Width = controlInfo.Width;
            element.Height = controlInfo.Height;
            Canvas.SetLeft(element, controlInfo.Left);
            Canvas.SetTop(element, controlInfo.Top);

            // 更新选择边框的大小和位置
            if (element.Tag is Border border)
            {
                border.Width = controlInfo.Width;
                border.Height = controlInfo.Height;
                Canvas.SetLeft(border, controlInfo.Left);
                Canvas.SetTop(border, controlInfo.Top);
            }

            // 更新运行控件的内容显示和文字颜色
            UpdatePreviewElementContent(element, controlInfo);
            UpdatePreviewElementForeground(element, controlInfo);
        }
    }

    private async void DeleteControl_Click(object? sender, RoutedEventArgs e)
    {
        if (selectedElement != null)
        {
            string? controlIdToDelete = null;

            // 获取要删除的控件ID
            if (selectedElement.DataContext is ControlInfo controlInfo)
            {
                controlIdToDelete = controlInfo.Id;

                // 验证控件确实存在于控件列表中
                if (!controlList.Any(c => c.Id == controlIdToDelete))
                {
                    Console.WriteLine($"警告：尝试删除不存在的控件 ID: {controlIdToDelete}");
                    return;
                }
            }
            else
            {
                Console.WriteLine("警告：选中的元素没有有效的ControlInfo");
                return;
            }

            // 从设计画布中移除控件
            designCanvas.Children.Remove(selectedElement);

            // 从控件列表中移除控件信息
            if (selectedElement.DataContext is ControlInfo controlInfoToRemove)
            {
                controlList.Remove(controlInfoToRemove);
            }

            // 移除选择边框
            if (selectedElement.Tag is Border border)
            {
                designCanvas.Children.Remove(border);
            }

            // 清理控件的Tag属性，避免内存泄漏
            selectedElement.Tag = null;

            // 处理控件组相关的清理工作
            if (!string.IsNullOrEmpty(controlIdToDelete) && connectionOperationManager != null)
            {
                try
                {
                    await HandleControlDeletion(controlIdToDelete);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"删除控件时发生错误: {ex.Message}");
                    // 即使连接清理失败，也要继续完成控件删除
                }
            }

            // 清空属性面板并取消选择
            selectedElement = null;
            ClearPropertyPanel();
        }
    }

    private async Task HandleControlDeletion(string deletedControlId)
    {
        Console.WriteLine($"开始删除控件: {deletedControlId}");

        if (connectionOperationManager != null)
            await connectionOperationManager.HandleControlDeletionAsync(deletedControlId, controlList);

        Console.WriteLine($"控件删除完成: {deletedControlId}");

        // 如果有打开的预览窗口，需要通知它们更新
        // 注意：这里可能需要维护一个预览窗口的引用列表
        // 目前的实现中每次都创建新的预览窗口，所以这个问题影响较小
    }

    // 更新运行控件的内容显示
    private void UpdatePreviewElementContent(Control element, ControlInfo controlInfo)
    {
        switch (controlInfo.Type)
        {
            case "Button":
                if (element is Label label)
                    label.Content = controlInfo.Content;
                break;
            case "Label":
                if (element is Label label1)
                    label1.Content = controlInfo.Content;
                break;
            case "TextBox":
                if (element is Border border && border.Child is TextBlock textBlock)
                    textBlock.Text = controlInfo.Content;
                break;
            case "CheckBox":
                if (element is Border border1 && border1.Child is StackPanel panel && panel.Children.Count > 1)
                {
                    if (panel.Children[1] is TextBlock textBlock1)
                        textBlock1.Text = controlInfo.Content;
                }
                break;
            case "ComboBox":
                if (element is Border border2 && border2.Child is Grid grid)
                {
                    if (grid.Children[0] is TextBlock textBlock2)
                        textBlock2.Text = controlInfo.Content;
                }
                break;
        }
    }

    // 更新运行控件的文字颜色
    private void UpdatePreviewElementForeground(Control element, ControlInfo controlInfo)
    {
        IBrush foregroundBrush = controlInfo.ForegroundColor == "White" ? Brushes.White : Brushes.Black;

        switch (controlInfo.Type)
        {
            case "Button":
                if (element is Label buttonLabel)
                    buttonLabel.Foreground = foregroundBrush;
                break;
            case "Label":
                if (element is Label label)
                    label.Foreground = foregroundBrush;
                break;
            case "TextBox":
                if (element is Border textBoxBorder && textBoxBorder.Child is TextBlock textBlock)
                    textBlock.Foreground = foregroundBrush;
                break;
            case "CheckBox":
                if (element is Border checkBoxBorder && checkBoxBorder.Child is StackPanel panel && panel.Children.Count > 1)
                {
                    if (panel.Children[1] is TextBlock checkBoxTextBlock)
                        checkBoxTextBlock.Foreground = foregroundBrush;
                }
                break;
            case "ComboBox":
                if (element is Border comboBoxBorder && comboBoxBorder.Child is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is TextBlock comboTextBlock)
                            comboTextBlock.Foreground = foregroundBrush;
                    }
                }
                break;
        }
    }

    // 运行
    private async void RunPreview_Click(object? sender, RoutedEventArgs e)
    {
        PreviewWindow previewWindow = new PreviewWindow();

        // 根据设计器中的控件创建实际可交互的控件
        foreach (ControlInfo controlInfo in controlList)
        {
            Control? element = CreateInteractiveControl(controlInfo);
            if (element != null)
            {
                Canvas.SetLeft(element, controlInfo.Left);
                Canvas.SetTop(element, controlInfo.Top);
                previewWindow.previewCanvas.Children.Add(element);
            }
        }

        // 初始化运行时功能管理器
        if (connectionOperationManager != null)
        {
            var runtimeFunctionManager = new NewAvalonia.Services.RuntimeFunctionManager(previewWindow.previewCanvas);
            var controlGroups = await connectionOperationManager.GetAllControlGroupsAsync();
            await runtimeFunctionManager.InitializeRuntimeAsync(controlList, controlGroups);

            // 将功能管理器保存到预览窗口，以便后续使用
            previewWindow.Tag = runtimeFunctionManager;
        }

        previewWindow.Show();
    }

    // 创建可交互的实际控件
    private Control? CreateInteractiveControl(ControlInfo controlInfo)
    {
        Control? element = null;

        switch (controlInfo.Type)
        {
            case "Button":
                element = new Button()
                {
                    Content = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Name = controlInfo.Name
                };
                break;
            case "Label":
                element = new Label()
                {
                    Content = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Name = controlInfo.Name
                };
                break;
            case "TextBox":
                element = new TextBox()
                {
                    Text = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Name = controlInfo.Name
                };
                break;
            case "CheckBox":
                element = new CheckBox()
                {
                    Content = controlInfo.Content,
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Name = controlInfo.Name
                };
                break;
            case "ComboBox":
                element = new ComboBox()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height,
                    Name = controlInfo.Name
                };
                (element as ComboBox)!.Items.Add("Item 1");
                (element as ComboBox)!.Items.Add("Item 2");
                (element as ComboBox)!.Items.Add(controlInfo.Content);
                if (!string.IsNullOrEmpty(controlInfo.Content))
                {
                    (element as ComboBox)!.SelectedItem = controlInfo.Content;
                }
                break;
            case "NewOxyPlotSinWaveControl":
                element = new NewAvalonia.Views.NewOxyPlotSinWaveControl()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height
                };
                break;
            case "DisplayControl":
                element = new NewAvalonia.Views.DisplayControl()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height
                };
                break;
            case "DisplayControl2":
                element = new NewAvalonia.Views.DisplayControl2()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height
                };
                break;
            case "SimulatedSignalSourceControl":
                element = new NewAvalonia.Views.SimulatedSignalSourceControl()
                {
                    Width = double.NaN,
                    Height = controlInfo.Height
                };
                break;
            case "TcpSquareWaveSkiaControl":
                element = new NewAvalonia.Views.TcpSquareWaveSkiaControl()
                {
                    Width = double.NaN,
                    Height = controlInfo.Height
                };
                break;
            case "TcpSquareWaveGlControl":
                element = new NewAvalonia.Views.TcpSquareWaveGlControl()
                {
                    Width = double.NaN,
                    Height = controlInfo.Height
                };
                break;
            case "TcpRealtimeWaveformControl":
                element = new NewAvalonia.Views.TcpRealtimeWaveformControl()
                {
                    Width = double.NaN,
                    Height = controlInfo.Height
                };
                break;
            case "GLSimulatedSignalControl":
                element = new NewAvalonia.Views.GLSimulatedSignalControl()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height
                };
                break;

            case "NewOpenGLSinWaveControl":
                element = new NewAvalonia.Views.NewOpenGLSinWaveControl()
                {
                    Width = controlInfo.Width,
                    Height = controlInfo.Height
                };
                break;
        }

        // 为运行时控件添加ToolTip显示控件名称和设置DataContext
        if (element != null)
        {
            // 对于有自己ViewModel的控件，不要覆盖它们的DataContext
            if (controlInfo.Type != "NewOxyPlotSinWaveControl"
                && controlInfo.Type != "DisplayControl"
                && controlInfo.Type != "DisplayControl2"
                && controlInfo.Type != "SimulatedSignalSourceControl"
                && controlInfo.Type != "TcpSquareWaveSkiaControl"
                && controlInfo.Type != "TcpSquareWaveGlControl"
                && controlInfo.Type != "GLSimulatedSignalControl"
                && controlInfo.Type != "NewOpenGLSinWaveControl"
                && controlInfo.Type != "TcpRealtimeWaveformControl")
            {
                element.DataContext = controlInfo;
            }

            // 使用Tag属性存储控件ID，供RuntimeFunctionManager查找使用
            element.Tag = controlInfo.Id;

            Avalonia.Controls.ToolTip.SetTip(element, controlInfo.Name);
        }

        return element;
    }

    // 图像处理器算法管理相关方法
    private async void BrowseAlgorithm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var openFileDialog = new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择算法文件 (.xtj/.xtjs)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("算法文件")
                    {
                        Patterns = new[] { "*.xtj", "*.xtjs" }
                    }
                }
            };

            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider == null)
            {
                return;
            }

            var files = await storageProvider.OpenFilePickerAsync(openFileDialog);

            if (files.Count > 0)
            {
                var selectedFile = files[0];
                // txtAlgorithmFile.Text = selectedFile.Path.LocalPath;
                
                // 更新当前选中控件的算法文件路径
                if (selectedElement?.DataContext is ControlInfo controlInfo)
                {
                    controlInfo.AlgorithmFilePath = selectedFile.Path.LocalPath;
                    controlInfo.AlgorithmType = "External";
                    
                    // 如果是SimulatedSignalSourceViewModel，也更新其ExternalAlgorithmPath
                    if (controlInfo.ViewModel is SimulatedSignalSourceViewModel signalViewModel)
                    {
                        signalViewModel.ExternalAlgorithmPath = selectedFile.Path.LocalPath;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"选择算法文件时发生错误: {ex.Message}");
        }
    }

    private void ImportAlgorithm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // TODO: 实现算法导入逻辑
            // 这里可以读取.xtj文件内容，解析算法参数等
            Console.WriteLine("导入算法功能待实现");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导入算法时发生错误: {ex.Message}");
        }
    }

    private void DeleteAlgorithm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 清空算法选择
            // cmbProcessingAlgorithm.SelectedIndex = -1;
            // txtAlgorithmFile.Text = "";
            
            // 更新当前选中控件的算法信息
            if (selectedElement?.DataContext is ControlInfo controlInfo)
            {
                controlInfo.SelectedAlgorithm = "";
                controlInfo.AlgorithmType = "Built-in";
                controlInfo.AlgorithmFilePath = "";
                controlInfo.AlgorithmParameters = "";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除算法时发生错误: {ex.Message}");
        }
    }

    private void AlgorithmSelection_Changed(object? sender, SelectionChangedEventArgs e)
    {
        try
        {
            // 注释掉不存在的UI元素引用
            /*
            if (cmbProcessingAlgorithm.SelectedItem is ComboBoxItem selectedItem && 
                selectedElement?.DataContext is ControlInfo controlInfo)
            {
                controlInfo.SelectedAlgorithm = selectedItem.Content?.ToString() ?? "";
                
                // 根据选择的算法类型更新界面
                if (selectedItem.Tag?.ToString() == "custom")
                {
                    controlInfo.AlgorithmType = "External";
                    builtinAlgorithmParams.IsVisible = false;
                    externalAlgorithmPanel.IsVisible = true;
                }
                else
                {
                    controlInfo.AlgorithmType = "Built-in";
                    builtinAlgorithmParams.IsVisible = true;
                    externalAlgorithmPanel.IsVisible = false;
                }
            }
            */
        }
        catch (Exception ex)
        {
            Console.WriteLine($"算法选择变更时发生错误: {ex.Message}");
        }
    }

    private void ReloadAlgorithm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 重新加载算法文件
            if (selectedElement?.DataContext is ControlInfo controlInfo && 
                !string.IsNullOrEmpty(controlInfo.AlgorithmFilePath))
            {
                // TODO: 实现算法文件重新加载逻辑
                Console.WriteLine($"重新加载算法文件: {controlInfo.AlgorithmFilePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"重新加载算法时发生错误: {ex.Message}");
        }
    }

    private void TestAlgorithm_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 测试算法效果
            if (selectedElement?.DataContext is ControlInfo controlInfo)
            {
                if (controlInfo.AlgorithmType == "External" && !string.IsNullOrEmpty(controlInfo.AlgorithmFilePath))
                {
                    // TODO: 实现外部算法测试逻辑
                    Console.WriteLine($"测试外部算法: {controlInfo.AlgorithmFilePath}");
                }
                else if (controlInfo.AlgorithmType == "Built-in")
                {
                    // TODO: 实现内置算法测试逻辑
                    Console.WriteLine($"测试内置算法: {controlInfo.SelectedAlgorithm}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"测试算法时发生错误: {ex.Message}");
        }
    }


    // 顶部工具栏：算法管理（原加密算法文件功能，现在使用动态库模块）
    private async void AlgorithmManagement_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            // 直接显示可用算法列表，而不是弹出上下文菜单
            ShowAvailableAlgorithms();
        }
        catch (Exception ex)
        {
            var dialog = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard("算法管理异常", ex.Message);
            await dialog.ShowAsync();
        }
    }

    // 显示可用算法
    private void ShowAvailableAlgorithms()
    {
        try
        {
            var algorithms = NewAvalonia.Services.AlgorithmManager.GetAllAlgorithms();
            var msg = "可用算法列表：\n\n";
            
            foreach (var algorithm in algorithms)
            {
                msg += $"名称: {algorithm.Name}\n";
                msg += $"描述: {algorithm.Description}\n";
                msg += $"版本: {algorithm.Version}\n";
                msg += $"作者: {algorithm.Author}\n";
                msg += "默认参数: ";
                
                foreach (var param in algorithm.DefaultParameters)
                {
                    msg += $"{param.Key}={param.Value} ";
                }
                msg += "\n\n";
            }

            if (algorithms.Count == 0)
            {
                msg = "当前没有可用的算法模块。";
            }

            var dialog = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard("可用算法", msg);
            _ = dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"显示算法列表时出错: {ex.Message}");
        }
    }

    // 使用动态库算法
    private void UseDynamicLibraryAlgorithms()
    {
        try
        {
            var msg = "系统现在使用动态库算法模块。\n\n" +
                     "优势：\n" +
                     "- 无需加密/解密算法文件\n" +
                     "- 更快的加载速度\n" +
                     "- 无需运行时编译\n" +
                     "- 更好的性能\n" +
                     "- 更强的安全性\n\n" +
                     "新的算法可以通过添加 IAlgorithm 接口的实现来扩展。";
            
            var dialog = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard("动态库算法", msg);
            _ = dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Console.WriteLine($"使用动态库算法时出错: {ex.Message}");
        }
    }

    // 顶部工具栏：加密算法文件 (.xtj → .xtjs)
    private async void EncryptAlgorithms_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null || topLevel.StorageProvider == null)
                return;

            var options = new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "选择要加密的算法文件 (.xtj)",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("算法文件")
                    {
                        Patterns = new[] { "*.xtj" }
                    }
                }
            };

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
            if (files == null || files.Count == 0) return;

            int success = 0, fail = 0;
            foreach (var f in files)
            {
                try
                {
                    var inputPath = f.Path.LocalPath;
                    var outputPath = System.IO.Path.ChangeExtension(inputPath, ".xtjs");
                    // 读取原始明文
                    var plaintext = await System.IO.File.ReadAllTextAsync(inputPath);
                    // 使用工具类加密
                    var encrypted = NewAvalonia.Services.XtjCrypto.EncryptJson(plaintext);
                    await System.IO.File.WriteAllTextAsync(outputPath, encrypted);
                    success++;
                }
                catch (Exception ex1)
                {
                    System.Console.WriteLine($"加密文件失败: {ex1.Message}");
                    fail++;
                }
            }

            var msg = $"加密完成：成功 {success} 个，失败 {fail} 个。\n输出为同名 .xtjs 文件（与原文件同目录）。";
            var dialog = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard("加密结果", msg);
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard("加密异常", ex.Message);
            await dialog.ShowAsync();
        }
    }

    private Window? GetHostWindow()
    {
        return TopLevel.GetTopLevel(this) as Window;
    }

}
