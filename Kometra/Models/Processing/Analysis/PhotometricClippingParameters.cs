namespace Kometra.Models.Processing.Analysis;

public enum PhotometricClippingMode
{
    // Taglio basato su valori assoluti di luminosità (ADU).
    AbsoluteAdu,

    
    // Taglio statistico basato sul fondo cielo e la sua deviazione standard (Sigma).
    SigmaThresh
}

public enum ClippingOutputMode
{
    // Sostituisce i pixel fuori soglia con 0 e mantiene i valori originali per quelli validi.
    
    ZeroedPixels,

    // Sostituisce i pixel fuori soglia con Double.NaN (ignorati dai calcoli statistici successivi).
    NanPixels,

    // Restituisce una maschera binaria pura (0.0 per fuori soglia, 1.0 per i pixel validi).     
    BinaryMask
}

public class PhotometricClippingParameters
{
    public PhotometricClippingMode Mode { get; set; } = PhotometricClippingMode.SigmaThresh;
    public ClippingOutputMode OutputMode { get; set; } = ClippingOutputMode.ZeroedPixels;

    // Parametri per Modalità AbsoluteAdu
    public double MinAdu { get; set; } = 0.0;
    public double MaxAdu { get; set; } = 65535.0;

    // Parametri per Modalità SigmaThresh 
    public double LowerSigma { get; set; } = 3.0; 
    public double UpperSigma { get; set; } = 100.0; 
}