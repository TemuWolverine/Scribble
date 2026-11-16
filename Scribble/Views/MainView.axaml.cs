using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Scribble.Core.Models;
using Scribble.ViewModels;

namespace Scribble.Views;

public partial class MainView : UserControl
{
    private readonly DispatcherTimer _longPressTimer;
    private ColorSwatch? _pressedSwatch;
    private Control? _pressedSwatchControl;
    private Point _pressStartPoint;
    private bool _longPressTriggered;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        CanvasControl.StrokeCompleted += OnStrokeCompleted;
        CanvasControl.StrokeErased += OnStrokeErased;

        _longPressTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _longPressTimer.Tick += OnLongPressTick;
    }

    private TopLevel? _attachedTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedTopLevel = TopLevel.GetTopLevel(this);
        if (_attachedTopLevel is not null)
        {
            _attachedTopLevel.KeyDown += OnTopLevelKeyDown;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_attachedTopLevel is not null)
        {
            _attachedTopLevel.KeyDown -= OnTopLevelKeyDown;
            _attachedTopLevel = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        // Let TextBox handle its own shortcuts when editing hex in the color picker
        if (vm.IsColorPickerOpen || e.Source is TextBox)
        {
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Z:
                if (vm.UndoCommand.CanExecute(null))
                {
                    vm.UndoCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.Y:
                if (vm.RedoCommand.CanExecute(null))
                {
                    vm.RedoCommand.Execute(null);
                    e.Handled = true;
                }
                break;

            case Key.V:
                e.Handled = true;
                await PasteReferenceImageAsync();
                break;
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.CanvasInvalidated += () => CanvasControl.InvalidateVisual();
            vm.ResetClockRequested += () => CanvasControl.ResetClock();
        }
    }

    private void OnStrokeCompleted(Stroke stroke) => ViewModel?.NotifyStrokeCompleted(stroke);
    private void OnStrokeErased(Stroke stroke) => ViewModel?.NotifyStrokeErased(stroke);

    private void OnSwatchPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ColorSwatch swatch } control || ViewModel is null)
        {
            return;
        }

        // Right-click also opens the picker immediately on desktop
        var props = e.GetCurrentPoint(control).Properties;
        if (props.IsRightButtonPressed)
        {
            ViewModel.OpenColorPicker(swatch);
            e.Handled = true;
            return;
        }

        _pressedSwatch = swatch;
        _pressedSwatchControl = control;
        _pressStartPoint = e.GetPosition(control);
        _longPressTriggered = false;
        e.Pointer.Capture(control);
        _longPressTimer.Stop();
        _longPressTimer.Start();
        e.Handled = true;
    }

    private void OnSwatchPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedSwatchControl is null || !_longPressTimer.IsEnabled)
        {
            return;
        }

        var current = e.GetPosition(_pressedSwatchControl);
        double dx = current.X - _pressStartPoint.X;
        double dy = current.Y - _pressStartPoint.Y;
        if (dx * dx + dy * dy > 100) // > 10px drag cancels long-press
        {
            CancelLongPress();
        }
    }

    private void OnSwatchPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _longPressTimer.Stop();
        if (!_longPressTriggered && _pressedSwatch is { } swatch && ViewModel is not null)
        {
            ViewModel.SelectColorCommand.Execute(swatch);
        }

        e.Pointer.Capture(null);
        _pressedSwatch = null;
        _pressedSwatchControl = null;
        _longPressTriggered = false;
        e.Handled = true;
    }

    private void OnSwatchPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CancelLongPress();
    }

    private void OnLongPressTick(object? sender, EventArgs e)
    {
        _longPressTimer.Stop();
        if (_pressedSwatch is { } swatch && ViewModel is not null)
        {
            _longPressTriggered = true;
            ViewModel.OpenColorPicker(swatch);
        }
    }

    private void CancelLongPress()
    {
        _longPressTimer.Stop();
        _pressedSwatch = null;
        _pressedSwatchControl = null;
        _longPressTriggered = false;
    }

    private async void OnExportFusionClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        if (vm.Document.Strokes.Count == 0)
        {
            vm.StatusText = "Draw something on the canvas before exporting.";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DaVinci Resolve Fusion Setting",
            SuggestedFileName = "ScribbleAnimation.setting",
            DefaultExtension = "setting",
            FileTypeChoices =
            [
                new FilePickerFileType("Fusion Setting Macro") { Patterns = ["*.setting"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] }
            ]
        });

        if (file is null) return;

        string content = vm.BuildFusionSetting();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(content);
        vm.StatusText = $"Exported Fusion .setting to {file.Name}";
    }

    private async void OnSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Scribble Project",
            SuggestedFileName = "Drawing.scribble",
            DefaultExtension = "scribble",
            FileTypeChoices =
            [
                new FilePickerFileType("Scribble Project") { Patterns = ["*.scribble"] }
            ]
        });

        if (file is null) return;

        string json = vm.SerializeProject();
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        await writer.WriteAsync(json);
        vm.StatusText = $"Saved {file.Name}";
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Scribble Project",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Scribble Project") { Patterns = ["*.scribble", "*.json"] }
            ]
        });

        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string json = await reader.ReadToEndAsync();
        vm.LoadProject(json);
    }

    private async void OnLoadReferenceImageClicked(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load Reference Image",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        if (files.Count == 0) return;

        await using var stream = await files[0].OpenReadAsync();
        vm.ReferenceImage = new Bitmap(stream);
        vm.StatusText = $"Loaded reference image: {files[0].Name} (not exported)";
    }

    private async Task PasteReferenceImageAsync()
    {
        if (ViewModel is not { } vm) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is null) return;

        var bitmap = await topLevel.Clipboard.TryGetBitmapAsync();
        if (bitmap is not null)
        {
            vm.ReferenceImage = bitmap;
            vm.StatusText = "Pasted reference image from clipboard (not exported)";
        }
        else
        {
            vm.StatusText = "No image found on clipboard.";
        }
    }
}
