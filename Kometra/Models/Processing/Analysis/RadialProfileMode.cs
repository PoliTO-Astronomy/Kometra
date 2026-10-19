namespace Kometra.Models.Processing.Analysis;

 
/// Specifica la metrica statistica utilizzata per calcolare il valore di un anello radiale.
 
public enum RadialProfileMode
{
     
    // Calcola la media aritmetica dei pixel nell'anello. Veloce ma sensibile ai picchi (es. stelle di campo).
    Mean,

    // Calcola la mediana dei pixel nell'anello. Ideale per escludere stelle sovrapposte, raggi cosmici e pixel caldi.
     
    Median,
 
    // Calcola la somma totale (flusso integrato) dell'anello.
     
    Sum
}