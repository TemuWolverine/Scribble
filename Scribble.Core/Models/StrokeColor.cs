using System.Globalization;

namespace Scribble.Core.Models;

/// <summary>
/// RGBA color for a stroke.
/// </summary>
public readonly record struct StrokeColor(byte R, byte G, byte B, byte A = 255)
{
    public static StrokeColor White => new(255, 255, 255, 255);
    public static StrokeColor Black => new(20, 20, 20, 255);
    public static StrokeColor Red => new(239, 68, 68, 255);
    public static StrokeColor Blue => new(59, 130, 246, 255);
    public static StrokeColor Green => new(34, 197, 94, 255);
    public static StrokeColor Yellow => new(250, 204, 21, 255);

    public (double R, double G, double B, double A) ToNormalized() =>
        (R / 255.0, G / 255.0, B / 255.0, A / 255.0);

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public static StrokeColor FromHex(string hex)
    {
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length == 6)
        {
            return new StrokeColor(
                byte.Parse(span[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                255);
        }
        if (span.Length == 8)
        {
            return new StrokeColor(
                byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(span[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(span[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }
        throw new FormatException($"Invalid hex color: {hex}");
    }
}
