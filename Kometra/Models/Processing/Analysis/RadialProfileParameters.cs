namespace Kometra.Models.Processing.Analysis;

public record RadialProfileParameters
{    
    public double CenterX { get; init; }
     
    public double CenterY { get; init; }
     
    public double MaxRadius { get; init; } = 100.0;
     
    public double StepSize { get; init; } = 1.0;

    public RadialProfileMode Mode { get; init; } = RadialProfileMode.Mean;

    public double StartingAngle { get; init; } = 0.0;
    public double IntegrationAngle { get; init; } = 180.0;
}