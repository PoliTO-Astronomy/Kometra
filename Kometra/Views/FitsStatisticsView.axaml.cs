using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Kometra.ViewModels.Fits;

namespace Kometra.Views;

public partial class FitsStatisticsView : Window
{
    private FitsStatisticsViewModel? _vm;

    public FitsStatisticsView()
    {
        InitializeComponent();
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is FitsStatisticsViewModel vm)
        {
            _vm = vm;
            _vm.RequestClose += Close;
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RequestClose -= Close;
            _vm.Dispose();
            _vm = null;
        }
    }
}