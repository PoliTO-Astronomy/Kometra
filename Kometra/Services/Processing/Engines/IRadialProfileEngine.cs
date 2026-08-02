using System.Collections.Generic;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public interface IRadialProfileEngine
{
    
    // Calcola il profilo radiale su una matrice di pixel FITS in virgola mobile.
    
    /// <param name="image">Matrice OpenCvSharp (tipicamente CV_32FC1).</param>
    /// <param name="parameters">Parametri geometrici e statistici di analisi.</param>
    /// <returns>Lista ordinata di punti del profilo radiale dal centro verso l'esterno.</returns>
    IReadOnlyList<RadialProfileDataPoint> CalculateProfile(Mat image, RadialProfileParameters parameters);
}