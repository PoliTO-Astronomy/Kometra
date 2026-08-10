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

    private FitsRenderer? _originalRenderer;

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
    [ObservableProperty] private string? _resultFilePath;
    [ObservableProperty] private double _startingAngle = 0.0;     
    [ObservableProperty] private double _integrationAngle = 180.0; 

    // NUOVO: Controlla la visibilità del grafico e del bottone esporta
    [ObservableProperty] private bool _hasCalculatedProfile = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => SelectedTabIndex == 0 ? "Esporta Grafico PNG" : "Esporta CSV";

    [ObservableProperty] private bool _isPreviewActive = false;
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
                
                _originalRenderer = await _rendererFactory.CreateAsync(pixelData, hdu.Header ?? new FitsHeader());
                await _originalRenderer.InitializeAsync();
                
                Viewport = _originalRenderer;
                IsPreviewActive = false;
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
        if (IsPreviewActive) return;

        CenterX = Math.Round(imageCoordinates.X, 1);
        CenterY = Math.Round(imageCoordinates.Y, 1);
        
        await Task.CompletedTask; // Il calcolo aspetta il bottone Anteprima
    }

    [RelayCommand]
    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        StatusMessage = "Calcolo del profilo e dell'anteprima 2D in corso...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            // 1. Calcola il profilo radiale e aggiorna l'UI del Grafico
            var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
            
            ProfilePoints.Clear();
            foreach (var pt in points)
            {
                ProfilePoints.Add(pt);
            }

            if (ProfilePoints.Count > 0)
            {
                HasCalculatedProfile = true; // Sblocca grafici e tabelle
            }
            else
            {
                StatusMessage = "Nessun punto calcolato, impossibile generare anteprima.";
                return;
            }

            // 2. Genera il modello FITS 2D sintetico
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "RadialModels");
            Directory.CreateDirectory(outputDir);
            string tempPath = Path.Combine(outputDir, $"RadialModel_{Guid.NewGuid():N}.fits");

            await Task.Run(async () =>
            {
                var dataPackage = _dataManager.LoadDataPackageAsync(file.FilePath).GetAwaiter().GetResult();
                var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                if (hdu == null) return;

                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                int rows = srcMat.Rows;
                int cols = srcMat.Cols;

                using Mat modelMat = new Mat(rows, cols, MatType.CV_32FC1, new Scalar(0));

                var sortedPoints = ProfilePoints.OrderBy(p => p.Radius).ToList();
                double maxR = sortedPoints.Last().Radius;

                var indexer = modelMat.GetGenericIndexer<float>();
                for (int y = 0; y < rows; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        double dx = x - CenterX;
                        double dy = y - CenterY;
                        double r = Math.Sqrt(dx * dx + dy * dy);

                        if (r <= maxR && sortedPoints.Count > 0)
                        {
                            indexer[y, x] = (float)EvaluateProfileValue(sortedPoints, r);
                        }
                        else
                        {
                            indexer[y, x] = 0f;
                        }
                    }
                }

                Array rawPixels = _converter.MatToRaw(modelMat, FitsBitDepth.Float);
                var header = hdu.Header ?? new FitsHeader();

                await _dataManager.SaveDataAsync(tempPath, rawPixels, header);

                var renderer = await _rendererFactory.CreateAsync(rawPixels, header);
                await renderer.InitializeAsync();

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Viewport = renderer;
                });
            }, _cts.Token);

            ResultFilePath = tempPath;
            IsPreviewActive = true;
            StatusMessage = "Anteprima generata e grafico aggiornato. Premi Applica per creare il nodo.";
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
    private void BackToOriginal()
    {
        if (_originalRenderer != null)
        {
            Viewport = _originalRenderer;
        }
        IsPreviewActive = false;
        StatusMessage = "Ripristinata immagine originale. Il grafico mostra ancora gli ultimi dati calcolati.";
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
                    "Esporta Grafico PNG",
                    "radial_profile.png");

                if (string.IsNullOrWhiteSpace(destinationPath)) return;

                SavePlotRequested?.Invoke(destinationPath);
                StatusMessage = $"Grafico esportato con successo in: {Path.GetFileName(destinationPath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore durante l'esportazione del grafico: {ex.Message}";
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
                        pt.Radius,
                        pt.Value,
                        pt.StandardDeviation,
                        pt.PixelCount));
                }

                await File.WriteAllTextAsync(destinationPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = $"Dati esportati con successo in: {Path.GetFileName(destinationPath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore durante l'esportazione CSV: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = "Generazione modelli 2D radiali per l'intera sequenza...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "RadialModels");
            Directory.CreateDirectory(outputDir);
            var generatedPaths = new List<string>();

            foreach (var file in _sourceFiles)
            {
                var points = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
                if (points == null || points.Count == 0) continue;

                string tempPath = Path.Combine(outputDir, $"RadialModel_{Guid.NewGuid():N}.fits");

                await Task.Run(async () =>
                {
                    var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
                    var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                    if (hdu == null) return;

                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    int rows = srcMat.Rows;
                    int cols = srcMat.Cols;

                    using Mat modelMat = new Mat(rows, cols, MatType.CV_32FC1, new Scalar(0));
                    var sortedPoints = points.OrderBy(p => p.Radius).ToList();
                    double maxR = sortedPoints.Last().Radius;

                    var indexer = modelMat.GetGenericIndexer<float>();
                    for (int y = 0; y < rows; y++)
                    {
                        for (int x = 0; x < cols; x++)
                        {
                            double dx = x - CenterX;
                            double dy = y - CenterY;
                            double r = Math.Sqrt(dx * dx + dy * dy);

                            if (r <= maxR && sortedPoints.Count > 0)
                            {
                                indexer[y, x] = (float)EvaluateProfileValue(sortedPoints, r);
                            }
                            else
                            {
                                indexer[y, x] = 0f;
                            }
                        }
                    }

                    Array rawPixels = _converter.MatToRaw(modelMat, FitsBitDepth.Float);
                    var header = hdu.Header ?? new FitsHeader();
                    await _dataManager.SaveDataAsync(tempPath, rawPixels, header);
                }, _cts.Token);

                generatedPaths.Add(tempPath);
            }

            ResultPaths = generatedPaths;
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
            if (!IsPreviewActive)
            {
                var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
                var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                if (hdu?.PixelData != null && hdu.Header != null)
                {
                    var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                    await renderer.InitializeAsync();
                    
                    Viewport = renderer;
                }
            }
            else
            {
                await CalculatePreviewAsync(); 
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
        _cts?.Dispose();
    }
}