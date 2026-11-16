using Scribble.Core.Export;
using Scribble.Core.Integration;
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

    [Fact]
    public void DaVinciResolveFrameGrabber_ComputesFrameOffsetFromTimecode()
    {
        Assert.Equal(4546, DaVinciResolveFrameGrabber.TimecodeToFrames("00:03:01:21", 25.0));
        Assert.Equal(4546, DaVinciResolveFrameGrabber.ComputeFrameOffset("00:03:01:21", "00:00:00:00", 25.0));
        Assert.Equal(150, DaVinciResolveFrameGrabber.ComputeFrameOffset("01:00:05;00", "01:00:00:00", 29.97));
        Assert.Equal(0, DaVinciResolveFrameGrabber.ComputeFrameOffset("00:00:00:00", "01:00:00:00", 24.0));
    }

    [Fact]
    public void DaVinciResolveFrameGrabber_BuildPythonGrabScriptIncludesExpectedApis()
    {
        string script = DaVinciResolveFrameGrabber.BuildPythonGrabScript();

        Assert.Contains("DaVinciResolveScript", script);
        Assert.Contains("ExportCurrentFrameAsStill", script);
        Assert.Contains("ExportFrame", script);
        Assert.Contains("GetCurrentTimecode", script);
        Assert.Contains(DaVinciResolveFrameGrabber.ResultMarker, script);
    }

    [Fact]
    public void DaVinciResolveFrameGrabber_ParseScriptResponseHandlesSuccessAndError()
    {
        byte[] samplePng = [0x89, 0x50, 0x4E, 0x47];
        string okStdout = $"debug line\n{DaVinciResolveFrameGrabber.ResultMarker}{{\"success\":true,\"project\":\"Demo\",\"timeline\":\"Main\",\"timecode\":\"00:03:01:21\",\"width\":3840,\"height\":2160,\"error\":null}}";

        var okResult = DaVinciResolveFrameGrabber.ParseScriptResponse(okStdout, string.Empty, samplePng);
        Assert.True(okResult.Success);
        Assert.Equal("Demo", okResult.ProjectName);
        Assert.Equal("Main", okResult.TimelineName);
        Assert.Equal("00:03:01:21", okResult.Timecode);
        Assert.Equal(3840, okResult.Width);
        Assert.Equal(2160, okResult.Height);
        Assert.Equal(samplePng, okResult.ImageBytes);

        var missingBytesResult = DaVinciResolveFrameGrabber.ParseScriptResponse(okStdout, string.Empty, null);
        Assert.False(missingBytesResult.Success);
        Assert.NotNull(missingBytesResult.Error);

        string errStdout = $"{DaVinciResolveFrameGrabber.ResultMarker}{{\"success\":false,\"error\":\"No timeline is currently open in DaVinci Resolve.\"}}";
        var errResult = DaVinciResolveFrameGrabber.ParseScriptResponse(errStdout, string.Empty, null);
        Assert.False(errResult.Success);
        Assert.Equal("No timeline is currently open in DaVinci Resolve.", errResult.Error);
    }

    [Fact]
    public void FusionExporter_ExportCompositionWrapsGroupWithMediaOutAndGlobalRange()
    {
        var doc = new DrawingDocument
        {
            Width = 1920,
            Height = 1080,
            FrameRate = 25
        };
        doc.Strokes.Add(new Stroke
        {
            Color = StrokeColor.Red,
            BaseWidth = 8,
            Points =
            [
                new StrokePoint(100, 100, 0.6f, 0),
                new StrokePoint(500, 300, 0.8f, 6000)
            ]
        });

        int requiredFrames = FusionSettingExporter.CalculateRequiredFrames(doc);
        // 6000ms @ 25fps = 150 frames + 25 frames hold = 175 frames -> GlobalOut = 174
        Assert.Equal(175, requiredFrames);

        string comp = FusionSettingExporter.ExportComposition(doc);

        Assert.StartsWith("Composition {", comp);
        Assert.Contains("RenderRange = { 0, 174 }", comp);
        Assert.Contains("GlobalRange = { 0, 174 }", comp);
        Assert.Contains("MediaOut1 = Saver", comp);
        Assert.Contains("SourceOp = \"StrokeMerge_1\"", comp);
        Assert.Contains("ScribbleAnimation = GroupOperator", comp);
        Assert.Contains("GlobalOut = Input { Value = 174, }", comp);

        int openBraces = comp.Count(c => c == '{');
        int closeBraces = comp.Count(c => c == '}');
        Assert.Equal(openBraces, closeBraces);
    }

    [Fact]
    public void DaVinciResolveFrameGrabber_BuildPythonInjectScriptAndParseResponse()
    {
        Assert.Equal("00:02:33:09", DaVinciResolveFrameGrabber.FramesToTimecode(3834, 25.0));

        string script = DaVinciResolveFrameGrabber.BuildPythonInjectScript();
        Assert.Contains("InsertFusionCompositionIntoTimeline", script);
        Assert.Contains("ImportFusionComp", script);
        Assert.Contains("GetCurrentVideoItem", script);
        Assert.Contains("SetPosition", script);
        Assert.Contains("AddTrack", script);

        string okStdout = $"{DaVinciResolveFrameGrabber.ResultMarker}{{\"success\":true,\"project\":\"Demo\",\"timeline\":\"Main\",\"timecode\":\"00:02:33:09\",\"trackIndex\":2,\"startFrame\":3834,\"duration\":125,\"error\":null}}";
        var okResult = DaVinciResolveFrameGrabber.ParseInjectResponse(okStdout, string.Empty);
        Assert.True(okResult.Success);
        Assert.Equal("Demo", okResult.ProjectName);
        Assert.Equal("Main", okResult.TimelineName);
        Assert.Equal("00:02:33:09", okResult.Timecode);
        Assert.Equal(2, okResult.TrackIndex);
        Assert.Equal(3834, okResult.StartFrame);
        Assert.Equal(125, okResult.DurationFrames);

        string errStdout = $"{DaVinciResolveFrameGrabber.ResultMarker}{{\"success\":false,\"error\":\"No timeline is currently open in DaVinci Resolve.\"}}";
        var errResult = DaVinciResolveFrameGrabber.ParseInjectResponse(errStdout, string.Empty);
        Assert.False(errResult.Success);
        Assert.Equal("No timeline is currently open in DaVinci Resolve.", errResult.Error);
    }
}
