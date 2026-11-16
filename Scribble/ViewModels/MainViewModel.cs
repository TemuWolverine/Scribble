using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scribble.Core.Export;
using Scribble.Core.Models;
using Scribble.Core.Processing;
using Scribble.Core.Serialization;
using Scribble.Services;

namespace Scribble.ViewModels;

public sealed record SpeedOption(string Label, double Multiplier)
{
    public override string ToString() => Label;
}

public sealed partial class ColorSwatch : ObservableObject
{
    public ColorSwatch(StrokeColor color)
    {
        StrokeColor = color;
    }

    [ObservableProperty]
    public partial StrokeColor StrokeColor { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public IBrush Brush => new SolidColorBrush(Color.FromArgb(
        StrokeColor.A, StrokeColor.R, StrokeColor.G, StrokeColor.B));

    partial void OnStrokeColorChanged(StrokeColor value) => OnPropertyChanged(nameof(Brush));
}

public partial class MainViewModel : ViewModelBase
{
    private sealed record UndoEntry(Action Undo, Action Redo);

    private readonly Stack<UndoEntry> _undoStack = new();
    private readonly Stack<UndoEntry> _redoStack = new();
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private double _playbackStartOffsetMs;
    private ColorSwatch? _editingSwatch;
    private bool _updatingPickerFields;

    public IReadOnlyList<CanvasPreset> Presets => CanvasPreset.All;
    public IReadOnlyList<int> FrameRates { get; } = [24, 30, 60];
    public IReadOnlyList<SpeedOption> SpeedOptions { get; } =
    [
        new("0.5x", 0.5),
        new("1x", 1.0),
        new("1.5x", 1.5),
        new("2x", 2.0),
        new("3x", 3.0),
        new("4x", 4.0)
    ];
    public ObservableCollection<ColorSwatch> Palette { get; } = [];

    [ObservableProperty]
    public partial DrawingDocument Document { get; set; } = new();

    [ObservableProperty]
    public partial CanvasPreset SelectedPreset { get; set; } = CanvasPreset.Hd1080p;

    [ObservableProperty]
    public partial int SelectedFps { get; set; } = 30;

    [ObservableProperty]
    public partial SpeedOption SelectedSpeed { get; set; } = new("1x", 1.0);

    [ObservableProperty]
    public partial StrokeColor ActiveColor { get; set; } = StrokeColor.White;

    [ObservableProperty]
    public partial double BaseWidth { get; set; } = 8.0;

    [ObservableProperty]
    public partial bool IsEraserMode { get; set; }

    [ObservableProperty]
    public partial bool PenOnlyMode { get; set; }

    [ObservableProperty]
    public partial bool CompressIdleGaps { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPlaybackActive { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial double PlaybackTimeMs { get; set; }

    [ObservableProperty]
    public partial double TotalDurationMs { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Draw with stylus or mouse. Long-press a color to customize.";

    // Color picker modal state
    [ObservableProperty]
    public partial bool IsColorPickerOpen { get; set; }

    [ObservableProperty]
    public partial double PickerRed { get; set; } = 255;

    [ObservableProperty]
    public partial double PickerGreen { get; set; } = 255;

    [ObservableProperty]
    public partial double PickerBlue { get; set; } = 255;

    [ObservableProperty]
    public partial string PickerHex { get; set; } = "#FFFFFF";

    [ObservableProperty]
    public partial Bitmap? ReferenceImage { get; set; }

    [ObservableProperty]
    public partial double ReferenceOpacity { get; set; } = 0.55;

    public bool HasReferenceImage => ReferenceImage is not null;

    public bool IsPenMode => !IsEraserMode;

    public string ActiveModeLabel => IsEraserMode
        ? "Mode: Eraser"
        : PenOnlyMode
            ? "Mode: Pen (Stylus Only)"
            : "Mode: Pen";

    public IBrush PickerPreviewBrush => new SolidColorBrush(Color.FromRgb(
        (byte)Math.Clamp(PickerRed, 0, 255),
        (byte)Math.Clamp(PickerGreen, 0, 255),
        (byte)Math.Clamp(PickerBlue, 0, 255)));

    public event Action? CanvasInvalidated;
    public event Action? ResetClockRequested;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public MainViewModel()
    {
        SelectedSpeed = SpeedOptions[1]; // 1x
        LoadPaletteFromSettings();

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playbackTimer.Tick += OnPlaybackTick;
    }

    private void LoadPaletteFromSettings()
    {
        var settings = SettingsStore.Load();
        Palette.Clear();
        foreach (string hex in settings.PaletteColors)
        {
            try
            {
                Palette.Add(new ColorSwatch(StrokeColor.FromHex(hex)));
            }
            catch
            {
                // Skip malformed entries
            }
        }

        if (Palette.Count == 0)
        {
            Palette.Add(new ColorSwatch(StrokeColor.White));
            Palette.Add(new ColorSwatch(StrokeColor.Black));
            Palette.Add(new ColorSwatch(StrokeColor.Red));
            Palette.Add(new ColorSwatch(StrokeColor.Blue));
            Palette.Add(new ColorSwatch(StrokeColor.Green));
            Palette.Add(new ColorSwatch(StrokeColor.Yellow));
        }

        SelectSwatch(Palette[0]);
    }

    private void SavePaletteToSettings()
    {
        var settings = new AppSettings
        {
            PaletteColors = Palette.Select(s => s.StrokeColor.ToHex()).ToList()
        };
        SettingsStore.Save(settings);
    }

    private void SelectSwatch(ColorSwatch swatch)
    {
        foreach (var item in Palette)
        {
            item.IsSelected = ReferenceEquals(item, swatch);
        }
        ActiveColor = swatch.StrokeColor;
    }

    partial void OnIsEraserModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPenMode));
        OnPropertyChanged(nameof(ActiveModeLabel));
    }

    partial void OnPenOnlyModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ActiveModeLabel));
    }

    partial void OnReferenceImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasReferenceImage));
        CanvasInvalidated?.Invoke();
    }

    [RelayCommand]
    private void ClearReferenceImage()
    {
        ReferenceImage = null;
        StatusText = "Reference image cleared.";
    }

    partial void OnPickerRedChanged(double value) => SyncPickerHexFromRgb();
    partial void OnPickerGreenChanged(double value) => SyncPickerHexFromRgb();
    partial void OnPickerBlueChanged(double value) => SyncPickerHexFromRgb();

    partial void OnPickerHexChanged(string value)
    {
        if (_updatingPickerFields) return;
        string trimmed = value.Trim();
        if (trimmed.StartsWith('#')) trimmed = trimmed[1..];
        if (trimmed.Length == 6)
        {
            try
            {
                var c = StrokeColor.FromHex(trimmed);
                _updatingPickerFields = true;
                PickerRed = c.R;
                PickerGreen = c.G;
                PickerBlue = c.B;
                OnPropertyChanged(nameof(PickerPreviewBrush));
                _updatingPickerFields = false;
            }
            catch
            {
                // Wait until valid 6-digit hex is typed
            }
        }
    }

    private void SyncPickerHexFromRgb()
    {
        OnPropertyChanged(nameof(PickerPreviewBrush));
        if (_updatingPickerFields) return;
        _updatingPickerFields = true;
        byte r = (byte)Math.Clamp(PickerRed, 0, 255);
        byte g = (byte)Math.Clamp(PickerGreen, 0, 255);
        byte b = (byte)Math.Clamp(PickerBlue, 0, 255);
        PickerHex = $"#{r:X2}{g:X2}{b:X2}";
        _updatingPickerFields = false;
    }

    partial void OnSelectedPresetChanged(CanvasPreset value)
    {
        Document.Width = value.Width;
        Document.Height = value.Height;
        CanvasInvalidated?.Invoke();
        StatusText = $"Canvas set to {value.Width}x{value.Height}";
    }

    partial void OnSelectedFpsChanged(int value)
    {
        Document.FrameRate = value;
        RefreshMetrics();
    }

    partial void OnSelectedSpeedChanged(SpeedOption value)
    {
        RefreshMetrics();
    }

    partial void OnCompressIdleGapsChanged(bool value)
    {
        RefreshMetrics();
    }

    partial void OnPlaybackTimeMsChanged(double value)
    {
        if (!IsPlaying && IsPlaybackActive)
        {
            _playbackStartOffsetMs = value;
            CanvasInvalidated?.Invoke();
        }
    }

    public void NotifyStrokeCompleted(Stroke stroke)
    {
        _redoStack.Clear();
        _undoStack.Push(new UndoEntry(
            Undo: () => Document.Strokes.Remove(stroke),
            Redo: () => Document.Strokes.Add(stroke)));
        RefreshMetrics();
    }

    public void NotifyStrokeErased(Stroke stroke)
    {
        _redoStack.Clear();
        _undoStack.Push(new UndoEntry(
            Undo: () => Document.Strokes.Add(stroke),
            Redo: () => Document.Strokes.Remove(stroke)));
        RefreshMetrics();
    }

    public void OpenColorPicker(ColorSwatch swatch)
    {
        _editingSwatch = swatch;
        _updatingPickerFields = true;
        PickerRed = swatch.StrokeColor.R;
        PickerGreen = swatch.StrokeColor.G;
        PickerBlue = swatch.StrokeColor.B;
        PickerHex = $"#{swatch.StrokeColor.R:X2}{swatch.StrokeColor.G:X2}{swatch.StrokeColor.B:X2}";
        _updatingPickerFields = false;
        OnPropertyChanged(nameof(PickerPreviewBrush));
        IsColorPickerOpen = true;
    }

    [RelayCommand]
    private void ApplyColorPicker()
    {
        if (_editingSwatch is not null)
        {
            var newColor = new StrokeColor(
                (byte)Math.Clamp(PickerRed, 0, 255),
                (byte)Math.Clamp(PickerGreen, 0, 255),
                (byte)Math.Clamp(PickerBlue, 0, 255),
                255);

            _editingSwatch.StrokeColor = newColor;
            SelectSwatch(_editingSwatch);
            IsEraserMode = false;
            SavePaletteToSettings();
            StatusText = $"Updated color to {PickerHex}";
        }

        IsColorPickerOpen = false;
        _editingSwatch = null;
    }

    [RelayCommand]
    private void CancelColorPicker()
    {
        IsColorPickerOpen = false;
        _editingSwatch = null;
    }

    [RelayCommand]
    private void SelectColor(ColorSwatch swatch)
    {
        SelectSwatch(swatch);
        IsEraserMode = false;
    }

    [RelayCommand]
    private void TogglePenTool()
    {
        IsEraserMode = false;
    }

    [RelayCommand]
    private void ToggleEraserTool()
    {
        IsEraserMode = true;
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        StopPlayback();
        var entry = _undoStack.Pop();
        entry.Undo();
        _redoStack.Push(entry);
        RefreshMetrics();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        StopPlayback();
        var entry = _redoStack.Pop();
        entry.Redo();
        _undoStack.Push(entry);
        RefreshMetrics();
    }

    [RelayCommand]
    private void ClearCanvas()
    {
        if (Document.Strokes.Count == 0) return;
        StopPlayback();
        var snapshot = Document.Strokes.ToList();
        _redoStack.Clear();
        _undoStack.Push(new UndoEntry(
            Undo: () =>
            {
                Document.Strokes.Clear();
                Document.Strokes.AddRange(snapshot);
            },
            Redo: () => Document.Strokes.Clear()));
        Document.Strokes.Clear();
        ResetClockRequested?.Invoke();
        RefreshMetrics();
        StatusText = "Canvas cleared.";
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (Document.Strokes.Count == 0)
        {
            StatusText = "Draw at least one stroke to preview.";
            return;
        }

        if (IsPlaying)
        {
            IsPlaying = false;
            _playbackTimer.Stop();
            _playbackClock.Stop();
            return;
        }

        var strokes = CompressIdleGaps
            ? StrokeSmoother.CompressIdleGaps(Document.Strokes)
            : Document.Strokes;

        double rawDuration = strokes.Count == 0 ? 0 : strokes.Max(s => s.EndTimeMs);
        TotalDurationMs = rawDuration / Math.Max(0.1, SelectedSpeed.Multiplier);
        if (TotalDurationMs <= 0) TotalDurationMs = 500;

        if (!IsPlaybackActive || PlaybackTimeMs >= TotalDurationMs)
        {
            PlaybackTimeMs = 0;
        }

        _playbackStartOffsetMs = PlaybackTimeMs;
        IsPlaybackActive = true;
        IsPlaying = true;
        _playbackClock.Restart();
        _playbackTimer.Start();
    }

    [RelayCommand]
    private void StopPlayback()
    {
        IsPlaying = false;
        IsPlaybackActive = false;
        _playbackTimer.Stop();
        _playbackClock.Stop();
        PlaybackTimeMs = 0;
        CanvasInvalidated?.Invoke();
    }

    private void OnPlaybackTick(object? sender, EventArgs e)
    {
        double elapsed = _playbackStartOffsetMs + _playbackClock.Elapsed.TotalMilliseconds;
        if (elapsed >= TotalDurationMs)
        {
            PlaybackTimeMs = TotalDurationMs;
            IsPlaying = false;
            _playbackTimer.Stop();
            _playbackClock.Stop();
        }
        else
        {
            PlaybackTimeMs = elapsed;
        }
        CanvasInvalidated?.Invoke();
    }

    public string BuildFusionSetting()
    {
        var options = new FusionExportOptions(
            CompressIdleGaps: CompressIdleGaps,
            SpeedMultiplier: SelectedSpeed.Multiplier);
        return FusionSettingExporter.Export(Document, options);
    }

    public string SerializeProject() => DocumentSerializer.Serialize(Document);

    public void LoadProject(string json)
    {
        var loaded = DocumentSerializer.Deserialize(json);
        if (loaded is null) return;

        StopPlayback();
        _undoStack.Clear();
        _redoStack.Clear();
        Document = loaded;

        var matchingPreset = Presets.FirstOrDefault(p => p.Width == loaded.Width && p.Height == loaded.Height);
        if (matchingPreset is not null)
        {
            SelectedPreset = matchingPreset;
        }
        if (FrameRates.Contains(loaded.FrameRate))
        {
            SelectedFps = loaded.FrameRate;
        }

        RefreshMetrics();
        StatusText = $"Loaded {loaded.Strokes.Count} strokes.";
    }

    private void RefreshMetrics()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        var active = CompressIdleGaps
            ? StrokeSmoother.CompressIdleGaps(Document.Strokes)
            : Document.Strokes;
        double rawDuration = active.Count == 0 ? 0 : active.Max(s => s.EndTimeMs);
        TotalDurationMs = rawDuration / Math.Max(0.1, SelectedSpeed.Multiplier);
        int frames = WriteOnTimeline.TimeMsToFrame(TotalDurationMs, SelectedFps);
        StatusText = $"{Document.Strokes.Count} stroke(s) • {TotalDurationMs / 1000.0:0.0}s @ {SelectedSpeed.Label} ({frames}f / {SelectedFps}fps)";
        CanvasInvalidated?.Invoke();
    }
}
