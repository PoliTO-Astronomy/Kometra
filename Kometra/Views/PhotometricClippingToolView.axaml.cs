using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kometra.ViewModels.ImageProcessing;

namespace Kometra.Views;

public partial class PhotometricClippingToolView : Window
{
    private PhotometricClippingToolViewModel? _vm;

    public PhotometricClippingToolView()
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
        if (DataContext is PhotometricClippingToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateUiValues();
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_vm == null) return;
            switch (e.PropertyName)
            {
                case nameof(PhotometricClippingToolViewModel.LowerSigma):
                    UpdateBox("LowerSigmaBox", _vm.LowerSigma, "F1");
                    break;
                case nameof(PhotometricClippingToolViewModel.UpperSigma):
                    UpdateBox("UpperSigmaBox", _vm.UpperSigma, "F1");
                    break;
                case nameof(PhotometricClippingToolViewModel.MinAdu):
                    UpdateBox("MinAduBox", _vm.MinAdu, "F0");
                    break;
                case nameof(PhotometricClippingToolViewModel.MaxAdu):
                    UpdateBox("MaxAduBox", _vm.MaxAdu, "F0");
                    break;
            }
        });
    }

    private void UpdateBox(string name, double value, string format)
    {
        var box = this.FindControl<TextBox>(name);
        if (box != null && !box.IsFocused)
        {
            box.Text = value.ToString(format, CultureInfo.InvariantCulture);
        }
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        UpdateBox("LowerSigmaBox", _vm.LowerSigma, "F1");
        UpdateBox("UpperSigmaBox", _vm.UpperSigma, "F1");
        UpdateBox("MinAduBox", _vm.MinAdu, "F0");
        UpdateBox("MaxAduBox", _vm.MaxAdu, "F0");
    }

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not TextBox box) return;

        string input = (box.Text ?? string.Empty).Replace(',', '.');
        var culture = CultureInfo.InvariantCulture;

        if (box.Name == "LowerSigmaBox" && double.TryParse(input, NumberStyles.Any, culture, out double ls))
            _vm.LowerSigma = ls;
        else if (box.Name == "UpperSigmaBox" && double.TryParse(input, NumberStyles.Any, culture, out double us))
            _vm.UpperSigma = us;
        else if (box.Name == "MinAduBox" && double.TryParse(input, NumberStyles.Any, culture, out double mia))
            _vm.MinAdu = mia;
        else if (box.Name == "MaxAduBox" && double.TryParse(input, NumberStyles.Any, culture, out double mxa))
            _vm.MaxAdu = mxa;

        UpdateUiValues();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus();
            e.Handled = true;
        }
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}