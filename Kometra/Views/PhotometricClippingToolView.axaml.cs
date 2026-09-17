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
using Kometra.ViewModels.ImageProcessing;
using Kometra.ViewModels.Visualization;

namespace Kometra.Views;

public partial class PhotometricClippingToolView : Window
{
    private Line? _cutLine;
    private PhotometricClippingToolViewModel? _vm;
    private bool _isDragging = false;

    public PhotometricClippingToolView()
    {
        InitializeComponent();
        
        this.Loaded += OnWindowLoaded;
        this.Unloaded += OnWindowUnloaded;
        this.SizeChanged += (s, e) => Dispatcher.UIThread.Post(UpdateLineOverlay, DispatcherPriority.Background);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _cutLine = this.FindControl<Line>("CutLineOverlay");

        var viewportCtrl = this.FindControl<ContentControl>("ViewportControl");
        if (viewportCtrl != null)
        {
            viewportCtrl.SizeChanged += (s, e) => Dispatcher.UIThread.Post(UpdateLineOverlay, DispatcherPriority.Background);
        }
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PhotometricClippingToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.ProfileData.CollectionChanged += (s, ev) => Dispatcher.UIThread.Post(UpdatePlot);
            _vm.SavePlotRequested += OnSavePlotRequested;

            UpdateUiValues();
            UpdatePlot();
            Dispatcher.UIThread.Post(UpdateLineOverlay, DispatcherPriority.Loaded);
        }
    }

    private void OnWindowUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.SavePlotRequested -= OnSavePlotRequested;
            _vm = null;
        }
    }

    private void OnSavePlotRequested(string filePath)
    {
        var plotControl = this.FindControl<ScottPlot.Avalonia.AvaPlot>("ProfilePlot");
        plotControl?.Plot.SavePng(filePath, 1200, 800);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_vm == null) return;
            switch (e.PropertyName)
            {
                case nameof(PhotometricClippingToolViewModel.StartX):
                    UpdateBox("StartXBox", _vm.StartX); UpdateLineOverlay(); break;
                case nameof(PhotometricClippingToolViewModel.StartY):
                    UpdateBox("StartYBox", _vm.StartY); UpdateLineOverlay(); break;
                case nameof(PhotometricClippingToolViewModel.EndX):
                    UpdateBox("EndXBox", _vm.EndX); UpdateLineOverlay(); break;
                case nameof(PhotometricClippingToolViewModel.EndY):
                    UpdateBox("EndYBox", _vm.EndY); UpdateLineOverlay(); break;
                case nameof(PhotometricClippingToolViewModel.Viewport):
                    UpdateLineOverlay(); break;
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
        UpdateBox("StartXBox", _vm.StartX);
        UpdateBox("StartYBox", _vm.StartY);
        UpdateBox("EndXBox", _vm.EndX);
        UpdateBox("EndYBox", _vm.EndY);
    }

    private void CommitValue(NumericUpDown box, double newValue)
    {
        if (_vm == null) return;
        bool hasChanged = false;
        int val = (int)newValue;

        if (box.Name == "StartXBox" && _vm.StartX != val) 
        { 
            _vm.StartX = val; 
            hasChanged = true; 
        }
        else if (box.Name == "StartYBox" && _vm.StartY != val) 
        { 
            _vm.StartY = val; 
            hasChanged = true; 
        }
        else if (box.Name == "EndXBox" && _vm.EndX != val) 
        { 
            _vm.EndX = val; 
            hasChanged = true; 
        }
        else if (box.Name == "EndYBox" && _vm.EndY != val) 
        { 
            _vm.EndY = val; 
            hasChanged = true; 
        }

        if (hasChanged && !_vm.IsDragging)
        {
            _ = _vm.CalculateProfileAsync();
        }
    }

    private void OnBoxValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box || !e.NewValue.HasValue) return;

        double newValue = (double)e.NewValue.Value;
        double oldValue = e.OldValue.HasValue ? (double)e.OldValue.Value : 0;
        double diff = Math.Abs(newValue - oldValue);

        double increment = (double)(box.Increment > 0 ? box.Increment : 1);

        // Se l'input proviene dalla tastiera, non eseguire il calcolo continuo
        if (box.IsKeyboardFocusWithin && Math.Abs(diff - increment) > 0.0001)
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
        // Ripristina l'interfaccia senza mostrare errori in caso di testo vuoto
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

    private void UpdatePlot()
    {
        var plotControl = this.FindControl<ScottPlot.Avalonia.AvaPlot>("ProfilePlot");
        if (plotControl == null || _vm == null) return;

        plotControl.Plot.Clear();
        
        plotControl.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
        plotControl.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
        plotControl.Plot.Axes.Color(ScottPlot.Color.FromHex("#000000"));
        plotControl.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E0E0E0");

        if (_vm.ProfileData.Count > 0)
        {
            double[] xs = new double[_vm.ProfileData.Count];
            double[] ys = new double[_vm.ProfileData.Count];

            for (int i = 0; i < _vm.ProfileData.Count; i++)
            {
                xs[i] = _vm.ProfileData[i].Distance;
                ys[i] = _vm.ProfileData[i].Value;
            }

            var line = plotControl.Plot.Add.ScatterLine(xs, ys, ScottPlot.Color.FromHex("#8058E8")); 
            line.LineWidth = 2.5f; 
            
            plotControl.Plot.Axes.Title.Label.Text = "\nTaglio Fotometrico\n";
            plotControl.Plot.Axes.Title.Label.FontSize = 20;

            plotControl.Plot.Axes.Bottom.Label.Text = "Distanza [px]";
            plotControl.Plot.Axes.Bottom.Label.FontSize = 16;
            plotControl.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;

            plotControl.Plot.Axes.Left.Label.Text = "\nIntensità [ADU]\n"; 
            plotControl.Plot.Axes.Left.Label.FontSize = 16;
            plotControl.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
            plotControl.Plot.Axes.Left.MinimumSize = 80; 
            
            var yTickGen = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 12 };
            plotControl.Plot.Axes.Left.TickGenerator = yTickGen;

            plotControl.Plot.Axes.AutoScale();
        }
        plotControl.Refresh();
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = GetFitsCoordinates(e);
        if (pos == null) return;

        _isDragging = true;
        _vm.IsDragging = true; 
        
        _vm.StartX = (int)pos.Value.X;
        _vm.StartY = (int)pos.Value.Y;
        _vm.EndX = (int)pos.Value.X;
        _vm.EndY = (int)pos.Value.Y;
        
        UpdateLineOverlay();
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _vm == null) return;

        var pos = GetFitsCoordinates(e);
        if (pos == null) return;

        _vm.EndX = (int)pos.Value.X;
        _vm.EndY = (int)pos.Value.Y;
        
        UpdateLineOverlay(); 
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging || _vm == null) return;

        _isDragging = false;
        _vm.IsDragging = false;
        
        _ = _vm.CalculateProfileAsync();
    }

    private Point? GetFitsCoordinates(PointerEventArgs e)
    {
        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null || _vm?.Viewport is not FitsRenderer { Image: Bitmap bitmap }) return null;

        Point clickPoint = e.GetPosition(containerGrid);
        double scale = Math.Min(containerGrid.Bounds.Width / bitmap.Size.Width, containerGrid.Bounds.Height / bitmap.Size.Height);
        if (scale <= 0) return null;

        double renderWidth = bitmap.Size.Width * scale;
        double renderHeight = bitmap.Size.Height * scale;
        double offsetX = (containerGrid.Bounds.Width - renderWidth) / 2.0;
        double offsetY = (containerGrid.Bounds.Height - renderHeight) / 2.0;

        double relX = clickPoint.X - offsetX;
        double relY = clickPoint.Y - offsetY;

        return new Point(
            Math.Clamp(relX / scale, 0, bitmap.Size.Width - 1),
            Math.Clamp(relY / scale, 0, bitmap.Size.Height - 1));
    }

    private void UpdateLineOverlay()
    {
        if (_vm == null || _cutLine == null)
        {
            if (_cutLine != null) _cutLine.IsVisible = false;
            return;
        }

        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null || containerGrid.Bounds.Width <= 0 || _vm.Viewport is not FitsRenderer { Image: Bitmap bitmap })
        {
            _cutLine.IsVisible = false;
            return;
        }

        double scale = Math.Min(containerGrid.Bounds.Width / bitmap.Size.Width, containerGrid.Bounds.Height / bitmap.Size.Height);
        if (scale <= 0) return;

        double offsetX = (containerGrid.Bounds.Width - (bitmap.Size.Width * scale)) / 2.0;
        double offsetY = (containerGrid.Bounds.Height - (bitmap.Size.Height * scale)) / 2.0;

        _cutLine.StartPoint = new Point(offsetX + (_vm.StartX * scale), offsetY + (_vm.StartY * scale));
        _cutLine.EndPoint = new Point(offsetX + (_vm.EndX * scale), offsetY + (_vm.EndY * scale));
        _cutLine.IsVisible = true;
    }
}