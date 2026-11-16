namespace Scribble.Core.Models;

/// <summary>
/// Root document containing canvas dimensions, frame rate, and recorded strokes.
/// </summary>
public sealed class DrawingDocument
{
    public int Width { get; set; } = CanvasPreset.Hd1080p.Width;
    public int Height { get; set; } = CanvasPreset.Hd1080p.Height;
    public int FrameRate { get; set; } = 30;
    public List<Stroke> Strokes { get; init; } = [];

    public double TotalDurationMs =>
        Strokes.Count == 0 ? 0 : Strokes.Max(s => s.EndTimeMs);
}
