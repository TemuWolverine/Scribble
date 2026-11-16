namespace Scribble.Core.Models;

/// <summary>
/// A continuous pen/stylus stroke with timing and pressure samples.
/// </summary>
public sealed class Stroke
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public StrokeColor Color { get; set; } = StrokeColor.White;
    public double BaseWidth { get; set; } = 6.0;
    public List<StrokePoint> Points { get; init; } = [];

    public double StartTimeMs => Points.Count > 0 ? Points[0].TimestampMs : 0;
    public double EndTimeMs => Points.Count > 0 ? Points[^1].TimestampMs : 0;

    /// <summary>
    /// Computes the average pressure across all points in the stroke (clamped to [0.15, 1.0]).
    /// </summary>
    public double AveragePressure
    {
        get
        {
            if (Points.Count == 0) return 1.0;
            double sum = 0;
            for (int i = 0; i < Points.Count; i++)
            {
                sum += Points[i].Pressure;
            }
            return Math.Clamp(sum / Points.Count, 0.15, 1.0);
        }
    }
}
