using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NewAvalonia.Models;
using NewAvalonia.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NewAvalonia.Views
{
    public partial class ControlGroupBorderView : UserControl
    {
        public string GroupId { get; set; } = string.Empty;
        public FunctionCombinationType FunctionType { get; set; } = FunctionCombinationType.None;
        
        // 事件：当用户选择工作逻辑时触发
        public event System.Action<string, WorkingLogic?>? WorkingLogicSelected;
        
        // 依赖的服务和数据
        private IWorkingLogicService? _workingLogicService;
        private List<ControlInfo>? _allControls;
        private ControlGroup? _controlGroup;

        public ControlGroupBorderView()
        {
            InitializeComponent();
            InitializeEvents();
        }
        
        private void InitializeEvents()
        {
            groupLabel.Click += OnGroupLabelClick;
        }

        public void SetBounds(Rect bounds)
        {
            Width = bounds.Width;
            Height = bounds.Height;
        }

        public void SetFunctionType(FunctionCombinationType functionType)
        {
            FunctionType = functionType;
            UpdateVisualStyle();
        }

        /// <summary>
        /// 设置工作逻辑服务和相关数据
        /// </summary>
        public void SetDependencies(IWorkingLogicService workingLogicService, ControlGroup controlGroup, List<ControlInfo> allControls)
        {
            _workingLogicService = workingLogicService;
            _controlGroup = controlGroup;
            _allControls = allControls;
        }
        
        /// <summary>
        /// 处理控件组标签点击事件
        /// </summary>
        private async void OnGroupLabelClick(object? sender, RoutedEventArgs e)
        {
            if (_workingLogicService == null || _controlGroup == null || _allControls == null)
                return;
                
            try
            {
                // 获取可用的工作逻辑
                var availableLogics = await _workingLogicService.GetAvailableLogicsAsync(_controlGroup, _allControls);
                
                // 显示选择对话框
                var parentWindow = TopLevel.GetTopLevel(this) as Window;
                var (confirmed, selectedLogic) = await WorkingLogicSelectionDialog.ShowDialogAsync(parentWindow, availableLogics);
                
                if (confirmed)
                {
                    // 触发工作逻辑选择事件
                    WorkingLogicSelected?.Invoke(GroupId, selectedLogic);
                }
            }
            catch (System.Exception ex)
            {
                // 处理异常（可以添加日志记录）
                System.Console.WriteLine($"选择工作逻辑时发生错误: {ex.Message}");
            }
        }

        private void UpdateVisualStyle()
        {
            // 清除现有样式类
            groupBorder.Classes.Clear();
            groupLabel.Classes.Clear();

            // 添加基础样式类
            groupBorder.Classes.Add("groupBorder");
            groupLabel.Classes.Add("groupLabel");

            // 根据功能类型添加特定样式
            switch (FunctionType)
            {
                case FunctionCombinationType.None:
                    groupBorder.Classes.Add("none");
                    groupLabel.Classes.Add("none");
                    groupLabel.Content = "控件组";
                    break;
                case FunctionCombinationType.SinWaveGenerator:
                    groupBorder.Classes.Add("sinwave");
                    groupLabel.Classes.Add("sinwave");
                    groupLabel.Content = "正弦波生成器";
                    break;
                case FunctionCombinationType.SquareWaveGenerator:
                    groupBorder.Classes.Add("squarewave");
                    groupLabel.Classes.Add("squarewave");
                    groupLabel.Content = "方波生成器";
                    break;
                case FunctionCombinationType.SimulatedSignalSource:
                    groupBorder.Classes.Add("simulatedsignalsource");
                    groupLabel.Classes.Add("simulatedsignalsource");
                    groupLabel.Content = "模拟信号源";
                    break;
            }
        }

        public void UpdatePosition(Point position)
        {
            Canvas.SetLeft(this, position.X);
            Canvas.SetTop(this, position.Y);
        }

        public static Point CalculatePosition(Rect bounds)
        {
            return new Point(bounds.X, bounds.Y);
        }
    }
}