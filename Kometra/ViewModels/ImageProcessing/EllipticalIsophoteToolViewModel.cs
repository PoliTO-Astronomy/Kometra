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
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.Factories;
using Kometra.ViewModels.Visualization;
using Kometra.Services.UI;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.ImageProcessing;

public partial class EllipticalIsophoteToolViewModel : ObservableObject, IDisposable
{
    private readonly IEllipticalIsophoteCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IWindowService _windowService;
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;

    private FitsRenderer? _originalRenderer;

    public event Action? RequestClose;

    public bool DialogResult { get; private set; } = false;
    public List<string> ResultPaths { get; private set; } = new();

    public SequenceNavigator Navigator { get; } = new();
    public bool HasMultipleImages => _sourceFiles.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "Pronto. Clicca sull'immagine o imposta i parametri.";

    [ObservableProperty] private double _centerX = 0.0;
    [ObservableProperty] private double _centerY = 0.0;
    [ObservableProperty] private double _maxRadius = 200.0;
    [ObservableProperty] private double _stepSize = 2.0;
    [ObservableProperty] private double _ellipticity = 0.20;
    [ObservableProperty] private double _positionAngleDeg = 0.0;

    [ObservableProperty] private FitsRenderer? _previewRenderer;
    [ObservableProperty] private bool _isPreviewActive = false;

    private string? _currentPreviewFilePath;
    public string? CurrentPreviewFilePath => _currentPreviewFilePath;

    public ObservableCollection<EllipticalIsophoteDataPoint> Isophotes { get; } = new();

    public EllipticalIsophoteToolViewModel(
        List<FitsFileReference> sourceFiles,
        IEllipticalIsophoteCoordinator coordinator,
        IFitsDataManager dataManager,
        IFitsRendererFactory rendererFactory,
        IWindowService windowService)
    {
        _sourceFiles = sourceFiles ?? new List<FitsFileReference>();
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _rendererFactory = rendererFactory ?? throw new ArgumentNullException(nameof(rendererFactory));
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

    private async Task InitializeAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var currentFile = _sourceFiles[Navigator.CurrentIndex];

        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(currentFile.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (hdu?.PixelData != null)
            {
                int width = hdu.PixelData.GetLength(1);
                int height = hdu.PixelData.GetLength(0);

                CenterX = width / 2.0;
                CenterY = height / 2.0;
                MaxRadius = Math.Min(width, height) / 4.0;
            }

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                _originalRenderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await _originalRenderer.InitializeAsync();
                PreviewRenderer = _originalRenderer;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore di inizializzazione: {ex.Message}";
        }
    }

    public async Task OnImageClickedAsync(Point imageCoordinates)
    {
        if (IsPreviewActive) return;

        CenterX = Math.Round(imageCoordinates.X, 1);
        CenterY = Math.Round(imageCoordinates.Y, 1);
        StatusMessage = $"Centro impostato a: X={CenterX}, Y={CenterY}";
    }

    [RelayCommand]
    private async Task GeneratePreviewNodeAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var currentFile = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        StatusMessage = "Calcolo isofote e generazione modello 2D...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var (points, previewPath) = await _coordinator.AnalyzeAndGenerateModelAsync(
                currentFile, CenterX, CenterY, MaxRadius, StepSize, Ellipticity, PositionAngleDeg, _cts.Token);

            var dataPackage = await _dataManager.LoadDataPackageAsync(previewPath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu?.PixelData != null && hdu.Header != null)
            {
                var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await renderer.InitializeAsync();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Isophotes.Clear();
                    foreach (var pt in points) Isophotes.Add(pt);

                    PreviewRenderer = renderer;
                    IsPreviewActive = true;
                });
            }

            _currentPreviewFilePath = previewPath;
            StatusMessage = "Anteprima generata con successo!";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Calcolo annullato.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore durante il calcolo: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RevertToOriginalAsync()
    {
        if (_originalRenderer != null)
        {
            PreviewRenderer = _originalRenderer;
        }
        IsPreviewActive = false;
        StatusMessage = "Visualizzazione immagine base ripristinata.";
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Isophotes.Count == 0) return;

        try
        {
            string? destinationPath = await _windowService.ShowSaveFileDialogAsync(
                "Esporta Isofote Ellittiche CSV",
                "elliptical_isophotes.csv");

            if (string.IsNullOrWhiteSpace(destinationPath)) return;

            var sb = new StringBuilder();
            sb.AppendLine("SemiMajorAxis,Ellipticity,PositionAngle,MeanValue,StandardDeviation");

            foreach (var pt in Isophotes)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0:F2},{1:F4},{2:F4},{3:F4},{4:F4}",
                    pt.SemiMajorAxis,
                    pt.Ellipticity,
                    pt.PositionAngleRad,
                    pt.MeanValue,
                    pt.StandardDeviation));
            }

            await File.WriteAllTextAsync(destinationPath, sb.ToString(), Encoding.UTF8);
            StatusMessage = $"Dati esportati con successo in: {Path.GetFileName(destinationPath)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore durante l'esportazione CSV: {ex.Message}";
        }
    }

    // --- MODIFICATO PER LA SEQUENZA COMPLETA ---
    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = "Generazione modelli 2D ellittici per l'intera sequenza...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var generatedPaths = new List<string>();

            foreach (var file in _sourceFiles)
            {
                var (_, previewPath) = await _coordinator.AnalyzeAndGenerateModelAsync(
                    file, CenterX, CenterY, MaxRadius, StepSize, Ellipticity, PositionAngleDeg, _cts.Token);

                if (!string.IsNullOrWhiteSpace(previewPath))
                {
                    generatedPaths.Add(previewPath);
                }
            }

            ResultPaths = generatedPaths;
            DialogResult = true;
            StatusMessage = "Nodo confermato!";
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
                    
                    PreviewRenderer = renderer; 
                }
            }
            else
            {
                await GeneratePreviewNodeAsync(); 
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