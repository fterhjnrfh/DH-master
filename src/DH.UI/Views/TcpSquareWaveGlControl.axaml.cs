using Avalonia.Controls;
using Avalonia.Threading;

namespace NewAvalonia.Views
{
    public partial class TcpSquareWaveGlControl : UserControl
    {
        private TextBlock? _windowLabel;
        private TcpSquareWaveGlCanvas? _canvas;

        public TcpSquareWaveGlControl()
        {
            InitializeComponent();
            _windowLabel = this.FindControl<TextBlock>("txtWindowRange");
            _canvas = this.FindControl<TcpSquareWaveGlCanvas>("SignalCanvas");
            if (_canvas != null)
            {
                _canvas.WindowRangeChanged += OnWindowRangeChanged;
                OnWindowRangeChanged(_canvas.CurrentWindowStart, _canvas.CurrentWindowEnd);
            }
        }

        private void OnWindowRangeChanged(double start, double end)
        {
            if (_windowLabel == null)
                return;
            var text = $"当前时间段：{start:0}ms - {end:0}ms";
            Dispatcher.UIThread.Post(() => _windowLabel.Text = text);
        }

        protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (_canvas != null)
            {
                _canvas.WindowRangeChanged -= OnWindowRangeChanged;
            }
            base.OnDetachedFromVisualTree(e);
        }
    }
}
