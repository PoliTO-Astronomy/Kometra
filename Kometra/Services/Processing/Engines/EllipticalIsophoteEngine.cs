using System;
using System.Collections.Generic;
using System.Linq;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class EllipticalIsophoteEngine : IEllipticalIsophoteEngine
{
    /// <summary>
    /// Campiona l'intensità lungo il perimetro di un'ellisse ruotata.
    /// </summary>
    public EllipticalIsophoteDataPoint AnalyzeEllipse(
        Mat srcMat, 
        double centerX, 
        double centerY, 
        double semiMajorA, 
        double ellipticity, 
        double positionAngleRad, 
        int samplePoints = 360)
    {
        double semiMinorB = semiMajorA * (1.0 - ellipticity);
        double cosTheta = Math.Cos(positionAngleRad);
        double sinTheta = Math.Sin(positionAngleRad);

        var sampledValues = new List<float>(samplePoints);
        var indexer = srcMat.GetGenericIndexer<float>();
        int rows = srcMat.Rows;
        int cols = srcMat.Cols;

        for (int i = 0; i < samplePoints; i++)
        {
            double E = (2.0 * Math.PI * i) / samplePoints;
            double cosE = Math.Cos(E);
            double sinE = Math.Sin(E);

            // Equazioni parametriche dell'ellisse ruotata di un angolo theta
            double x = centerX + semiMajorA * cosE * cosTheta - semiMinorB * sinE * sinTheta;
            double y = centerY + semiMajorA * cosE * sinTheta + semiMinorB * sinE * cosTheta;

            int ix = (int)Math.Round(x);
            int iy = (int)Math.Round(y);

            if (ix >= 0 && ix < cols && iy >= 0 && iy < rows)
            {
                sampledValues.Add(indexer[iy, ix]);
            }
        }

        if (sampledValues.Count == 0)
        {
            return new EllipticalIsophoteDataPoint 
            { 
                SemiMajorAxis = semiMajorA, 
                SemiMinorAxis = semiMinorB, 
                Ellipticity = ellipticity, 
                PositionAngleRad = positionAngleRad 
            };
        }

        sampledValues.Sort();
        double mean = sampledValues.Average();
        double median = sampledValues[sampledValues.Count / 2];
        
        double sumSq = sampledValues.Sum(v => (v - mean) * (v - mean));
        double stdDev = Math.Sqrt(sumSq / sampledValues.Count);

        return new EllipticalIsophoteDataPoint
        {
            SemiMajorAxis = semiMajorA,
            SemiMinorAxis = semiMinorB,
            Ellipticity = ellipticity,
            PositionAngleRad = positionAngleRad,
            MeanValue = mean,
            MedianValue = median,
            StandardDeviation = stdDev,
            PixelCount = sampledValues.Count
        };
    }

    /// <summary>
    /// Calcola l'intero profilo di isofote ellittiche dal centro verso il semiasse maggiore massimo.
    /// </summary>
    public List<EllipticalIsophoteDataPoint> AnalyzeProfile(
        Mat srcMat,
        double centerX,
        double centerY,
        double maxSemiMajorA,
        double stepSize,
        double ellipticity,
        double positionAngleRad)
    {
        var results = new List<EllipticalIsophoteDataPoint>();
        for (double a = stepSize; a <= maxSemiMajorA; a += stepSize)
        {
            results.Add(AnalyzeEllipse(srcMat, centerX, centerY, a, ellipticity, positionAngleRad));
        }
        return results;
    }

    /// <summary>
    /// Genera l'immagine modello 2D FITS partendo dal profilo ellittico analizzato.
    /// </summary>
    public Mat Generate2DEllipticalModel(
        int rows, 
        int cols, 
        double centerX, 
        double centerY, 
        List<EllipticalIsophoteDataPoint> isophotes,
        double ellipticity,
        double positionAngleRad)
    {
        var modelMat = new Mat(rows, cols, MatType.CV_32FC1, new Scalar(0));
        var indexer = modelMat.GetGenericIndexer<float>();

        var sorted = isophotes.OrderBy(p => p.SemiMajorAxis).ToList();
        if (sorted.Count == 0) return modelMat;

        double maxA = sorted.Last().SemiMajorAxis;
        double cosTheta = Math.Cos(positionAngleRad);
        double sinTheta = Math.Sin(positionAngleRad);
        
        // Rapporto tra gli assi q = b/a = 1 - ellitticità
        double q = Math.Max(0.05, 1.0 - ellipticity); 

        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < cols; x++)
            {
                double dx = x - centerX;
                double dy = y - centerY;

                // Ruotiamo le coordinate nel sistema di riferimento dell'ellisse
                double xRot = dx * cosTheta + dy * sinTheta;
                double yRot = -dx * sinTheta + dy * cosTheta;

                // Calcolo del semiasse maggiore 'a' equivalente passante per il pixel (x, y)
                // Dalla formula: (xRot / a)^2 + (yRot / b)^2 = 1 => a = sqrt(xRot^2 + (yRot / q)^2)
                double effectiveA = Math.Sqrt(xRot * xRot + (yRot * yRot) / (q * q));

                if (effectiveA <= maxA)
                {
                    indexer[y, x] = (float)EvaluateEllipticalValue(sorted, effectiveA);
                }
                else
                {
                    indexer[y, x] = 0f;
                }
            }
        }

        return modelMat;
    }

    private static double EvaluateEllipticalValue(List<EllipticalIsophoteDataPoint> pts, double a)
    {
        if (a <= pts[0].SemiMajorAxis) return pts[0].MeanValue;
        if (a >= pts[^1].SemiMajorAxis) return pts[^1].MeanValue;

        // Interpolazione lineare lungo il semiasse maggiore
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (a >= pts[i].SemiMajorAxis && a <= pts[i + 1].SemiMajorAxis)
            {
                double t = (a - pts[i].SemiMajorAxis) / (pts[i + 1].SemiMajorAxis - pts[i].SemiMajorAxis);
                return pts[i].MeanValue + t * (pts[i + 1].MeanValue - pts[i].MeanValue);
            }
        }
        return 0;
    }
}