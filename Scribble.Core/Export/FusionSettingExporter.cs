using System.Globalization;
using System.Text;
using Scribble.Core.Models;
using Scribble.Core.Processing;

namespace Scribble.Core.Export;

public sealed record FusionExportOptions(
    bool CompressIdleGaps = true,
    double MaxIdleGapMs = 450.0,
    double SimplifyEpsilon = 1.5,
    int MinStrokeFrames = 2,
    double SpeedMultiplier = 1.0);

/// <summary>
/// Exports a <see cref="DrawingDocument"/> as a DaVinci Resolve / Fusion .setting macro
/// containing transparent Background, PolylineMask vector strokes, and BezierSpline write-on keyframes.
/// </summary>
public static class FusionSettingExporter
{
    /// <summary>
    /// Converts screen coordinates [0..width, 0..height] (top-left origin)
    /// into Fusion PolylineMask normalized coordinates [-0.5..+0.5] (center origin, Y-up).
    /// </summary>
    public static (double X, double Y) ToFusionCoordinates(double screenX, double screenY, int width, int height)
    {
        double fx = (screenX / width) - 0.5;
        double fy = 0.5 - (screenY / height);
        return (fx, fy);
    }

    public static int CalculateRequiredFrames(DrawingDocument document, FusionExportOptions? options = null)
    {
        options ??= new FusionExportOptions();
        var strokes = GetPreparedStrokes(document, options);
        return CalculateRequiredFrames(document, strokes, options);
    }

    public static string Export(DrawingDocument document, FusionExportOptions? options = null)
    {
        options ??= new FusionExportOptions();
        var strokes = GetPreparedStrokes(document, options);

        var sb = new StringBuilder(8192);
        sb.AppendLine("{");
        sb.AppendLine("\tTools = ordered() {");
        AppendScribbleGroup(sb, document, strokes, options, indentTabs: 2, globalOut: null);
        sb.AppendLine("\t},");
        sb.AppendLine("\tActiveTool = \"ScribbleAnimation\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Exports a <see cref="DrawingDocument"/> wrapped inside a full DaVinci Resolve Fusion
    /// <c>Composition { ... }</c> (.comp) with <c>MediaOut1</c> wired to the animated stroke output.
    /// </summary>
    public static string ExportComposition(DrawingDocument document, FusionExportOptions? options = null)
    {
        options ??= new FusionExportOptions();
        var strokes = GetPreparedStrokes(document, options);
        int requiredFrames = CalculateRequiredFrames(document, strokes, options);
        int globalEnd = Math.Max(1, requiredFrames - 1);
        string finalOutputNode = strokes.Count == 0 ? "CanvasBg" : $"StrokeMerge_{strokes.Count}";

        var sb = new StringBuilder(8192);
        sb.AppendLine("Composition {");
        sb.AppendLine("\tCurrentTime = 0,");
        sb.AppendLine($"\tRenderRange = {{ 0, {globalEnd} }},");
        sb.AppendLine($"\tGlobalRange = {{ 0, {globalEnd} }},");
        sb.AppendLine("\tHiQ = true,");
        sb.AppendLine("\tPlaybackUpdateMode = 0,");
        sb.AppendLine("\tStereoMode = false,");
        sb.AppendLine("\tCustomData = { TEMPLATE_ID = \"Fusion Generator\" },");
        sb.AppendLine("\tTools = {");
        sb.AppendLine("\t\tMediaOut1 = Saver {");
        sb.AppendLine("\t\t\tInputs = {");
        sb.AppendLine("\t\t\t\tIndex = Input { Value = \"0\", },");
        sb.AppendLine("\t\t\t\tInput = Input {");
        sb.AppendLine($"\t\t\t\t\tSourceOp = \"{finalOutputNode}\",");
        sb.AppendLine("\t\t\t\t\tSource = \"Output\",");
        sb.AppendLine("\t\t\t\t},");
        sb.AppendLine("\t\t\t},");
        sb.AppendLine("\t\t\tViewInfo = OperatorInfo { Pos = { 440, 49.5 } },");
        sb.AppendLine("\t\t},");
        AppendScribbleGroup(sb, document, strokes, options, indentTabs: 2, globalOut: globalEnd);
        sb.AppendLine("\t},");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static IReadOnlyList<Stroke> GetPreparedStrokes(DrawingDocument document, FusionExportOptions options) =>
        options.CompressIdleGaps
            ? StrokeSmoother.CompressIdleGaps(document.Strokes, options.MaxIdleGapMs)
            : document.Strokes.Where(s => s.Points.Count > 0).ToList();

    private static int CalculateRequiredFrames(
        DrawingDocument document,
        IReadOnlyList<Stroke> strokes,
        FusionExportOptions options)
    {
        double speed = Math.Max(0.1, options.SpeedMultiplier);
        int maxEndFrame = 0;

        foreach (var stroke in strokes)
        {
            var (startFrame, endFrame) = ComputeStrokeFrameRange(stroke, document.FrameRate, speed, options.MinStrokeFrames);
            if (endFrame > maxEndFrame)
            {
                maxEndFrame = endFrame;
            }
        }

        int fps = Math.Max(1, document.FrameRate);
        return Math.Max(fps * 5, maxEndFrame + fps);
    }

    private static (int StartFrame, int EndFrame) ComputeStrokeFrameRange(
        Stroke stroke,
        int frameRate,
        double speed,
        int minStrokeFrames)
    {
        int startFrame = WriteOnTimeline.TimeMsToFrame(stroke.StartTimeMs / speed, frameRate);
        int endFrame = WriteOnTimeline.TimeMsToFrame(stroke.EndTimeMs / speed, frameRate);
        if (endFrame <= startFrame)
        {
            endFrame = startFrame + Math.Max(1, minStrokeFrames);
        }
        return (startFrame, endFrame);
    }

    private static void AppendScribbleGroup(
        StringBuilder sb,
        DrawingDocument document,
        IReadOnlyList<Stroke> strokes,
        FusionExportOptions options,
        int indentTabs,
        int? globalOut)
    {
        string indent = new('\t', indentTabs);
        string finalOutputNode = strokes.Count == 0 ? "CanvasBg" : $"StrokeMerge_{strokes.Count}";

        sb.AppendLine($"{indent}ScribbleAnimation = GroupOperator {{");
        sb.AppendLine($"{indent}\tOutputs = {{");
        sb.AppendLine($"{indent}\t\tMainOutput1 = InstanceOutput {{");
        sb.AppendLine($"{indent}\t\t\tSourceOp = \"{finalOutputNode}\",");
        sb.AppendLine($"{indent}\t\t\tSource = \"Output\",");
        sb.AppendLine($"{indent}\t\t}},");
        sb.AppendLine($"{indent}\t}},");
        sb.AppendLine($"{indent}\tViewInfo = GroupInfo {{ Pos = {{ 230, 49.5 }} }},");
        sb.AppendLine($"{indent}\tTools = ordered() {{");

        // Transparent base canvas
        AppendCanvasBackground(sb, document.Width, document.Height, globalOut);

        string previousMergeOutput = "CanvasBg";
        for (int i = 0; i < strokes.Count; i++)
        {
            int idx = i + 1;
            var stroke = strokes[i];
            AppendStrokePipeline(sb, idx, stroke, document, options, previousMergeOutput, globalOut);
            previousMergeOutput = $"StrokeMerge_{idx}";
        }

        sb.AppendLine($"{indent}\t}},");
        sb.AppendLine($"{indent}}},");
    }

    private static void AppendCanvasBackground(StringBuilder sb, int width, int height, int? globalOut)
    {
        sb.AppendLine("\t\t\t\tCanvasBg = Background {");
        sb.AppendLine("\t\t\t\t\tInputs = {");
        if (globalOut.HasValue)
        {
            sb.AppendLine($"\t\t\t\t\t\tGlobalOut = Input {{ Value = {globalOut.Value}, }},");
        }
        sb.AppendLine($"\t\t\t\t\t\tWidth = Input {{ Value = {width}, }},");
        sb.AppendLine($"\t\t\t\t\t\tHeight = Input {{ Value = {height}, }},");
        sb.AppendLine("\t\t\t\t\t\t[\"Gamut.SLogVersion\"] = Input { Value = FuID { \"SLog2\" }, },");
        sb.AppendLine("\t\t\t\t\t\tTopLeftAlpha = Input { Value = 0, },");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t\tViewInfo = OperatorInfo { Pos = { 0, 0 } },");
        sb.AppendLine("\t\t\t\t},");
    }

    private static void AppendStrokePipeline(
        StringBuilder sb,
        int idx,
        Stroke stroke,
        DrawingDocument doc,
        FusionExportOptions options,
        string previousOutputNode,
        int? globalOut)
    {
        var simplified = StrokeSmoother.SimplifyRdp(stroke.Points, options.SimplifyEpsilon);
        var fusionPoints = new List<(double X, double Y)>(simplified.Count);
        foreach (var pt in simplified)
        {
            fusionPoints.Add(ToFusionCoordinates(pt.X, pt.Y, doc.Width, doc.Height));
        }

        // Duplicate single-point taps slightly so Fusion renders a dot cleanly
        if (fusionPoints.Count == 1)
        {
            var p = fusionPoints[0];
            fusionPoints.Add((p.X + 0.0005, p.Y));
        }

        var bezier = StrokeSmoother.FitBezierCurve(fusionPoints);

        double speed = Math.Max(0.1, options.SpeedMultiplier);
        var (startFrame, endFrame) = ComputeStrokeFrameRange(stroke, doc.FrameRate, speed, options.MinStrokeFrames);

        double effectiveWidthPx = stroke.BaseWidth * stroke.AveragePressure;
        double borderWidth = Math.Max(0.0005, effectiveWidthPx / doc.Width);
        int xOffset = idx * 140;

        // 1. WriteLength BezierSpline
        sb.AppendLine($"\t\t\t\tWriteLengthSpline_{idx} = BezierSpline {{");
        sb.AppendLine("\t\t\t\t\tSplineColor = { Red = 255, Green = 160, Blue = 0 },");
        sb.AppendLine("\t\t\t\t\tKeyFrames = {");
        sb.AppendLine($"\t\t\t\t\t\t[{startFrame}] = {{ 0, Flags = {{ Linear = true }} }},");
        sb.AppendLine($"\t\t\t\t\t\t[{endFrame}] = {{ 1, Flags = {{ Linear = true }} }},");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t},");

        // 2. PolylineMask
        sb.AppendLine($"\t\t\t\tStrokeMask_{idx} = PolylineMask {{");
        sb.AppendLine("\t\t\t\t\tDrawMode = Input { Value = FuID { \"InsertAndModify\" }, },");
        sb.AppendLine("\t\t\t\t\tInputs = {");
        sb.AppendLine("\t\t\t\t\t\tFilter = Input { Value = FuID { \"Fast Gaussian\" }, },");
        sb.AppendLine("\t\t\t\t\t\tSoftEdge = Input { Value = 0.0008, },");
        sb.AppendLine($"\t\t\t\t\t\tBorderWidth = Input {{ Value = {F(borderWidth)}, }},");
        sb.AppendLine("\t\t\t\t\t\tSolid = Input { Value = 0, },");
        sb.AppendLine("\t\t\t\t\t\tCapStyle = Input { Value = 1, },");
        sb.AppendLine("\t\t\t\t\t\tJoinStyle = Input { Value = 1, },");
        sb.AppendLine("\t\t\t\t\t\tWritePosition = Input { Value = 0, },");
        sb.AppendLine($"\t\t\t\t\t\tWriteLength = Input {{ SourceOp = \"WriteLengthSpline_{idx}\", Source = \"Value\", }},");
        sb.AppendLine($"\t\t\t\t\t\tMaskWidth = Input {{ Value = {doc.Width}, }},");
        sb.AppendLine($"\t\t\t\t\t\tMaskHeight = Input {{ Value = {doc.Height}, }},");
        sb.AppendLine("\t\t\t\t\t\tPixelAspect = Input { Value = { 1, 1 }, },");
        sb.AppendLine("\t\t\t\t\t\tPolyline = Input {");
        sb.AppendLine("\t\t\t\t\t\t\tValue = Polyline {");
        sb.AppendLine("\t\t\t\t\t\t\t\tClosed = false,");
        sb.AppendLine("\t\t\t\t\t\t\t\tPoints = {");
        foreach (var v in bezier)
        {
            sb.AppendLine(
                $"\t\t\t\t\t\t\t\t\t{{ Linear = false, X = {F(v.X)}, Y = {F(v.Y)}, LX = {F(v.LeftHandleX)}, LY = {F(v.LeftHandleY)}, RX = {F(v.RightHandleX)}, RY = {F(v.RightHandleY)} }},");
        }
        sb.AppendLine("\t\t\t\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine($"\t\t\t\t\tViewInfo = OperatorInfo {{ Pos = {{ {xOffset}, -120 }} }},");
        sb.AppendLine("\t\t\t\t},");

        // 3. Stroke Color Background
        var (r, g, b, a) = stroke.Color.ToNormalized();
        sb.AppendLine($"\t\t\t\tStrokeBg_{idx} = Background {{");
        sb.AppendLine("\t\t\t\t\tInputs = {");
        if (globalOut.HasValue)
        {
            sb.AppendLine($"\t\t\t\t\t\tGlobalOut = Input {{ Value = {globalOut.Value}, }},");
        }
        sb.AppendLine($"\t\t\t\t\t\tWidth = Input {{ Value = {doc.Width}, }},");
        sb.AppendLine($"\t\t\t\t\t\tHeight = Input {{ Value = {doc.Height}, }},");
        sb.AppendLine("\t\t\t\t\t\t[\"Gamut.SLogVersion\"] = Input { Value = FuID { \"SLog2\" }, },");
        sb.AppendLine($"\t\t\t\t\t\tTopLeftRed = Input {{ Value = {F(r)}, }},");
        sb.AppendLine($"\t\t\t\t\t\tTopLeftGreen = Input {{ Value = {F(g)}, }},");
        sb.AppendLine($"\t\t\t\t\t\tTopLeftBlue = Input {{ Value = {F(b)}, }},");
        sb.AppendLine($"\t\t\t\t\t\tTopLeftAlpha = Input {{ Value = {F(a)}, }},");
        sb.AppendLine($"\t\t\t\t\t\tEffectMask = Input {{ SourceOp = \"StrokeMask_{idx}\", Source = \"Mask\", }},");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine($"\t\t\t\t\tViewInfo = OperatorInfo {{ Pos = {{ {xOffset}, -40 }} }},");
        sb.AppendLine("\t\t\t\t},");

        // 4. Blend BezierSpline (hides the stroke's start-point cap until it actually begins drawing)
        sb.AppendLine($"\t\t\t\tStrokeBlendSpline_{idx} = BezierSpline {{");
        sb.AppendLine("\t\t\t\t\tSplineColor = { Red = 0, Green = 200, Blue = 255 },");
        sb.AppendLine("\t\t\t\t\tKeyFrames = {");
        if (startFrame > 0)
        {
            sb.AppendLine("\t\t\t\t\t\t[0] = { 0, Flags = { Linear = true } },");
        }
        sb.AppendLine($"\t\t\t\t\t\t[{startFrame}] = {{ 0, Flags = {{ Linear = true }} }},");
        sb.AppendLine($"\t\t\t\t\t\t[{startFrame + 1}] = {{ 1, Flags = {{ Linear = true }} }},");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine("\t\t\t\t},");

        // 5. Merge onto running chain
        sb.AppendLine($"\t\t\t\tStrokeMerge_{idx} = Merge {{");
        sb.AppendLine("\t\t\t\t\tInputs = {");
        sb.AppendLine($"\t\t\t\t\t\tBlend = Input {{ SourceOp = \"StrokeBlendSpline_{idx}\", Source = \"Value\", }},");
        sb.AppendLine($"\t\t\t\t\t\tBackground = Input {{ SourceOp = \"{previousOutputNode}\", Source = \"Output\", }},");
        sb.AppendLine($"\t\t\t\t\t\tForeground = Input {{ SourceOp = \"StrokeBg_{idx}\", Source = \"Output\", }},");
        sb.AppendLine("\t\t\t\t\t\tPerformDepthMerge = Input { Value = 0, },");
        sb.AppendLine("\t\t\t\t\t},");
        sb.AppendLine($"\t\t\t\t\tViewInfo = OperatorInfo {{ Pos = {{ {xOffset}, 0 }} }},");
        sb.AppendLine("\t\t\t\t},");
    }

    private static string F(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);
}
