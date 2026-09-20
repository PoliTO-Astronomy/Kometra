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
using Kometra.Infrastructure;

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

    private string GetSelectionColorHex()
    {
        if (Application.Current != null)
        {
            if (Application.Current.TryGetResource("SelectionColor", out var res) || 
                Application.Current.TryGetResource("SystemAccentColor", out res))
            {
                if (res is Avalonia.Media.Color c) return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                if (res is Avalonia.Media.SolidColorBrush b) return $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
            }
        }
        return "#8058E8";
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
        if (box != null)
        {
            // Lasciamo gestire il testo ad Avalonia usando il FormatString="F2" definito nello XAML!
            box.Value = (decimal)value;
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

    private void CommitValue(NumericUpDown box, double newValue)
    {
        if (_vm == null) return;
        bool hasChanged = false;

        if (box.Name == "CenterXBox" && Math.Abs(_vm.CenterX - newValue) > 0.001) { _vm.CenterX = newValue; hasChanged = true; }
        else if (box.Name == "CenterYBox" && Math.Abs(_vm.CenterY - newValue) > 0.001) { _vm.CenterY = newValue; hasChanged = true; }
        else if (box.Name == "MaxRadiusBox" && Math.Abs(_vm.MaxRadius - newValue) > 0.001) { _vm.MaxRadius = newValue; hasChanged = true; }
        else if (box.Name == "StepSizeBox" && Math.Abs(_vm.StepSize - newValue) > 0.001) { _vm.StepSize = newValue; hasChanged = true; }
        else if (box.Name == "StartingAngleBox" && Math.Abs(_vm.StartingAngle - newValue) > 0.001) { _vm.StartingAngle = newValue; hasChanged = true; }
        else if (box.Name == "IntegrationAngleBox" && Math.Abs(_vm.IntegrationAngle - newValue) > 0.001) { _vm.IntegrationAngle = newValue; hasChanged = true; }

        if (hasChanged)
        {
            _vm.TriggerCalculation();
            UpdateCrosshairPosition();
        }
    }

    private void OnBoxValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_vm == null || sender is not NumericUpDown box || !e.NewValue.HasValue) return;

        double newValue = (double)e.NewValue.Value;
        double oldValue = e.OldValue.HasValue ? (double)e.OldValue.Value : 0;
        double diff = Math.Abs(newValue - oldValue);
        double increment = (double)(box.Increment > 0 ? box.Increment : 1);

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

        if (_vm.ProfilePoints.Count > 0)
        {
            double[] xs = new double[_vm.ProfilePoints.Count];
            double[] ys = new double[_vm.ProfilePoints.Count];

            for (int i = 0; i < _vm.ProfilePoints.Count; i++)
            {
                xs[i] = _vm.ProfilePoints[i].Radius;
                ys[i] = _vm.ProfilePoints[i].Value;
            }

            string hexColor = GetSelectionColorHex();
            var scatter = plotControl.Plot.Add.Scatter(xs, ys, ScottPlot.Color.FromHex(hexColor));
            scatter.MarkerStyle.Size = 0;
            scatter.LineStyle.Width = 2.5f; 
            
            var loc = LocalizationManager.Instance;
            string valueStr = loc["ColValue"] ?? "Value";
            
            string baseLabel = _vm.SelectedMode switch
            {
                RadialProfileMode.Sum => $"{valueStr} (Sum)",
                RadialProfileMode.Median => $"{valueStr} (Median)",
                _ => $"{valueStr} (Mean)"
            };

            plotControl.Plot.Axes.Title.Label.Text = $"\n{loc["MenuRadialProfiles"]}\n";
            plotControl.Plot.Axes.Title.Label.FontSize = 20;

            plotControl.Plot.Axes.Bottom.Label.Text = $"{loc["ColRadius"]} [px]";
            plotControl.Plot.Axes.Bottom.Label.FontSize = 16;
            plotControl.Plot.Axes.Bottom.TickLabelStyle.FontSize = 14;

            plotControl.Plot.Axes.Left.Label.Text = $"\n{baseLabel}\n"; 
            plotControl.Plot.Axes.Left.Label.FontSize = 16;
            plotControl.Plot.Axes.Left.TickLabelStyle.FontSize = 14;
            plotControl.Plot.Axes.Left.MinimumSize = 80; 
            
            var yTickGen = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 8 };
            plotControl.Plot.Axes.Left.TickGenerator = yTickGen;

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
            
            // CONVENZIONE ASTRONOMICA PER DISEGNO INTERFACCIA
            // 0° = Nord (Alto), 90° = Est (Sinistra), rotazione antioraria.
            Point p1 = new Point(screenX - r * Math.Sin(a1), screenY - r * Math.Cos(a1));
            Point p2 = new Point(screenX - r * Math.Sin(a2), screenY - r * Math.Cos(a2));

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
        if (_vm == null) return;

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