using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Data;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public partial class NewOpenGLSinWaveControl : UserControl
    {
        public NewOpenGLSinWaveControl()
        {
            InitializeComponent();
            var viewModel = new NewOpenGLSinWaveViewModel();
            DataContext = viewModel;
            
            // 获取控件并设置属性绑定
            var openGLWaveView = this.FindControl<OpenGLSinWaveControl>("OpenGLWaveView");
            if (openGLWaveView != null)
            {
                // 设置绑定以连接控件和ViewModel属性
                openGLWaveView[!OpenGLSinWaveControl.AmplitudeProperty] = 
                    new Binding("Amplitude");
                openGLWaveView[!OpenGLSinWaveControl.FrequencyProperty] = 
                    new Binding("Frequency");
                openGLWaveView[!OpenGLSinWaveControl.SpeedProperty] = 
                    new Binding("Speed");
                openGLWaveView[!OpenGLSinWaveControl.SelectedColorIndexProperty] = 
                    new Binding("SelectedColorIndex");
                openGLWaveView[!OpenGLSinWaveControl.PhaseProperty] = 
                    new Binding("Phase");
            }
        }
    }
}