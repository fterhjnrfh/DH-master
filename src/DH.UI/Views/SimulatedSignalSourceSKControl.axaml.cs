using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NewAvalonia.ViewModels;
using System.Linq;

namespace NewAvalonia.Views
{
    public partial class SimulatedSignalSourceSKControl : UserControl
    {
        private SKSimulatedSignalCanvas? _signalCanvas;

        public SimulatedSignalSourceSKControl()
        { 
            InitializeComponent();
            var viewModel = new SimulatedSignalSourceViewModel();
            DataContext = viewModel;
            
            // 在控件加载完成后设置画布的ViewModel
            this.AttachedToVisualTree += (s, e) => 
            {
                // 延迟一小段时间确保控件完全初始化
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _signalCanvas = this.FindControl<SKSimulatedSignalCanvas>("SignalSKCanvas");
                    if (_signalCanvas != null && DataContext is SimulatedSignalSourceViewModel vm)
                    {
                        _signalCanvas.SetViewModel(vm);
                    }
                }, DispatcherPriority.Loaded);
            };
            
            // 更新算法选择下拉菜单，添加动态库算法选项
            var algorithmCombo = this.FindControl<ComboBox>("cmbAlgorithmType");
            if (algorithmCombo != null)
            {
                // 清空现有选项
                algorithmCombo.Items.Clear();
                
                // 添加所有可用的算法选项（包括动态库算法）
                var algorithmOptions = viewModel.GetAlgorithmOptions();
                foreach (var option in algorithmOptions)
                {
                    algorithmCombo.Items.Add(new ComboBoxItem { Content = option });
                }
                
                // 设置默认选中项
                algorithmCombo.SelectedIndex = 0;
            }
            
            // 绑定浏览按钮事件
            var browseButton = this.FindControl<Button>("btnBrowseAlgorithm");
            if (browseButton != null)
            {
                browseButton.Click += BrowseAlgorithm_Click;
            }

            // 绑定测试按钮事件
            var testButton = this.FindControl<Button>("btnTestAlgorithm");
            if (testButton != null)
            {
                testButton.Click += TestAlgorithm_Click;
            }
        }

        private async void BrowseAlgorithm_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new FilePickerOpenOptions
                {
                    Title = "选择算法文件 (.xtj/.xtjs/.dll)",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("算法文件")
                        {
                            Patterns = new[] { "*.xtj", "*.xtjs", "*.dll" }
                        }
                    }
                };

                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel != null)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(openFileDialog);

                    if (files.Count > 0)
                    {
                        var selectedFile = files[0];
                        if (DataContext is SimulatedSignalSourceViewModel viewModel)
                        {
                            viewModel.ExternalAlgorithmPath = selectedFile.Path.LocalPath;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"浏览算法文件时发生错误: {ex.Message}");
            }
        }

        private void TestAlgorithm_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (DataContext is SimulatedSignalSourceViewModel viewModel)
                {
                    viewModel.ReloadExternalAlgorithm();
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"测试算法时发生错误: {ex.Message}");
            }
        }

        // 添加参数更改通知方法
        public void NotifyParameterChanged(string parameterName, object value)
        {
            if (DataContext is SimulatedSignalSourceViewModel viewModel)
            {
                viewModel.GetType().GetProperty(parameterName)?.SetValue(viewModel, value);
            }
        }

        // 获取当前参数值的方法
        public object? GetParameterValue(string parameterName)
        {
            if (DataContext is SimulatedSignalSourceViewModel viewModel)
            {
                return viewModel.GetType().GetProperty(parameterName)?.GetValue(viewModel);
            }
            return null;
        }
    }
}