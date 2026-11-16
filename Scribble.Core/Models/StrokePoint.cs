namespace Scribble.Core.Models;

/// <summary>
/// A single stylus/pointer sample captured during a stroke.
/// </summary>
public readonly record struct StrokePoint(
    double X,
    double Y,
    float Pressure,
    double TimestampMs);
