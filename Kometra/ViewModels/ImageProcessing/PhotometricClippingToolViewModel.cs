using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Factories;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Visualization;
using OpenCvSharp;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.ImageProcessing;

public partial class PhotometricClippingToolViewModel : ObservableObject, IDisposable
{
    private readonly IPhotometryCoordinator _coordinator;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IFitsDataManager _dataManager;
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;

    private FitsRenderer? _originalRenderer;

    public event Action? RequestClose;
    public List<string> ResultPaths { get; private set; } = new();
    public bool DialogResult { get; private set; }

    public SequenceNavigator Navigator { get; } = new();
    public bool HasMultipleImages => _sourceFiles.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _progressPercentage;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private PhotometricClippingMode _selectedMode = PhotometricClippingMode.SigmaThresh;
    [ObservableProperty] private ClippingOutputMode _selectedOutputMode = ClippingOutputMode.ZeroedPixels;
    [ObservableProperty] private double _minAdu = 0.0;
    [ObservableProperty] private double _maxAdu = 65000.0;
    [ObservableProperty] private double _lowerSigma = 3.0;
    [ObservableProperty] private double _upperSigma = 50.0;

    [ObservableProperty] private bool _isPreviewActive = false;
    [ObservableProperty] private FitsRenderer? _viewport;

    public PhotometricClippingToolViewModel(
        List<FitsFileReference> sourceFiles,
        IPhotometryCoordinator coordinator,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter,
        IFitsDataManager dataManager)
    {
        _sourceFiles = sourceFiles ?? new List<FitsFileReference>();
        _coordinator = coordinator;
        _rendererFactory = rendererFactory;
        _converter = converter;
        _dataManager = dataManager;

        Navigator.UpdateStatus(0, _sourceFiles.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = LoadOriginalImageAsync();
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadFrameAtIndexAsync(index);
    }

    private async Task LoadOriginalImageAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                _originalRenderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await _originalRenderer.InitializeAsync();
                Viewport = _originalRenderer;
                IsPreviewActive = false;
            }
        }
        catch (Exception) { }
    }

    private PhotometricClippingParameters GetCurrentParameters() => new()
    {
        Mode = SelectedMode, OutputMode = SelectedOutputMode,
        MinAdu = MinAdu, MaxAdu = MaxAdu,
        LowerSigma = LowerSigma, UpperSigma = UpperSigma
    };

    [RelayCommand]
    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var file = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            using Mat previewMat = await _coordinator.GeneratePreviewAsync(file, GetCurrentParameters(), _cts.Token);
            Array pixelData = _converter.MatToRaw(previewMat, FitsBitDepth.Float);
            
            var newRenderer = await _rendererFactory.CreateAsync(pixelData, new FitsHeader());
            await newRenderer.InitializeAsync();
            
            Viewport = newRenderer;
            IsPreviewActive = true;
        }
        catch (OperationCanceledException) { }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void BackToOriginal()
    {
        if (_originalRenderer != null)
        {
            Viewport = _originalRenderer;
        }
        IsPreviewActive = false;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            var processedFiles = await _coordinator.ExecuteBatchAsync(_sourceFiles, GetCurrentParameters(), null, _cts.Token);
            ResultPaths = processedFiles.Select(f => f.FilePath).ToList();
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException) { }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void Cancel()
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
            ProgressText = $"Errore caricamento frame {index + 1}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _cts?.Dispose();
    }
}