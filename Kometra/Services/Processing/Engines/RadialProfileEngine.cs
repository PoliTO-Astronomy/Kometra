using System;
using System.Collections.Generic;
using System.Linq;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class RadialProfileEngine : IRadialProfileEngine
{
    public IReadOnlyList<RadialProfileDataPoint> CalculateProfile(Mat image, RadialProfileParameters parameters)
    {
        if (image == null || image.Empty())
            throw new ArgumentNullException(nameof(image), "L'immagine di input non può essere nulla o vuota.");

        if (parameters.StepSize <= 0 || parameters.MaxRadius <= 0)
            return Array.Empty<RadialProfileDataPoint>();

        int numBins = (int)Math.Ceiling(parameters.MaxRadius / parameters.StepSize);
        var binValues = new List<float>[numBins];
        for (int i = 0; i < numBins; i++)
        {
            binValues[i] = new List<float>();
        }

        int minX = Math.Max(0, (int)Math.Floor(parameters.CenterX - parameters.MaxRadius));
        int maxX = Math.Min(image.Cols - 1, (int)Math.Ceiling(parameters.CenterX + parameters.MaxRadius));
        int minY = Math.Max(0, (int)Math.Floor(parameters.CenterY - parameters.MaxRadius));
        int maxY = Math.Min(image.Rows - 1, (int)Math.Ceiling(parameters.CenterY + parameters.MaxRadius));

        double maxRadiusSq = parameters.MaxRadius * parameters.MaxRadius;

        bool checkAngle = parameters.IntegrationAngle < 180.0;
        
        double startAngle = parameters.StartingAngle % 360.0;
        if (startAngle < 0) startAngle += 360.0;
        double minA = startAngle - parameters.IntegrationAngle;
        double maxA = startAngle + parameters.IntegrationAngle;

        using Mat floatMat = new Mat();
        if (image.Type() != MatType.CV_32FC1)
            image.ConvertTo(floatMat, MatType.CV_32FC1);
        else
            image.CopyTo(floatMat);

        var indexer = floatMat.GetGenericIndexer<float>();

        for (int y = minY; y <= maxY; y++)
        {
            // La Y è invertita in modo che +Y punti verso l'alto
            double dyForAngle = parameters.CenterY - y; 
            double dyForDist = y - parameters.CenterY;
            double dySq = dyForDist * dyForDist;

            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - parameters.CenterX;
                double distSq = dx * dx + dySq;

                if (distSq > maxRadiusSq)
                    continue;

                if (checkAngle && distSq > 0.0001) 
                {
                    // Atan2(Y, X) dove X positivo è 0°, Y positivo (sopra) è 90°
                    double angleDeg = Math.Atan2(dyForAngle, dx) * (180.0 / Math.PI);
                    if (angleDeg < 0) angleDeg += 360.0;

                    bool inSector = false;
                    
                    if (minA < 0)
                        inSector = (angleDeg >= (360 + minA)) || (angleDeg <= maxA);
                    else if (maxA >= 360)
                        inSector = (angleDeg >= minA) || (angleDeg <= (maxA - 360));
                    else
                        inSector = (angleDeg >= minA) && (angleDeg <= maxA);

                    if (!inSector)
                        continue; 
                }

                float val = indexer[y, x];

                if (float.IsNaN(val) || float.IsInfinity(val))
                    continue;

                double dist = Math.Sqrt(distSq);
                int binIdx = (int)(dist / parameters.StepSize);

                if (binIdx < numBins)
                {
                    binValues[binIdx].Add(val);
                }
            }
        }

        var result = new List<RadialProfileDataPoint>(numBins);

        for (int i = 0; i < numBins; i++)
        {
            var values = binValues[i];
            double r = (i + 0.5) * parameters.StepSize;

            if (values.Count == 0)
            {
                result.Add(new RadialProfileDataPoint
                {
                    Radius = r,
                    Value = 0.0,
                    StandardDeviation = 0.0,
                    PixelCount = 0
                });
                continue;
            }

            double statValue = 0.0;
            switch (parameters.Mode)
            {
                case RadialProfileMode.Mean:
                    statValue = values.Average(v => (double)v);
                    break;
                case RadialProfileMode.Sum:
                    statValue = values.Sum(v => (double)v);
                    break;
                case RadialProfileMode.Median:
                    values.Sort();
                    int mid = values.Count / 2;
                    statValue = (values.Count % 2 != 0)
                        ? values[mid]
                        : (values[mid - 1] + values[mid]) / 2.0;
                    break;
            }

            double mean = values.Average(v => (double)v);
            double sumSqDiff = values.Sum(v => Math.Pow(v - mean, 2));
            double stdDev = values.Count > 1 ? Math.Sqrt(sumSqDiff / (values.Count - 1)) : 0.0;

            result.Add(new RadialProfileDataPoint
            {
                Radius = r,
                Value = statValue,
                StandardDeviation = stdDev,
                PixelCount = values.Count
            });
        }

        return result;
    }
}