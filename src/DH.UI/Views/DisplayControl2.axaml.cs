using Avalonia.Controls;
using NewAvalonia.ViewModels;

namespace NewAvalonia.Views
{
    public partial class DisplayControl2 : UserControl
    {
        public DisplayControl2()
        {
            InitializeComponent();
            DataContext = new DisplayControl2ViewModel();
        }
    }
}