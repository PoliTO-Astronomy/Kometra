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

        using Mat floatMat = new Mat();
        if (image.Type() != MatType.CV_32FC1)
            image.ConvertTo(floatMat, MatType.CV_32FC1);
        else
            image.CopyTo(floatMat);

        // Indicizzazione sicura e ad alte prestazioni di OpenCvSharp per float 32-bit (senza unsafe)
        var indexer = floatMat.GetGenericIndexer<float>();

        for (int y = minY; y <= maxY; y++)
        {
            double dy = y - parameters.CenterY;
            double dySq = dy * dy;

            for (int x = minX; x <= maxX; x++)
            {
                double dx = x - parameters.CenterX;
                double distSq = dx * dx + dySq;

                if (distSq > maxRadiusSq)
                    continue;

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