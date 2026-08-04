using Microsoft.UI.Xaml;
using Sintek.Mail.App.ViewModels;

namespace Sintek.Mail.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }

    public MainWindow()
    {
        this.InitializeComponent();
        ViewModel = ((App)Microsoft.UI.Xaml.Application.Current).GetService<MainViewModel>();
    }
}
