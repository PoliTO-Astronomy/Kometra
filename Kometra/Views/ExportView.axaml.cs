using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using Kometra.ViewModels.ImportExport;

namespace Kometra.Views;

public partial class ExportView : Window
{
    private bool _isPanning;
    private Point? _lastPointerPos;
    private ExportViewModel? _vm;

    public ExportView()
    {
        InitializeComponent();
        
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;

        var previewBorder = this.FindControl<Border>("PreviewBorder");
        if(previewBorder != null)
        {
             previewBorder.SizeChanged += OnPreviewSizeChanged;
        }
    }

    private async void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExportViewModel vm)
        {
            _vm = vm;
            _vm.RequestClose += Close;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            try 
            {
                await _vm.ImageLoadedTcs.Task;
                CenterImage();
            }
            catch { }
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.RequestClose -= Close;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Controlliamo che l'immagine sia stata ricaricata (sia essa FITS o PNG)
        if (e.PropertyName == nameof(ExportViewModel.ActiveRenderer) || 
            e.PropertyName == nameof(ExportViewModel.StaticImage))
        {
            Dispatcher.UIThread.Post(() => CenterImage(), DispatcherPriority.Background);
        }
    }

    private void OnPreviewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_vm != null && e.NewSize.Width > 0 && e.NewSize.Height > 0) 
        { 
            _vm.Viewport.ViewportSize = e.NewSize; 
            if (_vm.ActiveRenderer != null || _vm.StaticImage != null)
            {
                CenterImage();
            }
        }
    }

    private void CenterImage()
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && border != null && border.Bounds.Width > 0 && border.Bounds.Height > 0) 
        { 
            _vm.Viewport.ViewportSize = border.Bounds.Size; 
            _vm.Viewport.ResetView(); 
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var border = sender as Control;
        if (border == null || (!_vm?.IsNavigationUIVisible ?? true)) return;

        var properties = e.GetCurrentPoint(border).Properties;
        
        bool isMiddlePan = properties.IsMiddleButtonPressed;
        bool isAltPan = properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (isMiddlePan || isAltPan)
        {
            _isPanning = true;
            _lastPointerPos = e.GetPosition(border);
            e.Pointer.Capture(border);
            this.Cursor = new Cursor(StandardCursorType.SizeAll);
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && (e.InitialPressMouseButton == MouseButton.Middle || e.InitialPressMouseButton == MouseButton.Left))
        {
            _isPanning = false;
            _lastPointerPos = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
        }
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || _vm == null || _lastPointerPos == null) return;
        
        var border = sender as Control;
        if (border == null) return;

        var props = e.GetCurrentPoint(border).Properties;

        if (!props.IsMiddleButtonPressed && !props.IsLeftButtonPressed)
        {
            _isPanning = false;
            _lastPointerPos = null;
            e.Pointer.Capture(null);
            this.Cursor = Cursor.Default;
            return;
        }
        
        var pos = e.GetPosition(border);
        var delta = pos - _lastPointerPos.Value;
        _lastPointerPos = pos;
        
        _vm.Viewport.ApplyPan(delta.X, delta.Y);
    }

    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_vm == null || (!_vm.IsNavigationUIVisible)) return;
        
        var visual = e.Source as Visual;
        if (visual == null) return;

        var modifiers = e.KeyModifiers;

        double effectiveDelta = Math.Abs(e.Delta.Y) > Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
        if (Math.Abs(effectiveDelta) < 0.0001) return;
        
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta))
        {
            var border = sender as Control;
            double zoomFactor = effectiveDelta > 0 ? 1.1 : 1.0 / 1.1;
            _vm.Viewport.ApplyZoomAtPoint(zoomFactor, e.GetPosition(border));
            e.Handled = true;
            return;
        }

        // Modifica dei limiti FITS disabilitata per immagini statiche PNG
        if (_vm.ActiveRenderer == null) return; 

        double currentRange = Math.Abs(_vm.CurrentWhitePoint - _vm.CurrentBlackPoint);
        double baseStep = (currentRange > 0.00001) ? currentRange * 0.05 : 1.0;
        double step = Math.Max(0.0001, baseStep); 

        if (effectiveDelta < 0) step = -step;

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            double newBlack = _vm.CurrentBlackPoint + step;
            if (step > 0)
            {
                double maxAllowed = _vm.CurrentWhitePoint - (step * 0.1);
                _vm.CurrentBlackPoint = Math.Clamp(Math.Min(newBlack, maxAllowed), _vm.DataMin, _vm.DataMax);
            }
            else
            {
                _vm.CurrentBlackPoint = Math.Max(_vm.DataMin, newBlack);
            }
        }
        else
        {
            double newWhite = _vm.CurrentWhitePoint + step;
            if (step < 0)
            {
                double minAllowed = _vm.CurrentBlackPoint + (Math.Abs(step) * 0.1);
                _vm.CurrentWhitePoint = Math.Clamp(Math.Max(newWhite, minAllowed), _vm.DataMin, _vm.DataMax);
            }
            else
            {
                _vm.CurrentWhitePoint = Math.Min(_vm.DataMax, newWhite);
            }
        }

        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && border != null) 
            _vm.Viewport.ApplyZoomAtPoint(1.2, border.Bounds.Center);
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        var border = this.FindControl<Border>("PreviewBorder");
        if (_vm != null && border != null) 
            _vm.Viewport.ApplyZoomAtPoint(1.0/1.2, border.Bounds.Center);
    }

    private void OnFitViewClicked(object? sender, RoutedEventArgs e)
    {
        CenterImage();
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnComboBoxPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }
}