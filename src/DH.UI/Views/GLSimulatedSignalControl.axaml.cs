using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;
using Avalonia.Platform.Storage;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public partial class GLSimulatedSignalControl : UserControl
    {
        private GLSimulatedSignalCanvas? _signalCanvas;

        public GLSimulatedSignalControl()
        {
            InitializeComponent();
            InitializeControl();
        }

        private void InitializeControl()
        {
            // 设置数据上下文
            var viewModel = new GLSimulatedSignalViewModel();
            DataContext = viewModel;
            
            _signalCanvas = this.FindControl<GLSimulatedSignalCanvas>("SignalGLCanvas");

            // 绑定算法选择和外部算法相关的 UI 元素
            var cmbAlgorithmType = this.FindControl<ComboBox>("cmbAlgorithmType");
            var txtAlgorithmFile = this.FindControl<TextBox>("txtAlgorithmFile");
            var btnBrowseAlgorithm = this.FindControl<Button>("btnBrowseAlgorithm");
            var btnTestAlgorithm = this.FindControl<Button>("btnTestAlgorithm");

            // 绑定外部算法文件路径
            if (txtAlgorithmFile != null && DataContext is GLSimulatedSignalViewModel vm)
            {
                txtAlgorithmFile.Bind(
                    TextBox.TextProperty,
                    new Avalonia.Data.Binding("ExternalAlgorithmPath", Avalonia.Data.BindingMode.TwoWay)
                );
            }

            // 绑定“浏览”按钮事件
            if (btnBrowseAlgorithm != null)
            {
                btnBrowseAlgorithm.Click += async (s, e) =>
                {
                    if (DataContext is GLSimulatedSignalViewModel viewModel)
                    {
                        var topLevel = TopLevel.GetTopLevel(this);
                        if (topLevel != null)
                        {
                            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                            {
                                Title = "选择外部算法文件",
                                AllowMultiple = false,
                                FileTypeFilter = new[] {
                                    new FilePickerFileType("算法文件") { Patterns = new[] { "*.xtj", "*.xtjs", "*.dll" } },
                                    new FilePickerFileType("所有文件") { Patterns = new[] { "*" } }
                                }
                            });

                            if (files != null && files.Count > 0)
                            {
                                viewModel.ExternalAlgorithmPath = files[0].Path.LocalPath;
                            }
                        }
                    }
                };
            }

            // 绑定“测试”按钮事件
            if (btnTestAlgorithm != null)
            {
                btnTestAlgorithm.Click += (s, e) =>
                {
                    if (DataContext is GLSimulatedSignalViewModel viewModel)
                    {
                        // 直接调用重新加载方法，不使用确认对话框以避免 API 不匹配问题
                        viewModel.ReloadExternalAlgorithm();
                    }
                };
            }
        }
    }
}