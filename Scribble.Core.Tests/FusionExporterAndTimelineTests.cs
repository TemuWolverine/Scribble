using Scribble.Core.Export;
using Scribble.Core.Models;
using Scribble.Core.Processing;
using Scribble.Core.Serialization;
using Xunit;

namespace Scribble.Core.Tests;

public class FusionExporterAndTimelineTests
{
    [Fact]
    public void ToFusionCoordinates_FlipsYAndCentersOrigin()
    {
        var topLeft = FusionSettingExporter.ToFusionCoordinates(0, 0, 1920, 1080);
        Assert.Equal(-0.5, topLeft.X, 5);
        Assert.Equal(0.5, topLeft.Y, 5);

        var center = FusionSettingExporter.ToFusionCoordinates(960, 540, 1920, 1080);
        Assert.Equal(0.0, center.X, 5);
        Assert.Equal(0.0, center.Y, 5);

        var bottomRight = FusionSettingExporter.ToFusionCoordinates(1920, 1080, 1920, 1080);
        Assert.Equal(0.5, bottomRight.X, 5);
        Assert.Equal(-0.5, bottomRight.Y, 5);
    }

    [Fact]
    public void CompressIdleGaps_ShiftsFirstStrokeToZeroAndCapsLongPauses()
    {
        var s1 = new Stroke
        {
            Points =
            [
                new StrokePoint(10, 10, 0.5f, 1000),
                new StrokePoint(50, 50, 0.5f, 1500)
            ]
        };
        var s2 = new Stroke
        {
            Points =
            [
                new StrokePoint(100, 100, 0.8f, 6500), // 5000ms gap
                new StrokePoint(200, 200, 0.8f, 7000)
            ]
        };

        var compressed = StrokeSmoother.CompressIdleGaps([s1, s2], maxGapMs: 400);

        Assert.Equal(2, compressed.Count);
        Assert.Equal(0, compressed[0].StartTimeMs, 3);
        Assert.Equal(500, compressed[0].EndTimeMs, 3);
        Assert.Equal(900, compressed[1].StartTimeMs, 3);
        Assert.Equal(1400, compressed[1].EndTimeMs, 3);
    }

    [Fact]
    public void WriteOnTimeline_InterpolatesPartialStrokeAtTimestamp()
    {
        var stroke = new Stroke
        {
            Points =
            [
                new StrokePoint(0, 0, 0.2f, 0),
                new StrokePoint(100, 200, 0.8f, 1000)
            ]
        };

        var half = WriteOnTimeline.GetVisiblePointsAtTime(stroke, 500);
        Assert.Equal(2, half.Count);
        Assert.Equal(50, half[1].X, 3);
        Assert.Equal(100, half[1].Y, 3);
        Assert.Equal(0.5f, half[1].Pressure, 3);
    }

    [Fact]
    public void FusionExporter_ProducesBalancedBracesAndWriteLengthKeyframes()
    {
        var doc = new DrawingDocument
        {
            Width = 1920,
            Height = 1080,
            FrameRate = 30
        };
        doc.Strokes.Add(new Stroke
        {
            Color = StrokeColor.Red,
            BaseWidth = 8,
            Points =
            [
                new StrokePoint(100, 100, 0.6f, 0),
                new StrokePoint(500, 300, 0.8f, 500),
                new StrokePoint(900, 200, 0.7f, 1000)
            ]
        });

        string lua = FusionSettingExporter.Export(doc);

        Assert.Contains("ScribbleAnimation = GroupOperator", lua);
        Assert.Contains("TopLeftAlpha = Input { Value = 0, }", lua);
        Assert.Contains("StrokeMask_1 = PolylineMask", lua);
        Assert.Contains("WriteLengthSpline_1 = BezierSpline", lua);
        Assert.Contains("StrokeBlendSpline_1 = BezierSpline", lua);
        Assert.Contains("Blend = Input { SourceOp = \"StrokeBlendSpline_1\", Source = \"Value\", }", lua);
        Assert.Contains("[0] = { 0, Flags = { Linear = true } }", lua);
        Assert.Contains("[30] = { 1, Flags = { Linear = true } }", lua);

        int openBraces = lua.Count(c => c == '{');
        int closeBraces = lua.Count(c => c == '}');
        Assert.Equal(openBraces, closeBraces);
    }

    [Fact]
    public void DocumentSerializer_RoundTripsDocument()
    {
        var doc = new DrawingDocument { Width = 3840, Height = 2160, FrameRate = 60 };
        doc.Strokes.Add(new Stroke
        {
            Color = StrokeColor.Blue,
            BaseWidth = 12,
            Points = [new StrokePoint(10, 20, 0.75f, 123.4)]
        });

        string json = DocumentSerializer.Serialize(doc);
        var restored = DocumentSerializer.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(3840, restored!.Width);
        Assert.Equal(60, restored.FrameRate);
        Assert.Single(restored.Strokes);
        Assert.Equal(StrokeColor.Blue, restored.Strokes[0].Color);
    }

    [Fact]
    public void AppSettings_RoundTripsCustomPalette()
    {
        var settings = new AppSettings
        {
            PaletteColors = ["#FF112233", "#FF445566"]
        };

        string json = settings.ToJson();
        var loaded = AppSettings.FromJson(json);

        Assert.Equal(2, loaded.PaletteColors.Count);
        Assert.Equal("#FF112233", loaded.PaletteColors[0]);
        Assert.Equal("#FF445566", loaded.PaletteColors[1]);
    }

    [Fact]
    public void FusionExporter_SpeedMultiplierScalesKeyframes()
    {
        var doc = new DrawingDocument
        {
            Width = 1920,
            Height = 1080,
            FrameRate = 30
        };
        doc.Strokes.Add(new Stroke
        {
            Color = StrokeColor.White,
            BaseWidth = 6,
            Points =
            [
                new StrokePoint(100, 100, 0.5f, 0),
                new StrokePoint(500, 500, 0.5f, 2000)
            ]
        });

        string lua = FusionSettingExporter.Export(doc, new FusionExportOptions(SpeedMultiplier: 2.0));

        // 2000ms at 30fps = 60 frames; at 2x speed = 30 frames
        Assert.Contains("[0] = { 0, Flags = { Linear = true } }", lua);
        Assert.Contains("[30] = { 1, Flags = { Linear = true } }", lua);
    }
}
