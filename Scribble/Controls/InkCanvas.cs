using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Scribble.Core.Models;
using Scribble.Core.Processing;

namespace Scribble.Controls;

/// <summary>
/// High-frequency stylus/pointer canvas with transparent checkerboard preview,
/// pressure-sensitive rendering, and write-on playback support.
/// </summary>
public sealed class InkCanvas : Control
{
    public static readonly StyledProperty<DrawingDocument?> DocumentProperty =
        AvaloniaProperty.Register<InkCanvas, DrawingDocument?>(nameof(Document));

    public static readonly StyledProperty<StrokeColor> ActiveColorProperty =
        AvaloniaProperty.Register<InkCanvas, StrokeColor>(nameof(ActiveColor), StrokeColor.White);

    public static readonly StyledProperty<double> BaseWidthProperty =
        AvaloniaProperty.Register<InkCanvas, double>(nameof(BaseWidth), 8.0);

    public static readonly StyledProperty<bool> IsEraserModeProperty =
        AvaloniaProperty.Register<InkCanvas, bool>(nameof(IsEraserMode));

    public static readonly StyledProperty<bool> PenOnlyModeProperty =
        AvaloniaProperty.Register<InkCanvas, bool>(nameof(PenOnlyMode));

    public static readonly StyledProperty<bool> IsPlaybackActiveProperty =
        AvaloniaProperty.Register<InkCanvas, bool>(nameof(IsPlaybackActive));

    public static readonly StyledProperty<double> PlaybackTimeMsProperty =
        AvaloniaProperty.Register<InkCanvas, double>(nameof(PlaybackTimeMs));

    public static readonly StyledProperty<bool> CompressIdleGapsProperty =
        AvaloniaProperty.Register<InkCanvas, bool>(nameof(CompressIdleGaps), true);

    public static readonly StyledProperty<Bitmap?> ReferenceImageProperty =
        AvaloniaProperty.Register<InkCanvas, Bitmap?>(nameof(ReferenceImage));

    public static readonly StyledProperty<double> ReferenceOpacityProperty =
        AvaloniaProperty.Register<InkCanvas, double>(nameof(ReferenceOpacity), 0.55);

    public static readonly StyledProperty<double> SpeedMultiplierProperty =
        AvaloniaProperty.Register<InkCanvas, double>(nameof(SpeedMultiplier), 1.0);

    static InkCanvas()
    {
        AffectsRender<InkCanvas>(
            DocumentProperty,
            IsPlaybackActiveProperty,
            PlaybackTimeMsProperty,
            ReferenceImageProperty,
            ReferenceOpacityProperty,
            SpeedMultiplierProperty);
    }

    public DrawingDocument? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public StrokeColor ActiveColor
    {
        get => GetValue(ActiveColorProperty);
        set => SetValue(ActiveColorProperty, value);
    }

    public double BaseWidth
    {
        get => GetValue(BaseWidthProperty);
        set => SetValue(BaseWidthProperty, value);
    }

    public bool IsEraserMode
    {
        get => GetValue(IsEraserModeProperty);
        set => SetValue(IsEraserModeProperty, value);
    }

    public bool PenOnlyMode
    {
        get => GetValue(PenOnlyModeProperty);
        set => SetValue(PenOnlyModeProperty, value);
    }

    public bool IsPlaybackActive
    {
        get => GetValue(IsPlaybackActiveProperty);
        set => SetValue(IsPlaybackActiveProperty, value);
    }

    public double PlaybackTimeMs
    {
        get => GetValue(PlaybackTimeMsProperty);
        set => SetValue(PlaybackTimeMsProperty, value);
    }

    public bool CompressIdleGaps
    {
        get => GetValue(CompressIdleGapsProperty);
        set => SetValue(CompressIdleGapsProperty, value);
    }

    public Bitmap? ReferenceImage
    {
        get => GetValue(ReferenceImageProperty);
        set => SetValue(ReferenceImageProperty, value);
    }

    public double ReferenceOpacity
    {
        get => GetValue(ReferenceOpacityProperty);
        set => SetValue(ReferenceOpacityProperty, value);
    }

    public double SpeedMultiplier
    {
        get => GetValue(SpeedMultiplierProperty);
        set => SetValue(SpeedMultiplierProperty, value);
    }

    public event Action<Stroke>? StrokeCompleted;
    public event Action<Stroke>? StrokeErased;

    private readonly Stopwatch _sessionClock = new();
    private Stroke? _activeStroke;
    private IPointer? _capturedPointer;

    private static readonly IBrush CheckerLight = new SolidColorBrush(Color.FromRgb(42, 42, 48));
    private static readonly IBrush CheckerDark = new SolidColorBrush(Color.FromRgb(30, 30, 35));
    private static readonly IPen CanvasBorderPen = new Pen(new SolidColorBrush(Color.FromRgb(75, 85, 99)), 1.5);

    public void ResetClock() => _sessionClock.Reset();

    /// <summary>
    /// Computes the letterboxed rectangle inside this control that represents the document frame.
    /// </summary>
    private Rect GetDocumentRect()
    {
        var doc = Document;
        double docW = doc?.Width > 0 ? doc.Width : 1920;
        double docH = doc?.Height > 0 ? doc.Height : 1080;

        double availW = Math.Max(1, Bounds.Width - 24);
        double availH = Math.Max(1, Bounds.Height - 24);

        double scale = Math.Min(availW / docW, availH / docH);
        double renderW = docW * scale;
        double renderH = docH * scale;
        double offsetX = (Bounds.Width - renderW) * 0.5;
        double offsetY = (Bounds.Height - renderH) * 0.5;

        return new Rect(offsetX, offsetY, renderW, renderH);
    }

    private (double DocX, double DocY) ToDocCoords(Point viewPt, Rect docRect)
    {
        var doc = Document;
        double docW = doc?.Width > 0 ? doc.Width : 1920;
        double docH = doc?.Height > 0 ? doc.Height : 1080;

        double normX = (viewPt.X - docRect.X) / docRect.Width;
        double normY = (viewPt.Y - docRect.Y) / docRect.Height;
        return (normX * docW, normY * docH);
    }

    private static Point ToViewCoords(StrokePoint pt, Rect docRect, double docW, double docH)
    {
        double vx = docRect.X + (pt.X / docW) * docRect.Width;
        double vy = docRect.Y + (pt.Y / docH) * docRect.Height;
        return new Point(vx, vy);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Document is null || IsPlaybackActive)
        {
            return;
        }

        if (PenOnlyMode && e.Pointer.Type == PointerType.Touch)
        {
            return;
        }

        var current = e.GetCurrentPoint(this);
        if (!current.Properties.IsLeftButtonPressed && e.Pointer.Type == PointerType.Mouse)
        {
            return;
        }

        var docRect = GetDocumentRect();
        if (!docRect.Contains(current.Position))
        {
            return;
        }

        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);

        if (IsEraserMode)
        {
            TryEraseAt(current.Position, docRect);
            e.Handled = true;
            return;
        }

        if (!_sessionClock.IsRunning)
        {
            _sessionClock.Start();
        }

        var (dx, dy) = ToDocCoords(current.Position, docRect);
        float pressure = NormalizePressure(current.Properties.Pressure, e.Pointer.Type);

        _activeStroke = new Stroke
        {
            Color = ActiveColor,
            BaseWidth = BaseWidth,
            Points = [new StrokePoint(dx, dy, pressure, _sessionClock.Elapsed.TotalMilliseconds)]
        };

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Document is null || _capturedPointer != e.Pointer)
        {
            return;
        }

        var docRect = GetDocumentRect();

        if (IsEraserMode)
        {
            var pt = e.GetCurrentPoint(this);
            TryEraseAt(pt.Position, docRect);
            e.Handled = true;
            return;
        }

        if (_activeStroke is null)
        {
            return;
        }

        var intermediate = e.GetIntermediatePoints(this);
        double baseTime = _sessionClock.Elapsed.TotalMilliseconds;
        int count = intermediate.Count;

        for (int i = 0; i < count; i++)
        {
            var p = intermediate[i];
            var (dx, dy) = ToDocCoords(p.Position, docRect);
            float pressure = NormalizePressure(p.Properties.Pressure, e.Pointer.Type);

            // Avoid duplicate zero-distance points
            if (_activeStroke.Points.Count > 0)
            {
                var last = _activeStroke.Points[^1];
                if (Math.Abs(last.X - dx) < 0.25 && Math.Abs(last.Y - dy) < 0.25)
                {
                    continue;
                }
            }

            _activeStroke.Points.Add(new StrokePoint(dx, dy, pressure, baseTime));
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        CompleteActiveStroke(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        CompleteActiveStroke(e.Pointer);
    }

    private void CompleteActiveStroke(IPointer pointer)
    {
        if (_capturedPointer != pointer)
        {
            return;
        }

        _capturedPointer = null;
        pointer.Capture(null);

        if (_activeStroke is { Points.Count: > 0 } completed && Document is not null)
        {
            Document.Strokes.Add(completed);
            StrokeCompleted?.Invoke(completed);
        }

        _activeStroke = null;
        InvalidateVisual();
    }

    private void TryEraseAt(Point viewPos, Rect docRect)
    {
        var doc = Document;
        if (doc is null || doc.Strokes.Count == 0)
        {
            return;
        }

        var (dx, dy) = ToDocCoords(viewPos, docRect);
        double hitRadius = Math.Max(16.0, BaseWidth * 2.0);
        double hitRadiusSq = hitRadius * hitRadius;

        for (int i = doc.Strokes.Count - 1; i >= 0; i--)
        {
            var stroke = doc.Strokes[i];
            foreach (var pt in stroke.Points)
            {
                double ox = pt.X - dx;
                double oy = pt.Y - dy;
                if (ox * ox + oy * oy <= hitRadiusSq)
                {
                    doc.Strokes.RemoveAt(i);
                    StrokeErased?.Invoke(stroke);
                    InvalidateVisual();
                    return;
                }
            }
        }
    }

    private static float NormalizePressure(float rawPressure, PointerType type)
    {
        if (type == PointerType.Mouse || rawPressure <= 0f)
        {
            return 0.65f;
        }
        return Math.Clamp(rawPressure, 0.08f, 1.0f);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var docRect = GetDocumentRect();

        // Draw transparency checkerboard inside the document frame
        using (context.PushClip(docRect))
        {
            context.FillRectangle(CheckerDark, docRect);
            const double tile = 20.0;
            int cols = (int)Math.Ceiling(docRect.Width / tile);
            int rows = (int)Math.Ceiling(docRect.Height / tile);
            for (int r = 0; r < rows; r++)
            {
                for (int c = (r % 2); c < cols; c += 2)
                {
                    var cell = new Rect(
                        docRect.X + c * tile,
                        docRect.Y + r * tile,
                        tile,
                        tile);
                    context.FillRectangle(CheckerLight, cell);
                }
            }

            if (ReferenceImage is { } refImg)
            {
                var imgSize = refImg.Size;
                if (imgSize.Width > 0 && imgSize.Height > 0)
                {
                    double imgScale = Math.Min(docRect.Width / imgSize.Width, docRect.Height / imgSize.Height);
                    double destW = imgSize.Width * imgScale;
                    double destH = imgSize.Height * imgScale;
                    double destX = docRect.X + (docRect.Width - destW) * 0.5;
                    double destY = docRect.Y + (docRect.Height - destH) * 0.5;

                    var srcRect = new Rect(0, 0, imgSize.Width, imgSize.Height);
                    var destRect = new Rect(destX, destY, destW, destH);
                    using (context.PushOpacity(Math.Clamp(ReferenceOpacity, 0.05, 1.0)))
                    {
                        context.DrawImage(refImg, srcRect, destRect);
                    }
                }
            }

            if (Document is not null)
            {
                double scale = docRect.Width / Math.Max(1, Document.Width);
                IReadOnlyList<Stroke> strokesToRender = IsPlaybackActive && CompressIdleGaps
                    ? StrokeSmoother.CompressIdleGaps(Document.Strokes)
                    : Document.Strokes;

                double effectivePlaybackTimeMs = PlaybackTimeMs * Math.Max(0.1, SpeedMultiplier);
                foreach (var stroke in strokesToRender)
                {
                    IReadOnlyList<StrokePoint> pts = IsPlaybackActive
                        ? WriteOnTimeline.GetVisiblePointsAtTime(stroke, effectivePlaybackTimeMs)
                        : stroke.Points;

                    DrawStroke(context, pts, stroke.Color, stroke.BaseWidth * scale, docRect, Document.Width, Document.Height);
                }

                if (_activeStroke is not null && !IsPlaybackActive)
                {
                    DrawStroke(context, _activeStroke.Points, _activeStroke.Color, _activeStroke.BaseWidth * scale, docRect, Document.Width, Document.Height);
                }
            }
        }

        context.DrawRectangle(null, CanvasBorderPen, docRect);
    }

    private static void DrawStroke(
        DrawingContext context,
        IReadOnlyList<StrokePoint> pts,
        StrokeColor color,
        double scaledBaseWidth,
        Rect docRect,
        int docW,
        int docH)
    {
        if (pts.Count == 0)
        {
            return;
        }

        var avaloniaColor = Color.FromArgb(color.A, color.R, color.G, color.B);
        var brush = new SolidColorBrush(avaloniaColor);

        if (pts.Count == 1)
        {
            var p = ToViewCoords(pts[0], docRect, docW, docH);
            double radius = Math.Max(1.0, scaledBaseWidth * pts[0].Pressure * 0.5);
            context.DrawEllipse(brush, null, p, radius, radius);
            return;
        }

        for (int i = 1; i < pts.Count; i++)
        {
            var a = pts[i - 1];
            var b = pts[i];
            var va = ToViewCoords(a, docRect, docW, docH);
            var vb = ToViewCoords(b, docRect, docW, docH);

            double avgPressure = (a.Pressure + b.Pressure) * 0.5;
            double thickness = Math.Max(1.0, scaledBaseWidth * avgPressure);
            var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawLine(pen, va, vb);
        }
    }
}
