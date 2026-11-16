using System.Text.Json;
using Scribble.Core.Models;

namespace Scribble.Core.Serialization;

/// <summary>
/// Persisted user preferences (custom palette colors, etc.).
/// </summary>
public sealed class AppSettings
{
    public List<string> PaletteColors { get; set; } =
    [
        StrokeColor.White.ToHex(),
        StrokeColor.Black.ToHex(),
        StrokeColor.Red.ToHex(),
        StrokeColor.Blue.ToHex(),
        StrokeColor.Green.ToHex(),
        StrokeColor.Yellow.ToHex()
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static AppSettings FromJson(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
}
