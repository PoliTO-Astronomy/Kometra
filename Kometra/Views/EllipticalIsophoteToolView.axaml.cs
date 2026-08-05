using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kometra.ViewModels.ImageProcessing;
using Kometra.ViewModels.Visualization;

namespace Kometra.Views;

public partial class EllipticalIsophoteToolView : Window
{
    private Line? _lineH;
    private Line? _lineV;
    private Ellipse? _circle;
    private EllipticalIsophoteToolViewModel? _vm;

    public EllipticalIsophoteToolView()
    {
        InitializeComponent();
        
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;

        this.SizeChanged += (s, e) => Dispatcher.UIThread.Post(UpdateCrosshairPosition, DispatcherPriority.Background);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _lineH = this.FindControl<Line>("CrosshairLineH");
        _lineV = this.FindControl<Line>("CrosshairLineV");
        _circle = this.FindControl<Ellipse>("CrosshairCircle");

        var viewportCtrl = this.FindControl<ContentControl>("ViewportControl");
        if (viewportCtrl != null)
        {
            viewportCtrl.SizeChanged += (s, e) => Dispatcher.UIThread.Post(UpdateCrosshairPosition, DispatcherPriority.Background);
        }
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is EllipticalIsophoteToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateUiValues();
            
            Dispatcher.UIThread.Post(UpdateCrosshairPosition, DispatcherPriority.Loaded);
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
                case nameof(EllipticalIsophoteToolViewModel.CenterX):
                    UpdateBox("CenterXBox", _vm.CenterX);
                    UpdateCrosshairPosition();
                    break;
                case nameof(EllipticalIsophoteToolViewModel.CenterY):
                    UpdateBox("CenterYBox", _vm.CenterY);
                    UpdateCrosshairPosition();
                    break;
                case nameof(EllipticalIsophoteToolViewModel.MaxRadius): UpdateBox("MaxRadiusBox", _vm.MaxRadius); break;
                case nameof(EllipticalIsophoteToolViewModel.StepSize): UpdateBox("StepSizeBox", _vm.StepSize); break;
                case nameof(EllipticalIsophoteToolViewModel.Ellipticity): UpdateBox("EllipticityBox", _vm.Ellipticity); break;
                case nameof(EllipticalIsophoteToolViewModel.PositionAngleDeg): UpdateBox("PositionAngleBox", _vm.PositionAngleDeg); break;
                case nameof(EllipticalIsophoteToolViewModel.PreviewRenderer):
                    UpdateCrosshairPosition();
                    break;
                case nameof(EllipticalIsophoteToolViewModel.IsPreviewActive):
                    if (_vm.IsPreviewActive) HideCrosshair();
                    else UpdateCrosshairPosition();
                    break;
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
        UpdateBox("CenterXBox", _vm.CenterX);
        UpdateBox("CenterYBox", _vm.CenterY);
        UpdateBox("MaxRadiusBox", _vm.MaxRadius);
        UpdateBox("StepSizeBox", _vm.StepSize);
        UpdateBox("EllipticityBox", _vm.Ellipticity);
        UpdateBox("PositionAngleBox", _vm.PositionAngleDeg);
    }

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box) return;

        string input = (box.Text ?? string.Empty).Replace(',', '.');
        var culture = CultureInfo.InvariantCulture;

        if (box.Name == "CenterXBox" && double.TryParse(input, NumberStyles.Any, culture, out double cx)) _vm.CenterX = cx;
        else if (box.Name == "CenterYBox" && double.TryParse(input, NumberStyles.Any, culture, out double cy)) _vm.CenterY = cy;
        else if (box.Name == "MaxRadiusBox" && double.TryParse(input, NumberStyles.Any, culture, out double mr)) _vm.MaxRadius = mr;
        else if (box.Name == "StepSizeBox" && double.TryParse(input, NumberStyles.Any, culture, out double st)) _vm.StepSize = st;
        else if (box.Name == "EllipticityBox" && double.TryParse(input, NumberStyles.Any, culture, out double el)) _vm.Ellipticity = el;
        else if (box.Name == "PositionAngleBox" && double.TryParse(input, NumberStyles.Any, culture, out double pa)) _vm.PositionAngleDeg = pa;

        UpdateUiValues();
        UpdateCrosshairPosition();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            this.Focus();
            e.Handled = true;
        }
    }

    private void UpdateCrosshairPosition()
    {
        if (_vm == null || _vm.IsPreviewActive || _lineH == null || _lineV == null || _circle == null)
        {
            HideCrosshair();
            return;
        }

        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null ||
            containerGrid.Bounds.Width <= 0 ||
            containerGrid.Bounds.Height <= 0 ||
            _vm.PreviewRenderer is not FitsRenderer { Image: Bitmap bitmap })
        {
            HideCrosshair();
            return;
        }

        double imgWidth = bitmap.Size.Width;
        double imgHeight = bitmap.Size.Height;
        double ctrlWidth = containerGrid.Bounds.Width;
        double ctrlHeight = containerGrid.Bounds.Height;

        double scale = Math.Min(ctrlWidth / imgWidth, ctrlHeight / imgHeight);
        if (scale <= 0)
        {
            HideCrosshair();
            return;
        }

        double renderWidth = imgWidth * scale;
        double renderHeight = imgHeight * scale;
        double offsetX = (ctrlWidth - renderWidth) / 2.0;
        double offsetY = (ctrlHeight - renderHeight) / 2.0;

        double screenX = offsetX + (_vm.CenterX * scale);
        double screenY = offsetY + (_vm.CenterY * scale);

        if (screenX < offsetX || screenX > offsetX + renderWidth ||
            screenY < offsetY || screenY > offsetY + renderHeight)
        {
            HideCrosshair();
            return;
        }

        _lineH.StartPoint = new Point(offsetX, screenY);
        _lineH.EndPoint = new Point(offsetX + renderWidth, screenY);
        _lineH.IsVisible = true;

        _lineV.StartPoint = new Point(screenX, offsetY);
        _lineV.EndPoint = new Point(screenX, offsetY + renderHeight);
        _lineV.IsVisible = true;

        double circleRadius = _circle.Width / 2.0;
        Canvas.SetLeft(_circle, screenX - circleRadius);
        Canvas.SetTop(_circle, screenY - circleRadius);
        _circle.IsVisible = true;
    }

    private void HideCrosshair()
    {
        if (_lineH != null) _lineH.IsVisible = false;
        if (_lineV != null) _lineV.IsVisible = false;
        if (_circle != null) _circle.IsVisible = false;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm == null || _vm.IsPreviewActive) return;

        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null ||
            containerGrid.Bounds.Width <= 0 ||
            containerGrid.Bounds.Height <= 0 ||
            _vm.PreviewRenderer is not FitsRenderer { Image: Bitmap bitmap })
        {
            return;
        }

        Point clickPoint = e.GetPosition(containerGrid);

        double imgWidth = bitmap.Size.Width;
        double imgHeight = bitmap.Size.Height;
        double ctrlWidth = containerGrid.Bounds.Width;
        double ctrlHeight = containerGrid.Bounds.Height;

        double scale = Math.Min(ctrlWidth / imgWidth, ctrlHeight / imgHeight);
        if (scale <= 0) return;

        double renderWidth = imgWidth * scale;
        double renderHeight = imgHeight * scale;
        double offsetX = (ctrlWidth - renderWidth) / 2.0;
        double offsetY = (ctrlHeight - renderHeight) / 2.0;

        double relX = clickPoint.X - offsetX;
        double relY = clickPoint.Y - offsetY;

        if (relX < 0 || relX > renderWidth || relY < 0 || relY > renderHeight)
            return;

        double fitsX = Math.Clamp(relX / scale, 0, imgWidth - 1);
        double fitsY = Math.Clamp(relY / scale, 0, imgHeight - 1);

        _ = _vm.OnImageClickedAsync(new Point(fitsX, fitsY));
        UpdateCrosshairPosition();
    }

    private void OnControlsPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}