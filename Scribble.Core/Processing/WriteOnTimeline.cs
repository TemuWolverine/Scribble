using Scribble.Core.Models;

namespace Scribble.Core.Processing;

/// <summary>
/// Helpers for write-on playback preview and frame-rate time conversion.
/// </summary>
public static class WriteOnTimeline
{
    /// <summary>
    /// Converts a timestamp in milliseconds to a rounded frame index at the given FPS.
    /// </summary>
    public static int TimeMsToFrame(double timeMs, int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        if (timeMs <= 0) return 0;
        return (int)Math.Round(timeMs / 1000.0 * fps);
    }

    /// <summary>
    /// Returns the visible prefix of a stroke at <paramref name="timeMs"/>,
    /// linearly interpolating the partial tip segment for smooth playback.
    /// </summary>
    public static IReadOnlyList<StrokePoint> GetVisiblePointsAtTime(Stroke stroke, double timeMs)
    {
        var pts = stroke.Points;
        if (pts.Count == 0 || timeMs < pts[0].TimestampMs)
        {
            return [];
        }

        if (timeMs >= pts[^1].TimestampMs)
        {
            return pts;
        }

        var visible = new List<StrokePoint> { pts[0] };
        for (int i = 1; i < pts.Count; i++)
        {
            var prev = pts[i - 1];
            var curr = pts[i];
            if (curr.TimestampMs <= timeMs)
            {
                visible.Add(curr);
            }
            else
            {
                double span = curr.TimestampMs - prev.TimestampMs;
                if (span > 1e-6)
                {
                    double t = Math.Clamp((timeMs - prev.TimestampMs) / span, 0.0, 1.0);
                    visible.Add(new StrokePoint(
                        prev.X + (curr.X - prev.X) * t,
                        prev.Y + (curr.Y - prev.Y) * t,
                        (float)(prev.Pressure + (curr.Pressure - prev.Pressure) * t),
                        timeMs));
                }
                break;
            }
        }

        return visible;
    }
}
