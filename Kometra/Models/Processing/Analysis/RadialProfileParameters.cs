namespace Kometra.Models.Processing.Analysis;

// Parametri di input per il calcolo del profilo radiale.
 
public record RadialProfileParameters
{    
    // Coordinate X (colonna) del centro di analisi in pixel.
     
    public double CenterX { get; init; }
     
    // Coordinate Y (riga) del centro di analisi in pixel.
     
    public double CenterY { get; init; }
     
    // Raggio massimo di analisi in pixel.
     
    public double MaxRadius { get; init; } = 100.0;
     
    // Ampiezza di ogni anello concentrico (bin) in pixel. Default: 1 pixel.
     
    public double StepSize { get; init; } = 1.0;

    // Metrica statistica per il calcolo.
     
    public RadialProfileMode Mode { get; init; } = RadialProfileMode.Mean;
}