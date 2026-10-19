using System;
using System.Collections.Generic;
using System.Linq;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class EllipticalIsophoteEngine : IEllipticalIsophoteEngine
{
    public IsophoteAnalysisResult CalculateIsophotes(Mat image, EllipticalIsophoteParameters parameters)
    {
        if (image == null || image.Empty()) return new IsophoteAnalysisResult();

        Mat compositeBgra = new Mat(image.Rows, image.Cols, MatType.CV_8UC4, new Scalar(255, 255, 255, 255));
        var tempPoints = new List<EllipticalIsophoteDataPoint>();
        var validContours = new List<(double Level, Point[][] Contours, double Area)>();

        for (double level = parameters.MinValue; level <= parameters.MaxValue; level += parameters.StepSize)
        {
            using Mat mask = new Mat();
            Cv2.Threshold(image, mask, level, 255, ThresholdTypes.Binary);
            mask.ConvertTo(mask, MatType.CV_8UC1);

            Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);

            if (contours.Length > 0)
            {
                double area = contours.Sum(c => Cv2.ContourArea(c));
                validContours.Add((level, contours, area));
            }
        }

        int totalContours = validContours.Count;
        for (int i = 0; i < totalContours; i++)
        {
            var item = validContours[i];
            double colorRatio = totalContours > 1 ? (double)i / (totalContours - 1) : 1.0;
            
            // Qui usiamo il colore convertito se monocromatico
            Scalar overlayColor = parameters.IsMonochromatic 
                ? HexToScalar(parameters.MonoColorHex) 
                : GetJetColorWithAlpha(colorRatio);
            
            Cv2.DrawContours(compositeBgra, item.Contours, -1, overlayColor, 2);
            tempPoints.Add(new EllipticalIsophoteDataPoint { MeanValue = item.Level, PixelCount = (int)item.Area });
        }

        return new IsophoteAnalysisResult { Isophotes = tempPoints, OverlayImage = compositeBgra };
    }

    private Scalar HexToScalar(string hex)
    {
        hex = hex.Replace("#", "");
        if (hex.Length == 8) hex = hex.Substring(2); // Rimuove Alpha se presente
        if (hex.Length != 6) return new Scalar(232, 88, 128, 255); // Fallback

        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return new Scalar(b, g, r, 255); // OpenCV usa BGRA
    }

    private Scalar GetJetColorWithAlpha(double ratio)
    {
        byte val = (byte)Math.Clamp(ratio * 255.0, 0, 255);
        using var mat1x1 = new Mat(1, 1, MatType.CV_8UC1, new Scalar(val));
        using var color1x1 = new Mat();
        Cv2.ApplyColorMap(mat1x1, color1x1, ColormapTypes.Jet);
        var vec = color1x1.Get<Vec3b>(0, 0);
        return new Scalar(vec.Item0, vec.Item1, vec.Item2, 255);
    }
}