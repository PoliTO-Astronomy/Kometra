using System;
using System.Collections.Generic;
using Kometra.Models.Processing.Analysis;
using OpenCvSharp;

namespace Kometra.Services.Processing.Engines;

public class PhotometricProfileEngine : IPhotometricProfileEngine
{
    public IReadOnlyList<PhotometricDataPoint> CalculateProfile(Mat image, PhotometricCutParameters parameters)
    {
        if (image == null || image.Empty()) return Array.Empty<PhotometricDataPoint>();

        var points = GetBresenhamLine(parameters.StartX, parameters.StartY, parameters.EndX, parameters.EndY);
        var result = new List<PhotometricDataPoint>();

        for (int i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            if (pt.X >= 0 && pt.X < image.Cols && pt.Y >= 0 && pt.Y < image.Rows)
            {
                float val = image.At<float>(pt.Y, pt.X);
                double dist = Math.Sqrt(Math.Pow(pt.X - parameters.StartX, 2) + Math.Pow(pt.Y - parameters.StartY, 2));
                
                result.Add(new PhotometricDataPoint
                {
                    PixelIndex = i,
                    X = pt.X,
                    Y = pt.Y,
                    Distance = dist,
                    Value = val
                });
            }
        }
        return result;
    }

    private List<(int X, int Y)> GetBresenhamLine(int x0, int y0, int x1, int y1)
    {
        var points = new List<(int X, int Y)>();
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy, e2;

        while (true)
        {
            points.Add((x0, y0));
            if (x0 == x1 && y0 == y1) break;
            e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
        return points;
    }
}