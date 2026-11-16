using System.Text.Json;
using Scribble.Core.Models;

namespace Scribble.Core.Serialization;

/// <summary>
/// Serializes and deserializes <see cref="DrawingDocument"/> projects to JSON (.scribble).
/// </summary>
public static class DocumentSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string Serialize(DrawingDocument document) =>
        JsonSerializer.Serialize(document, JsonOptions);

    public static DrawingDocument? Deserialize(string json) =>
        JsonSerializer.Deserialize<DrawingDocument>(json, JsonOptions);
}
