using Microsoft.UI.Xaml.Controls;
using Sintek.Mail.App.ViewModels;

namespace Sintek.Mail.App;

public sealed partial class MainPage : Page
{
    public MainPageViewModel ViewModel { get; } = new();

    public MainPage()
    {
        InitializeComponent();
    }
}
