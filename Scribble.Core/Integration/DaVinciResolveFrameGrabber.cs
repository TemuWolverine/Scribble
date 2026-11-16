using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Scribble.Core.Integration;

/// <summary>
/// Result of grabbing the current playhead frame from DaVinci Resolve.
/// </summary>
public sealed record ResolveFrameGrabResult(
    bool Success,
    byte[]? ImageBytes = null,
    string? ProjectName = null,
    string? TimelineName = null,
    string? Timecode = null,
    int Width = 0,
    int Height = 0,
    string? Error = null);

/// <summary>
/// Result of injecting a Fusion composition into the active DaVinci Resolve timeline.
/// </summary>
public sealed record ResolveInjectResult(
    bool Success,
    string? ProjectName = null,
    string? TimelineName = null,
    string? Timecode = null,
    int TrackIndex = 0,
    int StartFrame = 0,
    int DurationFrames = 0,
    string? Error = null);

/// <summary>
/// Integrates with a running DaVinci Resolve instance via its local Python scripting API
/// to capture playhead frames and inject Fusion compositions into the active timeline.
/// </summary>
public static class DaVinciResolveFrameGrabber
{
    public const string ResultMarker = "SCRIBBLE_RESOLVE_JSON:";

    private const string WindowsResolvePythonPath =
        @"C:\Program Files\Blackmagic Design\DaVinci Resolve\ResolvePython\ResolvePython.exe";

    private const string MacOsResolvePythonPath =
        "/Applications/DaVinci Resolve/DaVinci Resolve.app/Contents/Applications/ResolvePython";

    private const string LinuxResolvePythonPath =
        "/opt/resolve/bin/ResolvePython";

    private const string SharedPythonPrelude = """
import json
import os
import sys

def emit(payload):
    print("SCRIBBLE_RESOLVE_JSON:" + json.dumps(payload))

def tc_to_frames(tc_str, fps):
    if not tc_str:
        return 0
    parts = tc_str.strip().replace(";", ":").split(":")
    if len(parts) != 4:
        return 0
    try:
        hh, mm, ss, ff = (int(p) for p in parts)
    except ValueError:
        return 0
    fps_int = max(1, int(round(float(fps))))
    return ((hh * 3600 + mm * 60 + ss) * fps_int) + ff

def frames_to_tc(frames, fps):
    fps_int = max(1, int(round(float(fps))))
    safe_frames = max(0, int(frames))
    ff = safe_frames % fps_int
    total_sec = safe_frames // fps_int
    ss = total_sec % 60
    mm = (total_sec // 60) % 60
    hh = total_sec // 3600
    return f"{hh:02d}:{mm:02d}:{ss:02d}:{ff:02d}"

def parse_int(val):
    try:
        return int(float(val))
    except (TypeError, ValueError):
        return 0

def get_timeline_context():
    try:
        import DaVinciResolveScript as dvr_script
    except Exception as ex:
        emit({"success": False, "error": f"Failed to import DaVinciResolveScript: {ex}"})
        return None

    resolve = dvr_script.scriptapp("Resolve")
    if not resolve:
        emit({"success": False, "error": "DaVinci Resolve is not running or external scripting is disabled."})
        return None

    project = resolve.GetCurrentProject()
    if not project:
        emit({"success": False, "error": "No project is currently open in DaVinci Resolve."})
        return None

    timeline = project.GetCurrentTimeline()
    if not timeline:
        emit({
            "success": False,
            "project": project.GetName(),
            "error": "No timeline is currently open in DaVinci Resolve."
        })
        return None

    return resolve, project, timeline
""";

    /// <summary>
    /// Converts a SMPTE timecode string (<c>HH:MM:SS:FF</c> or <c>HH:MM:SS;FF</c>) to a total frame count.
    /// </summary>
    public static int TimecodeToFrames(string? timecode, double fps)
    {
        if (string.IsNullOrWhiteSpace(timecode))
        {
            return 0;
        }

        string normalized = timecode.Trim().Replace(';', ':');
        string[] parts = normalized.Split(':');
        if (parts.Length != 4)
        {
            return 0;
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hh) ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mm) ||
            !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ss) ||
            !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ff))
        {
            return 0;
        }

        int fpsInt = Math.Max(1, (int)Math.Round(fps));
        return ((hh * 3600 + mm * 60 + ss) * fpsInt) + ff;
    }

    /// <summary>
    /// Formats a frame count into non-drop SMPTE timecode (<c>HH:MM:SS:FF</c>).
    /// </summary>
    public static string FramesToTimecode(int totalFrames, double fps)
    {
        int fpsInt = Math.Max(1, (int)Math.Round(fps));
        int safeFrames = Math.Max(0, totalFrames);
        int ff = safeFrames % fpsInt;
        int totalSeconds = safeFrames / fpsInt;
        int ss = totalSeconds % 60;
        int mm = (totalSeconds / 60) % 60;
        int hh = totalSeconds / 3600;
        return string.Create(CultureInfo.InvariantCulture, $"{hh:00}:{mm:00}:{ss:00}:{ff:00}");
    }

    /// <summary>
    /// Computes the 0-based frame offset from the timeline start timecode to the current playhead timecode.
    /// </summary>
    public static int ComputeFrameOffset(string? currentTimecode, string? startTimecode, double fps)
    {
        return Math.Max(0, TimecodeToFrames(currentTimecode, fps) - TimecodeToFrames(startTimecode, fps));
    }

    /// <summary>
    /// Builds the Python script executed against DaVinci Resolve's scripting API to export the playhead frame.
    /// </summary>
    public static string BuildPythonGrabScript()
    {
        return SharedPythonPrelude + "\n" + """
def main():
    if len(sys.argv) < 2 or not sys.argv[1]:
        emit({"success": False, "error": "Missing output file path argument."})
        return

    output_path = sys.argv[1]
    ctx = get_timeline_context()
    if not ctx:
        return
    _, project, timeline = ctx

    project_name = project.GetName()
    timeline_name = timeline.GetName()
    cur_tc = timeline.GetCurrentTimecode() or ""
    start_tc = timeline.GetStartTimecode() or "00:00:00:00"
    settings = timeline.GetSettings() or {}
    try:
        fps = float(settings.get("timelineFrameRate") or 24.0)
    except (TypeError, ValueError):
        fps = 24.0
    width = parse_int(settings.get("timelineResolutionWidth"))
    height = parse_int(settings.get("timelineResolutionHeight"))

    ok = bool(project.ExportCurrentFrameAsStill(output_path))
    file_ready = os.path.isfile(output_path) and os.path.getsize(output_path) > 0
    error_msg = None

    if not ok or not file_ready:
        frame_offset = max(0, tc_to_frames(cur_tc, fps) - tc_to_frames(start_tc, fps))
        res = timeline.ExportFrame(frame_offset, {"outputType": "file", "filePath": output_path})
        ok = bool(isinstance(res, dict) and res.get("success"))
        file_ready = os.path.isfile(output_path) and os.path.getsize(output_path) > 0
        if not ok or not file_ready:
            error_msg = (res.get("error") if isinstance(res, dict) else None) or "DaVinci Resolve could not export the current playhead frame."

    emit({
        "success": bool(ok and file_ready),
        "project": project_name,
        "timeline": timeline_name,
        "timecode": cur_tc,
        "width": width,
        "height": height,
        "error": error_msg
    })

if __name__ == "__main__":
    main()
""";
    }

    /// <summary>
    /// Builds the Python script executed against DaVinci Resolve's scripting API to insert a Fusion
    /// composition on the next free video track above the current item at the playhead position.
    /// </summary>
    public static string BuildPythonInjectScript()
    {
        return SharedPythonPrelude + "\n" + """
def main():
    if len(sys.argv) < 2 or not sys.argv[1]:
        emit({"success": False, "error": "Missing composition file path argument."})
        return

    comp_path = sys.argv[1]
    req_duration = parse_int(sys.argv[2]) if len(sys.argv) > 2 else 0

    ctx = get_timeline_context()
    if not ctx:
        return
    _, project, timeline = ctx

    project_name = project.GetName()
    timeline_name = timeline.GetName()
    orig_tc = timeline.GetCurrentTimecode() or "00:00:00:00"
    start_tc = timeline.GetStartTimecode() or "00:00:00:00"
    start_frame = int(timeline.GetStartFrame() or 0)
    end_frame = int(timeline.GetEndFrame() or start_frame)
    settings = timeline.GetSettings() or {}
    try:
        fps = float(settings.get("timelineFrameRate") or 24.0)
    except (TypeError, ValueError):
        fps = 24.0

    frame_offset = max(0, tc_to_frames(orig_tc, fps) - tc_to_frames(start_tc, fps))
    playhead_frame = start_frame + frame_offset

    cur_item = timeline.GetCurrentVideoItem()
    if cur_item:
        track_info = cur_item.GetTrackTypeAndIndex()
        base_track = int(track_info[1]) if (track_info and len(track_info) >= 2) else 1
    else:
        base_track = 0

    check_dur = max(req_duration, int(round(fps * 5)))
    track_count = int(timeline.GetTrackCount("video") or 0)
    target_track = None

    for t_idx in range(base_track + 1, track_count + 1):
        if timeline.GetIsTrackLocked("video", t_idx):
            continue
        items = timeline.GetItemListInTrack("video", t_idx) or []
        overlaps = any(
            it.GetStart() < (playhead_frame + check_dur) and it.GetEnd() > playhead_frame
            for it in items
        )
        if not overlaps:
            target_track = t_idx
            break

    if target_track is None:
        if not timeline.AddTrack("video"):
            emit({
                "success": False,
                "project": project_name,
                "timeline": timeline_name,
                "timecode": orig_tc,
                "error": "Failed to add a new video track above the current layer."
            })
            return
        target_track = int(timeline.GetTrackCount("video"))

    end_tc = frames_to_tc(tc_to_frames(start_tc, fps) + max(0, end_frame - start_frame), fps)
    orig_lock_1 = bool(timeline.GetIsTrackLocked("video", 1))
    new_item = None
    moved = False

    try:
        if orig_lock_1:
            timeline.SetTrackLock("video", 1, False)
        timeline.SetCurrentTimecode(end_tc)
        new_item = timeline.InsertFusionCompositionIntoTimeline()
        if new_item:
            moved = bool(new_item.SetPosition({"trackIndex": target_track, "startFrame": playhead_frame}))
    finally:
        if orig_lock_1:
            timeline.SetTrackLock("video", 1, True)
        timeline.SetCurrentTimecode(orig_tc)

    if not new_item:
        emit({
            "success": False,
            "project": project_name,
            "timeline": timeline_name,
            "timecode": orig_tc,
            "error": "DaVinci Resolve did not create a Fusion composition timeline item."
        })
        return

    if not moved:
        timeline.DeleteClips([new_item], False)
        emit({
            "success": False,
            "project": project_name,
            "timeline": timeline_name,
            "timecode": orig_tc,
            "error": f"Failed to move Fusion composition to Video {target_track} at playhead."
        })
        return

    imported = new_item.ImportFusionComp(comp_path)
    if not imported:
        timeline.DeleteClips([new_item], False)
        emit({
            "success": False,
            "project": project_name,
            "timeline": timeline_name,
            "timecode": orig_tc,
            "error": "Failed to import Scribble Fusion composition into the timeline item."
        })
        return

    pos = new_item.GetPosition() or {}
    final_track = int(pos.get("trackIndex") or target_track)
    final_start = int(pos.get("startFrame") or playhead_frame)
    final_dur = int(new_item.GetDuration() or 0)

    emit({
        "success": True,
        "project": project_name,
        "timeline": timeline_name,
        "timecode": orig_tc,
        "trackIndex": final_track,
        "startFrame": final_start,
        "duration": final_dur,
        "error": None
    })

if __name__ == "__main__":
    main()
""";
    }

    /// <summary>
    /// Parses the marker-prefixed JSON line from Python stdout together with any captured image bytes.
    /// </summary>
    public static ResolveFrameGrabResult ParseScriptResponse(string stdout, string stderr, byte[]? imageBytes)
    {
        if (!TryParseJsonDocument(stdout, stderr, out var doc, out string? extractError))
        {
            return new ResolveFrameGrabResult(Success: false, Error: extractError);
        }

        using (doc)
        {
            var root = doc.RootElement;
            bool scriptSuccess = IsScriptSuccess(root);
            string? project = GetOptionalString(root, "project");
            string? timeline = GetOptionalString(root, "timeline");
            string? timecode = GetOptionalString(root, "timecode");
            int width = GetOptionalInt(root, "width");
            int height = GetOptionalInt(root, "height");
            string? error = GetOptionalString(root, "error");

            if (!scriptSuccess)
            {
                return new ResolveFrameGrabResult(
                    Success: false,
                    ProjectName: project,
                    TimelineName: timeline,
                    Timecode: timecode,
                    Width: width,
                    Height: height,
                    Error: string.IsNullOrWhiteSpace(error) ? "Failed to grab frame from DaVinci Resolve." : error);
            }

            if (imageBytes is not { Length: > 0 })
            {
                return new ResolveFrameGrabResult(
                    Success: false,
                    ProjectName: project,
                    TimelineName: timeline,
                    Timecode: timecode,
                    Width: width,
                    Height: height,
                    Error: "DaVinci Resolve reported success, but no image data was produced.");
            }

            return new ResolveFrameGrabResult(
                Success: true,
                ImageBytes: imageBytes,
                ProjectName: project,
                TimelineName: timeline,
                Timecode: timecode,
                Width: width,
                Height: height);
        }
    }

    /// <summary>
    /// Parses the marker-prefixed JSON line returned by <see cref="BuildPythonInjectScript"/>.
    /// </summary>
    public static ResolveInjectResult ParseInjectResponse(string stdout, string stderr)
    {
        if (!TryParseJsonDocument(stdout, stderr, out var doc, out string? extractError))
        {
            return new ResolveInjectResult(Success: false, Error: extractError);
        }

        using (doc)
        {
            var root = doc.RootElement;
            bool scriptSuccess = IsScriptSuccess(root);
            string? project = GetOptionalString(root, "project");
            string? timeline = GetOptionalString(root, "timeline");
            string? timecode = GetOptionalString(root, "timecode");
            int trackIndex = GetOptionalInt(root, "trackIndex");
            int startFrame = GetOptionalInt(root, "startFrame");
            int duration = GetOptionalInt(root, "duration");
            string? error = GetOptionalString(root, "error");

            if (!scriptSuccess)
            {
                return new ResolveInjectResult(
                    Success: false,
                    ProjectName: project,
                    TimelineName: timeline,
                    Timecode: timecode,
                    TrackIndex: trackIndex,
                    StartFrame: startFrame,
                    DurationFrames: duration,
                    Error: string.IsNullOrWhiteSpace(error) ? "Failed to inject Fusion composition into DaVinci Resolve." : error);
            }

            return new ResolveInjectResult(
                Success: true,
                ProjectName: project,
                TimelineName: timeline,
                Timecode: timecode,
                TrackIndex: trackIndex,
                StartFrame: startFrame,
                DurationFrames: duration);
        }
    }

    /// <summary>
    /// Captures the frame at the current playhead from the running DaVinci Resolve session.
    /// </summary>
    public static async Task<ResolveFrameGrabResult> GrabCurrentFrameAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupportedDesktopPlatform())
        {
            return new ResolveFrameGrabResult(
                Success: false,
                Error: "Grabbing frames directly from DaVinci Resolve is only supported on desktop platforms (Windows, macOS, Linux).");
        }

        string tempFilePath = Path.Combine(Path.GetTempPath(), $"scribble_resolve_frame_{Guid.NewGuid():N}.png");

        try
        {
            var run = await RunResolvePythonAsync(
                BuildPythonGrabScript(),
                [tempFilePath],
                "Timed out waiting for DaVinci Resolve to export the current frame.",
                cancellationToken).ConfigureAwait(false);

            if (!run.Started)
            {
                return new ResolveFrameGrabResult(Success: false, Error: run.Error);
            }

            byte[]? imageBytes = File.Exists(tempFilePath)
                ? await File.ReadAllBytesAsync(tempFilePath, cancellationToken).ConfigureAwait(false)
                : null;

            return ParseScriptResponse(run.Stdout, run.Stderr, imageBytes);
        }
        finally
        {
            TryDeleteFile(tempFilePath);
        }
    }

    /// <summary>
    /// Injects a Fusion composition (<c>.comp</c> Lua string) into the active DaVinci Resolve timeline
    /// on the next free video track above the current playhead item.
    /// </summary>
    public static async Task<ResolveInjectResult> InjectFusionCompositionAsync(
        string compContent,
        int requiredDurationFrames = 0,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedDesktopPlatform())
        {
            return new ResolveInjectResult(
                Success: false,
                Error: "Injecting compositions directly into DaVinci Resolve is only supported on desktop platforms (Windows, macOS, Linux).");
        }

        string tempCompPath = Path.Combine(Path.GetTempPath(), $"scribble_resolve_inject_{Guid.NewGuid():N}.comp");

        try
        {
            await File.WriteAllTextAsync(tempCompPath, compContent, cancellationToken).ConfigureAwait(false);

            var run = await RunResolvePythonAsync(
                BuildPythonInjectScript(),
                [tempCompPath, requiredDurationFrames.ToString(CultureInfo.InvariantCulture)],
                "Timed out waiting for DaVinci Resolve to inject the Fusion composition.",
                cancellationToken).ConfigureAwait(false);

            return run.Started
                ? ParseInjectResponse(run.Stdout, run.Stderr)
                : new ResolveInjectResult(Success: false, Error: run.Error);
        }
        finally
        {
            TryDeleteFile(tempCompPath);
        }
    }

    private static bool IsSupportedDesktopPlatform() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private readonly record struct PythonRunResult(bool Started, string Stdout, string Stderr, string? Error = null);

    private static async Task<PythonRunResult> RunResolvePythonAsync(
        string script,
        IReadOnlyList<string> args,
        string timeoutErrorMessage,
        CancellationToken cancellationToken)
    {
        var (pythonExe, useResolveEnv) = ResolvePythonExecutable();

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            if (useResolveEnv)
            {
                ConfigureFallbackResolveEnvironment(startInfo);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new PythonRunResult(false, string.Empty, string.Empty, $"Unable to launch Python interpreter '{pythonExe}'.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return new PythonRunResult(false, string.Empty, string.Empty, timeoutErrorMessage);
            }

            string stdout = await stdoutTask.ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            return new PythonRunResult(true, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PythonRunResult(false, string.Empty, string.Empty, $"Could not run DaVinci Resolve script ({pythonExe}): {ex.Message}");
        }
    }

    private static bool TryParseJsonDocument(
        string stdout,
        string stderr,
        out JsonDocument doc,
        out string? error)
    {
        string? jsonLine = null;
        if (!string.IsNullOrEmpty(stdout))
        {
            string[] lines = stdout.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith(ResultMarker, StringComparison.Ordinal))
                {
                    jsonLine = trimmed[ResultMarker.Length..].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(jsonLine))
        {
            string detail = !string.IsNullOrWhiteSpace(stderr)
                ? stderr.Trim()
                : !string.IsNullOrWhiteSpace(stdout)
                    ? stdout.Trim()
                    : "No response received from DaVinci Resolve scripting host.";
            doc = null!;
            error = $"Failed to communicate with DaVinci Resolve: {detail}";
            return false;
        }

        try
        {
            doc = JsonDocument.Parse(jsonLine);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            doc = null!;
            error = $"Invalid response from DaVinci Resolve script: {ex.Message}";
            return false;
        }
    }

    private static bool IsScriptSuccess(JsonElement root) =>
        root.TryGetProperty("success", out var successProp) &&
        successProp.ValueKind == JsonValueKind.True;

    private static (string Executable, bool NeedsResolveEnv) ResolvePythonExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(WindowsResolvePythonPath)
                ? (WindowsResolvePythonPath, false)
                : ("python", true);
        }

        if (OperatingSystem.IsMacOS())
        {
            return File.Exists(MacOsResolvePythonPath)
                ? (MacOsResolvePythonPath, false)
                : ("python3", true);
        }

        return File.Exists(LinuxResolvePythonPath)
            ? (LinuxResolvePythonPath, false)
            : ("python3", true);
    }

    private static void ConfigureFallbackResolveEnvironment(ProcessStartInfo startInfo)
    {
        string apiDir;
        string libPath;

        if (OperatingSystem.IsWindows())
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            apiDir = Path.Combine(programData, "Blackmagic Design", "DaVinci Resolve", "Support", "Developer", "Scripting");
            libPath = @"C:\Program Files\Blackmagic Design\DaVinci Resolve\fusionscript.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            apiDir = "/Library/Application Support/Blackmagic Design/DaVinci Resolve/Developer/Scripting";
            libPath = "/Applications/DaVinci Resolve/DaVinci Resolve.app/Contents/Libraries/Fusion/fusionscript.so";
        }
        else
        {
            apiDir = "/opt/resolve/Developer/Scripting";
            libPath = "/opt/resolve/libs/Fusion/fusionscript.so";
        }

        string modulesDir = Path.Combine(apiDir, "Modules");
        startInfo.Environment["RESOLVE_SCRIPT_API"] = apiDir;
        startInfo.Environment["RESOLVE_SCRIPT_LIB"] = libPath;

        string? existingPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
        startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existingPythonPath)
            ? modulesDir
            : $"{existingPythonPath}{Path.PathSeparator}{modulesDir}";
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static int GetOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
        {
            return 0;
        }

        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out int val) ? val : 0;
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
