using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Factories;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.UI;
using Kometra.ViewModels.Visualization;
using OpenCvSharp;

namespace Kometra.ViewModels.ImageProcessing;

public class PhotometricDataPoint
{
    public int PixelIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Distance { get; set; }
    public double Value { get; set; }
    public string CoordinateText => $"[{X}, {Y}]";
}

public partial class PhotometricClippingToolViewModel : ObservableObject, IDisposable
{
    private readonly IPhotometryCoordinator _coordinator;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IFitsOpenCvConverter _converter;
    private readonly IFitsDataManager _dataManager;
    private readonly IWindowService _windowService;
    
    private readonly List<FitsFileReference> _sourceFiles;
    
    [ObservableProperty]
    private int _currentFileIndex = 0;

    [ObservableProperty] private FitsRenderer? _viewport;

    public ObservableCollection<PhotometricDataPoint> ProfileData { get; } = new();

    public event Action<string>? SavePlotRequested;
    public event Action? RequestClose;

    public List<string> ResultPaths { get; private set; } = new();
    public bool DialogResult { get; private set; }

    [ObservableProperty] private int _startX = 0;
    [ObservableProperty] private int _startY = 0;
    [ObservableProperty] private int _endX = 100;
    [ObservableProperty] private int _endY = 100;
    [ObservableProperty] private int _imageWidth = 1000;
    [ObservableProperty] private int _imageHeight = 1000;

    [ObservableProperty] private string _infoDistance = "Distanza: 0 pixel";
    [ObservableProperty] private string _infoPeak = "Picco MAX: - ADU";
    [ObservableProperty] private string _statusMessage = "Pronto. Clicca e trascina sull'immagine.";
    [ObservableProperty] private bool _isLoading;
    
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(CanModifyInput))]
    private bool _hasCalculatedProfile = false;

    public bool CanModifyInput => !HasCalculatedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => SelectedTabIndex == 0 ? "Esporta PNG" : "Esporta CSV";

    public string CurrentImageText => $"{CurrentFileIndex + 1} / {_sourceFiles.Count}";
    public bool HasMultipleFiles => _sourceFiles.Count > 1;

    public PhotometricClippingToolViewModel(
        List<FitsFileReference> sourceFiles,
        IPhotometryCoordinator coordinator,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter,
        IFitsDataManager dataManager,
        IWindowService windowService)
    {
        _sourceFiles = sourceFiles ?? throw new ArgumentException("Nessun file fornito.");
        _coordinator = coordinator;
        _rendererFactory = rendererFactory;
        _converter = converter;
        _dataManager = dataManager;
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));

        _ = LoadCurrentImageAsync();
    }

    private async Task LoadCurrentImageAsync()
    {
        if (_sourceFiles.Count == 0) return;
        try
        {
            var fileRef = _sourceFiles[CurrentFileIndex];
            var dataPackage = await _dataManager.LoadDataPackageAsync(fileRef.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ImageWidth = srcMat.Cols - 1;
                    ImageHeight = srcMat.Rows - 1;
                    
                    if (CurrentFileIndex == 0 && !HasCalculatedProfile)
                    {
                        StartX = srcMat.Cols / 2 - 100;
                        StartY = srcMat.Rows / 2 + 100;
                        EndX = srcMat.Cols / 2 + 100;
                        EndY = srcMat.Rows / 2 - 100;
                    }
                    
                    OnPropertyChanged(nameof(CurrentImageText));
                });

                var renderer = await _rendererFactory.CreateAsync(hdu.PixelData, hdu.Header);
                await renderer.InitializeAsync();
                Viewport = renderer; 
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore di inizializzazione: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task NextImageAsync()
    {
        if (CurrentFileIndex < _sourceFiles.Count - 1)
        {
            CurrentFileIndex++;
            await LoadCurrentImageAsync();
            if (HasCalculatedProfile) await CalculateProfileAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousImageAsync()
    {
        if (CurrentFileIndex > 0)
        {
            CurrentFileIndex--;
            await LoadCurrentImageAsync();
            if (HasCalculatedProfile) await CalculateProfileAsync();
        }
    }

    partial void OnStartXChanged(int value) => OnLineChanged();
    partial void OnStartYChanged(int value) => OnLineChanged();
    partial void OnEndXChanged(int value) => OnLineChanged();
    partial void OnEndYChanged(int value) => OnLineChanged();

    private void OnLineChanged()
    {
        // Nessuna azione immediata fino al calcolo
    }

    [RelayCommand]
    private async Task CalculateProfileAsync()
    {
        IsLoading = true;
        StatusMessage = "Calcolo dell'anteprima in corso...";

        try
        {
            if (_sourceFiles.Count == 0) return;
            var fileRef = _sourceFiles[CurrentFileIndex];
            var dataPackage = await _dataManager.LoadDataPackageAsync(fileRef.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu == null) return;

            using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
            using Mat floatMat = new Mat();
            if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
            else srcMat.CopyTo(floatMat);

            var points = GetBresenhamLine(StartX, StartY, EndX, EndY);
            var tempProfile = new List<PhotometricDataPoint>();
            double maxAdu = double.MinValue;
            
            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];
                if (pt.X >= 0 && pt.X < floatMat.Cols && pt.Y >= 0 && pt.Y < floatMat.Rows)
                {
                    float val = floatMat.At<float>(pt.Y, pt.X);
                    double dist = Math.Sqrt(Math.Pow(pt.X - StartX, 2) + Math.Pow(pt.Y - StartY, 2));
                    
                    if (val > maxAdu) maxAdu = val;

                    tempProfile.Add(new PhotometricDataPoint
                    {
                        PixelIndex = i,
                        X = pt.X,
                        Y = pt.Y,
                        Distance = dist,
                        Value = val
                    });
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProfileData.Clear();
                foreach (var item in tempProfile) ProfileData.Add(item);

                HasCalculatedProfile = ProfileData.Count > 0;
                InfoDistance = $"Distanza: {points.Count} pixel";
                InfoPeak = $"Picco Massimo Rilevato: {maxAdu:F1} ADU";
                StatusMessage = "Anteprima calcolata. Premi Conferma per generare il nodo analitico sulla board.";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore calcolo: {ex.Message}";
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
        ProfileData.Clear();
        StatusMessage = "Modalità tracciamento sbloccata. Modifica la linea e ricalcola l'anteprima.";
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (ProfileData.Count == 0) return;

        if (SelectedTabIndex == 0) 
        {
            try
            {
                string? destPath = await _windowService.ShowSaveFileDialogAsync("Esporta PNG", "photometric_cut.png");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                SavePlotRequested?.Invoke(destPath);
                StatusMessage = $"Grafico salvato in: {Path.GetFileName(destPath)}";
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
                string? destPath = await _windowService.ShowSaveFileDialogAsync("Esporta CSV", "photometric_cut.csv");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                var sb = new StringBuilder();
                sb.AppendLine("Distanza (px),X,Y,Intensita (ADU)");
                foreach (var pt in ProfileData)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}", pt.PixelIndex, pt.X, pt.Y, pt.Value));
                }
                await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = $"CSV salvato in: {Path.GetFileName(destPath)}";
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
        StatusMessage = "Generazione dei grafici della sequenza in corso...";
        
        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "Photometry");
            Directory.CreateDirectory(outputDir);
            
            string baseId = Guid.NewGuid().ToString("N");
            string csvPath = Path.Combine(outputDir, $"PhotometricCut_{baseId}.csv");
            
            var sb = new StringBuilder();
            sb.AppendLine("Distanza (px),X,Y,Intensita (ADU)");
            foreach (var pt in ProfileData)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}", pt.PixelIndex, pt.X, pt.Y, pt.Value));
            }
            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);

            var pngPaths = new List<string>();
            
            foreach (var fileRef in _sourceFiles)
            {
                string pngPath = Path.Combine(outputDir, $"PhotometricCut_{Guid.NewGuid():N}.png");
                
                var dataPackage = await _dataManager.LoadDataPackageAsync(fileRef.FilePath);
                var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
                if (hdu != null)
                {
                    using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                    using Mat floatMat = new Mat();
                    if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
                    else srcMat.CopyTo(floatMat);

                    var points = GetBresenhamLine(StartX, StartY, EndX, EndY);
                    var localProfile = new List<(double Dist, double Val)>();
                    foreach (var pt in points)
                    {
                        if (pt.X >= 0 && pt.X < floatMat.Cols && pt.Y >= 0 && pt.Y < floatMat.Rows)
                        {
                            float val = floatMat.At<float>(pt.Y, pt.X);
                            double dist = Math.Sqrt(Math.Pow(pt.X - StartX, 2) + Math.Pow(pt.Y - StartY, 2));
                            localProfile.Add((dist, val));
                        }
                    }

                    await Task.Run(() => 
                    {
                        var plt = new ScottPlot.Plot();
                        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
                        plt.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
                        plt.Axes.Color(ScottPlot.Color.FromHex("#AAAAAA"));
                        plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");

                        if (localProfile.Count > 0)
                        {
                            double[] xs = localProfile.Select(p => p.Dist).ToArray();
                            double[] ys = localProfile.Select(p => p.Val).ToArray();

                            var line = plt.Add.ScatterLine(xs, ys, ScottPlot.Color.FromHex("#8058E8"));
                            line.LineWidth = 2.0f;
                            
                            plt.Axes.Title.Label.Text = "Taglio Fotometrico";
                            plt.Axes.Bottom.Label.Text = "Distanza (px)";
                            plt.Axes.Left.Label.Text = "Intensita [ADU]";
                            plt.Axes.AutoScale();
                        }
                        plt.SavePng(pngPath, 1200, 800);
                    });
                    pngPaths.Add(pngPath);
                }
            }

            ResultPaths = pngPaths;
            ResultPaths.Add(csvPath);
            
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore durante la generazione: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<(int X, int Y)> GetBresenhamLine(int x0, int y0, int x1, int y1)
    {
        var points = new List<(int X, int Y)>();
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, e2;

        while (true)
        {
            points.Add((x0, y0));
            if (x0 == x1 && y0 == y1) break;
            e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
        return points;
    }

    public void Dispose()
    {
        Viewport?.Dispose();
    }
}