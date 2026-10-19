using System;
namespace Kometra.Models.Processing.Analysis;

public class EllipticalIsophoteDataPoint
{
    public double PositionAngleRad { get; set; }
    public double PositionAngleDeg => PositionAngleRad * (180.0 / Math.PI);
    public double SemiMajorAxis { get; set; }  // a
    public double SemiMinorAxis { get; set; }  // b
    public double Ellipticity { get; set; }    // 1 - (b/a)
    public double PositionAngle { get; set; }  // Angolo theta in radianti
    public double MeanValue { get; set; }      // Intensità media I(a)
    public double MedianValue { get; set; }
    public double StandardDeviation { get; set; }
    public int PixelCount { get; set; }
}