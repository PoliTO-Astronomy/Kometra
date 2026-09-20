using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
using Kometra.Infrastructure;
using OpenCvSharp;
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

    public event Action? RequestClose;

    public bool DialogResult { get; private set; } = false;
    public List<string> ResultPaths { get; private set; } = new();

    public SequenceNavigator Navigator { get; } = new();
    public bool HasMultipleImages => _sourceFiles.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "...";

    [ObservableProperty] private double _minValue = 1.0;
    [ObservableProperty] private double _maxValue = 65535.0;
    [ObservableProperty] private double _stepSize = 100.0;
    [ObservableProperty] private bool _isMonochromatic = false;

    [ObservableProperty] private FitsRenderer? _previewRenderer;
    [ObservableProperty] private bool _hasCalculatedProfile = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => $"{LocalizationManager.Instance["ExportExecute"]} {(SelectedTabIndex == 0 ? "PNG" : "CSV")}";

    [ObservableProperty] private Bitmap? _overlayImage;

    private readonly Dictionary<int, (Bitmap Overlay, List<EllipticalIsophoteDataPoint> Points)> _previewCache = new();
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

    partial void OnIsMonochromaticChanged(bool value) => _ = CalculatePreviewAsync();

    public void TriggerCalculation() => _ = CalculatePreviewAsync();

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadFrameAtIndexAsync(index);
    }

    private EllipticalIsophoteParameters GetCurrentParameters() => new()
    {
        MinValue = MinValue, MaxValue = MaxValue, StepSize = StepSize, 
        IsMonochromatic = IsMonochromatic,
        MonoColorHex = GetSelectionColorHex()
    };

    private async Task InitializeAsync()
    {
        if (_sourceFiles.Count == 0) return;
        var currentFile = _sourceFiles[Navigator.CurrentIndex];

        IsLoading = true;
        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(currentFile.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                Cv2.MinMaxLoc(srcMat, out double minVal, out double maxVal);
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    MaxValue = Math.Round(maxVal, 1);
                    MinValue = Math.Max(1.0, Math.Round(minVal, 1));
                    StepSize = Math.Max(1.0, Math.Round((MaxValue - MinValue) / 50.0, 0));
                });

                var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await renderer.InitializeAsync();
                PreviewRenderer = renderer; 
                
                _ = CalculatePreviewAsync();
            }
        }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message); 
        }
        finally { IsLoading = false; }
    }

    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;

        IsLoading = true;
        StatusMessage = LocalizationManager.Instance["StatusAnalyzing"] ?? "Analyzing...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _previewCache.Clear();

        try
        {
            int frameIdx = Navigator.CurrentIndex;
            var file = _sourceFiles[frameIdx];

            using var result = await _coordinator.AnalyzeProfileAsync(file, GetCurrentParameters(), _cts.Token);
            if (_cts.Token.IsCancellationRequested || result.OverlayImage == null) return;

            var bmpOverlay = CreateAvaloniaBitmap(result.OverlayImage);
            var pts = new List<EllipticalIsophoteDataPoint>(result.Isophotes);

            lock (_previewCache) { _previewCache[frameIdx] = (bmpOverlay, pts); }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HasCalculatedProfile = true;
                OverlayImage = bmpOverlay;
                Isophotes.Clear();
                foreach(var pt in pts) Isophotes.Add(pt);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Done.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); 
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (Isophotes.Count == 0) return;

        if (SelectedTabIndex == 0) 
        {
            try
            {
                if (OverlayImage == null) return;
                string? destPath = await _windowService.ShowSaveFileDialogAsync("PNG", "isophotes_overlay.png");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                OverlayImage.Save(destPath);
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
                string? destPath = await _windowService.ShowSaveFileDialogAsync("CSV", "isophotes_data.csv");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                var sb = new StringBuilder();
                sb.AppendLine("Livello (ADU),Area (px)");
                foreach (var pt in Isophotes)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1}", pt.MeanValue, pt.PixelCount));
                
                await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
            }
            catch (Exception ex) 
            { 
                StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); 
            }
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = LocalizationManager.Instance["StatusProcessing"] ?? "Processing...";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "TopologicalIsophotes");
            Directory.CreateDirectory(outputDir);

            string baseId = Guid.NewGuid().ToString("N");
            string csvPath = Path.Combine(outputDir, $"Isophotes_{baseId}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Livello (ADU),Area (px)");
            foreach (var pt in Isophotes)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1}", pt.MeanValue, pt.PixelCount));
            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);

            var pngPaths = new List<string>();
            var parameters = GetCurrentParameters();

            for (int frameIdx = 0; frameIdx < _sourceFiles.Count; frameIdx++)
            {
                var file = _sourceFiles[frameIdx];
                string pngPath = Path.Combine(outputDir, $"Isophotes_{frameIdx}_{Guid.NewGuid():N}.png");

                using var result = await _coordinator.AnalyzeProfileAsync(file, parameters, _cts.Token);
                if (result.OverlayImage == null) continue;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    using var bmp = CreateAvaloniaBitmap(result.OverlayImage);
                    bmp.Save(pngPath);
                });

                if (File.Exists(pngPath)) pngPaths.Add(pngPath);
            }

            ResultPaths = pngPaths;
            ResultPaths.Add(csvPath);

            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); 
        }
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
                
                PreviewRenderer?.Dispose();
                PreviewRenderer = renderer; 
            }

            if (HasCalculatedProfile)
            {
                lock (_previewCache)
                {
                    if (_previewCache.TryGetValue(index, out var cacheItem))
                    {
                        OverlayImage = cacheItem.Overlay;
                        Isophotes.Clear();
                        foreach (var pt in cacheItem.Points) Isophotes.Add(pt);
                    }
                    else _ = CalculatePreviewAsync(); 
                }
            }
        }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message); 
        }
    }

    private Bitmap CreateAvaloniaBitmap(Mat bgraMat)
    {
        var bmp = new WriteableBitmap(
            new PixelSize(bgraMat.Cols, bgraMat.Rows),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);

        using (var locked = bmp.Lock())
        {
            using var dstMat = Mat.FromPixelData(bgraMat.Rows, bgraMat.Cols, MatType.CV_8UC4, locked.Address, locked.RowBytes);
            bgraMat.CopyTo(dstMat);
        }
        return bmp;
    }

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _cts?.Dispose();
        OverlayImage?.Dispose();
        foreach (var item in _previewCache.Values) item.Overlay?.Dispose();
        _previewCache.Clear();
    }
}