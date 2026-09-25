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
    private bool _isPanning;
    private Point? _lastPointerPosForPanning;

    public EllipticalIsophoteToolView()
    {
        InitializeComponent();
        this.AddHandler(PointerPressedEvent, OnWindowPointerPressed_Global, RoutingStrategies.Tunnel, handledEventsToo: true);
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
            
            // FIX: Tornati all'aggancio pulito e identico allo StarMaskingView
            _vm.RequestClose += Close; 
            
            UpdateUiValues();
            CenterImage();
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.RequestClose -= Close;
            _vm.Dispose();
            _vm = null;
        }
    }

    private void CenterImage()
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && previewBorder != null && previewBorder.Bounds.Width > 0)
        {
            _vm.Viewport.ViewportSize = previewBorder.Bounds.Size;
            if (_vm.Viewport.ImageSize.Width > 0) _vm.Viewport.ResetView();
        }
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null) _vm.Viewport.ViewportSize = e.NewSize;
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (previewBorder == null) return;

        var props = e.GetCurrentPoint(previewBorder).Properties;
        bool isMiddlePan = props.IsMiddleButtonPressed;
        bool isAltPan = props.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (isMiddlePan || isAltPan)
        {
            _lastPointerPosForPanning = e.GetPosition(previewBorder);
            _isPanning = true;
            e.Pointer.Capture(previewBorder);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (!_isPanning || _vm == null || previewBorder == null || _lastPointerPosForPanning == null) return;

        var props = e.GetCurrentPoint(previewBorder).Properties;
        if (!props.IsMiddleButtonPressed && !props.IsLeftButtonPressed)
        {
            _isPanning = false;
            _lastPointerPosForPanning = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }

        var currentPos = e.GetPosition(previewBorder);
        var delta = currentPos - _lastPointerPosForPanning.Value;
        _lastPointerPosForPanning = currentPos;
        _vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (_vm == null || previewBorder == null) return;
        
        double effectiveDelta = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(effectiveDelta) < 0.0001) return;

        var mousePos = e.GetPosition(previewBorder);
        double factor = effectiveDelta > 0 ? 1.1 : (1.0 / 1.1);
        _vm.Viewport.ApplyZoomAtPoint(factor, mousePos);
        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && previewBorder != null) _vm.Viewport.ApplyZoomAtPoint(1.2, previewBorder.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && previewBorder != null) _vm.Viewport.ApplyZoomAtPoint(1.0 / 1.2, previewBorder.Bounds.Center);
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
    private void OnWindowPointerPressed_Global(object? sender, PointerPressedEventArgs e) => this.Focus();

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