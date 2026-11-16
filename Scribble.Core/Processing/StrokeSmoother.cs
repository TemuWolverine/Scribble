using Scribble.Core.Models;

namespace Scribble.Core.Processing;

/// <summary>
/// A simplified Bezier vertex with relative left/right control handles.
/// </summary>
public readonly record struct BezierVertex(
    double X,
    double Y,
    double LeftHandleX,
    double LeftHandleY,
    double RightHandleX,
    double RightHandleY);

/// <summary>
/// Simplifies raw stylus samples and fits smooth cubic Bezier handles.
/// </summary>
public static class StrokeSmoother
{
    /// <summary>
    /// Ramer-Douglas-Peucker simplification preserving start/end points, pressure, and timestamps.
    /// </summary>
    public static IReadOnlyList<StrokePoint> SimplifyRdp(IReadOnlyList<StrokePoint> points, double epsilon = 1.5)
    {
        if (points.Count <= 2)
        {
            return points;
        }

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;

        SimplifyRange(points, 0, points.Count - 1, epsilon, keep);

        var result = new List<StrokePoint>();
        for (int i = 0; i < points.Count; i++)
        {
            if (keep[i])
            {
                result.Add(points[i]);
            }
        }
        return result;
    }

    private static void SimplifyRange(
        IReadOnlyList<StrokePoint> points,
        int start,
        int end,
        double epsilon,
        bool[] keep)
    {
        if (end - start < 2)
        {
            return;
        }

        double maxDist = 0;
        int maxIdx = start;

        var a = points[start];
        var b = points[end];

        for (int i = start + 1; i < end; i++)
        {
            double dist = PerpendicularDistance(points[i], a, b);
            if (dist > maxDist)
            {
                maxDist = dist;
                maxIdx = i;
            }
        }

        if (maxDist > epsilon)
        {
            keep[maxIdx] = true;
            SimplifyRange(points, start, maxIdx, epsilon, keep);
            SimplifyRange(points, maxIdx, end, epsilon, keep);
        }
    }

    private static double PerpendicularDistance(StrokePoint p, StrokePoint a, StrokePoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq <= 1e-9)
        {
            double ox = p.X - a.X;
            double oy = p.Y - a.Y;
            return Math.Sqrt(ox * ox + oy * oy);
        }

        double cross = Math.Abs(dy * p.X - dx * p.Y + b.X * a.Y - b.Y * a.X);
        return cross / Math.Sqrt(lenSq);
    }

    /// <summary>
    /// Computes smooth Catmull-Rom-derived cubic Bezier handles for a sequence of points.
    /// Coordinates are kept in the input coordinate space; handles are relative to each vertex.
    /// </summary>
    public static IReadOnlyList<BezierVertex> FitBezierCurve(
        IReadOnlyList<(double X, double Y)> points,
        double tension = 0.25)
    {
        int n = points.Count;
        if (n == 0)
        {
            return [];
        }

        if (n == 1)
        {
            return [new BezierVertex(points[0].X, points[0].Y, 0, 0, 0, 0)];
        }

        var result = new BezierVertex[n];
        for (int i = 0; i < n; i++)
        {
            var curr = points[i];
            var prev = i > 0 ? points[i - 1] : curr;
            var next = i < n - 1 ? points[i + 1] : curr;

            double tx = (next.X - prev.X) * tension;
            double ty = (next.Y - prev.Y) * tension;

            double lx = i == 0 ? 0 : -tx;
            double ly = i == 0 ? 0 : -ty;
            double rx = i == n - 1 ? 0 : tx;
            double ry = i == n - 1 ? 0 : ty;

            result[i] = new BezierVertex(curr.X, curr.Y, lx, ly, rx, ry);
        }

        return result;
    }

    /// <summary>
    /// Compresses long idle pauses between consecutive strokes so the exported animation
    /// doesn't have dead air while the user was thinking between strokes.
    /// </summary>
    public static IReadOnlyList<Stroke> CompressIdleGaps(
        IReadOnlyList<Stroke> strokes,
        double maxGapMs = 450.0)
    {
        if (strokes.Count == 0)
        {
            return [];
        }

        var ordered = strokes
            .Where(s => s.Points.Count > 0)
            .OrderBy(s => s.StartTimeMs)
            .ToList();

        if (ordered.Count == 0)
        {
            return [];
        }

        var adjusted = new List<Stroke>(ordered.Count);
        double cumulativeShift = ordered[0].StartTimeMs; // Shift first stroke to start at t=0
        double prevEndAfterShift = 0;

        for (int i = 0; i < ordered.Count; i++)
        {
            var src = ordered[i];
            if (i > 0)
            {
                double currentStart = src.StartTimeMs - cumulativeShift;
                double gap = currentStart - prevEndAfterShift;
                if (gap > maxGapMs)
                {
                    cumulativeShift += gap - maxGapMs;
                }
            }

            var newPoints = new List<StrokePoint>(src.Points.Count);
            foreach (var pt in src.Points)
            {
                newPoints.Add(pt with { TimestampMs = Math.Max(0, pt.TimestampMs - cumulativeShift) });
            }

            var copy = new Stroke
            {
                Id = src.Id,
                Color = src.Color,
                BaseWidth = src.BaseWidth,
                Points = newPoints
            };
            adjusted.Add(copy);
            prevEndAfterShift = copy.EndTimeMs;
        }

        return adjusted;
    }
}
