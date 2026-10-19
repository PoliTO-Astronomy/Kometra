using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Visualization;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Processing.Rendering;
using OpenCvSharp;
using Size = Avalonia.Size;

namespace Kometra.ViewModels.Visualization;

public partial class FitsRenderer : ObservableObject, IDisposable
{
    private Array? _rawPixels; 
    private readonly double _bScale;
    private readonly double _bZero;
    private readonly int _width;
    private readonly int _height;
    private readonly FitsBitDepth _targetBitDepth; 

    private readonly IFitsOpenCvConverter _converter;
    private readonly IImagePresentationService _presentationService;

    private CancellationTokenSource? _regenerationCts;
    private Mat? _cachedScientificMat; 
    private WriteableBitmap? _backBuffer; 
    private bool _disposedValue;

    private (double Mean, double StdDev)? _presentationRequirements;

    public Action<Mat>? PostProcessAction { get; set; }

    public Size ImageSize => new(_width, _height);
    public bool IsDisposed => _disposedValue; 

    public FitsBitDepth RenderBitDepth => _targetBitDepth;

    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private double _blackPoint;
    [ObservableProperty] private double _whitePoint;
    [ObservableProperty] private VisualizationMode _visualizationMode = VisualizationMode.Linear;

    public FitsRenderer(
        Array pixelData,
        double bScale,
        double bZero,
        FitsBitDepth targetBitDepth, 
        IFitsOpenCvConverter converter,    
        IImagePresentationService presentationService) 
    {
        _rawPixels = pixelData ?? throw new ArgumentNullException(nameof(pixelData));
        _bScale = bScale;
        _bZero = bZero;
        _targetBitDepth = targetBitDepth;
        _converter = converter;
        _presentationService = presentationService;

        _height = _rawPixels.GetLength(0);
        _width = _rawPixels.GetLength(1);
    }

    public async Task InitializeAsync()
    {
        if (_disposedValue || _rawPixels == null) return;

        await Task.Run(() =>
        {
            _cachedScientificMat = _converter.RawToMat(_rawPixels, _bScale, _bZero, _targetBitDepth);
            _rawPixels = null; 
        });

        await ResetThresholdsAsync(skipRegeneration: true);
        await TriggerRegeneration();
    }

    public SigmaContrastProfile CaptureSigmaProfile()
    {
        if (_disposedValue || _presentationRequirements == null) 
            return new SigmaContrastProfile(-1.5, 10.0);

        return _presentationService.GetRelativeProfile(
            CaptureContrastProfile(), 
            _presentationRequirements.Value);
    }

    public void ApplyRelativeProfile(SigmaContrastProfile relativeProfile)
    {
        if (_disposedValue || _presentationRequirements == null || relativeProfile == null) return;

        var absoluteProfile = _presentationService.GetAbsoluteProfile(
            relativeProfile, 
            _presentationRequirements.Value);

        ApplyContrastProfile(absoluteProfile);
    }

    public AbsoluteContrastProfile CaptureContrastProfile() => new(BlackPoint, WhitePoint);

    public void ApplyContrastProfile(AbsoluteContrastProfile profile)
    {
        if (_disposedValue || profile == null) return;
        
        BlackPoint = profile.BlackAdu;
        WhitePoint = profile.WhiteAdu;
    }

    public (double Mean, double StdDev) PresentationMetrics => _presentationRequirements ?? (0, 1);

    public async Task ResetThresholdsAsync(bool skipRegeneration = false)
    {
        if (_disposedValue || _cachedScientificMat == null) return;

        _presentationRequirements = await Task.Run(() => 
            _presentationService.GetPresentationRequirements(_cachedScientificMat));
        
        var profile = _presentationService.GetInitialProfile(_cachedScientificMat);

        if (skipRegeneration)
        {
            SetProperty(ref _blackPoint, profile.BlackAdu, nameof(BlackPoint));
            SetProperty(ref _whitePoint, profile.WhiteAdu, nameof(WhitePoint));
        }
        else
        {
            BlackPoint = profile.BlackAdu;
            WhitePoint = profile.WhiteAdu;
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(BlackPoint) ||
            e.PropertyName == nameof(WhitePoint) ||
            e.PropertyName == nameof(VisualizationMode))
        {
            _ = TriggerRegeneration();
        }
    }

    public void RequestRefresh() => _ = TriggerRegeneration();

    private async Task TriggerRegeneration()
    {
        if (_disposedValue || _cachedScientificMat == null) return;
        
        _regenerationCts?.Cancel();
        _regenerationCts?.Dispose();
        _regenerationCts = new CancellationTokenSource();
        var token = _regenerationCts.Token;

        try { await RegeneratePreviewImageAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine($"[FitsRenderer] Error: {ex.Message}"); }
    }

    private async Task RegeneratePreviewImageAsync(CancellationToken token)
    {
        if (_cachedScientificMat == null || _cachedScientificMat.IsDisposed) return;
        
        WriteableBitmap targetBitmap = GetOrCreateBuffer();

        try
        {
            using (var lockedBuffer = targetBitmap.Lock())
            {
                await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    using var dstMat = Mat.FromPixelData(_height, _width, MatType.CV_8UC1, lockedBuffer.Address, lockedBuffer.RowBytes);
                    _presentationService.RenderTo8Bit(_cachedScientificMat, dstMat, BlackPoint, WhitePoint, VisualizationMode);
                    PostProcessAction?.Invoke(dstMat);

                }, token);
            }

            if (!token.IsCancellationRequested) SwapBuffer(targetBitmap);
            else RecycleBuffer(targetBitmap);
        }
        catch 
        { 
            targetBitmap.Dispose(); 
            throw; 
        }
    }

    private WriteableBitmap GetOrCreateBuffer()
    {
        if (_backBuffer != null && _backBuffer.PixelSize.Width == _width && _backBuffer.PixelSize.Height == _height)
        {
            var tmp = _backBuffer; _backBuffer = null; return tmp;
        }

        return new WriteableBitmap(
            new PixelSize(_width, _height), 
            new Vector(96, 96), 
            PixelFormats.Gray8, 
            AlphaFormat.Opaque);
    }

    private void RecycleBuffer(WriteableBitmap bmp) => _backBuffer ??= bmp;

    private void SwapBuffer(WriteableBitmap newImage)
    {
        var oldImage = Image as WriteableBitmap;
        Image = newImage;
        if (oldImage != null) RecycleBuffer(oldImage);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                _regenerationCts?.Cancel(); _regenerationCts?.Dispose();
                Image?.Dispose(); _backBuffer?.Dispose();
                PostProcessAction = null; 
            }
            _cachedScientificMat?.Dispose();
            _disposedValue = true;
        }
    }

    public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
}