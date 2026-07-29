using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

// Motore dedicato alle operazioni fotometriche e di segmentazione quantitativa dei pixel.
public interface IPhotometryEngine
{

    // Applica un taglio fotometrico alla matrice sorgente in base ai parametri specificati.

    /// <param name="source">Matrice sorgente (deve essere in virgola mobile CV_32F o CV_64F).</param>
    /// <param name="parameters">Configurazione delle soglie e della modalità di output.</param>
    /// <returns>Una nuova matrice contenente il risultato del taglio fotometrico.</returns>
    Mat ApplyPhotometricClipping(Mat source, PhotometricClippingParameters parameters);
}