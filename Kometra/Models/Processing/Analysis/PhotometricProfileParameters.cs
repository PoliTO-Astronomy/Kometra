namespace Kometra.Models.Processing.Analysis;

public record PhotometricCutParameters
{
    public int StartX { get; init; }
    public int StartY { get; init; }
    public int EndX { get; init; }
    public int EndY { get; init; }
}