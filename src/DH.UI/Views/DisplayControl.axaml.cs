using Avalonia.Controls;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public partial class DisplayControl : UserControl
    {
        public DisplayControl()
        {
            InitializeComponent();
            DataContext = new DisplayControlViewModel();
        }
    }
}