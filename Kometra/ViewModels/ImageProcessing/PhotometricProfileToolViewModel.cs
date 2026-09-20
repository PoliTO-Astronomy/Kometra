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
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Factories;
using Kometra.Services.Processing.Coordinators;
using Kometra.Services.UI;
using Kometra.ViewModels.Visualization;
using Kometra.Infrastructure;
using OpenCvSharp;

namespace Kometra.ViewModels.ImageProcessing;

public partial class PhotometricProfileToolViewModel : ObservableObject, IDisposable
{
    private readonly IPhotometricProfileCoordinator _coordinator;
    private readonly IFitsRendererFactory _rendererFactory;
    private readonly IFitsDataManager _dataManager;
    private readonly IWindowService _windowService;
    
    private readonly List<FitsFileReference> _sourceFiles;
    private CancellationTokenSource? _cts;
    
    [ObservableProperty] private int _currentFileIndex = 0;
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

    [ObservableProperty] private string _infoDistance = "---";
    [ObservableProperty] private string _infoPeak = "---";
    [ObservableProperty] private string _statusMessage = "...";
    [ObservableProperty] private bool _isLoading;
    
    [ObservableProperty] private bool _hasCalculatedProfile = false;

    public bool IsDragging { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => $"{LocalizationManager.Instance["ExportExecute"]} {(SelectedTabIndex == 0 ? "PNG" : "CSV")}";
    public string CurrentImageText => $"{CurrentFileIndex + 1} / {_sourceFiles.Count}";
    public bool HasMultipleFiles => _sourceFiles.Count > 1;

    public PhotometricProfileToolViewModel(
        List<FitsFileReference> sourceFiles,
        IPhotometricProfileCoordinator coordinator,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter,
        IFitsDataManager dataManager,
        IWindowService windowService)
    {
        _sourceFiles = sourceFiles ?? throw new ArgumentException("Nessun file fornito.");
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _rendererFactory = rendererFactory;
        _dataManager = dataManager;
        _windowService = windowService;
        
        StatusMessage = LocalizationManager.Instance["StatusReady"] ?? "Ready.";
        _ = LoadCurrentImageAsync();
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
                
                Viewport?.Dispose();
                Viewport = renderer; 
                
                if (!HasCalculatedProfile) _ = CalculateProfileAsync();
            }
        }
        catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorInit"] ?? "Error: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task NextImageAsync()
    {
        if (CurrentFileIndex < _sourceFiles.Count - 1)
        {
            CurrentFileIndex++;
            await LoadCurrentImageAsync();
            await CalculateProfileAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousImageAsync()
    {
        if (CurrentFileIndex > 0)
        {
            CurrentFileIndex--;
            await LoadCurrentImageAsync();
            await CalculateProfileAsync();
        }
    }

    private PhotometricCutParameters GetCurrentParameters() => new()
    {
        StartX = StartX, StartY = StartY, EndX = EndX, EndY = EndY
    };

    public async Task CalculateProfileAsync()
    {
        if (_sourceFiles.Count == 0) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var fileRef = _sourceFiles[CurrentFileIndex];
            var points = await _coordinator.AnalyzeProfileAsync(fileRef, GetCurrentParameters(), token);
            
            if (token.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProfileData.Clear();
                double maxAdu = double.MinValue;
                foreach (var item in points)
                {
                    if (item.Value > maxAdu) maxAdu = item.Value;
                    ProfileData.Add(item);
                }

                HasCalculatedProfile = ProfileData.Count > 0;
                
                string distLabel = LocalizationManager.Instance["ColDistance"] ?? "Distance";
                string peakLabel = LocalizationManager.Instance["ColValue"] ?? "Value";
                
                InfoDistance = $"{distLabel}: {points.Count} px";
                InfoPeak = $"Max {peakLabel}: {(maxAdu == double.MinValue ? 0 : maxAdu):F1} ADU";
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Done.";
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
    }

    [RelayCommand]
    private async Task ExportDataAsync()
    {
        if (ProfileData.Count == 0) return;

        if (SelectedTabIndex == 0) 
        {
            try
            {
                string? destPath = await _windowService.ShowSaveFileDialogAsync("PNG", "photometric_cut.png");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                SavePlotRequested?.Invoke(destPath);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
            }
            catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
        }
        else 
        {
            try
            {
                string? destPath = await _windowService.ShowSaveFileDialogAsync("CSV", "photometric_cut.csv");
                if (string.IsNullOrWhiteSpace(destPath)) return;

                var sb = new StringBuilder();
                sb.AppendLine("Distanza (px),X,Y,Intensita (ADU)");
                foreach (var pt in ProfileData)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}", pt.PixelIndex, pt.X, pt.Y, pt.Value));
                
                await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = LocalizationManager.Instance["StatusDone"] ?? "Export completed.";
            }
            catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        IsLoading = true;
        StatusMessage = LocalizationManager.Instance["StatusProcessing"] ?? "Processing in progress...";
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        try
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "PhotometricProfile");
            Directory.CreateDirectory(outputDir);
            
            string baseId = Guid.NewGuid().ToString("N");
            string csvPath = Path.Combine(outputDir, $"PhotometricCut_{baseId}.csv");
            
            var sb = new StringBuilder();
            sb.AppendLine("Distanza (px),X,Y,Intensita (ADU)");
            foreach (var pt in ProfileData)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}", pt.PixelIndex, pt.X, pt.Y, pt.Value));
            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);

            var pngPaths = new List<string>();
            var parameters = GetCurrentParameters();
            
            string titleLoc = LocalizationManager.Instance["MenuPhotometricProfile"] ?? "Photometric Profile";
            string xLoc = LocalizationManager.Instance["ColDistance"] ?? "Distance [px]";
            string yLoc = LocalizationManager.Instance["ColIntensity"] ?? "Intensity [ADU]";
            string hexColor = GetSelectionColorHex();
            
            foreach (var fileRef in _sourceFiles)
            {
                var points = await _coordinator.AnalyzeProfileAsync(fileRef, parameters, _cts.Token);
                if (points.Count == 0) continue;

                string pngPath = Path.Combine(outputDir, $"PhotometricCut_{Guid.NewGuid():N}.png");
                
                await Task.Run(() => 
                {
                    var plt = new ScottPlot.Plot();
                    plt.FigureBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
                    plt.DataBackground.Color = ScottPlot.Color.FromHex("#FFFFFF");
                    plt.Axes.Color(ScottPlot.Color.FromHex("#000000"));
                    plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E0E0E0");

                    double[] xs = points.Select(p => p.Distance).ToArray();
                    double[] ys = points.Select(p => p.Value).ToArray();

                    var line = plt.Add.ScatterLine(xs, ys, ScottPlot.Color.FromHex(hexColor));
                    line.LineWidth = 2.5f;
                    
                    plt.Axes.Title.Label.Text = $"\n\n{titleLoc}\n";
                    plt.Axes.Title.Label.FontSize = 24;
                    plt.Axes.Bottom.Label.Text = xLoc;
                    plt.Axes.Bottom.Label.FontSize = 18;
                    plt.Axes.Bottom.TickLabelStyle.FontSize = 14;
                    
                    plt.Axes.Left.Label.Text = $"\n\n{yLoc}\n\n";
                    plt.Axes.Left.Label.FontSize = 18;
                    plt.Axes.Left.TickLabelStyle.FontSize = 14;
                    plt.Axes.Left.MinimumSize = 150;  

                    var yTickGen = new ScottPlot.TickGenerators.NumericAutomatic { TargetTickCount = 12 };
                    plt.Axes.Left.TickGenerator = yTickGen;

                    plt.Axes.AutoScale();
                    plt.SavePng(pngPath, 1200, 800);
                });
                pngPaths.Add(pngPath);
            }

            ResultPaths = pngPaths;
            ResultPaths.Add(csvPath);
            
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (Exception ex) { StatusMessage = string.Format(LocalizationManager.Instance["ErrorGeneric"] ?? "Error: {0}", ex.Message); }
        finally { IsLoading = false; }
    }

    public void Dispose()
    {
        Viewport?.Dispose();
        _cts?.Dispose();
    }
}