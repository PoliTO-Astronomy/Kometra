using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Infrastructure;
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
    [ObservableProperty] private string _windowTitle = LocalizationManager.Instance["StatsWindowTitle"] ?? "FITS Statistics";
    [ObservableProperty] private string _statPath = "---";
    [ObservableProperty] private string _statDimension = "---";
    [ObservableProperty] private string _statPixel = "---";
    [ObservableProperty] private string _statRange = "---";
    [ObservableProperty] private string _statMin = "---";
    [ObservableProperty] private string _statMax = "---";
    [ObservableProperty] private string _statBackground = "---";
    [ObservableProperty] private string _statStdDev = "---";
    [ObservableProperty] private string _statMean = "---";
    [ObservableProperty] private string _statSum = "---";

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

    private static string FormatAdu(double val)
    {
        return Math.Abs(val - Math.Round(val)) < 1e-4
            ? Math.Round(val).ToString("0", CultureInfo.InvariantCulture)
            : val.ToString("0.###", CultureInfo.InvariantCulture);
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
                    using Mat doubleMat = new Mat();

                    if (srcMat.Type() != MatType.CV_64FC1) srcMat.ConvertTo(doubleMat, MatType.CV_64FC1);
                    else srcMat.CopyTo(doubleMat);

                    int rows = doubleMat.Rows;
                    int cols = doubleMat.Cols;
                    double[] pixels = new double[rows * cols];
                    System.Runtime.InteropServices.Marshal.Copy(doubleMat.Data, pixels, 0, pixels.Length);

                    double minVal = double.MaxValue;
                    double maxVal = double.MinValue;
                    Point minLoc = new Point(0, 0);
                    Point maxLoc = new Point(0, 0);

                    List<double> validPixels = new List<double>(pixels.Length);
                    double sum = 0;
                    int idx = 0;

                    for (int y = 0; y < rows; y++)
                    {
                        int astroY = (rows - 1) - y;

                        for (int x = 0; x < cols; x++)
                        {
                            double p = pixels[idx++];
                            if (!double.IsNaN(p) && !double.IsInfinity(p))
                            {
                                validPixels.Add(p);
                                sum += p;
                                if (p < minVal) { minVal = p; minLoc = new Point(x, astroY); }
                                if (p > maxVal) { maxVal = p; maxLoc = new Point(x, astroY); }
                            }
                        }
                    }

                    if (token.IsCancellationRequested) return;

                    double mean = 0;
                    double background = 0;
                    double stdDev = 0;

                    if (validPixels.Count > 1)
                    {
                        mean = sum / validPixels.Count;

                        double sqSum = 0;
                        foreach (var p in validPixels)
                        {
                            sqSum += (p - mean) * (p - mean);
                        }
                        stdDev = Math.Sqrt(sqSum / (validPixels.Count - 1));

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
                        mean = validPixels[0];
                        background = validPixels[0];
                        stdDev = 0;
                    }
                    else
                    {
                        minVal = 0;
                        maxVal = 0;
                    }

                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        WindowTitle = $"#{Navigator.DisplayIndex} {Path.GetFileName(file.FilePath)}";
                        StatPath = file.FilePath;
                        StatDimension = $"{cols} x {rows}";
                        StatPixel = (cols * rows).ToString(CultureInfo.InvariantCulture);
                        StatRange = $"{FormatAdu(minVal)} : {FormatAdu(maxVal)}";
                        StatMin = $"{FormatAdu(minVal)} @ {minLoc.X},{minLoc.Y}";
                        StatMax = $"{FormatAdu(maxVal)} @ {maxLoc.X},{maxLoc.Y}";
                        StatBackground = FormatAdu(background);
                        StatStdDev = stdDev.ToString("F5", CultureInfo.InvariantCulture);
                        StatMean = mean.ToString("F3", CultureInfo.InvariantCulture);
                        StatSum = Math.Round(sum).ToString("0", CultureInfo.InvariantCulture);
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