using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace Kometra.Models.Processing.Analysis;

public class IsophoteAnalysisResult : IDisposable
{
    public List<EllipticalIsophoteDataPoint> Isophotes { get; init; } = new();
    public Mat? OverlayImage { get; init; }

    public void Dispose()
    {
        OverlayImage?.Dispose();
    }
}