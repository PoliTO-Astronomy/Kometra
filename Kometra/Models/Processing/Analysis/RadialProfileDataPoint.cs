namespace Kometra.Models.Processing.Analysis;


// Rappresenta un singolo punto (anello concentrico) nel profilo radiale.

public record RadialProfileDataPoint
{
    
    // Raggio medio dell'anello dal centro (in pixel).
    
    public double Radius { get; init; }

    
    // Valore statistico calcolato (Media, Mediana o Somma) degli ADU nell'anello.
    
    public double Value { get; init; }

    
    // Deviazione standard dei pixel all'interno dell'anello (utile per barre d'errore nel grafico).
    
    public double StandardDeviation { get; init; }

    
    // Numero di pixel validi campionati all'interno dell'anello.
    
    public int PixelCount { get; init; }
}