using Avalonia.Controls;
using Avalonia.Threading;

namespace NewAvalonia.Views
{
    public partial class TcpSquareWaveSkiaControl : UserControl
    {
        private TextBlock? _windowInfo;
        private TcpSquareWaveSkiaCanvas? _canvas;

        public TcpSquareWaveSkiaControl()
        {
            InitializeComponent();
            _windowInfo = this.FindControl<TextBlock>("txtWindowRange");
            _canvas = this.FindControl<TcpSquareWaveSkiaCanvas>("TcpCanvas");
            if (_canvas != null)
            {
                _canvas.WindowRangeChanged += OnWindowRangeChanged;
                UpdateWindowLabel(_canvas.CurrentWindowStart, _canvas.CurrentWindowEnd);
            }
        }

        private void OnWindowRangeChanged(double start, double end)
        {
            UpdateWindowLabel(start, end);
        }

        private void UpdateWindowLabel(double start, double end)
        {
            if (_windowInfo == null) return;
            var text = $"当前时间段：{start:0}ms - {end:0}ms";
            Dispatcher.UIThread.Post(() => _windowInfo.Text = text);
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
