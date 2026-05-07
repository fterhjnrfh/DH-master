using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using NewAvalonia.Models;
using NewAvalonia.Views;
using NewAvalonia.ViewModels;
using OxyPlot;
using NewAvalonia.Interfaces;

namespace NewAvalonia.Services
{
    // 功能绑定接口
    public interface IFunctionBinding
    {
        Task InitializeAsync(List<Control> controls);
        Task UpdateParameterAsync(string parameterId, object value);
        Task DeactivateAsync();
    }
    public class RuntimeFunctionManager
    {
        private readonly Canvas _runtimeCanvas;
        private readonly Dictionary<string, object> _functionInstances = new();
        private readonly Dictionary<string, List<Control>> _groupControls = new();

        public RuntimeFunctionManager(Canvas runtimeCanvas)
        {
            _runtimeCanvas = runtimeCanvas;
        }

        public async Task InitializeRuntimeAsync(List<ControlInfo> designControls, List<ControlGroup> controlGroups)
        {
            // 清理之前的运行时实例
            _functionInstances.Clear();
            _groupControls.Clear();

            // 为每个控件组检测并激活功能
            foreach (var group in controlGroups)
            {
                if (group.FunctionType == FunctionCombinationType.SinWaveGenerator)
                {
                    await InitializeSinWaveFunction(group, designControls);
                }
                else if (group.FunctionType == FunctionCombinationType.SquareWaveGenerator)
                {
                    await InitializeSquareWaveFunction(group, designControls);
                }
                else if (group.FunctionType == FunctionCombinationType.SimulatedSignalSource)
        {
            await InitializeSimulatedSignalSourceFunction(group, designControls);
        }
            }
        }

        private async Task InitializeSinWaveFunction(ControlGroup group, List<ControlInfo> allControls)
        {
            await Task.CompletedTask;

            var groupControls = allControls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var textBoxes = groupControls.Where(c => c.Type == "TextBox").OrderBy(c => c.Id).ToList();
            var displays = groupControls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            if (textBoxes.Count == 2 && displays.Count == 1)
            {
                // 找到运行时对应的控件，按照设计时的顺序
                var runtimeControls = new List<Control>();
                
                // 按照textBoxes的顺序添加TextBox控件
                foreach (var textBoxInfo in textBoxes)
                {
                    var runtimeControl = FindRuntimeControl(textBoxInfo.Id);
                    if (runtimeControl != null)
                    {
                        runtimeControls.Add(runtimeControl);
                    }
                }
                
                // 添加DisplayControl
                foreach (var displayInfo in displays)
                {
                    var runtimeControl = FindRuntimeControl(displayInfo.Id);
                    if (runtimeControl != null)
                    {
                        runtimeControls.Add(runtimeControl);
                    }
                }

                if (runtimeControls.Count == 3)
                {
                    // 创建正弦波功能绑定
                    var sinWaveBinding = new SinWaveFunctionBinding();
                    await sinWaveBinding.InitializeAsync(runtimeControls, textBoxes, displays);
                    
                    _functionInstances[group.Id] = sinWaveBinding;
                    _groupControls[group.Id] = runtimeControls;
                }
            }
        }

        private async Task InitializeSquareWaveFunction(ControlGroup group, List<ControlInfo> allControls)
        {
            await Task.CompletedTask;

            var groupControls = allControls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var textBoxes = groupControls.Where(c => c.Type == "TextBox").OrderBy(c => c.Id).ToList();
            var displays = groupControls.Where(c => c.Type == "DisplayControl" || c.Type == "DisplayControl2").ToList();

            if (textBoxes.Count == 2 && displays.Count == 1)
            {
                // 找到运行时对应的控件，按照设计时的顺序
                var runtimeControls = new List<Control>();
                
                // 按照textBoxes的顺序添加TextBox控件
                foreach (var textBoxInfo in textBoxes)
                {
                    var runtimeControl = FindRuntimeControl(textBoxInfo.Id);
                    if (runtimeControl != null)
                    {
                        runtimeControls.Add(runtimeControl);
                    }
                }
                
                // 添加DisplayControl
                foreach (var displayInfo in displays)
                {
                    var runtimeControl = FindRuntimeControl(displayInfo.Id);
                    if (runtimeControl != null)
                    {
                        runtimeControls.Add(runtimeControl);
                    }
                }

                if (runtimeControls.Count == 3)
                {
                    // 创建方波功能绑定
                    var squareWaveBinding = new SquareWaveFunctionBinding();
                    await squareWaveBinding.InitializeAsync(runtimeControls, textBoxes, displays);
                    
                    _functionInstances[group.Id] = squareWaveBinding;
                    _groupControls[group.Id] = runtimeControls;
                }
            }
        }

        private async Task InitializeSimulatedSignalSourceFunction(ControlGroup group, List<ControlInfo> allControls)
        {
            await Task.CompletedTask;

            var groupControls = allControls.Where(c => group.ControlIds.Contains(c.Id)).ToList();
            var simulatedSignalSources = groupControls.Where(c => c.Type == "SimulatedSignalSourceControl").ToList();

            if (simulatedSignalSources.Count == 1)
            {
                // 找到运行时对应的控件
                var runtimeControls = new List<Control>();
                
                // 添加SimulatedSignalSource控件
                foreach (var signalSourceInfo in simulatedSignalSources)
                {
                    var runtimeControl = FindRuntimeControl(signalSourceInfo.Id);
                    if (runtimeControl != null)
                    {
                        runtimeControls.Add(runtimeControl);
                    }
                }

                if (runtimeControls.Count == 1)
                {
                    // 创建模拟信号源功能绑定
                    var simulatedSignalBinding = new SimulatedSignalSourceFunctionBinding();
                    await simulatedSignalBinding.InitializeAsync(runtimeControls);
                    
                    _functionInstances[group.Id] = simulatedSignalBinding;
                    _groupControls[group.Id] = runtimeControls;
                }
            }
        }

        private Control? FindRuntimeControl(string controlId)
        {
            // 在运行时画布中查找对应的控件
            foreach (Control child in _runtimeCanvas.Children)
            {
                // 使用Tag属性查找控件ID
                if (child.Tag?.ToString() == controlId)
                {
                    return child;
                }
            }
            return null;
        }

        public async Task UpdateFunctionParameterAsync(string groupId, string parameterId, object value)
        {
            await Task.CompletedTask;

            if (_functionInstances.TryGetValue(groupId, out var functionInstance))
            {
                if (functionInstance is SinWaveFunctionBinding sinWaveBinding)
                {
                    await sinWaveBinding.UpdateParameterAsync(parameterId, value);
                }
                else if (functionInstance is SquareWaveFunctionBinding squareWaveBinding)
                {
                    await squareWaveBinding.UpdateParameterAsync(parameterId, value);
                }
                else if (functionInstance is SimulatedSignalSourceFunctionBinding simulatedSignalBinding)
                {
                    await simulatedSignalBinding.UpdateParameterAsync(parameterId, value);
                }
            }
        }

        public async Task DeactivateAllFunctionsAsync()
        {
            foreach (var functionInstance in _functionInstances.Values)
            {
                if (functionInstance is SinWaveFunctionBinding sinWaveBinding)
                {
                    await sinWaveBinding.DeactivateAsync();
                }
                else if (functionInstance is SquareWaveFunctionBinding squareWaveBinding)
                {
                    await squareWaveBinding.DeactivateAsync();
                }
                else if (functionInstance is SimulatedSignalSourceFunctionBinding simulatedSignalBinding)
                {
                    await simulatedSignalBinding.DeactivateAsync();
                }
            }

            _functionInstances.Clear();
            _groupControls.Clear();
        }
    }

    // 正弦波功能绑定类
    public class SinWaveFunctionBinding : IFunctionBinding
    {
        private TextBox? _amplitudeTextBox;
        private TextBox? _frequencyTextBox;
        private object? _displayControl; // 通用显示控件（DisplayControl 或 DisplayControl2）
        private IWaveformDisplay? _waveformDisplay; // 波形显示接口
        private DispatcherTimer? _timer;
        private double _phase;

        public async Task InitializeAsync(List<Control> controls)
        {
            await Task.CompletedTask;
            // 这里需要重新实现以匹配新的接口
            // 暂时保留原有逻辑的简化版本
        }

        public async Task InitializeAsync(List<Control> runtimeControls, List<ControlInfo> textBoxInfos, List<ControlInfo> displayInfos)
        {
            await Task.CompletedTask;

            // 按照textBoxInfos的顺序找到对应的TextBox控件
            var textBoxControls = new List<TextBox>();
            var displayControls = new List<Control>(); // 使用通用Control类型

            // 按照设计时的顺序匹配TextBox控件
            foreach (var textBoxInfo in textBoxInfos)
            {
                var textBox = runtimeControls.OfType<TextBox>().FirstOrDefault(tb => 
                    tb.Name == textBoxInfo.Id || tb.Tag?.ToString() == textBoxInfo.Id);
                if (textBox != null)
                {
                    textBoxControls.Add(textBox);
                }
            }
            
            // 找到DisplayControl或DisplayControl2
            foreach (var control in runtimeControls)
            {
                if (control is DisplayControl || control is DisplayControl2)
                {
                    displayControls.Add(control);
                }
            }

            if (textBoxControls.Count >= 2 && displayControls.Count >= 1)
            {
                // 分配角色：第一个TextBox为幅值，第二个为频率
                _amplitudeTextBox = textBoxControls[0];
                _frequencyTextBox = textBoxControls[1];
                _displayControl = displayControls[0];

                // 获取DisplayControl或DisplayControl2的ViewModel并实现IWaveformDisplay接口
                if (_displayControl is DisplayControl displayControl)
                {
                    _waveformDisplay = displayControl.DataContext as DisplayControlViewModel;
                }
                else if (_displayControl is DisplayControl2 displayControl2)
                {
                    _waveformDisplay = displayControl2.DataContext as DisplayControl2ViewModel;
                }

                // 绑定事件
                if (_amplitudeTextBox != null)
                {
                    _amplitudeTextBox.TextChanged += OnParameterChanged;
                }

                if (_frequencyTextBox != null)
                {
                    _frequencyTextBox.TextChanged += OnParameterChanged;
                }

                // 设置初始值并启动动画
                StartSinWaveDisplay();
            }
        }

        private void OnParameterChanged(object? sender, TextChangedEventArgs e)
        {
            // 当参数变化时，不需要做任何事，因为timer会持续读取最新值
        }

        private void StartSinWaveDisplay()
        {
            if (_timer != null) return;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_waveformDisplay == null) return;

            // 解析幅值
            if (!double.TryParse(_amplitudeTextBox?.Text, out double amplitude) || amplitude <= 0)
            {
                amplitude = 25; // 默认值
            }

            // 解析频率
            if (!double.TryParse(_frequencyTextBox?.Text, out double frequency) || frequency <= 0)
            {
                frequency = 0.1; // 默认值
            }

            // 更新幅值
            _waveformDisplay.Amplitude = amplitude;

            // 生成波形数据
            _phase += 0.2; // 控制波形移动
            var points = new List<(double X, double Y)>();
            double width = 600; // 与OxyPlot正弦波控件保持一致的X轴范围

            // 生成高密度的波形数据点，与OxyPlot正弦波控件保持一致
            for (double x = 0; x <= width; x += 0.5)
            {
                // 使用与OxyPlot正弦波控件相同的频率计算方式
                double y = amplitude * Math.Sin(_phase + x * frequency * 2 * Math.PI / 100.0);
                points.Add((x, y));
            }

            // 更新显示
            _waveformDisplay.UpdateWaveform(points);
        }

        public async Task UpdateParameterAsync(string parameterId, object value)
        {
            // 这个方法现在可以保持为空，因为UI的更新由timer驱动
            await Task.CompletedTask;
        }

        public async Task DeactivateAsync()
        {
            await Task.CompletedTask;

            // 停止计时器
            _timer?.Stop();
            _timer = null;

            // 解绑事件
            if (_amplitudeTextBox != null)
            {
                _amplitudeTextBox.TextChanged -= OnParameterChanged;
            }

            if (_frequencyTextBox != null)
            {
                _frequencyTextBox.TextChanged -= OnParameterChanged;
            }
            
            // 清理显示
            _waveformDisplay?.ClearWaveform();

            // 清理引用
            _amplitudeTextBox = null;
            _frequencyTextBox = null;
            _displayControl = null;
            _waveformDisplay = null;
        }
    }

    // 图像处理器功能绑定类
    // 模拟信号源功能绑定类
    public class SimulatedSignalSourceFunctionBinding : IFunctionBinding
    {
        private SimulatedSignalSourceControl? _signalSourceControl;
        private SimulatedSignalSourceViewModel? _signalSourceViewModel;
        private AlgorithmProcessor? _algorithmProcessor;
        private AlgorithmProcessor.AlgorithmDefinition? _currentAlgorithm;
        private Dictionary<string, object> _algorithmParameters = new();

        public async Task InitializeAsync(List<Control> controls)
        {
            await Task.CompletedTask;

            // 初始化算法处理器
            _algorithmProcessor = new AlgorithmProcessor();

            // 查找SimulatedSignalSourceControl
            _signalSourceControl = controls.OfType<SimulatedSignalSourceControl>().FirstOrDefault();
            
            if (_signalSourceControl != null)
            {
                // 获取ViewModel
                _signalSourceViewModel = _signalSourceControl.DataContext as SimulatedSignalSourceViewModel;
                
                if (_signalSourceViewModel != null)
                {
                    // 设置算法处理回调
                    _signalSourceViewModel.SetAlgorithmProcessor(ApplyCurrentAlgorithm);
                    
                    // 加载默认算法
                    await LoadDefaultAlgorithmAsync();
                }
            }
        }

        private async Task LoadDefaultAlgorithmAsync()
        {
            try
            {
                var defaultAlgorithmPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Algorithms", "SimpleMovingAverage.xtj");
                if (_algorithmProcessor != null)
                {
                    _currentAlgorithm = await _algorithmProcessor.LoadAlgorithmAsync(defaultAlgorithmPath);
                    if (_currentAlgorithm != null)
                    {
                        Console.WriteLine($"已加载默认算法: {_currentAlgorithm.AlgorithmName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载默认算法失败: {ex.Message}");
            }
        }

        public async Task LoadAlgorithmAsync(string algorithmPath)
        {
            if (_algorithmProcessor != null)
            {
                _currentAlgorithm = await _algorithmProcessor.LoadAlgorithmAsync(algorithmPath);
                if (_currentAlgorithm != null)
                {
                    Console.WriteLine($"已加载算法: {_currentAlgorithm.AlgorithmName}");
                }
            }
        }

        private List<DataPoint> ApplyCurrentAlgorithm(List<DataPoint> inputData)
        {
            if (_algorithmProcessor == null || _currentAlgorithm == null)
                return inputData;

            return _algorithmProcessor.ApplyAlgorithm(inputData, _currentAlgorithm, _algorithmParameters);
        }

        public async Task UpdateParameterAsync(string parameterId, object value)
        {
            await Task.CompletedTask;
            _algorithmParameters[parameterId] = value;
        }

        public async Task DeactivateAsync()
        {
            await Task.CompletedTask;

            // 清理算法处理
            if (_signalSourceViewModel != null)
            {
                _signalSourceViewModel.SetAlgorithmProcessor(null);
            }

            // 清理引用
            _signalSourceControl = null;
            _signalSourceViewModel = null;
            _algorithmProcessor = null;
            _currentAlgorithm = null;
            _algorithmParameters.Clear();
        }
    }

    // 方波功能绑定类
    public class SquareWaveFunctionBinding
    {
        private TextBox? _amplitudeTextBox;
        private TextBox? _frequencyTextBox;
        private object? _displayControl; // 通用显示控件（DisplayControl 或 DisplayControl2）
        private IWaveformDisplay? _waveformDisplay; // 波形显示接口
        private DispatcherTimer? _timer;
        private double _phase;

        public async Task InitializeAsync(List<Control> runtimeControls, List<ControlInfo> textBoxInfos, List<ControlInfo> displayInfos)
        {
            await Task.CompletedTask;

            // 按照textBoxInfos的顺序找到对应的TextBox控件
            var textBoxControls = new List<TextBox>();
            var displayControls = new List<Control>(); // 使用通用Control类型

            // 按照设计时的顺序匹配TextBox控件
            foreach (var textBoxInfo in textBoxInfos)
            {
                var textBox = runtimeControls.OfType<TextBox>().FirstOrDefault(tb => 
                    tb.Name == textBoxInfo.Id || tb.Tag?.ToString() == textBoxInfo.Id);
                if (textBox != null)
                {
                    textBoxControls.Add(textBox);
                }
            }
            
            // 找到DisplayControl或DisplayControl2
            foreach (var control in runtimeControls)
            {
                if (control is DisplayControl || control is DisplayControl2)
                {
                    displayControls.Add(control);
                }
            }

            if (textBoxControls.Count >= 2 && displayControls.Count >= 1)
            {
                // 分配角色：第一个TextBox为幅值，第二个为频率
                _amplitudeTextBox = textBoxControls[0];
                _frequencyTextBox = textBoxControls[1];
                _displayControl = displayControls[0];

                // 获取DisplayControl或DisplayControl2的ViewModel并实现IWaveformDisplay接口
                if (_displayControl is DisplayControl displayControl)
                {
                    _waveformDisplay = displayControl.DataContext as DisplayControlViewModel;
                }
                else if (_displayControl is DisplayControl2 displayControl2)
                {
                    _waveformDisplay = displayControl2.DataContext as DisplayControl2ViewModel;
                }

                // 绑定事件
                if (_amplitudeTextBox != null)
                {
                    _amplitudeTextBox.TextChanged += OnParameterChanged;
                }

                if (_frequencyTextBox != null)
                {
                    _frequencyTextBox.TextChanged += OnParameterChanged;
                }

                // 设置初始值并启动动画
                StartSquareWaveDisplay();
            }
        }

        private void OnParameterChanged(object? sender, TextChangedEventArgs e)
        {
            // 当参数变化时，不需要做任何事，因为timer会持续读取最新值
        }

        private void StartSquareWaveDisplay()
        {
            if (_timer != null) return;

            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(30)
            };
            _timer.Tick += OnTimerTick;
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            if (_waveformDisplay == null) return;

            // 解析幅值
            if (!double.TryParse(_amplitudeTextBox?.Text, out double amplitude) || amplitude <= 0)
            {
                amplitude = 25; // 默认值
            }

            // 解析频率
            if (!double.TryParse(_frequencyTextBox?.Text, out double frequency) || frequency <= 0)
            {
                frequency = 0.1; // 默认值
            }

            // 更新幅值
            _waveformDisplay.Amplitude = amplitude;

            // 生成方波数据
            _phase += 0.2; // 控制波形移动
            var points = new List<(double X, double Y)>();
            double width = 600; // 与正弦波控件保持一致的X轴范围

            // 生成方波数据点
            for (double x = 0; x <= width; x += 0.5)
            {
                // 方波计算：使用符号函数生成方波
                double sinValue = Math.Sin(_phase + x * frequency * 2 * Math.PI / 100.0);
                double y = amplitude * Math.Sign(sinValue);
                points.Add((x, y));
            }

            // 更新显示
            _waveformDisplay.UpdateWaveform(points);
        }

        public async Task UpdateParameterAsync(string parameterId, object value)
        {
            // 这个方法现在可以保持为空，因为UI的更新由timer驱动
            await Task.CompletedTask;
        }

        public async Task DeactivateAsync()
        {
            await Task.CompletedTask;

            // 停止计时器
            _timer?.Stop();
            _timer = null;

            // 解绑事件
            if (_amplitudeTextBox != null)
            {
                _amplitudeTextBox.TextChanged -= OnParameterChanged;
            }

            if (_frequencyTextBox != null)
            {
                _frequencyTextBox.TextChanged -= OnParameterChanged;
            }
            
            // 清理显示
            _waveformDisplay?.ClearWaveform();

            // 清理引用
            _amplitudeTextBox = null;
            _frequencyTextBox = null;
            _displayControl = null;
            _waveformDisplay = null;
        }
    }
}