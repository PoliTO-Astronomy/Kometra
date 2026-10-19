using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Factories;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.UI;
using Kometra.ViewModels.Visualization;
using Kometra.Infrastructure;
using OpenCvSharp;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.ImageProcessing;

public partial class RadialProfileToolViewModel : ObservableObject, IDisposable
{
    private readonly IRadialProfileCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IWindowService _windowService;
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;

    public event Action? RequestClose;
    public event Action<string>? SavePlotRequested;

    public bool DialogResult { get; private set; } = false;
    public List<string> ResultPaths { get; private set; } = new();

    public SequenceNavigator Navigator { get; } = new();
    public bool HasMultipleImages => _sourceFiles.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "...";

    [ObservableProperty] private double _centerX = 0.0;
    [ObservableProperty] private double _centerY = 0.0;
    [ObservableProperty] private double _maxRadius = 100.0;
    [ObservableProperty] private double _stepSize = 1.0;
    [ObservableProperty] private RadialProfileMode _selectedMode = RadialProfileMode.Mean;
    [ObservableProperty] private double _startingAngle = 0.0;     
    [ObservableProperty] private double _integrationAngle = 180.0; 

    [ObservableProperty] private bool _hasProfileData = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => $"{LocalizationManager.Instance["ExportExecute"]} {(SelectedTabIndex == 0 ? "PNG" : "CSV")}";

    [ObservableProperty] private FitsRenderer? _viewport;

    public ObservableCollection<RadialProfileDataPoint> ProfilePoints { get; } = new();

    public RadialProfileToolViewModel(
        List<FitsFileReference> sourceFiles,
        IRadialProfileCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter,
        IWindowService windowService)
    {
        _sourceFiles = sourceFiles ?? new List<FitsFileReference>();
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));

        StatusMessage = LocalizationManager.Instance["StatusReady"] ?? "Ready.";

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeAsync();
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

    partial void OnSelectedModeChanged(RadialProfileMode value) => _ = CalculateProfileAsync();

    public void TriggerCalculation()
    {
        _ = CalculateProfileAsync();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadFrameAtIndexAsync(index);
    }

    private RadialProfileParameters GetCurrentParameters() => new()
    {
        CenterX = CenterX,
        CenterY = CenterY,
        MaxRadius = MaxRadius,
        StepSize = StepSize,
        Mode = SelectedMode,
        StartingAngle = StartingAngle, 
        IntegrationAngle = IntegrationAngle
    };

    // Parser che estrae passivamente le coordinate salvate nell'Header FITS dall'Allineamento
    private double ParseFitsCoordinate(FitsHeader header, params string[] possibleKeys)
    {
        if (header?.Cards == null) return double.NaN;

        foreach (var key in possibleKeys)
        {
            var card = header.Cards.FirstOrDefault(c => 
                c.Key != null && c.Key.Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            
            if (card != null && !string.IsNullOrWhiteSpace(card.Value))
            {
                string val = card.Value;
                int slashIdx = val.IndexOf('/');
                if (slashIdx >= 0) val = val.Substring(0, slashIdx);
                
                val = val.Replace("'", "").Trim().Replace(",", ".");
                
                if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsedVal))
                {
                    return parsedVal;
                }
            }
        }
        return double.NaN;
    }

    private async Task InitializeAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu != null)
            {
                int width = hdu.PixelData?.GetLength(1) ?? 0;
                int height = hdu.PixelData?.GetLength(0) ?? 0;

                double defaultX = width / 2.0;
                double defaultY = height / 2.0;

                // Cerca ESCLUSIVAMENTE nell'header FITS. Nessun ricalcolo intelligente.
                if (hdu.Header != null)
                {
                    double parsedX = ParseFitsCoordinate(hdu.Header, "OBJCT_X", "COMET_X", "CRPIX1", "CENTER_X");
                    double parsedY = ParseFitsCoordinate(hdu.Header, "OBJCT_Y", "COMET_Y", "CRPIX2", "CENTER_Y");

                    if (!double.IsNaN(parsedX)) defaultX = parsedX;
                    if (!double.IsNaN(parsedY)) defaultY = parsedY;
                }

                CenterX = Math.Round(defaultX, 2);
                CenterY = Math.Round(defaultY, 2);

                using Mat imgMat = _dataManager.GetMatFromHdu(hdu);
                Array pixelData = _converter.MatToRaw(imgMat, FitsBitDepth.Float);
                
                var renderer = await _rendererFactory.CreateAsync(pixelData, hdu.Header ?? new FitsHeader());
                await renderer.InitializeAsync();
                
                Viewport = renderer;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }

        if (!HasProfileData)
        {
            _ = CalculateProfileAsync();
        }
    }

    public async Task OnImageClickedAsync(Avalonia.Point imageCoordinates)
    {
        CenterX = Math.Round(imageCoordinates.X, 2);
        CenterY = Math.Round(imageCoordinates.Y, 2);
        await Task.CompletedTask; 
        _ = CalculateProfileAsync();
    }

    private async Task CalculateProfileAsync()
    {
        if (_sourceFiles.Count == 0 || _isLoading) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), token);
            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() => 
            {
                ProfilePoints.Clear();
                foreach (var pt in points)
                {
                    ProfilePoints.Add(pt);
                }

                HasProfileData = ProfilePoints.Count > 0;
                if (HasProfileData)
                {
                    StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Done.";
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (ProfilePoints.Count == 0) return;

        if (SelectedTabIndex == 0) 
        {
            try
            {
                string? destinationPath = await _windowService.ShowSaveFileDialogAsync("PNG", "radial_profile.png");
                if (string.IsNullOrWhiteSpace(destinationPath)) return;

                SavePlotRequested?.Invoke(destinationPath);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message);
            }
        }
        else 
        {
            try
            {
                string? destinationPath = await _windowService.ShowSaveFileDialogAsync("CSV", "radial_profile.csv");
                if (string.IsNullOrWhiteSpace(destinationPath)) return;

                var sb = new StringBuilder();
                sb.AppendLine("Radius,Value,StandardDeviation,PixelCount");
                foreach (var pt in ProfilePoints)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "{0:F2},{1:F4},{2:F4},{3}",
                        pt.Radius, pt.Value, pt.StandardDeviation, pt.PixelCount));
                }
                await File.WriteAllTextAsync(destinationPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
            }
            catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = LocalizationManager.Instance["StatusProcessing"] ?? "Processing in progress...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "RadialProfiles");
            Directory.CreateDirectory(outputDir);

            string baseId = Guid.NewGuid().ToString("N");
            string csvPath = Path.Combine(outputDir, $"RadialProfile_{baseId}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Radius,Value,StandardDeviation,PixelCount");
            foreach (var pt in ProfilePoints)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:F2},{1:F4},{2:F4},{3}",
                    pt.Radius, pt.Value, pt.StandardDeviation, pt.PixelCount));
            }
            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);

            var pngPaths = new List<string>();

            string titleLoc = LocalizationManager.Instance["MenuRadialProfiles"] ?? "Radial Profile";
            string xLoc = LocalizationManager.Instance["ColRadius"] ?? "Radius";
            string valueStr = LocalizationManager.Instance["ColValue"] ?? "Value";
            string hexColor = GetSelectionColorHex();

            string yLoc = SelectedMode switch
            {
                RadialProfileMode.Sum => $"{valueStr} (Sum)",
                RadialProfileMode.Median => $"{valueStr} (Median)",
                _ => $"{valueStr} (Mean)"
            };

            foreach (var file in _sourceFiles)
            {
                var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
                if (points == null || points.Count == 0) continue;

                string pngPath = Path.Combine(outputDir, $"RadialProfile_{Guid.NewGuid():N}.png");

                await Task.Run(() =>
                {
                    var plt = new ScottPlot.Plot();
                    
                    plt.FigureBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
                    plt.DataBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
                    plt.Axes.Color(ScottPlot.Color.FromHex("#000000"));
                    plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E0E0E0");

                    double[] xs = new double[points.Count];
                    double[] ys = new double[points.Count];

                    for (int i = 0; i < points.Count; i++)
                    {
                        xs[i] = points[i].Radius;
                        ys[i] = points[i].Value;
                    }

                    var scatter = plt.Add.Scatter(xs, ys, ScottPlot.Color.FromHex(hexColor));
                    scatter.MarkerStyle.Size = 0;
                    scatter.LineStyle.Width = 2.5f;

                    plt.Axes.Title.Label.Text = $"\n\n{titleLoc}\n";
                    plt.Axes.Title.Label.FontSize = 24;
                    
                    plt.Axes.Bottom.Label.Text = $"{xLoc} [px]";
                    plt.Axes.Bottom.Label.FontSize = 18;
                    plt.Axes.Bottom.TickLabelStyle.FontSize = 14;
                    
                    plt.Axes.Left.Label.Text = $"\n\n{yLoc}\n\n";
                    plt.Axes.Left.Label.FontSize = 18;
                    plt.Axes.Left.TickLabelStyle.FontSize = 14;

                    var yTickGen = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 8 };
                    plt.Axes.Left.TickGenerator = yTickGen;
                    
                    plt.Axes.AutoScale();

                    plt.SavePng(pngPath, 1200, 800);
                }, _cts.Token);

                pngPaths.Add(pngPath);
            }

            ResultPaths = pngPaths;
            ResultPaths.Add(csvPath);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        DialogResult = false;
        RequestClose?.Invoke();
    }

    private async Task LoadFrameAtIndexAsync(int index)
    {
        if (index < 0 || index >= _sourceFiles.Count) return;
        var file = _sourceFiles[index];

        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu?.PixelData != null && hdu.Header != null)
            {
                var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await renderer.InitializeAsync();
                
                Viewport?.Dispose();
                Viewport = renderer;

                await CalculateProfileAsync();
            }
        }
        catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message); }
    }

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        Viewport?.Dispose();
        _cts?.Dispose();
    }
}