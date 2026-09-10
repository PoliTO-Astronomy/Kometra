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
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Factories;
using Kometra.Services.Processing.Coordinators;
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
    private readonly FitsFileReference _sourceFile;

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
    [ObservableProperty] private string _statusMessage = "Pronto. Traccia una linea sull'immagine.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasCalculatedProfile = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExportButtonText))]
    private int _selectedTabIndex = 0;

    public string ExportButtonText => SelectedTabIndex == 0 ? "Esporta Grafico PNG" : "Esporta CSV";

    public PhotometricClippingToolViewModel(
        List<FitsFileReference> sourceFiles,
        IPhotometryCoordinator coordinator,
        IFitsRendererFactory rendererFactory,
        IFitsOpenCvConverter converter,
        IFitsDataManager dataManager)
    {
        _sourceFile = sourceFiles?.FirstOrDefault() ?? throw new ArgumentException("Nessun file fornito.");
        _coordinator = coordinator;
        _rendererFactory = rendererFactory;
        _converter = converter;
        _dataManager = dataManager;

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(_sourceFile.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;

            if (hdu?.PixelData != null && hdu.Header != null)
            {
                using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
                
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ImageWidth = srcMat.Cols - 1;
                    ImageHeight = srcMat.Rows - 1;
                    
                    StartX = srcMat.Cols / 2 - 100;
                    StartY = srcMat.Rows / 2 + 100;
                    EndX = srcMat.Cols / 2 + 100;
                    EndY = srcMat.Rows / 2 - 100;
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

    // Questi trigger intercettano lo spostamento della linea (da input o da trascinamento mouse)
    partial void OnStartXChanged(int value) => OnLineChanged();
    partial void OnStartYChanged(int value) => OnLineChanged();
    partial void OnEndXChanged(int value) => OnLineChanged();
    partial void OnEndYChanged(int value) => OnLineChanged();

    private void OnLineChanged()
    {
        // Se c'è già un grafico a schermo, NON lo cancelliamo e NON spegniamo l'interfaccia.
        // Avvisiamo solo l'utente che le coordinate attuali non corrispondono al grafico visibile.
        if (HasCalculatedProfile)
        {
            StatusMessage = "Linea modificata. Premi 'Calcola Taglio Fotometrico' per aggiornare il grafico.";
        }
    }

    [RelayCommand]
    private async Task CalculateProfileAsync()
    {
        IsLoading = true;
        StatusMessage = "Calcolo del profilo in corso...";

        try
        {
            var dataPackage = await _dataManager.LoadDataPackageAsync(_sourceFile.FilePath);
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

                // Mostra i contenitori solo se sono stati estratti effettivamente dei punti
                HasCalculatedProfile = ProfileData.Count > 0;
                InfoDistance = $"Distanza: {points.Count} pixel";
                InfoPeak = $"Picco Massimo Rilevato: {maxAdu:F1} ADU";
                StatusMessage = "Profilo calcolato con successo.";
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
    private async Task ExportDataAsync()
    {
        if (ProfileData.Count == 0) return;

        string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (SelectedTabIndex == 0) 
        {
            try
            {
                string destPath = Path.Combine(downloadsPath, $"photometric_cut_{DateTime.Now:yyyyMMdd_HHmmss}.png");
                SavePlotRequested?.Invoke(destPath);
                StatusMessage = $"Grafico salvato in: {destPath}";
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
                string destPath = Path.Combine(downloadsPath, $"photometric_cut_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("Distanza (px),X,Y,Intensita (ADU)");
                foreach (var pt in ProfileData)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F2}", pt.PixelIndex, pt.X, pt.Y, pt.Value));
                }
                await File.WriteAllTextAsync(destPath, sb.ToString(), Encoding.UTF8);
                StatusMessage = $"CSV salvato in: {destPath}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Errore esportazione CSV: {ex.Message}";
            }
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

    [RelayCommand]
    private void Apply()
    {
        DialogResult = true;
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        RequestClose?.Invoke();
    }

    public void Dispose()
    {
        Viewport?.Dispose();
    }
}