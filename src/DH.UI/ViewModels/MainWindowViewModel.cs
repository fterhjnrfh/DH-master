using System.ComponentModel;

namespace NewAvalonia.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private string _title = "NewAvalonia Application";
    
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }
    
    public string Greeting => "Welcome to Avalonia!";
}