using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NewAvalonia.Views;

namespace NewAvalonia.Controls
{
    public partial class SimplifiedWaveformDisplay : UserControl
    {
        private AlgorithmCapableTcpRealtimeWaveformControl? _tcpWaveformControl;

        public SimplifiedWaveformDisplay()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);

            // 查找控件（只保留TCP波形控件）
            _tcpWaveformControl = this.FindControl<AlgorithmCapableTcpRealtimeWaveformControl>("TcpWaveformControl");
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
        }
    }
}
