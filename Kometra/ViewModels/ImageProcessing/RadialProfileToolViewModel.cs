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
    [ObservableProperty] private string _statusMessage = "Pronto. Sposta il centro e imposta i parametri, poi premi 'Calcola Anteprima'.";

    [ObservableProperty] private double _centerX = 0.0;
    [ObservableProperty] private double _centerY = 0.0;
    [ObservableProperty] private double _maxRadius = 100.0;
    [ObservableProperty] private double _stepSize = 1.0;
    [ObservableProperty] private RadialProfileMode _selectedMode = RadialProfileMode.Mean;
    [ObservableProperty] private double _startingAngle = 0.0;     
    [ObservableProperty] private double _integrationAngle = 180.0; 

    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanModifyInput))]
    private bool _hasCalculatedProfile = false;

    public bool CanModifyInput => !HasCalculatedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => SelectedTabIndex == 0 ? "Esporta PNG" : "Esporta CSV";

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

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = InitializeAsync();
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

                CenterX = width / 2.0;
                CenterY = height / 2.0;

                using Mat imgMat = _dataManager.GetMatFromHdu(hdu);
                Array pixelData = _converter.MatToRaw(imgMat, FitsBitDepth.Float);
                
                var renderer = await _rendererFactory.CreateAsync(pixelData, hdu.Header ?? new FitsHeader());
                await renderer.InitializeAsync();
                
                Viewport = renderer;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore inizializzazione: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task OnImageClickedAsync(Avalonia.Point imageCoordinates)
    {
        if (HasCalculatedProfile) return;

        CenterX = Math.Round(imageCoordinates.X, 1);
        CenterY = Math.Round(imageCoordinates.Y, 1);
        
        await Task.CompletedTask; 
    }

    [RelayCommand]
    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        StatusMessage = "Calcolo del profilo in corso...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
            
            ProfilePoints.Clear();
            foreach (var pt in points)
            {
                ProfilePoints.Add(pt);
            }

            if (ProfilePoints.Count > 0)
            {
                HasCalculatedProfile = true; 
                StatusMessage = "Anteprima calcolata. Premi Conferma per generare i modelli sulla board.";
            }
            else
            {
                StatusMessage = "Nessun punto calcolato, impossibile generare anteprima.";
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Calcolo annullato.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ResetToPreview()
    {
        HasCalculatedProfile = false;
        ProfilePoints.Clear();
        StatusMessage = "Modalità tracciamento sbloccata. Sposta il centro e ricalcola l'anteprima.";
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (ProfilePoints.Count == 0) return;

        if (SelectedTabIndex == 0) 
        {
            try
            {
                string? destinationPath = await _windowService.ShowSaveFileDialogAsync(
                    "Esporta PNG",
                    "radial_profile.png");

                if (string.IsNullOrWhiteSpace(destinationPath)) return;

                SavePlotRequested?.Invoke(destinationPath);
                StatusMessage = $"Grafico esportato con successo in: {Path.GetFileName(destinationPath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore esportazione: {ex.Message}";
            }
        }
        else 
        {
            try
            {
                string? destinationPath = await _windowService.ShowSaveFileDialogAsync(
                    "Esporta Profilo Radiale CSV",
                    "radial_profile.csv");

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
                StatusMessage = $"Dati esportati con successo in: {Path.GetFileName(destinationPath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore esportazione CSV: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = "Generazione dei grafici della sequenza in corso...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "RadialProfiles");
            Directory.CreateDirectory(outputDir);

            // 1. Genera il CSV globale della selezione attuale
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

            // 2. Genera i plot PNG per ogni file della sequenza FITS
            foreach (var file in _sourceFiles)
            {
                var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
                if (points == null || points.Count == 0) continue;

                string pngPath = Path.Combine(outputDir, $"RadialProfile_{Guid.NewGuid():N}.png");

                await Task.Run(() =>
                {
                    var plt = new ScottPlot.Plot();
                    plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
                    plt.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
                    plt.Axes.Color(ScottPlot.Color.FromHex("#AAAAAA"));
                    plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");

                    double[] xs = new double[points.Count];
                    double[] ys = new double[points.Count];

                    for (int i = 0; i < points.Count; i++)
                    {
                        xs[i] = points[i].Radius;
                        ys[i] = points[i].Value;
                    }

                    var scatter = plt.Add.Scatter(xs, ys, ScottPlot.Color.FromHex("#8058E8"));
                    scatter.MarkerStyle.Size = 0;
                    scatter.LineStyle.Width = 2.0f;

                    string yLabel = SelectedMode switch
                    {
                        RadialProfileMode.Sum => "Total Intensity [ADU]",
                        RadialProfileMode.Median => "Median Intensity [ADU]",
                        _ => "Normalized Integrated Intensity"
                    };

                    plt.Axes.Title.Label.Text = "Radial Profile";
                    plt.Axes.Bottom.Label.Text = "Radius [pixels]";
                    plt.Axes.Left.Label.Text = yLabel;
                    plt.Axes.AutoScale();

                    plt.SavePng(pngPath, 1200, 800);
                }, _cts.Token);

                pngPaths.Add(pngPath);
            }

            // Aggiungiamo i PNG seguiti dal CSV (stesso schema del PhotometricClipping)
            ResultPaths = pngPaths;
            ResultPaths.Add(csvPath);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore durante la generazione batch: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        DialogResult = false;
        RequestClose?.Invoke();
    }

    private static double EvaluateProfileValue(List<RadialProfileDataPoint> pts, double r)
    {
        if (r <= pts[0].Radius) return pts[0].Value;
        if (r >= pts[^1].Radius) return pts[^1].Value;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (r >= pts[i].Radius && r <= pts[i + 1].Radius)
            {
                double t = (r - pts[i].Radius) / (pts[i + 1].Radius - pts[i].Radius);
                return pts[i].Value + t * (pts[i + 1].Value - pts[i].Value);
            }
        }
        return 0;
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

                if (HasCalculatedProfile)
                {
                    await CalculatePreviewAsync();
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore caricamento frame {index + 1}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        Viewport?.Dispose();
        _cts?.Dispose();
    }
}