using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Services.Fits;
using OpenCvSharp;
using SequenceNavigator = Kometra.ViewModels.Shared.SequenceNavigator;

namespace Kometra.ViewModels.Fits;

public partial class FitsStatisticsViewModel : ObservableObject, IDisposable
{
    private readonly IFitsDataManager _dataManager;
    private readonly List<FitsFileReference> _files;
    private CancellationTokenSource? _cts;

    public event Action? RequestClose;

    public SequenceNavigator Navigator { get; } = new();
    public bool HasMultipleImages => _files.Count > 1;
    public string CurrentImageText => $"{Navigator.DisplayIndex} / {_files.Count}";

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _windowTitle = "Statistiche FITS";
    [ObservableProperty] private string _statPath = "---";
    [ObservableProperty] private string _statDimension = "---";
    [ObservableProperty] private string _statPixel = "---";
    [ObservableProperty] private string _statRange = "---";
    [ObservableProperty] private string _statMin = "---";
    [ObservableProperty] private string _statMax = "---";
    [ObservableProperty] private string _statBackground = "---";
    [ObservableProperty] private string _statStdDev = "---";

    public FitsStatisticsViewModel(List<FitsFileReference> files, IFitsDataManager dataManager)
    {
        _files = files ?? new List<FitsFileReference>();
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));

        Navigator.UpdateStatus(0, _files.Count);
        Navigator.IndexChanged += OnNavigatorIndexChanged;

        _ = LoadStatisticsAtIndexAsync(0);
    }

    private async void OnNavigatorIndexChanged(object? sender, int index)
    {
        OnPropertyChanged(nameof(CurrentImageText));
        await LoadStatisticsAtIndexAsync(index);
    }

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

    private async Task LoadStatisticsAtIndexAsync(int index)
    {
        if (index < 0 || index >= _files.Count) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = true;
        var file = _files[index];

        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (token.IsCancellationRequested) return;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                await Task.Run(() =>
                {
                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    using Mat floatMat = new Mat();

                    if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                    else srcMat.CopyTo(floatMat);

                    float[] pixels = new float[floatMat.Rows * floatMat.Cols];
                    System.Runtime.InteropServices.Marshal.Copy(floatMat.Data, pixels, 0, pixels.Length);

                    var (bscale, bzero) = GetBScaleBZero(hdu.Header);

                    double minVal = double.MaxValue;
                    double maxVal = double.MinValue;
                    Point minLoc = new Point(0, 0);
                    Point maxLoc = new Point(0, 0);

                    List<double> validPixels = new List<double>(pixels.Length);
                    double sum = 0;
                    int idx = 0;

                    for (int y = 0; y < floatMat.Rows; y++)
                    {
                        for (int x = 0; x < floatMat.Cols; x++)
                        {
                            float p = pixels[idx++];
                            if (!float.IsNaN(p) && !float.IsInfinity(p))
                            {
                                double scaledValue = (p * bscale) + bzero;
                                validPixels.Add(scaledValue);
                                sum += scaledValue;
                                if (scaledValue < minVal) { minVal = scaledValue; minLoc = new Point(x, y); }
                                if (scaledValue > maxVal) { maxVal = scaledValue; maxLoc = new Point(x, y); }
                            }
                        }
                    }

                    if (token.IsCancellationRequested) return;

                    double background = 0;
                    double stdDev = 0;

                    if (validPixels.Count > 1)
                    {
                        // Deviazione standard campionaria (N - 1)
                        double mean = sum / validPixels.Count;
                        double sqSum = 0;
                        foreach (var p in validPixels)
                        {
                            sqSum += (p - mean) * (p - mean);
                        }
                        stdDev = Math.Sqrt(sqSum / (validPixels.Count - 1));

                        // Background calcolato come mediana con Sigma Clipping iterativo a 3-sigma
                        List<double> clipData = new List<double>(validPixels);
                        for (int iter = 0; iter < 3; iter++)
                        {
                            if (clipData.Count < 2) break;

                            double iterMean = 0;
                            foreach (var val in clipData) iterMean += val;
                            iterMean /= clipData.Count;

                            double iterSqSum = 0;
                            foreach (var val in clipData) iterSqSum += (val - iterMean) * (val - iterMean);
                            double iterStdDev = Math.Sqrt(iterSqSum / (clipData.Count - 1));

                            double lowerBound = iterMean - 3 * iterStdDev;
                            double upperBound = iterMean + 3 * iterStdDev;

                            int countBefore = clipData.Count;
                            clipData = clipData.FindAll(v => v >= lowerBound && v <= upperBound);

                            if (clipData.Count == countBefore) break;
                        }

                        clipData.Sort();
                        background = clipData.Count > 0 ? clipData[clipData.Count / 2] : 0;
                    }
                    else if (validPixels.Count == 1)
                    {
                        background = validPixels[0];
                        stdDev = 0;
                    }
                    else
                    {
                        minVal = 0;
                        maxVal = 0;
                    }

                    int cols = floatMat.Cols;
                    int rows = floatMat.Rows;

                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        WindowTitle = $"#{Navigator.DisplayIndex} {Path.GetFileName(file.FilePath)}";
                        StatPath = file.FilePath;
                        StatDimension = $"{cols} x {rows}";
                        StatPixel = (cols * rows).ToString(CultureInfo.InvariantCulture);
                        StatRange = string.Format(CultureInfo.InvariantCulture, "{0:F3} : {1:F1}", minVal, maxVal);
                        StatMin = string.Format(CultureInfo.InvariantCulture, "{0:F3} @ {1},{2}", minVal, minLoc.X, minLoc.Y);
                        StatMax = string.Format(CultureInfo.InvariantCulture, "{0:F1} @ {1},{2}", maxVal, maxLoc.X, maxLoc.Y);
                        StatBackground = background.ToString("F1", CultureInfo.InvariantCulture);
                        StatStdDev = stdDev.ToString("F4", CultureInfo.InvariantCulture);
                    });
                }, token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (!token.IsCancellationRequested) IsLoading = false;
        }
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    public void Dispose()
    {
        Navigator.IndexChanged -= OnNavigatorIndexChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}