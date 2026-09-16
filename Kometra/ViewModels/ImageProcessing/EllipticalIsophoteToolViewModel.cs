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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.Factories;
using Kometra.ViewModels.Visualization;
using Kometra.Services.UI;
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
    [ObservableProperty] private string _statusMessage = "Pronto. Imposta i livelli e premi 'Calcola Anteprima'.";

    [ObservableProperty] private double _minValue = 1.0;
    [ObservableProperty] private double _maxValue = 65535.0;
    [ObservableProperty] private double _stepSize = 100.0;
    [ObservableProperty] private bool _isMonochromatic = false;

    [ObservableProperty] private FitsRenderer? _previewRenderer;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanModifyInput))]
    private bool _hasCalculatedProfile = false;

    public bool CanModifyInput => !HasCalculatedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => SelectedTabIndex == 0 ? "Esporta PNG" : "Esporta CSV";

    [ObservableProperty] private Bitmap? _overlayImage;

    // Cache per l'anteprima PNG composita
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
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore di inizializzazione: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;

        IsLoading = true;
        StatusMessage = "Estrazione dei contorni topologici in corso...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        _previewCache.Clear();

        try
        {
            await Task.Run(async () =>
            {
                int frameIdx = Navigator.CurrentIndex;
                var file = _sourceFiles[frameIdx];

                var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
                var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                if (hdu == null) return;

                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                using Mat floatMat = new Mat();
                if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                else srcMat.CopyTo(floatMat);

                using Mat base8Bit = new Mat();
                Cv2.Normalize(srcMat, base8Bit, 0, 255, NormTypes.MinMax, MatType.CV_8UC1.Value);
                using Mat compositeBgra = new Mat();
                Cv2.CvtColor(base8Bit, compositeBgra, ColorConversionCodes.GRAY2BGRA);

                var tempPoints = new List<EllipticalIsophoteDataPoint>();
                var validContours = new List<(double Level, OpenCvSharp.Point[][] Contours, double Area)>();

                for (double level = MinValue; level <= MaxValue; level += StepSize)
                {
                    using Mat mask = new Mat();
                    Cv2.Threshold(floatMat, mask, level, 255, ThresholdTypes.Binary);
                    mask.ConvertTo(mask, MatType.CV_8UC1);

                    Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);

                    if (contours.Length > 0)
                    {
                        double area = contours.Sum(c => Cv2.ContourArea(c));
                        validContours.Add((level, contours, area));
                    }
                }

                int totalContours = validContours.Count;

                for (int i = 0; i < totalContours; i++)
                {
                    var item = validContours[i];
                    double colorRatio = totalContours > 1 ? (double)i / (totalContours - 1) : 1.0;
                    
                    Scalar overlayColor = IsMonochromatic 
                        ? new Scalar(232, 88, 128, 255) 
                        : GetJetColorWithAlpha(colorRatio);
                    
                    Cv2.DrawContours(compositeBgra, item.Contours, -1, overlayColor, 1);
                    tempPoints.Add(new EllipticalIsophoteDataPoint { MeanValue = item.Level, PixelCount = (int)item.Area });
                }

                var bmpOverlay = CreateAvaloniaBitmap(compositeBgra);

                lock (_previewCache)
                {
                    _previewCache[frameIdx] = (bmpOverlay, tempPoints);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    HasCalculatedProfile = true;
                    OverlayImage = bmpOverlay;
                    
                    Isophotes.Clear();
                    foreach(var pt in tempPoints) Isophotes.Add(pt);
                    
                    StatusMessage = "Anteprima generata. L'immagine composita si trova nel Tab. Premi Conferma per generare il nodo.";
                });

            }, _cts.Token);
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
    private void ResetToPreview()
    {
        HasCalculatedProfile = false;
        Isophotes.Clear();
        OverlayImage = null;
        StatusMessage = "Visualizzazione ripristinata. Modifica i parametri e ricalcola l'anteprima.";
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
                string? destPath = await _windowService.ShowSaveFileDialogAsync("Esporta PNG", "isophotes_overlay.png");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                OverlayImage.Save(destPath);
                StatusMessage = $"Immagine salvata con successo in: {Path.GetFileName(destPath)}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore esportazione PNG: {ex.Message}";
            }
        }
        else 
        {
            try
            {
                string? destPath = await _windowService.ShowSaveFileDialogAsync("Esporta Dati Isofote CSV", "isophotes_data.csv");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                var sb = new StringBuilder();
                sb.AppendLine("Livello (ADU),Area (px)");
                foreach (var pt in Isophotes)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1}", pt.MeanValue, pt.PixelCount));
                }
                await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = $"CSV salvato con successo in: {Path.GetFileName(destPath)}";
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
        StatusMessage = "Generazione dei file immagine compositi per l'intera sequenza...";

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
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1}", pt.MeanValue, pt.PixelCount));
            }
            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);

            var pngPaths = new List<string>();

            for (int frameIdx = 0; frameIdx < _sourceFiles.Count; frameIdx++)
            {
                var file = _sourceFiles[frameIdx];
                string pngPath = Path.Combine(outputDir, $"Isophotes_{frameIdx}_{Guid.NewGuid():N}.png");

                await Task.Run(() =>
                {
                    var dataPackage = _dataManager.LoadDataPackageAsync(file.FilePath).GetAwaiter().GetResult();
                    var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                    if (hdu == null) return;

                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    using Mat floatMat = new Mat();
                    if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                    else srcMat.CopyTo(floatMat);

                    using Mat base8Bit = new Mat();
                    Cv2.Normalize(srcMat, base8Bit, 0, 255, NormTypes.MinMax, MatType.CV_8UC1.Value);
                    using Mat compositeBgra = new Mat();
                    Cv2.CvtColor(base8Bit, compositeBgra, ColorConversionCodes.GRAY2BGRA);

                    var validContours = new List<OpenCvSharp.Point[][]>();

                    for (double level = MinValue; level <= MaxValue; level += StepSize)
                    {
                        using Mat mask = new Mat();
                        Cv2.Threshold(floatMat, mask, level, 255, ThresholdTypes.Binary);
                        mask.ConvertTo(mask, MatType.CV_8UC1);
                        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);

                        if (contours.Length > 0)
                        {
                            validContours.Add(contours);
                        }
                    }

                    int totalContours = validContours.Count;
                    for (int i = 0; i < totalContours; i++)
                    {
                        double colorRatio = totalContours > 1 ? (double)i / (totalContours - 1) : 1.0;
                        Scalar overlayColor = IsMonochromatic 
                            ? new Scalar(232, 88, 128, 255) 
                            : GetJetColorWithAlpha(colorRatio);
                        
                        Cv2.DrawContours(compositeBgra, validContours[i], -1, overlayColor, 1);
                    }

                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        using var bmp = CreateAvaloniaBitmap(compositeBgra);
                        bmp.Save(pngPath);
                    }).GetAwaiter().GetResult();

                }, _cts.Token);

                if (File.Exists(pngPath)) pngPaths.Add(pngPath);
            }

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
                    else
                    {
                        _ = CalculatePreviewAsync(); 
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore caricamento frame {index + 1}: {ex.Message}";
        }
    }

    private Scalar GetJetColorWithAlpha(double ratio)
    {
        byte val = (byte)Math.Clamp(ratio * 255.0, 0, 255);
        using var mat1x1 = new Mat(1, 1, MatType.CV_8UC1, new Scalar(val));
        using var color1x1 = new Mat();
        Cv2.ApplyColorMap(mat1x1, color1x1, ColormapTypes.Jet);
        var vec = color1x1.Get<Vec3b>(0, 0);
        return new Scalar(vec.Item0, vec.Item1, vec.Item2, 255);
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