using System;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class PhotometryEngine : IPhotometryEngine
{
    public Mat ApplyPhotometricClipping(Mat source, PhotometricClippingParameters parameters)
    {
        if (source == null || source.Empty())
            throw new ArgumentException("La matrice sorgente non può essere nulla o vuota.", nameof(source));

        double lowerBound;
        double upperBound;

        if (parameters.Mode == PhotometricClippingMode.AbsoluteAdu)
        {
            lowerBound = parameters.MinAdu;
            upperBound = parameters.MaxAdu;
        }
        else
        {
            // Calcolo nativo di Media e Sigma tramite OpenCV per evitare errori di interfacciamento
            Cv2.MeanStdDev(source, out Scalar mean, out Scalar stddev);
            double bgMean = mean.Val0;
            double bgSigma = stddev.Val0;

            lowerBound = bgMean + (parameters.LowerSigma * bgSigma);
            upperBound = bgMean + (parameters.UpperSigma * bgSigma);
        }

        using Mat mask8U = new Mat();
        Cv2.InRange(source, new Scalar(lowerBound), new Scalar(upperBound), mask8U);

        return parameters.OutputMode switch
        {
            ClippingOutputMode.BinaryMask => GenerateBinaryMask(mask8U, source.Type()),
            ClippingOutputMode.ZeroedPixels => GenerateMaskedImage(source, mask8U, 0.0),
            ClippingOutputMode.NanPixels => GenerateMaskedImage(source, mask8U, double.NaN),
            _ => throw new NotSupportedException($"Modalità di output non supportata: {parameters.OutputMode}")
        };
    }

    private Mat GenerateBinaryMask(Mat mask8U, MatType targetType)
    {
        Mat binaryFloatMask = new Mat();
        mask8U.ConvertTo(binaryFloatMask, targetType, 1.0 / 255.0);
        return binaryFloatMask;
    }

    private Mat GenerateMaskedImage(Mat source, Mat mask8U, double fillValue)
    {
        Mat destination = new Mat(source.Size(), source.Type(), new Scalar(fillValue));
        source.CopyTo(destination, mask8U);
        return destination;
    }
}