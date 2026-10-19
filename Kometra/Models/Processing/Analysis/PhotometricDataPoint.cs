using System;

namespace Kometra.Models.Processing.Analysis;

public class PhotometricDataPoint
{
    public int PixelIndex { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public double Distance { get; set; }
    public double Value { get; set; }
    public string CoordinateText => $"[{X}, {Y}]";
}