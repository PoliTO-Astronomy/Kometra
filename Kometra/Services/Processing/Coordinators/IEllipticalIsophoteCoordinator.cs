using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;

namespace Kometra.Services.Processing.Coordinators;

/// <summary>
/// Definisce il coordinatore per l'orchestrazione del flusso di analisi
/// delle isofote ellittiche e la generazione del file FITS di modello 2D.
/// </summary>
public interface IEllipticalIsophoteCoordinator
{
    /// <summary>
    /// Carica l'immagine FITS sorgente, esegue l'analisi del profilo di isofote ellittiche 
    /// tramite l'engine matematico e genera un file FITS temporaneo contenente il modello 2D.
    /// </summary>
    /// <param name="sourceFile">Riferimento al file FITS sorgente selezionato.</param>
    /// <param name="centerX">Coordinata X del centro dell'analisi in pixel.</param>
    /// <param name="centerY">Coordinata Y del centro dell'analisi in pixel.</param>
    /// <param name="maxRadius">Semiasse maggiore massimo 'a' da raggiungere in pixel.</param>
    /// <param name="stepSize">Passo di incremento del semiasse maggiore fra un'isofota e la successiva.</param>
    /// <param name="ellipticity">Ellitticità ε = 1 - (b/a), con valori tra 0.00 e ~0.85.</param>
    /// <param name="positionAngleDeg">Angolo di posizione (PA) in gradi (tra -180° e +180°).</param>
    /// <param name="cancellationToken">Token per annullare l'operazione asincrona.</param>
    /// <returns>
    /// Una tupla contenente la lista delle isofote analizzate e il percorso assoluto 
    /// del file FITS temporaneo generato per l'anteprima 2D sulla Board.
    /// </returns>
    Task<(List<EllipticalIsophoteDataPoint> Isophotes, string PreviewFilePath)> AnalyzeAndGenerateModelAsync(
        FitsFileReference sourceFile,
        double centerX,
        double centerY,
        double maxRadius,
        double stepSize,
        double ellipticity,
        double positionAngleDeg,
        CancellationToken cancellationToken = default);
}