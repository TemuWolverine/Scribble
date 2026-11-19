# Scribble

A freehand drawing and write-on animation tool for DaVinci Resolve, built with C# and Avalonia for **Windows**.

Draw annotations, diagrams, or handwriting with a stylus or mouse on a transparent canvas, preview the write-on playback, and export animated vector strokes straight into DaVinci Resolve Fusion.

## Features

- **Freehand drawing**: Pressure-sensitive stylus and mouse input on a transparent canvas with resolution presets (1080p, 4K UHD, 9:16 vertical, 1:1 square), adjustable brush size, real-time stroke smoothing, eraser, undo/redo, a pen-only palm rejection mode, and a customizable color palette (long-press or right-click a swatch to edit).
- **Write-on playback**: Preview stroke animations with a timeline scrubber, selectable frame rates (24, 30, or 60 fps), speed multipliers (0.5x to 4x), and optional pause trimming to compress idle gaps between strokes.
- **Reference images**: Load an image from disk or paste from the clipboard (`Ctrl+V`) behind the canvas for tracing. Reference images are never included in exports.
- **DaVinci Resolve round-trip**:
  - **Grab Frame**: Captures the frame at Resolve's current playhead and sets it as the canvas background.
  - **Inject Resolve**: Inserts the animated Fusion composition onto the next free video track above the current clip at the playhead.
- **Fusion `.setting` export**: Export a standalone DaVinci Resolve Fusion `.setting` macro built from transparent `Background`, `PolylineMask`, and `BezierSpline` write-on nodes.
- **Project files**: Save and reopen drawings as `.scribble` files.

## DaVinci Resolve integration

Direct frame grabbing and timeline injection run on desktop using DaVinci Resolve's local Python scripting API. DaVinci Resolve must be running with external scripting enabled and an active timeline open.

## Building from source

Requires the **.NET 11 SDK** (preview).

**Run on Windows:**
```sh
dotnet run --project Scribble.Desktop
```

**Run tests:**
```sh
dotnet test
```

## License

[MIT](LICENSE)
