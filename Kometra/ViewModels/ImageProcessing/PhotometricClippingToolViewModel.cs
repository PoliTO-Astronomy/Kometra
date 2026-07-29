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
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Factories;
using Kometra.Services.Processing.Coordinators;
using Kometra.ViewModels.Visualization;
using OpenCvSharp;

namespace Kometra.ViewModels.ImageProcessing;

public partial class PhotometricClippingToolViewModel : ObservableObject, IDisposable
{
    private readonly IPhotometryCoordinator _coordinator;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;

    public event Action? RequestClose;
    public List<string> ResultPaths { get; private set; } = new();
    public bool DialogResult { get; private set; }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private int _progressPercentage;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private PhotometricClippingMode _selectedMode = PhotometricClippingMode.SigmaThresh;
    [ObservableProperty] private ClippingOutputMode _selectedOutputMode = ClippingOutputMode.ZeroedPixels;
    [ObservableProperty] private double _minAdu = 0.0;
    [ObservableProperty] private double _maxAdu = 65000.0;
    [ObservableProperty] private double _lowerSigma = 3.0;
    [ObservableProperty] private double _upperSigma = 50.0;

    [ObservableProperty] private FitsRenderer? _viewport;

    public PhotometricClippingToolViewModel(
        List<FitsFileReference> sourceFiles,
        IPhotometryCoordinator coordinator,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter)
    {
        _sourceFiles = sourceFiles ?? new List<FitsFileReference>();
        _coordinator = coordinator;
        _rendererFactory = rendererFactory;
        _converter = converter;
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
        var file = _sourceFiles.FirstOrDefault();
        if (file == null) return;

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
        }
        catch (OperationCanceledException) { }
        finally { IsLoading = false; }
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

    public void Dispose() => _cts?.Dispose();
}