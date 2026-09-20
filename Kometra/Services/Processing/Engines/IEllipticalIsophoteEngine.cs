using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public interface IEllipticalIsophoteEngine
{
    IsophoteAnalysisResult CalculateIsophotes(Mat image, EllipticalIsophoteParameters parameters);
}