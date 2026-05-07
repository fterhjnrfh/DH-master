using Avalonia.Controls;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public partial class NewOxyPlotSinWaveControl : UserControl
    {
        public NewOxyPlotSinWaveControl()
        {
            InitializeComponent();
            DataContext = new NewOxyPlotSinWaveViewModel();
        }
    }
}