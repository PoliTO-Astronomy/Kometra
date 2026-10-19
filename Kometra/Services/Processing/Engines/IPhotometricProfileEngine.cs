using System.Collections.Generic;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public interface IPhotometricProfileEngine
{
    IReadOnlyList<PhotometricDataPoint> CalculateProfile(Mat image, PhotometricCutParameters parameters);
}