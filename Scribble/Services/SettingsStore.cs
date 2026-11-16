using System;
using System.IO;
using Scribble.Core.Serialization;

namespace Scribble.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> in the platform's local application data folder
/// (works on both Windows and Android without extra permissions).
/// </summary>
public static class SettingsStore
{
    private static string SettingsFilePath
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                baseDir = AppContext.BaseDirectory;
            }
            return Path.Combine(baseDir, "Scribble", "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(path);
            return AppSettings.FromJson(json);
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            string path = SettingsFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, settings.ToJson());
        }
        catch
        {
            // Ignore IO errors so drawing/UI never crashes
        }
    }
}
