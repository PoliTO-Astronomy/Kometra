using System;
using System.ComponentModel;
using System.Collections.Specialized;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kometra.ViewModels.ImageProcessing;
using Kometra.ViewModels.Visualization;
using Kometra.Models.Processing.Analysis;

namespace Kometra.Views;

public partial class RadialProfileToolView : Window
{
    private Line? _lineH;
    private Line? _lineV;
    private Ellipse? _circle;
    private Path? _sectorOverlay;
    private RadialProfileToolViewModel? _vm;

    public RadialProfileToolView()
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
        _sectorOverlay = this.FindControl<Path>("SectorOverlay");

        var viewportCtrl = this.FindControl<ContentControl>("ViewportControl");
        if (viewportCtrl != null)
        {
            viewportCtrl.SizeChanged += (s, e) => Dispatcher.UIThread.Post(UpdateCrosshairPosition, DispatcherPriority.Background);
        }
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RadialProfileToolViewModel vm)
        {
            _vm = vm;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            
            _vm.ProfilePoints.CollectionChanged += (s, ev) => Dispatcher.UIThread.Post(UpdatePlot);

            _vm.SavePlotRequested += OnSavePlotRequested;

            UpdateUiValues();
            UpdatePlot();
            
            Dispatcher.UIThread.Post(UpdateCrosshairPosition, DispatcherPriority.Loaded);
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
        if (plotControl != null)
        {
            plotControl.Plot.SavePng(filePath, 1200, 800);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_vm == null) return;
            switch (e.PropertyName)
            {
                case nameof(RadialProfileToolViewModel.CenterX):
                    UpdateBox("CenterXBox", _vm.CenterX);
                    UpdateCrosshairPosition();
                    break;
                case nameof(RadialProfileToolViewModel.CenterY):
                    UpdateBox("CenterYBox", _vm.CenterY);
                    UpdateCrosshairPosition();
                    break;
                case nameof(RadialProfileToolViewModel.MaxRadius):
                    UpdateBox("MaxRadiusBox", _vm.MaxRadius);
                    UpdateCrosshairPosition();
                    break;
                case nameof(RadialProfileToolViewModel.StepSize):
                    UpdateBox("StepSizeBox", _vm.StepSize);
                    break;
                case nameof(RadialProfileToolViewModel.StartingAngle):
                    UpdateBox("StartingAngleBox", _vm.StartingAngle);
                    UpdateCrosshairPosition();
                    break;
                case nameof(RadialProfileToolViewModel.IntegrationAngle):
                    UpdateBox("IntegrationAngleBox", _vm.IntegrationAngle);
                    UpdateCrosshairPosition();
                    break;
                case nameof(RadialProfileToolViewModel.Viewport):
                    UpdateCrosshairPosition();
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
        UpdateBox("StartingAngleBox", _vm.StartingAngle);
        UpdateBox("IntegrationAngleBox", _vm.IntegrationAngle);
    }

    private void OnManualInputCommit(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box) return;

        string input = (box.Text ?? string.Empty).Replace(',', '.');
        var culture = CultureInfo.InvariantCulture;

        if (box.Name == "CenterXBox" && double.TryParse(input, NumberStyles.Any, culture, out double cx))
            _vm.CenterX = cx;
        else if (box.Name == "CenterYBox" && double.TryParse(input, NumberStyles.Any, culture, out double cy))
            _vm.CenterY = cy;
        else if (box.Name == "MaxRadiusBox" && double.TryParse(input, NumberStyles.Any, culture, out double mr))
            _vm.MaxRadius = mr;
        else if (box.Name == "StepSizeBox" && double.TryParse(input, NumberStyles.Any, culture, out double st))
            _vm.StepSize = st;
        else if (box.Name == "StartingAngleBox" && double.TryParse(input, NumberStyles.Any, culture, out double sa))
            _vm.StartingAngle = sa;
        else if (box.Name == "IntegrationAngleBox" && double.TryParse(input, NumberStyles.Any, culture, out double ia))
            _vm.IntegrationAngle = ia;

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

    private void UpdatePlot()
    {
        var plotControl = this.FindControl<ScottPlot.Avalonia.AvaPlot>("ProfilePlot");
        if (plotControl == null || _vm == null) return;

        plotControl.Plot.Clear();

        plotControl.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plotControl.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        
        plotControl.Plot.Axes.Color(ScottPlot.Color.FromHex("#AAAAAA"));
        plotControl.Plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");

        if (_vm.ProfilePoints.Count > 0)
        {
            double[] xs = new double[_vm.ProfilePoints.Count];
            double[] ys = new double[_vm.ProfilePoints.Count];

            for (int i = 0; i < _vm.ProfilePoints.Count; i++)
            {
                xs[i] = _vm.ProfilePoints[i].Radius;
                ys[i] = _vm.ProfilePoints[i].Value;
            }

            var scatter = plotControl.Plot.Add.Scatter(xs, ys, ScottPlot.Color.FromHex("#8058E8"));
            scatter.MarkerStyle.Size = 0;
            scatter.LineStyle.Width = 2.0f;
            
            string yLabel = _vm.SelectedMode switch
            {
                RadialProfileMode.Sum => "Total Intensity [ADU]",
                RadialProfileMode.Median => "Median Intensity [ADU]",
                _ => "Normalized Integrated Intensity"
            };

            plotControl.Plot.Axes.Title.Label.Text = "Radial Profile";
            plotControl.Plot.Axes.Bottom.Label.Text = "Radius [pixels]";
            plotControl.Plot.Axes.Left.Label.Text = yLabel + "\n "; 
            plotControl.Plot.Axes.AutoScale();
        }

        plotControl.Refresh();
    }

    private void UpdateCrosshairPosition()
    {
        if (_vm == null || _lineH == null || _lineV == null || _circle == null || _sectorOverlay == null)
        {
            HideCrosshair();
            return;
        }

        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null ||
            containerGrid.Bounds.Width <= 0 ||
            containerGrid.Bounds.Height <= 0 ||
            _vm.Viewport is not FitsRenderer { Image: Bitmap bitmap })
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

        if (_vm.IntegrationAngle >= 180)
        {
            double r = _vm.MaxRadius * scale;
            var geo = new EllipseGeometry { Rect = new Rect(screenX - r, screenY - r, r * 2, r * 2) };
            _sectorOverlay.Data = geo;
            _sectorOverlay.IsVisible = true;
        }
        else
        {
            double r = _vm.MaxRadius * scale;
            
            double a1 = (_vm.StartingAngle - _vm.IntegrationAngle) * (Math.PI / 180.0);
            double a2 = (_vm.StartingAngle + _vm.IntegrationAngle) * (Math.PI / 180.0);

            Point pCenter = new Point(screenX, screenY);
            
            Point p1 = new Point(screenX + r * Math.Cos(a1), screenY - r * Math.Sin(a1));
            Point p2 = new Point(screenX + r * Math.Cos(a2), screenY - r * Math.Sin(a2));

            var geo = new PathGeometry();
            var figure = new PathFigure { StartPoint = pCenter, IsClosed = true };
            figure.Segments.Add(new LineSegment { Point = p1 });
            
            figure.Segments.Add(new ArcSegment
            {
                Point = p2,
                Size = new Size(r, r),
                SweepDirection = SweepDirection.CounterClockwise,
                IsLargeArc = _vm.IntegrationAngle > 90
            });

            geo.Figures.Add(figure);
            _sectorOverlay.Data = geo;
            _sectorOverlay.IsVisible = true;
        }
    }

    private void HideCrosshair()
    {
        if (_lineH != null) _lineH.IsVisible = false;
        if (_lineV != null) _lineV.IsVisible = false;
        if (_circle != null) _circle.IsVisible = false;
        if (_sectorOverlay != null) _sectorOverlay.IsVisible = false;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Blocca il click se il profilo è già calcolato
        if (_vm == null || _vm.HasCalculatedProfile) return;

        var containerGrid = this.FindControl<Grid>("ImageContainerGrid");
        if (containerGrid == null ||
            containerGrid.Bounds.Width <= 0 ||
            containerGrid.Bounds.Height <= 0 ||
            _vm.Viewport is not FitsRenderer { Image: Bitmap bitmap })
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
}