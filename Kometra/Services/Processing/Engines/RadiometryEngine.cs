using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Kometra.Models.Visualization;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class RadiometryEngine : IRadiometryEngine
{
    public RadiometryEngine() { }

    public (double Mean, double StdDev) ComputeStatistics(Mat image)
    {
        if (image == null || image.Empty()) return (0, 1);
        
        using Mat mask = CreateValidMask(image);
        double validCount = Cv2.CountNonZero(mask);

        if (validCount == 0) return (0, 1);

        
        Cv2.MeanStdDev(image, out Scalar mean, out Scalar stdDev, mask);
        
        return (mean.Val0, stdDev.Val0);
    }
    
    
    public (double Mean, double StdDev, double MedianApprox) ComputeRobustStatistics(Mat image, double sigma = 3.0, int maxIterations = 5)
    {
        if (image == null || image.Empty()) return (0, 1, 0);

        using Mat currentMask = new Mat();
        Cv2.InRange(image, new Scalar(-1e30), new Scalar(1e30), currentMask);

        double mean = 0, stdDev = 1;
        double prevMean = double.MaxValue;

        for (int i = 0; i < maxIterations; i++)
        {
            if (Cv2.CountNonZero(currentMask) == 0) return (0, 1, 0);

            Cv2.MeanStdDev(image, out Scalar mu, out Scalar sigmaVal, currentMask);
            mean = mu.Val0;
            stdDev = sigmaVal.Val0;

            if (stdDev < 1e-9 || Math.Abs(mean - prevMean) < 1e-5 * Math.Abs(prevMean)) 
                break;

            prevMean = mean;

            double lower = mean - (sigma * stdDev);
            double upper = mean + (sigma * stdDev);

            using Mat rangeMask = new Mat();
            Cv2.InRange(image, new Scalar(lower), new Scalar(upper), rangeMask);
            
            Cv2.BitwiseAnd(currentMask, rangeMask, currentMask);
        }

        return (mean, stdDev, mean);
    }


    public AbsoluteContrastProfile CalculateAutoStretchProfile(Mat image)
    {
        if (image == null || image.Empty()) return new AbsoluteContrastProfile(0, 65535);

        using Mat mask = CreateValidMask(image);
        double totalValid = Cv2.CountNonZero(mask);
        
        if (totalValid < 100) return new AbsoluteContrastProfile(0, 65535);

        double min, max;
        Cv2.MinMaxLoc(image, out min, out max, out _, out _, mask);

        if (Math.Abs(max - min) < 1e-8) return new AbsoluteContrastProfile(min, min + 100);

        Mat histInput = image;
        bool tempCreated = false;
        if (image.Type() != MatType.CV_32FC1)
        {
            histInput = new Mat();
            image.ConvertTo(histInput, MatType.CV_32FC1);
            tempCreated = true;
        }

        try 
        {
            int histSize = 2000; 
            Rangef[] ranges = { new Rangef((float)min, (float)max) };
            using Mat hist = new Mat();
            Cv2.CalcHist(new[] { histInput }, new[] { 0 }, mask, hist, 1, new[] { histSize }, ranges);

            return AnalyzeHistogram(hist, totalValid, min, max, histSize);
        }
        finally { if (tempCreated) histInput.Dispose(); }
    }

    private AbsoluteContrastProfile AnalyzeHistogram(Mat hist, double totalValid, double min, double max, int histSize)
    {
        double blackGoal = totalValid * 0.25; 
        double whiteGoal = totalValid * 0.997; 

        double currentSum = 0;
        double b = min, w = max;
        bool blackFound = false;

        for (int i = 0; i < histSize; i++)
        {
            float binCount = hist.At<float>(i);
            currentSum += binCount;
            double binVal = min + (i * (max - min) / histSize);

            if (!blackFound && currentSum >= blackGoal) { b = binVal; blackFound = true; }
            if (currentSum >= whiteGoal) { w = binVal; break; }
        }

        if (w <= b) w = b + (max - min) * 0.05;
        return new AbsoluteContrastProfile(b, w);
    }

    
    public SigmaContrastProfile ComputeSigmaProfile(double currentBlack, double currentWhite, (double Mean, double StdDev) stats)
    {
        double sigma = stats.StdDev < 1e-9 ? 1.0 : stats.StdDev;
        return new SigmaContrastProfile((currentBlack - stats.Mean) / sigma, (currentWhite - stats.Mean) / sigma);
    }

    public AbsoluteContrastProfile ComputeAbsoluteFromSigma(SigmaContrastProfile profile, (double Mean, double StdDev) stats)
    {
        return new AbsoluteContrastProfile(
            stats.Mean + (profile.KBlack * stats.StdDev), 
            stats.Mean + (profile.KWhite * stats.StdDev));
    }


    public double[] GetPixelSamples(Mat image, int maxSamples = 10000)
    {
        int w = image.Width;
        int h = image.Height;
        if (w * h == 0) return Array.Empty<double>();

        var samples = new List<double>(maxSamples);
        int step = (int)Math.Max(1, Math.Sqrt((double)w * h / maxSamples));

        if (image.Type() == MatType.CV_32FC1)
        {
            var indexer = image.GetGenericIndexer<float>();
            for (int y = 0; y < h; y += step) {
                for (int x = 0; x < w; x += step) {
                    float val = indexer[y, x];
                    if (float.IsFinite(val)) samples.Add(val);
                    if (samples.Count >= maxSamples) break;
                }
                if (samples.Count >= maxSamples) break;
            }
        }
        else 
        {
            var indexer = image.GetGenericIndexer<double>();
            for (int y = 0; y < h; y += step) {
                for (int x = 0; x < w; x += step) {
                    double val = indexer[y, x];
                    if (double.IsFinite(val)) samples.Add(val);
                    if (samples.Count >= maxSamples) break;
                }
                if (samples.Count >= maxSamples) break;
            }
        }

        return samples.ToArray();
    }

    private Mat CreateValidMask(Mat image)
    {
        Mat mask = new Mat();
        Cv2.Compare(image, image, mask, CmpTypes.EQ);
    
        return mask;
    }
}