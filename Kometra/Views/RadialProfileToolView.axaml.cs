using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Kometra.ViewModels.ImageProcessing;
using Kometra.ViewModels.Visualization;

namespace Kometra.Views;

public partial class RadialProfileToolView : Window
{
    private Line? _lineH;
    private Line? _lineV;
    private Ellipse? _circle;

    public RadialProfileToolView()
    {
        InitializeComponent();
        
        // Colleghiamo l'evento SizeChanged per ricalcolare la posizione del mirino se ridimensioni la finestra
        this.SizeChanged += (s, e) => UpdateCrosshairPosition();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _lineH = this.FindControl<Line>("CrosshairLineH");
        _lineV = this.FindControl<Line>("CrosshairLineV");
        _circle = this.FindControl<Ellipse>("CrosshairCircle");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RadialProfileToolViewModel vm)
        {
            // Ascoltiamo i cambiamenti del ViewModel per aggiornare il mirino se l'utente digita i numeri a mano
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateCrosshairPosition();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadialProfileToolViewModel.CenterX) ||
            e.PropertyName == nameof(RadialProfileToolViewModel.CenterY) ||
            e.PropertyName == nameof(RadialProfileToolViewModel.Viewport))
        {
            UpdateCrosshairPosition();
        }
    }

    /// <summary>
    /// Calcola la posizione sullo schermo delle coordinate astronomiche (CenterX, CenterY) e disegna il mirino.
    /// </summary>
    private void UpdateCrosshairPosition()
    {
        if (DataContext is not RadialProfileToolViewModel viewModel ||
            _lineH == null || _lineV == null || _circle == null)
        {
            return;
        }

        // Recuperiamo il controllo Image e la bitmap
        var imageControl = this.FindControl<ContentControl>("ViewportControl")?
                               .FindDescendantOfType<Image>();

        if (imageControl == null ||
            imageControl.Bounds.Width <= 0 ||
            imageControl.Bounds.Height <= 0 ||
            viewModel.Viewport is not FitsRenderer { Image: Bitmap bitmap })
        {
            HideCrosshair();
            return;
        }

        double imgWidth = bitmap.Size.Width;
        double imgHeight = bitmap.Size.Height;
        double ctrlWidth = imageControl.Bounds.Width;
        double ctrlHeight = imageControl.Bounds.Height;

        // Calcoliamo la scala visiva (Stretch="Uniform")
        double scale = Math.Min(ctrlWidth / imgWidth, ctrlHeight / imgHeight);
        if (scale <= 0)
        {
            HideCrosshair();
            return;
        }

        // Calcoliamo gli offset dei bordi neri attorno all'immagine all'interno del contenitore
        double renderWidth = imgWidth * scale;
        double renderHeight = imgHeight * scale;
        double offsetX = (ctrlWidth - renderWidth) / 2.0;
        double offsetY = (ctrlHeight - renderHeight) / 2.0;

        // Convertiamo la coordinata astronomica (FITS pixel) in coordinata visiva (Pixel schermo nel Canvas)
        double screenX = offsetX + (viewModel.CenterX * scale);
        double screenY = offsetY + (viewModel.CenterY * scale);

        // Se il centro è fuori dai confini reali dell'immagine, nascondiamo il mirino
        if (screenX < offsetX || screenX > offsetX + renderWidth ||
            screenY < offsetY || screenY > offsetY + renderHeight)
        {
            HideCrosshair();
            return;
        }

        // 1. Linea Orizzontale (da sinistra a destra sopra il centro)
        _lineH.StartPoint = new Point(offsetX, screenY);
        _lineH.EndPoint = new Point(offsetX + renderWidth, screenY);
        _lineH.IsVisible = true;

        // 2. Linea Verticale (dall'alto in basso sopra il centro)
        _lineV.StartPoint = new Point(screenX, offsetY);
        _lineV.EndPoint = new Point(screenX, offsetY + renderHeight);
        _lineV.IsVisible = true;

        // 3. Cerchio centrale (centrato sull'intersezione)
        double circleRadius = _circle.Width / 2.0;
        Canvas.SetLeft(_circle, screenX - circleRadius);
        Canvas.SetTop(_circle, screenY - circleRadius);
        _circle.IsVisible = true;
    }

    private void HideCrosshair()
    {
        if (_lineH != null) _lineH.IsVisible = false;
        if (_lineV != null) _lineV.IsVisible = false;
        if (_circle != null) _circle.IsVisible = false;
    }

    // Gestisce il clic sull'immagine per centrare il profilo radiale sulle coordinate del mouse.
    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RadialProfileToolViewModel viewModel)
            return;

        var imageControl = this.FindControl<ContentControl>("ViewportControl")?
                               .FindDescendantOfType<Image>();

        if (imageControl == null ||
            imageControl.Bounds.Width <= 0 ||
            imageControl.Bounds.Height <= 0 ||
            viewModel.Viewport is not FitsRenderer { Image: Bitmap bitmap })
        {
            return;
        }

        Point clickPoint = e.GetPosition(imageControl);

        double imgWidth = bitmap.Size.Width;
        double imgHeight = bitmap.Size.Height;
        double ctrlWidth = imageControl.Bounds.Width;
        double ctrlHeight = imageControl.Bounds.Height;

        double scale = Math.Min(ctrlWidth / imgWidth, ctrlHeight / imgHeight);
        if (scale <= 0)
            return;

        double renderWidth = imgWidth * scale;
        double renderHeight = imgHeight * scale;
        double offsetX = (ctrlWidth - renderWidth) / 2.0;
        double offsetY = (ctrlHeight - renderHeight) / 2.0;

        double relX = clickPoint.X - offsetX;
        double relY = clickPoint.Y - offsetY;

        if (relX < 0 || relX > renderWidth || relY < 0 || relY > renderHeight)
            return;

        double fitsX = relX / scale;
        double fitsY = relY / scale;

        fitsX = Math.Clamp(fitsX, 0, imgWidth - 1);
        fitsY = Math.Clamp(fitsY, 0, imgHeight - 1);

        // Aggiorniamo le coordinate nel ViewModel (il binding scatenerà in automatico UpdateCrosshairPosition)
        _ = viewModel.OnImageClickedAsync(new Point(fitsX, fitsY));
        UpdateCrosshairPosition();
    }
}