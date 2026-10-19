namespace Kometra.Models.Processing.Analysis;

public record EllipticalIsophoteParameters
{
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
    public double StepSize { get; init; }
    public bool IsMonochromatic { get; init; }
    public string MonoColorHex { get; init; } = "#8058E8"; 
}