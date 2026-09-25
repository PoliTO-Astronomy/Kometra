using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

public class IsophoteCsvRow
{
    public double Sma { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}

public partial class EllipticalIsophoteToolViewModel : ObservableObject, IDisposable
{
    private readonly IEllipticalIsophoteCoordinator _coordinator;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IWindowService _windowService;
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;
    private bool _isInitialized;

    public event Action? RequestClose;

    public bool DialogResult { get; private set; }
    public List<string>? ResultPaths { get; private set; }

    public SequenceNavigator Navigator { get; } = new();
    public AlignmentImageViewport Viewport { get; } = new();
    
    public bool HasMultipleImages => _sourceFiles.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_sourceFiles.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = "...";

    [ObservableProperty] private double _minValue = 1.0;
    [ObservableProperty] private double _maxValue = 65535.0;
    [ObservableProperty] private double _stepSize = 100.0;

    [ObservableProperty] private FitsRenderer? _previewRenderer;
    [ObservableProperty] private bool _hasCalculatedProfile = false;

    [ObservableProperty] private Bitmap? _overlayImage;

    private readonly List<IsophoteCsvRow> _fullCsvData = new();
    [ObservableProperty] private ObservableCollection<IsophoteCsvRow> _csvPreviewRows = new();

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

    [RelayCommand] private void ResetView() => Viewport.ResetView();
    [RelayCommand] private async Task ResetThresholds() { if (PreviewRenderer != null) await PreviewRenderer.ResetThresholdsAsync(); }
    public void TriggerCalculation() => _ = CalculatePreviewAsync();

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadFrameAtIndexAsync(index);
    }

    private EllipticalIsophoteParameters GetCurrentParameters() => new()
    {
        MinValue = MinValue, MaxValue = MaxValue, StepSize = StepSize, 
        IsMonochromatic = false,
        MonoColorHex = "#FFFFFF" 
    };

    private (double bscale, double bzero) GetBScaleBZero(object headerObj)
    {
        double bscale = 1.0;
        double bzero = 0.0;
        try 
        {
            dynamic dynHeader = headerObj;
            System.Collections.IEnumerable? cardsEnum = null;
            var cardsProp = dynHeader.GetType().GetProperty("Cards") ?? dynHeader.GetType().GetProperty("Records");
            
            if (cardsProp != null) cardsEnum = cardsProp.GetValue(dynHeader) as System.Collections.IEnumerable;

            if (cardsEnum != null)
            {
                foreach (var card in cardsEnum)
                {
                    var keyProp = card.GetType().GetProperty("Key");
                    var valProp = card.GetType().GetProperty("Value");
                    string key = keyProp?.GetValue(card)?.ToString()?.ToUpper() ?? "";
                    string val = valProp?.GetValue(card)?.ToString() ?? "";
                    if (key == "BSCALE") double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out bscale);
                    if (key == "BZERO") double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out bzero);
                }
            }
        } 
        catch { }
        return (bscale, bzero);
    }

    private Bitmap? RawMaskToOverlayBitmapSafe(Mat maskMat)
    {
        if (maskMat.Empty()) return null;

        int height = maskMat.Rows;
        int width = maskMat.Cols;

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), 
            new Vector(96, 96), 
            Avalonia.Platform.PixelFormat.Bgra8888, 
            Avalonia.Platform.AlphaFormat.Unpremul);

        byte[] bytes = new byte[width * height];
        System.Runtime.InteropServices.Marshal.Copy(maskMat.Data, bytes, 0, bytes.Length);

        using (var frameBuffer = bitmap.Lock())
        {
            IntPtr backBuffer = frameBuffer.Address;
            int rowBytes = frameBuffer.RowBytes;

            Parallel.For(0, height, y =>
            {
                int[] rowBuffer = new int[width];
                int offset = y * width;
                for (int x = 0; x < width; x++)
                {
                    byte intensity = bytes[offset + x];
                    if (intensity > 0)
                    {
                        rowBuffer[x] = (255 << 24) | (intensity << 16) | (intensity << 8) | intensity;
                    }
                    else
                    {
                        rowBuffer[x] = 0;
                    }
                }
            
                IntPtr destPtr = IntPtr.Add(backBuffer, y * rowBytes);
                System.Runtime.InteropServices.Marshal.Copy(rowBuffer, 0, destPtr, width);
            });
        }

        return bitmap;
    }

    private async Task InitializeAsync()
    {
        if (_sourceFiles.Count == 0) return;
        await LoadFrameAtIndexAsync(Navigator.CurrentIndex);
    }

    private async Task CalculatePreviewAsync()
    {
        if (_sourceFiles.Count == 0) return;

        IsLoading = true;
        StatusMessage = LocalizationManager.Instance["StatusAnalyzing"] ?? "Analyzing...";

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            int frameIdx = Navigator.CurrentIndex;
            var file = _sourceFiles[frameIdx];

            _fullCsvData.Clear();
            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            
            if (token.IsCancellationRequested) return;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                using Mat floatMat = new Mat();
                if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                else srcMat.CopyTo(floatMat);

                Cv2.PatchNaNs(floatMat, 0.0);
                var (bscale, bzero) = GetBScaleBZero(hdu.Header);
                var param = GetCurrentParameters();

                using Mat allContoursMask = new Mat(floatMat.Rows, floatMat.Cols, MatType.CV_8UC1, new Scalar(0));
                
                double range = param.MaxValue - param.MinValue;
                if (range <= 0) range = 1;

                for (double level = param.MinValue; level <= param.MaxValue; level += param.StepSize)
                {
                    if (token.IsCancellationRequested) return;

                    double unscaledLevel = (level - bzero) / bscale;
                    using Mat mask32 = new Mat();
                    using Mat mask = new Mat();
                    
                    Cv2.Threshold(floatMat, mask32, unscaledLevel, 255.0, ThresholdTypes.Binary);
                    mask32.ConvertTo(mask, MatType.CV_8UC1);
                    Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
                    
                    byte intensity = (byte)Math.Clamp(255 * (level - param.MinValue) / range, 80, 255);
                    Cv2.DrawContours(allContoursMask, contours, -1, new Scalar(intensity), 1);

                    foreach (var contour in contours)
                    {
                        if (contour.Length >= 5)
                        {
                            var ellipse = Cv2.FitEllipse(contour);
                            double sma = Math.Max(ellipse.Size.Width, ellipse.Size.Height) / 2.0;
                            foreach (var pt in contour)
                            {
                                _fullCsvData.Add(new IsophoteCsvRow { Sma = sma, X = pt.X, Y = pt.Y });
                            }
                        }
                    }
                }

                if (token.IsCancellationRequested) return;

                var overlayBmp = RawMaskToOverlayBitmapSafe(allContoursMask);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (token.IsCancellationRequested) return;
                    OverlayImage = overlayBmp;
                    CsvPreviewRows = new ObservableCollection<IsophoteCsvRow>(_fullCsvData);

                    HasCalculatedProfile = true;
                    StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Done.";
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); 
        }
        finally 
        { 
            if (!(_cts?.Token.IsCancellationRequested ?? false)) IsLoading = false; 
        }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (_fullCsvData.Count == 0) return;

        try
        {
            string? destPath = await _windowService.ShowSaveFileDialogAsync("CSV", "isophotes_sma_x_y.csv");
            if (string.IsNullOrWhiteSpace(destPath)) return;

            var sb = new StringBuilder();
            sb.AppendLine("sma,x,y");
            foreach (var pt in _fullCsvData)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", pt.Sma, pt.X, pt.Y));
            }
            
            await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
            StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
        }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); 
        }
    }

    private async Task<string> SaveIsophoteHduAsync(Kometra.Models.Fits.Structure.FitsHeader header, float[,] pixelData)
    {
        header.AddOrUpdateCard("BITPIX", "-32", "IEEE single precision floating point");
        header.AddOrUpdateCard("BZERO", "0.0", "Offset");
        header.AddOrUpdateCard("BSCALE", "1.0", "Scale");

        var fileRef = await _dataManager.SaveAsTemporaryAsync(pixelData, header, "TopologicalIsophotes");
        return fileRef.FilePath;
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
            var parameters = GetCurrentParameters();
            var newPaths = new List<string>();

            for (int frameIdx = 0; frameIdx < _sourceFiles.Count; frameIdx++)
            {
                var file = _sourceFiles[frameIdx];

                var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
                var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                if (hdu?.PixelData != null && hdu.Header != null)
                {
                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    using Mat floatMat = new Mat();
                    if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                    else srcMat.CopyTo(floatMat);

                    Cv2.PatchNaNs(floatMat, 0.0);
                    var (bscale, bzero) = GetBScaleBZero(hdu.Header);

                    using Mat allContoursMask = new Mat(floatMat.Rows, floatMat.Cols, MatType.CV_8UC1, new Scalar(0));

                    for (double level = parameters.MinValue; level <= parameters.MaxValue; level += parameters.StepSize)
                    {
                        double unscaledLevel = (level - bzero) / bscale;
                        using Mat mask32 = new Mat();
                        using Mat mask = new Mat();
                        
                        Cv2.Threshold(floatMat, mask32, unscaledLevel, 255.0, ThresholdTypes.Binary);
                        mask32.ConvertTo(mask, MatType.CV_8UC1);
                        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
                        Cv2.DrawContours(allContoursMask, contours, -1, new Scalar(255), 1);
                    }

                    float[] originalPixels = new float[floatMat.Rows * floatMat.Cols];
                    byte[] maskPixels = new byte[allContoursMask.Rows * allContoursMask.Cols];
                    float[,] outputPixels2D = new float[floatMat.Rows, floatMat.Cols];

                    System.Runtime.InteropServices.Marshal.Copy(floatMat.Data, originalPixels, 0, originalPixels.Length);
                    System.Runtime.InteropServices.Marshal.Copy(allContoursMask.Data, maskPixels, 0, maskPixels.Length);

                    double finalMin = double.MaxValue;
                    double finalMax = double.MinValue;

                    int idx = 0;
                    for (int y = 0; y < floatMat.Rows; y++)
                    {
                        for (int x = 0; x < floatMat.Cols; x++)
                        {
                            if (maskPixels[idx] > 0)
                            {
                                float scaledVal = (float)((originalPixels[idx] * bscale) + bzero);
                                outputPixels2D[y, x] = scaledVal;
                                if (scaledVal < finalMin) finalMin = scaledVal;
                                if (scaledVal > finalMax) finalMax = scaledVal;
                            }
                            else
                            {
                                outputPixels2D[y, x] = 0f;
                                if (0f < finalMin) finalMin = 0f;
                                if (0f > finalMax) finalMax = 0f;
                            }
                            idx++;
                        }
                    }

                    if (finalMin == double.MaxValue) finalMin = 0;
                    if (finalMax == double.MinValue) finalMax = 1;

                    hdu.Header.AddOrUpdateCard("DATAMIN", finalMin.ToString("0.0", CultureInfo.InvariantCulture), "Minimum data value");
                    hdu.Header.AddOrUpdateCard("DATAMAX", finalMax.ToString("0.0", CultureInfo.InvariantCulture), "Maximum data value");

                    string savedPath = await SaveIsophoteHduAsync(hdu.Header, outputPixels2D);
                    if (File.Exists(savedPath)) newPaths.Add(savedPath);
                }
            }

            ResultPaths = newPaths;
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
                if (!_isInitialized)
                {
                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    using Mat floatMat = new Mat();
                    if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                    else srcMat.CopyTo(floatMat);

                    Cv2.PatchNaNs(floatMat, 0.0);
                    Cv2.MinMaxLoc(floatMat, out double rawMin, out double rawMax);
                    var (bscale, bzero) = GetBScaleBZero(hdu.Header);

                    double minVal = (rawMin * bscale) + bzero;
                    double maxVal = (rawMax * bscale) + bzero;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        MaxValue = Math.Round(maxVal, 1);
                        MinValue = Math.Max(1.0, Math.Round(minVal, 1));
                        StepSize = Math.Max(1.0, Math.Round((MaxValue - MinValue) / 50.0, 0));
                    });

                    _isInitialized = true;
                }

                var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await renderer.InitializeAsync();
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PreviewRenderer?.Dispose();
                    PreviewRenderer = renderer; 
                    Viewport.ImageSize = PreviewRenderer.ImageSize;
                    Viewport.ResetView();
                });

                _ = CalculatePreviewAsync();
            }
        }
        catch (Exception ex) 
        { 
            StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message); 
        }
    }

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _cts?.Dispose();
        OverlayImage?.Dispose();
        PreviewRenderer?.Dispose();
    }
}