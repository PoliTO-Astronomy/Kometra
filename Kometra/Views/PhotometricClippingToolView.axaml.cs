using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Kometra.Views;

public partial class PhotometricClippingToolView : Window
{
    public PhotometricClippingToolView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}