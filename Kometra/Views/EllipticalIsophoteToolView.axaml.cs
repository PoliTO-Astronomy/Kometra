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
using Kometra.Infrastructure;

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
        if (box != null) 
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

    private void CommitValue(NumericUpDown box, double newValue)
    {
        if (_vm == null) return;
        bool hasChanged = false;

        if (box.Name == "MinValueBox" && Math.Abs(_vm.MinValue - newValue) > 0.001) 
        { 
            _vm.MinValue = newValue; 
            hasChanged = true; 
        }
        else if (box.Name == "MaxValueBox" && Math.Abs(_vm.MaxValue - newValue) > 0.001) 
        { 
            _vm.MaxValue = newValue; 
            hasChanged = true; 
        }
        else if (box.Name == "StepSizeBox" && Math.Abs(_vm.StepSize - newValue) > 0.001) 
        { 
            _vm.StepSize = newValue; 
            hasChanged = true; 
        }

        if (hasChanged)
        {
            _vm.TriggerCalculation();
        }
    }

    private void OnBoxValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box || !e.NewValue.HasValue) return;

        double newValue = (double)e.NewValue.Value;
        double oldValue = e.OldValue.HasValue ? (double)e.OldValue.Value : 0;
        double diff = Math.Abs(newValue - oldValue);

        if (box.IsKeyboardFocusWithin && Math.Abs(diff - (double)box.Increment) > 0.0001)
        {
            return;
        }

        CommitValue(box, newValue);
    }

    private void OnInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is NumericUpDown box && box.Value.HasValue)
        {
            CommitValue(box, (double)box.Value.Value);
        }
        UpdateUiValues(); 
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is NumericUpDown box && box.Value.HasValue)
            {
                CommitValue(box, (double)box.Value.Value);
            }
            this.Focus();
            e.Handled = true;
        }
    }
}