using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kometra.ViewModels.ImageProcessing;

namespace Kometra.Views;

public partial class EllipticalIsophoteToolView : Window
{
    private EllipticalIsophoteToolViewModel? _vm;

    public EllipticalIsophoteToolView()
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
        if (DataContext is EllipticalIsophoteToolViewModel vm)
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
                case nameof(EllipticalIsophoteToolViewModel.MinValue): UpdateBox("MinValueBox", _vm.MinValue); break;
                case nameof(EllipticalIsophoteToolViewModel.MaxValue): UpdateBox("MaxValueBox", _vm.MaxValue); break;
                case nameof(EllipticalIsophoteToolViewModel.StepSize): UpdateBox("StepSizeBox", _vm.StepSize); break;
            }
        });
    }

    private void UpdateBox(string name, double value)
    {
        var box = this.FindControl<NumericUpDown>(name);
        if (box != null && !box.IsFocused)
        {
            box.Value = (decimal)value;
            box.Text = value.ToString(CultureInfo.CurrentCulture);
        }
    }

    private void UpdateUiValues()
    {
        if (_vm == null) return;
        UpdateBox("MinValueBox", _vm.MinValue);
        UpdateBox("MaxValueBox", _vm.MaxValue);
        UpdateBox("StepSizeBox", _vm.StepSize);
    }

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box) return;

        string input = (box.Text ?? string.Empty).Replace(',', '.');
        var culture = CultureInfo.InvariantCulture;

        if (string.IsNullOrWhiteSpace(input) || !double.TryParse(input, NumberStyles.Any, culture, out double parsedValue))
        {
            UpdateUiValues();
            return;
        }

        if (box.Name == "MinValueBox") _vm.MinValue = parsedValue;
        else if (box.Name == "MaxValueBox") _vm.MaxValue = parsedValue;
        else if (box.Name == "StepSizeBox") _vm.StepSize = parsedValue;

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
}