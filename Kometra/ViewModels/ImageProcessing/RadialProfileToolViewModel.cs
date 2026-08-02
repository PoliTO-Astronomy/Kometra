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

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Pronto. Seleziona un punto sull'immagine per centrare il profilo.";

    [ObservableProperty] private double _centerX = 0.0;
    [ObservableProperty] private double _centerY = 0.0;
    [ObservableProperty] private double _maxRadius = 100.0;
    [ObservableProperty] private double _stepSize = 1.0;
    [ObservableProperty] private RadialProfileMode _selectedMode = RadialProfileMode.Mean;
    [ObservableProperty] private string? _resultFilePath;

    [ObservableProperty] 
    [NotifyCanExecuteChangedFor(nameof(ConfirmAndCreateNodeCommand))]
    private bool _hasPreview = false;

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

        _ = InitializeAsync();
    }

    private RadialProfileParameters GetCurrentParameters() => new()
    {
        CenterX = CenterX,
        CenterY = CenterY,
        MaxRadius = MaxRadius,
        StepSize = StepSize,
        Mode = SelectedMode
    };

    private async Task InitializeAsync()
    {
        var file = _sourceFiles.FirstOrDefault();
        if (file == null) return;

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

            await CalculateProfileAsync();
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
        CenterX = Math.Round(imageCoordinates.X, 1);
        CenterY = Math.Round(imageCoordinates.Y, 1);
        await CalculateProfileAsync();
    }

    [RelayCommand]
    private async Task CalculateProfileAsync()
    {
        var file = _sourceFiles.FirstOrDefault();
        if (file == null) return;

        IsLoading = true;
        StatusMessage = "Calcolo del profilo radiale in corso...";
        
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

            StatusMessage = $"Profilo calcolato: {ProfilePoints.Count} anelli analizzati.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Calcolo annullato.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore di calcolo: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (ProfilePoints.Count == 0) return;

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

    [RelayCommand]
    private void Close()
    {
        _cts?.Cancel();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private async Task ResetPreviewAsync()
    {
        HasPreview = false;
        ResultFilePath = null;
        await InitializeAsync();
        StatusMessage = "Anteprima reimpostata sull'immagine originale.";
    }

    [RelayCommand]
    private async Task GeneratePreviewAsync()
    {
        var file = _sourceFiles.FirstOrDefault();
        if (file == null || ProfilePoints.Count == 0) return;

        IsLoading = true;
        StatusMessage = "Calcolo e anteprima del modello 2D in corso...";

        try
        {
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

                // Aggiorniamo l'anteprima nel Viewport di destra
                var renderer = await _rendererFactory.CreateAsync(rawPixels, header);
                await renderer.InitializeAsync();

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Viewport = renderer;
                });
            });

            ResultFilePath = tempPath;
            HasPreview = true;
            StatusMessage = "Anteprima modello 2D generata. Conferma per creare il nodo sulla Board.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore generazione anteprima: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirmNode))]
    private void ConfirmAndCreateNode()
    {
        if (string.IsNullOrWhiteSpace(ResultFilePath)) return;
        
        // Chiude la finestra: il WindowService leggerà e restituirà ResultFilePath alla Board
        RequestClose?.Invoke();
    }

    private bool CanConfirmNode() => HasPreview && !string.IsNullOrWhiteSpace(ResultFilePath);

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
    public void Dispose() => _cts?.Dispose();
}