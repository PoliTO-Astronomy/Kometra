using System.Collections.Generic;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

/// <summary>
/// Definisce il motore matematico per l'analisi morfologica di isofote ellittiche 
/// e la generazione di modelli sintetici 2D.
/// </summary>
public interface IEllipticalIsophoteEngine
{
    /// <summary>
    /// Campiona l'intensità dei pixel lungo il perimetro di una singola ellisse ruotata
    /// e ne calcola le statistiche (media, mediana, deviazione standard).
    /// </summary>
    /// <param name="srcMat">Matrice OpenCvSharp dell'immagine FITS sorgente.</param>
    /// <param name="centerX">Coordinata X del centro dell'ellisse in pixel.</param>
    /// <param name="centerY">Coordinata Y del centro dell'ellisse in pixel.</param>
    /// <param name="semiMajorA">Semiasse maggiore 'a' dell'ellisse in pixel.</param>
    /// <param name="ellipticity">Ellitticità ε = 1 - (b/a), con valori tipici tra 0.00 e 0.85.</param>
    /// <param name="positionAngleRad">Angolo di posizione (PA) del semiasse maggiore in radianti.</param>
    /// <param name="samplePoints">Numero di punti angolari da campionare lungo il perimetro (default: 360).</param>
    /// <returns>Il punto dati <see cref="EllipticalIsophoteDataPoint"/> con le statistiche calcolate.</returns>
    EllipticalIsophoteDataPoint AnalyzeEllipse(
        Mat srcMat,
        double centerX,
        double centerY,
        double semiMajorA,
        double ellipticity,
        double positionAngleRad,
        int samplePoints = 360);

    /// <summary>
    /// Esegue la scansione radiale completa dall'interno verso l'esterno, analizzando
    /// un set concentrico di isofote ellittiche fino al semiasse maggiore massimo specificato.
    /// </summary>
    /// <param name="srcMat">Matrice OpenCvSharp dell'immagine FITS sorgente.</param>
    /// <param name="centerX">Coordinata X del centro in pixel.</param>
    /// <param name="centerY">Coordinata Y del centro in pixel.</param>
    /// <param name="maxSemiMajorA">Semiasse maggiore massimo da raggiungere nell'analisi.</param>
    /// <param name="stepSize">Incremento in pixel del semiasse maggiore tra un'ellisse e la successiva.</param>
    /// <param name="ellipticity">Ellitticità ε = 1 - (b/a).</param>
    /// <param name="positionAngleRad">Angolo di posizione in radianti.</param>
    /// <returns>La lista ordinata di punti profilo calcolati lungo i vari semiassi.</returns>
    List<EllipticalIsophoteDataPoint> AnalyzeProfile(
        Mat srcMat,
        double centerX,
        double centerY,
        double maxSemiMajorA,
        double stepSize,
        double ellipticity,
        double positionAngleRad);

    /// <summary>
    /// Genera una matrice FITS sintetica 2D (a virgola mobile a 32 bit) che rappresenta
    /// il modello ellittico continuo interpolando i valori delle isofote analizzate.
    /// </summary>
    /// <param name="rows">Altezza dell'immagine di output in pixel.</param>
    /// <param name="cols">Larghezza dell'immagine di output in pixel.</param>
    /// <param name="centerX">Coordinata X del centro del modello.</param>
    /// <param name="centerY">Coordinata Y del centro del modello.</param>
    /// <param name="isophotes">Lista dei dati analizzati da cui interpolare le intensità.</param>
    /// <param name="ellipticity">Ellitticità del modello finale.</param>
    /// <param name="positionAngleRad">Angolo di posizione del modello finale in radianti.</param>
    /// <returns>Una nuova <see cref="Mat"/> OpenCvSharp (CV_32FC1) contenente il modello 2D.</returns>
    Mat Generate2DEllipticalModel(
        int rows,
        int cols,
        double centerX,
        double centerY,
        List<EllipticalIsophoteDataPoint> isophotes,
        double ellipticity,
        double positionAngleRad);
}