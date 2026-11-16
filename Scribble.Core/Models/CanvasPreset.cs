namespace Scribble.Core.Models;

/// <summary>
/// Standard video/animation canvas resolution presets for DaVinci Resolve.
/// </summary>
public sealed record CanvasPreset(string Name, int Width, int Height)
{
    public static CanvasPreset Hd1080p { get; } = new("1080p (16:9)", 1920, 1080);
    public static CanvasPreset Uhd4K { get; } = new("4K UHD (16:9)", 3840, 2160);
    public static CanvasPreset Vertical1080 { get; } = new("Vertical (9:16)", 1080, 1920);
    public static CanvasPreset Square1080 { get; } = new("Square (1:1)", 1080, 1080);

    public static IReadOnlyList<CanvasPreset> All { get; } =
    [
        Hd1080p,
        Uhd4K,
        Vertical1080,
        Square1080
    ];

    public override string ToString() => Name;
}
